using System;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.World;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Items
{
    /// <summary>Where a rod is in its cycle. One byte on the wire; everything else is derived.</summary>
    public enum FishingState : byte
    {
        /// <summary>Nothing in the water.</summary>
        Idle,

        /// <summary>Bobber out, waiting for a bite. The relaxing part.</summary>
        Waiting,

        /// <summary>Something took it. A short window to strike.</summary>
        Biting,

        /// <summary>Hooked. Reel during the calm, let go during the run.</summary>
        Fighting,
    }

    /// <summary>
    /// The fishing rod, end to end: cast, wait, strike, fight, and something in the bag.
    ///
    /// It lives on the player rather than on the water for the same reason <c>Trading</c> and
    /// <c>Upgrading</c> do - the sea is owned by nobody, so a <c>ServerRpc</c> on it would have no
    /// owner to require and would take a message from anyone. The bag has an owner.
    ///
    /// **One button does the whole minigame.** Attack casts, Attack strikes, and holding Attack
    /// reels. That is not minimalism for its own sake: a rod with three keys would need three
    /// bindings, three tutorial lines and a rule about what the other two do while you are holding a
    /// machete. Holding the rod is what makes Attack mean this instead of a punch, and
    /// <c>PlayerCombatInput</c> asks here first and falls through to the weapon when the answer is no.
    ///
    /// **The client sends presses and a direction. Nothing else.** Where the bobber landed, what is
    /// on the line, how hard it pulls and whether it got away are all decided here, on the server,
    /// from the server's own copy of the catalog. A client that lies about its aim gets a cast it did
    /// not want; a client that lies about anything else has nowhere to put the lie.
    ///
    /// **The roll happens at the cast, not at the landing**, so the fight already knows what it is.
    /// Deciding at the end would mean a tuna that fought like a sardine, and the fight is the only
    /// place the game can tell you what you have got.
    ///
    /// The one honest simplification: the bobber is a replicated point and a line drawn to it, not a
    /// networked object. Nobody can knock your float about, which would be extremely funny and is
    /// what the "not done" list in the docs is for.
    /// </summary>
    public class Fishing : NetworkBehaviour
    {
        [Header("References")]
        [Tooltip("Every fish in the game, plus the rod that opens the table. The wire carries an "
                 + "index into this, so every peer needs the identical asset.")]
        [SerializeField] FishCatalog _catalog;

        [SerializeField] Inventory _inventory;

        [Tooltip("Where the cast is aimed from - the camera, so the bobber lands where you looked.")]
        [SerializeField] Transform _aimOrigin;

        [Header("The rod")]
        [Tooltip("Furthest the bobber may land, in metres.")]
        [Min(2f)]
        [SerializeField] float _castRange = 30f;

        [Tooltip("Metres of water needed under the bobber. Stops anybody fishing a puddle, and is "
                 + "the reason walking out to the point is worth it.")]
        [Min(0f)]
        [SerializeField] float _minDepth = 2.5f;

        [Tooltip("Tension the line sheds per second while you are not reeling. The one number that "
                 + "belongs to the rod rather than the fish.")]
        [Min(0.05f)]
        [SerializeField] float _relaxPerSecond = 0.6f;

        [Tooltip("Multiple of the starting distance at which a fish has simply taken all your line.")]
        [Min(1.05f)]
        [SerializeField] float _spoolFactor = 1.6f;

        [Tooltip("How far you may wander from your own bobber before the line goes. Multiples of the "
                 + "cast range: fishing while sprinting away is not fishing.")]
        [Min(1f)]
        [SerializeField] float _leashFactor = 1.5f;

        [SerializeField] LayerMask _blockMask = ~0;

        [Header("Anti-cheat")]
        [Tooltip("Degrees the requested aim may deviate from where the character is actually facing.")]
        [Range(30f, 180f)]
        [SerializeField] float _maxAimDeviation = 100f;

        /// <summary>Where in the cycle this rod is, on every peer.</summary>
        readonly SyncVar<FishingState> _state = new(new SyncTypeSettings(0.05f));

        /// <summary>Where the float is sitting. Sea level by construction; the line is drawn to it.</summary>
        readonly SyncVar<Vector3> _bobber = new(new SyncTypeSettings(0.1f));

        /// <summary>What is on the line, as a catalog index. Stays 0 until it is actually hooked.</summary>
        readonly SyncVar<ushort> _hooked = new();

        /// <summary>0..1 towards the line snapping. What the bar on the HUD is.</summary>
        readonly SyncVar<float> _tension = new(new SyncTypeSettings(0.05f));

        /// <summary>1 at the hook, 0 when it is at your feet. The other half of the bar.</summary>
        readonly SyncVar<float> _line = new(new SyncTypeSettings(0.05f));

        Health _health;
        StunState _stun;
        RagdollController _ragdoll;

        // Server only. None of this is replicated: a client that knew the bite time would know when
        // to press, and a client that knew the struggle phase would never lose a fish.
        FishDef _fish;
        float _biteAt;
        float _windowEndsAt;
        float _distance;
        float _startDistance;
        float _fightSeconds;
        bool _reeling;

        // Owner only, so the reel is sent on the edge rather than every frame.
        bool _sentReel;

        bool _log;

        /// <summary>Raised on every peer when something is landed, with what and how many.</summary>
        public event Action<FishDef, int> Caught;

        /// <summary>Raised on every peer when the line comes back empty, with why.</summary>
        public event Action<string> Lost;

        /// <summary>Raised on every peer the moment something takes the bait. The cue to strike.</summary>
        public event Action Bit;

        public FishCatalog Catalog => _catalog != null ? _catalog : FishCatalog.Active;

        public FishingState State => _state.Value;

        /// <summary>True whenever the line is in the water in any form.</summary>
        public bool Busy => _state.Value != FishingState.Idle;

        public Vector3 Bobber => _bobber.Value;

        /// <summary>0..1 towards a snapped line. Zero unless something is hooked.</summary>
        public float Tension => _tension.Value;

        /// <summary>1 at the hook, 0 at your feet. Zero unless something is hooked.</summary>
        public float Line => _line.Value;

        /// <summary>What is on the line, on every peer, or null. Only ever set once it is hooked.</summary>
        public FishDef Hooked
        {
            get
            {
                FishCatalog catalog = Catalog;
                return catalog != null ? catalog.At(_hooked.Value) : null;
            }
        }

        /// <summary>True when the selected slot holds the rod. What makes Attack mean 'cast'.</summary>
        public bool HasRod
        {
            get
            {
                FishCatalog catalog = Catalog;
                ItemDef rod = catalog != null ? catalog.Rod : null;

                return rod != null && _inventory != null && _inventory.Selected.Def == rod;
            }
        }

        void Awake()
        {
            _health = GetComponent<Health>();
            _stun = GetComponent<StunState>();
            _ragdoll = GetComponent<RagdollController>();

            if (_inventory == null) _inventory = GetComponent<Inventory>();

            FishCatalog.Use(_catalog);

            _log = CommandLine.HasFlag("-fishLog");
        }

        // ------------------------------------------------------------------ owner side

        /// <summary>
        /// The Attack key, offered here before the weapon sees it. Returns whether fishing took the
        /// press - false means you are holding a machete and should punch somebody with it.
        /// </summary>
        public bool RequestAttack()
        {
            if (!IsOwner) return false;

            // Not holding the rod: only interesting if a cast is already out, in which case the press
            // reels the empty line in rather than punching with a rod-shaped hand.
            if (!HasRod && !Busy) return false;

            switch (_state.Value)
            {
                case FishingState.Idle:
                    ServerCastRpc(AimDirection());
                    return true;

                case FishingState.Waiting:
                    // Second press on a quiet float: pull it in. Recasting somewhere better is the
                    // whole of what a player does with a bite that never comes.
                    ServerGiveUpRpc();
                    return true;

                case FishingState.Biting:
                    ServerStrikeRpc();
                    return true;

                default:
                    // Fighting. The verb here is a hold, not a tap; swallow the press so it does not
                    // fall through and start a punch mid-fight.
                    return true;
            }
        }

        /// <summary>
        /// Whether the Attack key is down, from the input component. Sent on the edge only: the
        /// server runs the fight itself and a stream of 'still holding' packets tells it nothing.
        /// </summary>
        public void NotifyReel(bool held)
        {
            if (!IsOwner) return;

            if (_state.Value != FishingState.Fighting)
            {
                _sentReel = false;
                return;
            }

            if (held == _sentReel) return;

            _sentReel = held;
            ServerReelRpc(held);
        }

        Vector3 AimDirection()
        {
            Transform origin = _aimOrigin != null ? _aimOrigin : transform;
            return origin.forward;
        }

        // ------------------------------------------------------------------ the wire

        [ServerRpc]
        void ServerCastRpc(Vector3 direction)
        {
            if (!ServerTryCast(direction, out string why)) Refused(why);
        }

        [ServerRpc]
        void ServerStrikeRpc() => ServerStrike();

        [ServerRpc]
        void ServerReelRpc(bool held) => ServerReel(held);

        [ServerRpc]
        void ServerGiveUpRpc() => ServerGiveUp();

        [ObserversRpc(RunLocally = true)]
        void AnnounceCatch(ushort index, int count)
        {
            FishCatalog catalog = Catalog;
            FishDef def = catalog != null ? catalog.At(index) : null;

            Caught?.Invoke(def, count);

            if (_log && def != null)
                Debug.Log($"[Fishing] {name} landed {count}x {def.Id} "
                          + $"({(def.Catch != null ? def.Catch.Id : "nothing")}).");
        }

        [ObserversRpc(RunLocally = true)]
        void AnnounceLoss(string why)
        {
            Lost?.Invoke(why);

            if (_log) Debug.Log($"[Fishing] {name} lost it: {why}.");
        }

        [ObserversRpc(RunLocally = true)]
        void AnnounceBite()
        {
            Bit?.Invoke();

            if (_log) Debug.Log($"[Fishing] {name} has a bite.");
        }

        void Refused(string why)
        {
            if (_log) Debug.Log($"[Fishing] {name} cannot cast: {why}.");
        }

        // ------------------------------------------------------------------ server side

        /// <summary>
        /// Puts a float in the water in a direction. Separate from the RPC so the harness and any
        /// future NPC angler can call it. Returns false with a reason a player could act on.
        /// </summary>
        [Server]
        public bool ServerTryCast(Vector3 direction, out string why)
        {
            if (Busy)
            {
                why = "the line is already out";
                return false;
            }

            if (!Able(out why)) return false;

            if (!HasRod)
            {
                why = "no rod in hand";
                return false;
            }

            if (direction.sqrMagnitude < 0.001f)
            {
                why = "no direction";
                return false;
            }

            direction.Normalize();

            // The same check the weapon makes, for the same reason: the client picks the direction,
            // so the server has to refuse one that points somewhere the body is not facing.
            Vector3 facing = transform.forward;
            var flatAim = new Vector3(direction.x, 0f, direction.z);
            if (flatAim.sqrMagnitude > 0.001f
                && Vector3.Angle(flatAim.normalized, facing) > _maxAimDeviation)
            {
                why = "not facing that way";
                return false;
            }

            if (!ServerWaterHit(direction, out Vector3 point, out why)) return false;

            return ServerCastAt(point, forced: null, out why);
        }

        /// <summary>
        /// Puts a float on a known point of water, optionally with a decided catch. The harness uses
        /// <paramref name="forced"/> to fight one species at a time; nothing in the game does.
        /// </summary>
        [Server]
        public bool ServerCastAt(Vector3 point, FishDef forced, out string why)
        {
            FishCatalog catalog = Catalog;

            if (catalog == null || catalog.Count == 0)
            {
                why = "there is nothing in this sea";
                return false;
            }

            if (!Able(out why)) return false;

            // Decided now rather than at the landing, so the fight can already be the fight this
            // species has. See the class comment.
            _fish = forced != null ? forced : catalog.Roll(UnityEngine.Random.value);

            if (_fish == null)
            {
                why = "the table rolled nothing";
                return false;
            }

            Vector2 wait = _fish.BiteSeconds;

            _bobber.Value = new Vector3(point.x, WaterSurface.SeaLevel, point.z);
            _biteAt = Time.time + UnityEngine.Random.Range(wait.x, wait.y);
            _tension.Value = 0f;
            _line.Value = 1f;
            _hooked.Value = 0;
            _reeling = false;
            _state.Value = FishingState.Waiting;

            why = null;
            return true;
        }

        /// <summary>Strikes. Sets the hook inside the window, and loses the fish outside it.</summary>
        [Server]
        public bool ServerStrike()
        {
            if (_state.Value != FishingState.Biting || _fish == null) return false;

            _hooked.Value = Catalog != null ? Catalog.IndexOf(_fish) : (ushort)0;
            _startDistance = _fish.Distance;
            _distance = _startDistance;
            _fightSeconds = 0f;
            _reeling = false;
            _tension.Value = 0f;
            _line.Value = 1f;
            _state.Value = FishingState.Fighting;

            return true;
        }

        /// <summary>Reeling on or off. The whole of the player's input during a fight.</summary>
        [Server]
        public void ServerReel(bool on)
        {
            if (_state.Value != FishingState.Fighting) return;

            _reeling = on;
        }

        /// <summary>Winds the line back in with nothing on it. What a second press on a quiet float does.</summary>
        [Server]
        public void ServerGiveUp() => ServerEnd("reeled in", announce: _state.Value != FishingState.Waiting);

        void Update()
        {
            if (IsServerStarted) ServerTick();
            if (IsOwner) OwnerTick();
        }

        void OwnerTick()
        {
            // The fight ended between one input frame and the next. Clearing the edge here means the
            // first press of the *next* fight is seen as a change rather than swallowed as a repeat.
            if (_state.Value != FishingState.Fighting) _sentReel = false;
        }

        [Server]
        void ServerTick()
        {
            if (_state.Value == FishingState.Idle) return;

            // Anything that takes the controls away takes the rod with it, and so does walking off.
            if (!Able(out string why) || !HasRod)
            {
                ServerEnd(why ?? "the rod is put away", announce: true);
                return;
            }

            if ((transform.position - _bobber.Value).sqrMagnitude
                > _castRange * _leashFactor * (_castRange * _leashFactor))
            {
                ServerEnd("you walked off with it", announce: true);
                return;
            }

            switch (_state.Value)
            {
                case FishingState.Waiting:
                    if (Time.time < _biteAt) return;

                    _state.Value = FishingState.Biting;
                    _windowEndsAt = Time.time + _fish.HookSeconds;
                    AnnounceBite();
                    return;

                case FishingState.Biting:
                    if (Time.time < _windowEndsAt) return;

                    ServerEnd("it spat the hook", announce: true);
                    return;

                case FishingState.Fighting:
                    Fight(Time.deltaTime);
                    return;
            }
        }

        [Server]
        void Fight(float dt)
        {
            _fightSeconds += dt;

            bool running = _fish.Running(_fightSeconds);

            if (_reeling)
            {
                _distance -= _fish.ReelSpeed * dt;
                _tension.Value += (running ? _fish.RunPull : _fish.CalmPull) * dt;
            }
            else
            {
                _distance += _fish.SlipSpeed * dt;
                _tension.Value = Mathf.Max(0f, _tension.Value - _relaxPerSecond * dt);
            }

            _line.Value = Mathf.Clamp01(_distance / _startDistance);

            if (_tension.Value >= 1f)
            {
                ServerEnd("the line snapped", announce: true);
                return;
            }

            if (_distance >= _startDistance * _spoolFactor)
            {
                ServerEnd("it took all your line", announce: true);
                return;
            }

            if (_distance <= 0f) ServerLand();
        }

        [Server]
        void ServerLand()
        {
            FishDef fish = _fish;
            int count = UnityEngine.Random.Range(fish.Low, fish.High + 1);

            ushort index = Catalog != null ? Catalog.IndexOf(fish) : (ushort)0;

            ServerEnd(null, announce: false);

            if (fish.Catch != null && count > 0)
            {
                // Straight into the bag, unlike a kill in #53. The difference is the point: hunting
                // leaves a carcass four people can argue over, and fishing is the quiet thing you do
                // alone. What does not fit goes on the ground at your feet rather than evaporating.
                int taken = _inventory != null ? _inventory.Add(fish.Catch, count) : 0;
                int spare = count - taken;

                if (spare > 0)
                {
                    ItemCatalog items = _inventory != null ? _inventory.Catalog : ItemCatalog.Active;
                    ushort item = items != null ? items.IndexOf(fish.Catch) : (ushort)0;

                    if (item != 0)
                        WorldItemSpawner.Drop(new ItemStack(item, spare),
                                              transform.position + transform.forward * 0.6f
                                              + Vector3.up * 0.4f,
                                              Quaternion.identity);
                }
            }

            AnnounceCatch(index, count);
        }

        /// <summary>Puts everything back to idle. The one exit from every state.</summary>
        [Server]
        void ServerEnd(string why, bool announce)
        {
            _fish = null;
            _reeling = false;
            _distance = 0f;
            _startDistance = 0f;
            _fightSeconds = 0f;

            _state.Value = FishingState.Idle;
            _hooked.Value = 0;
            _tension.Value = 0f;
            _line.Value = 0f;

            if (announce && why != null) AnnounceLoss(why);
        }

        /// <summary>Upright, conscious and in control. The same list a use has to pass in #43.</summary>
        bool Able(out string why)
        {
            if (_health != null && _health.IsIncapacitated)
            {
                why = "you are on the floor";
                return false;
            }

            if (_stun != null && _stun.IsStunned)
            {
                why = "you are stunned";
                return false;
            }

            if (_ragdoll != null && _ragdoll.IsRagdolled)
            {
                why = "you are a ragdoll";
                return false;
            }

            why = null;
            return true;
        }

        /// <summary>
        /// Where an aim direction meets the sea, and whether that is a place a bobber may land.
        ///
        /// Three refusals rather than one, because they are three different mistakes and the player
        /// fixes them three different ways: aim lower, walk closer, or find deeper water.
        /// </summary>
        [Server]
        public bool ServerWaterHit(Vector3 direction, out Vector3 point, out string why)
        {
            point = default;

            Transform origin = _aimOrigin != null ? _aimOrigin : transform;
            Vector3 from = origin.position;

            float drop = from.y - WaterSurface.SeaLevel;

            if (drop <= 0.1f)
            {
                why = "you are already in it";
                return false;
            }

            if (direction.y >= -0.02f)
            {
                why = "aim at the water";
                return false;
            }

            float t = drop / -direction.y;
            if (t > _castRange)
            {
                why = "too far - aim steeper or walk in";
                return false;
            }

            point = from + direction * t;
            point.y = WaterSurface.SeaLevel;

            // Anything solid between the eye and the float means you are looking at the sea past a
            // rock, which is a view rather than a cast. The caster's own body does not count.
            RaycastHit[] blocked = Physics.RaycastAll(from, direction, t, _blockMask,
                                                      QueryTriggerInteraction.Ignore);
            foreach (RaycastHit hit in blocked)
            {
                if (hit.transform == null || hit.transform.IsChildOf(transform)) continue;

                why = "something is in the way";
                return false;
            }

            // No water collider in this game - the sea is a mesh and a function - so depth is asked
            // of the ground instead: a ray straight down that hits anything shallow means seabed.
            if (Physics.Raycast(point + Vector3.up * 0.5f, Vector3.down, _minDepth + 0.5f,
                                _blockMask, QueryTriggerInteraction.Ignore))
            {
                why = "too shallow";
                return false;
            }

            why = null;
            return true;
        }

        /// <summary>One line for the log and the HUD.</summary>
        public string Describe()
        {
            switch (_state.Value)
            {
                case FishingState.Idle: return HasRod ? "rod ready" : "idle";
                case FishingState.Waiting: return "waiting";
                case FishingState.Biting: return "STRIKE";
                default:
                    FishDef fish = Hooked;
                    return $"{(fish != null ? fish.DisplayName : "something")} "
                           + $"- line {Line * 100f:F0}%, tension {Tension * 100f:F0}%";
            }
        }

        /// <summary>Bake time only.</summary>
        public void Configure(FishCatalog catalog, Inventory inventory, Transform aimOrigin)
        {
            _catalog = catalog;
            _inventory = inventory;
            _aimOrigin = aimOrigin;
        }
    }
}
