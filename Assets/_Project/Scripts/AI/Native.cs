using System.Collections.Generic;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.World;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.AI
{
    /// <summary>What a native is doing. Server-side; a client only ever sees the transform move.</summary>
    public enum NativeState
    {
        Idle,
        Patrol,
        Investigate,
        Chase,
        Attack,

        /// <summary>Carrying a downed player home. See <see cref="Native.TickAbduct"/>.</summary>
        Abduct,

        Flee,
        Dead,
    }

    /// <summary>
    /// One native: the island's other people, and the first thing here that hunts you back.
    ///
    /// Structurally this is <see cref="Animal"/>'s cousin - server-authoritative FSM, one prefab
    /// wearing N roles from a <see cref="NativeDef"/>, a role index in a SyncVar so a client can
    /// build the right body straight off the spawn message. Everything below that line is different,
    /// because #55's acceptance is a sentence an animal could never satisfy: **a real threat at
    /// night, and not unfair in daylight.**
    ///
    /// Three rules carry that, and all three are the same rule seen from different angles - *in
    /// daylight the player has information, and at night they do not*:
    ///
    /// 1. **By day they have to see you.** <see cref="Sense"/> checks a vision cone and then a
    ///    physical line of sight, so a hill, a hut or a rock is cover, and walking round the long way
    ///    is a real answer. At night the cone and the raycast are dropped and the radius grows: they
    ///    hear you, and the only cover is distance.
    /// 2. **By day the leash is short.** A daylight chase ends when you are forty metres off their
    ///    ground; at night they will follow you a hundred and thirty, which is most of the way home.
    /// 3. **Nothing here outruns a sprint.** 6.6 m/s against the player's 7.5: running away always
    ///    works, and costs you the stamina and whatever you were in the middle of doing.
    ///
    /// The thing that makes a camp frightening rather than a queue of individuals is
    /// <see cref="Alarm"/>. A native that notices you shouts, and everyone inside the shout goes to
    /// look at where you were. One scout on a ridge is therefore not one fight - which is exactly
    /// what a night raid on a village should feel like, and exactly why the daylight version of the
    /// same shout is survivable: they arrive at a place you have already left.
    ///
    /// The blend between the two worlds is <see cref="WorldClock.Night01"/>, which is a function of
    /// the sun's actual height, so dusk is a slope rather than a switch.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class Native : NetworkBehaviour, ICarryHolder
    {
        [Header("Data")]
        [Tooltip("Every role. The prefab holds it so the index in the SyncVar can be resolved.")]
        [SerializeField] NativeCatalog _catalog;

        [Tooltip("Used when something spawns this prefab without saying what it is. Debug convenience.")]
        [SerializeField] NativeDef _fallback;

        [Header("Body")]
        [Tooltip("The visible box. Scaled from the role at spawn on every peer.")]
        [SerializeField] Transform _body;

        [Tooltip("A smaller block on top, painted with the role's warpaint. Also where a dart comes from.")]
        [SerializeField] Transform _head;

        [SerializeField] CapsuleCollider _collider;

        [Tooltip("Where an abducted body's hips are parented. Over the shoulder, roughly.")]
        [SerializeField] Transform _carrySocket;

        [Header("Behaviour")]
        [Tooltip("Seconds between sweeps for players. Cheap, and still instant to a human.")]
        [SerializeField] float _senseInterval = 0.3f;

        [Tooltip("Seconds the body stays before it despawns. The loot has already dropped by then.")]
        [SerializeField] float _corpseSeconds = 10f;

        [Tooltip("What a line of sight is blocked by. Terrain and buildings; not other natives.")]
        [SerializeField] LayerMask _sightMask = ~0;

        /// <summary>Which role this is. Set before the spawn, so it arrives with the object.</summary>
        readonly SyncVar<ushort> _role = new();

        NavMeshAgent _agent;
        Health _health;
        NativeDef _def;

        NativeState _state = NativeState.Idle;
        Vector3 _camp;
        float _stateUntil;
        float _nextSense;
        float _nextAttack;
        float _swingAt;
        float _forgetAt;
        float _despawnAt;
        float _lastHealth;

        Health _target;

        /// <summary>The body being dragged home, or null. Server-only; the claim lives here.</summary>
        Carryable _haul;

        /// <summary>The abductee's health, kept so the haul can end when they are helped up.</summary>
        Health _hauled;

        /// <summary>
        /// The abductee's hips. A limp body is not where its root transform says it is, and walking
        /// to the root means walking to where they were standing when they went down.
        /// </summary>
        Transform _hips;

        /// <summary>Where the body is being taken. The camp, until #108 puts hooks in it.</summary>
        Vector3 _delivery;

        /// <summary>Where the target was last actually perceived. What Investigate walks to.</summary>
        Vector3 _suspect;
        bool _hasSuspect;

        /// <summary>Every native alive on this peer. The spawner counts it; the harness reads it.</summary>
        static readonly List<Native> _live = new();

        public static IReadOnlyList<Native> Live => _live;

        /// <summary>What this is. Null until the role index has arrived.</summary>
        public NativeDef Def => _def;

        public NativeState State => _state;

        /// <summary>Who it is hunting. Null when it has not noticed anybody.</summary>
        public Health Target => _target;

        /// <summary>Where it belongs. Leashes measure from here, and a flee runs to it.</summary>
        public Vector3 Camp => _camp;

        /// <summary>Where it thinks you are. Only meaningful while it has lost sight of you.</summary>
        public Vector3 Suspect => _suspect;

        public bool HasSuspect => _hasSuspect;

        /// <inheritdoc />
        public Transform CarrySocket => _carrySocket;

        /// <summary>The body this one has claimed, or null. Server-side truth.</summary>
        public Carryable Haul => _haul;

        /// <summary>Whether the body is actually on its shoulder rather than merely claimed.</summary>
        public bool IsHauling => _haul != null && _haul.IsCarried && _haul.Carrier == NetworkObject;

        /// <summary>Raised on the server for every blow that lands, with the victim and the damage.</summary>
        public event System.Action<Native, Health, float> Struck;

        /// <summary>Raised on the server the moment a native goes from unaware to hunting.</summary>
        public event System.Action<Native, Health> Noticed;

        /// <summary>Raised on the server after the loot is on the ground.</summary>
        public event System.Action<Native, IReadOnlyList<ItemStack>> Looted;

        void Awake()
        {
            _health = GetComponent<Health>();
            _agent = GetComponent<NavMeshAgent>();

            NativeCatalog.Use(_catalog);
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            _live.Add(this);

            _role.OnChange += OnRoleChanged;
            Resolve(_role.Value);
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();

            _live.Remove(this);
            _role.OnChange -= OnRoleChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            if (_camp == Vector3.zero) _camp = transform.position;

            _lastHealth = _health.Current;
            _health.ServerStateChanged += OnServerStateChanged;
            _health.Changed += OnHealthChanged;

            // Idempotent, and armed from here rather than from a bootstrap because an island with
            // nobody on it to drag you anywhere does not need to be listening for bodies.
            AbductionWatch.Arm();

            EnterIdle();
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            _health.ServerStateChanged -= OnServerStateChanged;
            _health.Changed -= OnHealthChanged;

            // A despawn mid-haul must not leave a body welded to an object that no longer exists.
            Release();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Same reason as the animals: on a pure client the transform arrives over the wire, and
            // an agent left enabled would fight it for ownership of the same transform.
            if (!IsServerStarted && _agent != null) _agent.enabled = false;
        }

        /// <summary>
        /// Server only, and **before** <c>ServerManager.Spawn</c>. Says what this body is and which
        /// camp it belongs to - the two things a shared prefab cannot know about itself.
        /// </summary>
        public void ServerConfigure(NativeDef def, Vector3 camp)
        {
            if (def == null) return;

            _def = def;
            _camp = camp;

            NativeCatalog catalog = _catalog != null ? _catalog : NativeCatalog.Active;
            if (catalog != null) _role.Value = catalog.IndexOf(def);

            _health.ServerConfigure(def.MaxHealth, canBeDowned: false);
            _lastHealth = def.MaxHealth;

            ApplyShape(def);
            ApplyAgent(def);

            name = def.DisplayName;
        }

        void OnRoleChanged(ushort previous, ushort next, bool asServer) => Resolve(next);

        void Resolve(ushort index)
        {
            NativeCatalog catalog = _catalog != null ? _catalog : NativeCatalog.Active;
            NativeDef def = catalog != null ? catalog.At(index) : null;

            if (def == null) def = _fallback;
            if (def == null || def == _def) return;

            _def = def;
            ApplyShape(def);
            if (IsServerStarted) ApplyAgent(def);
        }

        // ---------------------------------------------------------------- shape

        void ApplyShape(NativeDef def)
        {
            Vector3 size = def.BodySize;

            if (_body != null)
            {
                _body.localScale = new Vector3(size.x, size.y * 0.75f, size.z);
                _body.localPosition = new Vector3(0f, size.y * 0.375f, 0f);
                Paint(_body, def.Colour);
            }

            if (_head != null)
            {
                float head = size.x * 0.8f;
                _head.localScale = new Vector3(head, head, head);
                _head.localPosition = new Vector3(0f, size.y * 0.85f, 0f);

                // The warpaint, and the only thing that tells a spearman from a blowgunner at forty
                // metres in fog. A role you cannot identify is a role you cannot plan against.
                Paint(_head, def.MarkColour);
            }

            if (_collider != null)
            {
                _collider.radius = Mathf.Max(0.1f, Mathf.Min(size.x, size.z) * 0.5f);
                _collider.height = Mathf.Max(_collider.radius * 2f, size.y);
                _collider.center = new Vector3(0f, size.y * 0.5f, 0f);
            }
        }

        static void Paint(Transform part, Color colour)
        {
            var renderer = part.GetComponent<Renderer>();
            if (renderer == null) return;

            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            block.SetColor("_BaseColor", colour);
            block.SetColor("_Color", colour);
            renderer.SetPropertyBlock(block);
        }

        void ApplyAgent(NativeDef def)
        {
            if (_agent == null) return;

            _agent.radius = def.AgentRadius;
            _agent.height = Mathf.Max(0.2f, def.BodyHeight);
            _agent.speed = def.WalkSpeed;
            _agent.acceleration = Mathf.Max(8f, def.RunSpeed * 3f);
            _agent.angularSpeed = 720f;
            _agent.stoppingDistance = 0.4f;
        }

        // ---------------------------------------------------------------- the machine

        void Update()
        {
            if (!IsServerStarted || _def == null) return;

            if (_state == NativeState.Dead)
            {
                if (Time.time >= _despawnAt && NetworkObject != null && NetworkObject.IsSpawned)
                    ServerManager.Despawn(NetworkObject);

                return;
            }

            if (Time.time >= _nextSense)
            {
                _nextSense = Time.time + _senseInterval;
                Sense();
            }

            switch (_state)
            {
                case NativeState.Idle: TickIdle(); break;
                case NativeState.Patrol: TickPatrol(); break;
                case NativeState.Investigate: TickInvestigate(); break;
                case NativeState.Chase: TickChase(); break;
                case NativeState.Attack: TickAttack(); break;
                case NativeState.Abduct: TickAbduct(); break;
                case NativeState.Flee: TickFlee(); break;
            }
        }

        /// <summary>How dark it is where this native is standing. 0 by day, 1 at midnight.</summary>
        public static float Night => WorldClock.Night01;

        /// <summary>
        /// Looks for somebody to be angry about.
        ///
        /// The whole day/night contract lives in this method. In daylight a player has to be inside
        /// the notice radius, inside the vision cone, and on the end of an unblocked ray - three
        /// conditions, any of which the player can break on purpose. At night only the radius is
        /// left. <see cref="NativeDef.Earshot"/> sits underneath both: walk into somebody and it
        /// does not matter what the sun is doing.
        /// </summary>
        void Sense()
        {
            // Fleeing has stopped caring and a haul has already decided. A native with somebody over
            // its shoulder that still went looking for a second victim would drop the first one every
            // time somebody walked past, which is a mechanic nobody can read.
            if (_state == NativeState.Flee || _state == NativeState.Abduct) return;

            float night = Night;
            float notice = _def.NoticeRadius(night);
            bool needsSight = _def.NeedsSight(night);

            Health best = null;
            float bestDistance = float.MaxValue;

            foreach (NetworkPlayerRegistry.PlayerBody body in NetworkPlayerRegistry.Players)
            {
                if (!body.IsValid) continue;

                var health = body.Object.GetComponent<Health>();
                if (health == null || !health.IsAlive) continue;

                float distance = Vector3.Distance(transform.position, body.Object.transform.position);
                if (distance >= bestDistance) continue;

                bool close = distance <= _def.Earshot;
                if (!close)
                {
                    if (distance > notice) continue;
                    if (needsSight && !CanSee(health)) continue;
                }

                best = health;
                bestDistance = distance;
            }

            if (best == null)
            {
                // Lost them. The memory is what stops a native from turning round mid-stride the
                // instant a rock passes between you: it keeps hunting the last place it had you,
                // which is both better behaviour and better for the player, because that place is a
                // spot they can deliberately leave.
                if (_target != null && Time.time >= _forgetAt) Give();
                return;
            }

            bool fresh = _target != best;

            _target = best;
            _suspect = best.transform.position;
            _hasSuspect = true;
            _forgetAt = Time.time + _def.MemorySeconds;

            if (fresh)
            {
                Noticed?.Invoke(this, best);
                Alarm(this, best, _suspect);

                if (CommandLine.HasFlag("-nativeLog"))
                    Debug.Log($"[Native] {_def.Id} {ObjectId} noticed {best.ObjectId} at "
                              + $"{bestDistance:F1}m (notice {notice:F0}m, night {night:F2}, "
                              + $"sight {(needsSight ? "required" : "not needed")}).");
            }

            if (Leashed()) return;

            if (_state != NativeState.Chase && _state != NativeState.Attack) EnterChase();
        }

        /// <summary>
        /// Cone first, then the ray, because the cone is two dot products and the ray is a physics
        /// query - and at four players and a dozen natives that ordering is the difference between a
        /// sense sweep costing nothing and costing a frame.
        /// </summary>
        bool CanSee(Health who)
        {
            Vector3 eye = Eye();
            Vector3 mark = who.transform.position + Vector3.up * 1.2f;
            Vector3 to = mark - eye;

            if (Vector3.Angle(Facing(), to) > _def.VisionAngle) return false;

            if (!Physics.Raycast(eye, to.normalized, out RaycastHit hit, to.magnitude,
                                 _sightMask, QueryTriggerInteraction.Ignore))
                return true;

            // Something is in the way, unless the thing in the way is them.
            return hit.collider.GetComponentInParent<Health>() == who;
        }

        Vector3 Eye() => transform.position + Vector3.up * (_def.BodyHeight * 0.9f);

        Vector3 Facing()
        {
            Vector3 forward = transform.forward;
            forward.y = 0f;
            return forward.sqrMagnitude < 0.001f ? Vector3.forward : forward.normalized;
        }

        /// <summary>
        /// The shout. Every native inside the radius that is not already busy goes to look at where
        /// the player was - not at where the player *is*, which is the difference between a camp
        /// that reacts and a camp that cheats.
        /// </summary>
        static void Alarm(Native caller, Health about, Vector3 where)
        {
            if (caller == null || caller.Def == null) return;

            float radius = caller.Def.AlarmRadius;
            if (radius <= 0f) return;

            int woken = 0;

            foreach (Native other in _live)
            {
                if (other == caller || other == null || other._def == null) continue;
                if (!other.IsServerStarted || other._state == NativeState.Dead) continue;
                if (other._state == NativeState.Chase || other._state == NativeState.Attack) continue;

                // Already carrying somebody. The shout is a call for help, not a change of orders.
                if (other._state == NativeState.Abduct) continue;
                if (Vector3.Distance(other.transform.position, caller.transform.position) > radius) continue;

                other._target = about;
                other._suspect = where;
                other._hasSuspect = true;
                other._forgetAt = Time.time + other._def.MemorySeconds;
                other.EnterInvestigate();
                woken++;
            }

            if (woken > 0 && CommandLine.HasFlag("-nativeLog"))
                Debug.Log($"[Native] {caller.Def.Id} {caller.ObjectId} shouted; {woken} came to look.");
        }

        /// <summary>
        /// Beyond the leash, they stop. Measured from the camp rather than from the spawn point, so a
        /// native that chased you a hundred metres yesterday still guards the same ground today.
        /// </summary>
        bool Leashed()
        {
            float leash = _def.LeashRange(Night);
            if (Vector3.Distance(transform.position, _camp) <= leash) return false;

            Give();
            EnterPatrol();
            return true;
        }

        void Give()
        {
            _target = null;
            _hasSuspect = false;

            if (_state == NativeState.Chase || _state == NativeState.Attack) EnterInvestigate();
        }

        void TickIdle()
        {
            if (Time.time >= _stateUntil) EnterPatrol();
        }

        void TickPatrol()
        {
            if (Arrived() || Time.time >= _stateUntil) EnterIdle();
        }

        void TickInvestigate()
        {
            if (_target != null && !Leashed())
            {
                EnterChase();
                return;
            }

            if (Time.time >= _stateUntil) EnterPatrol();
        }

        void TickChase()
        {
            if (_target == null || !_target.IsAlive)
            {
                Give();
                return;
            }

            if (Leashed()) return;

            float distance = Vector3.Distance(transform.position, _target.transform.position);

            if (InRange(distance) && (!_def.IsRanged || CanSee(_target)))
            {
                EnterAttack();
                return;
            }

            Go(_target.transform.position, _def.RunSpeed);
        }

        /// <summary>Reach measured centre to centre, which is what the distance check has.</summary>
        float Reach() => _def.IsRanged ? _def.AttackRange : _def.AttackRange + _def.BodySize.z * 0.5f;

        bool InRange(float distance) => distance <= Reach();

        void TickAttack()
        {
            if (_target == null || !_target.IsAlive)
            {
                Give();
                return;
            }

            Vector3 toTarget = _target.transform.position - transform.position;
            float distance = toTarget.magnitude;

            if (distance > Reach() * 1.15f || (_def.IsRanged && !CanSee(_target)))
            {
                EnterChase();
                return;
            }

            Face(toTarget);

            // A blowgunner backs off rather than being shoved into a melee it cannot win. This is the
            // one place a native retreats while still fighting, and it is what makes the role read as
            // a different opponent rather than as a spearman with a longer arm.
            if (_def.IsRanged && distance < _def.Standoff * 0.6f)
            {
                Vector3 away = transform.position - _target.transform.position;
                away.y = 0f;
                Go(transform.position + away.normalized * _def.Standoff, _def.RunSpeed);
            }
            else
            {
                Stop();
            }

            if (_swingAt > 0f)
            {
                if (Time.time < _swingAt) return;

                _swingAt = 0f;
                Strike(_target, toTarget);
                return;
            }

            if (Time.time < _nextAttack) return;

            // The wind-up. Half a second of standing still and facing you before anything lands,
            // because a hit with no tell is a hit you can only lose to.
            _nextAttack = Time.time + _def.AttackInterval;
            _swingAt = Time.time + _def.WindupSeconds;
        }

        void TickFlee()
        {
            if (Time.time >= _stateUntil || Vector3.Distance(transform.position, _camp) <= 4f)
            {
                EnterIdle();
                return;
            }

            if (Arrived()) Go(_camp, _def.RunSpeed);
        }

        // ---------------------------------------------------------------- abduction

        /// <summary>Metres from the body it has to be before it can get a hand under it.</summary>
        const float GrabRange = 2f;

        /// <summary>Metres from the delivery point that count as arrived. The agent stops short.</summary>
        const float DeliveryRange = 2.5f;

        /// <summary>
        /// Seconds a haul is allowed to take before the body is dumped where it stands. A door that
        /// never opens is worse than a body on the floor: without this, one unreachable delivery
        /// point means somebody spends the rest of the run on a shoulder.
        /// </summary>
        const float HaulSeconds = 90f;

        /// <summary>
        /// Server only. Somebody just went down. Offers the body to the nearest native that is in the
        /// business of taking it, and returns whoever claimed it - or null, which is the ordinary
        /// answer and means nothing was close enough.
        ///
        /// The claim is exclusive by construction rather than by a registry: a native holds exactly
        /// one <see cref="Haul"/>, and the sweep skips a body somebody already holds. Two natives
        /// arriving at the same body is a thing that should look like a scuffle later; two natives
        /// each believing they are carrying it is a bug now.
        /// </summary>
        public static Native ServerOffer(Carryable body, Health victim)
        {
            if (body == null || victim == null) return null;
            if (Claimed(body)) return null;

            Native best = null;
            float bestDistance = float.MaxValue;

            foreach (Native native in _live)
            {
                if (native == null || native._def == null || !native.IsServerStarted) continue;
                if (!native._def.Abducts || native._haul != null) continue;
                if (native._state == NativeState.Dead || native._state == NativeState.Flee) continue;

                float distance = Vector3.Distance(native.transform.position, body.transform.position);
                if (distance > native._def.AbductRadius || distance >= bestDistance) continue;

                best = native;
                bestDistance = distance;
            }

            if (best == null) return null;

            best.EnterAbduct(body, victim);

            Debug.Log($"[Native] {best._def.Id} {best.ObjectId} claimed {victim.ObjectId} "
                      + $"{bestDistance:F1}m away; hauling to {best._delivery}.");

            return best;
        }

        /// <summary>Whether somebody already has a hand on this body. Cheap: there are never many.</summary>
        static bool Claimed(Carryable body)
        {
            foreach (Native native in _live)
                if (native != null && native._haul == body) return true;

            return false;
        }

        void EnterAbduct(Carryable body, Health victim)
        {
            _state = NativeState.Abduct;
            _haul = body;
            _hauled = victim;
            _swingAt = 0f;
            _target = null;
            _hasSuspect = false;
            _stateUntil = Time.time + HaulSeconds;

            var ragdoll = body.GetComponent<RagdollController>();
            _hips = ragdoll != null ? ragdoll.HipBone : body.transform;

            // The camp, until #108 puts something in it to hang them on.
            _delivery = _camp;
        }

        /// <summary>
        /// Fetch, then carry, then let go. Both halves are in one state because from the player's
        /// side they are one event - *somebody is taking your friend away* - and splitting them would
        /// mean two states that drop the body for the same four reasons.
        ///
        /// Those four reasons are the counter-play, and every one of them is something the other
        /// three players can cause: kill the carrier, hurt it enough to break it, get to the body
        /// first, or pick your friend up off the floor before it arrives.
        /// </summary>
        void TickAbduct()
        {
            if (_haul == null || _hauled == null)
            {
                Release();
                EnterPatrol();
                return;
            }

            // Helped up, and no longer a parcel. Whoever did that is now standing next to a native
            // that is about to be very interested in them, which is the intended reward.
            if (_hauled.IsAlive)
            {
                Release();
                EnterChase();
                return;
            }

            if (Time.time >= _stateUntil)
            {
                Debug.LogWarning($"[Native] {_def.Id} {ObjectId} gave up hauling {_hauled.ObjectId} "
                                 + $"after {HaulSeconds:0}s; dropped at {transform.position}.");

                Release();
                EnterPatrol();
                return;
            }

            if (!IsHauling)
            {
                // Somebody else got a hand on it first - another native, or a player carrying their
                // friend out of trouble. Either way this one has lost the argument.
                if (_haul.IsCarried)
                {
                    Release();
                    EnterPatrol();
                    return;
                }

                Vector3 where = _hips != null ? _hips.position : _haul.transform.position;

                if (Vector3.Distance(transform.position, where) <= GrabRange) Grab();
                else Go(where, _def.RunSpeed);

                return;
            }

            if (Vector3.Distance(transform.position, _delivery) <= DeliveryRange)
            {
                Deliver();
                return;
            }

            // Slow on purpose. The haul is the window the other three players get, and a kidnapper
            // that moved at a run would close it before anybody had finished shouting about it.
            if (!Go(_delivery, _def.HaulSpeed))
            {
                Release();
                EnterPatrol();
            }
        }

        void Grab()
        {
            if (!_haul.ServerCanBeCarriedBy(NetworkObject)) return;

            _haul.ServerAttach(NetworkObject);

            if (!IsHauling) return;

            Debug.Log($"[Native] {_def.Id} {ObjectId} picked up {_hauled.ObjectId} at "
                      + $"{transform.position}; {Vector3.Distance(transform.position, _delivery):F0}m to go.");
        }

        void Deliver()
        {
            Health victim = _hauled;

            Debug.Log($"[Native] {_def.Id} {ObjectId} delivered {(victim != null ? victim.ObjectId : 0)} "
                      + $"to {transform.position}; {(victim != null ? victim.BleedOutRemaining : 0f):F0}s "
                      + "of bleed-out left.");

            Release();
            Stop();
            EnterIdle();
        }

        /// <summary>
        /// Puts the body down wherever this native happens to be and forgets about it. The one path
        /// out of a haul: death, flee, rescue, timeout and despawn all come through here, so there is
        /// exactly one place that can leave a body attached to nothing.
        /// </summary>
        void Release()
        {
            if (_haul != null && _haul.Carrier == NetworkObject) _haul.ServerDetach();

            _haul = null;
            _hauled = null;
            _hips = null;
        }

        // ---------------------------------------------------------------- violence

        void Strike(Health victim, Vector3 toTarget)
        {
            if (victim == null || !victim.IsAlive) return;

            Vector3 direction = toTarget.sqrMagnitude > 0.001f ? toTarget.normalized : transform.forward;

            if (_def.IsRanged)
            {
                Dart(victim, direction);
                return;
            }

            Vector3 contact = transform.position + direction * (_def.BodySize.z * 0.5f)
                              + Vector3.up * (_def.BodyHeight * 0.6f);

            Land(victim, direction, contact, DamageType.Blunt);
        }

        /// <summary>
        /// A dart, resolved as a ray with a spread cone. Hitscan rather than a travelling projectile
        /// for the same reason <see cref="Weapon"/> is: the server decides on the frame it fires, so
        /// there is no in-flight object for a laggy client to disagree about. The spread is what
        /// makes it miss - a blowgun that always hits is a sniper with a straw.
        /// </summary>
        void Dart(Health victim, Vector3 direction)
        {
            Vector3 origin = Eye();
            Vector3 aim = (victim.transform.position + Vector3.up * 1.1f) - origin;

            // The cone is built in the aim's own frame rather than by rotating the direction with a
            // world-space Euler, which is the same spread at every compass heading instead of a
            // spread that collapses whenever the shot happens to run along the axis it pitches about.
            float spread = _def.SpreadDegrees;
            Vector3 line = aim.normalized;
            Vector3 shot = spread <= 0f
                ? line
                : Quaternion.LookRotation(line)
                  * (Quaternion.Euler(Random.Range(-spread, spread), Random.Range(-spread, spread), 0f)
                     * Vector3.forward);

            Vector3 end = origin + shot * _def.AttackRange;

            bool hit = Physics.Raycast(origin, shot, out RaycastHit info, _def.AttackRange,
                                       _sightMask, QueryTriggerInteraction.Ignore);

            Health struck = hit ? info.collider.GetComponentInParent<Health>() : null;
            if (hit) end = info.point;

            RpcDart(origin, end);

            if (struck == null || struck == _health)
            {
                if (CommandLine.HasFlag("-nativeLog"))
                    Debug.Log($"[Native] {_def.Id} {ObjectId} missed with a dart.");

                return;
            }

            Land(struck, shot, end, DamageType.Blunt);
        }

        /// <summary>
        /// One blow: damage, then the stun if the blow did not already put them down. Same shape as
        /// <c>Weapon.ApplyHit</c> and <c>Animal.Strike</c>, because a hit is a hit whoever threw it.
        /// </summary>
        void Land(Health victim, Vector3 direction, Vector3 contact, DamageType type)
        {
            var info = new DamageInfo(_def.AttackDamage, type, direction * _def.AttackKnockback,
                                      contact, _def.AttackStun, ObjectId);

            bool wasStanding = victim.IsAlive;
            victim.TakeDamage(info);

            bool knockedDown = wasStanding && victim.IsIncapacitated;
            if (!knockedDown)
            {
                var stun = victim.GetComponent<StunState>();
                if (stun != null) stun.ServerStun(info);
            }

            Struck?.Invoke(this, victim, _def.AttackDamage);

            if (CommandLine.HasFlag("-nativeLog"))
                Debug.Log($"[Native] {_def.Id} {ObjectId} hit {victim.ObjectId} for {_def.AttackDamage}, "
                          + $"victim now {victim.Current:F0} hp.");
        }

        /// <summary>The dart's line, for anybody with a screen. Cosmetic and already resolved.</summary>
        [ObserversRpc(RunLocally = true)]
        void RpcDart(Vector3 from, Vector3 to) => DartFired?.Invoke(from, to);

        /// <summary>Raised on every peer when a dart has been fired. Purely for drawing it.</summary>
        public event System.Action<Vector3, Vector3> DartFired;

        /// <summary>
        /// Hit back at whoever hit you, whatever the sun is doing. A native you can shoot in the back
        /// at noon without it ever turning round would be scenery, and "fair in daylight" is about
        /// the player having the information, not about the natives being harmless.
        /// </summary>
        void OnHealthChanged(float previous, float current)
        {
            if (!IsServerStarted || _def == null || _state == NativeState.Dead) return;

            float before = _lastHealth;
            _lastHealth = current;

            if (current >= before) return;

            if (current / Mathf.Max(1f, _health.Max) <= _def.FleeHealth)
            {
                EnterFlee();
                return;
            }

            // Committed. Shooting a kidnapper in the back does not make it turn round - it makes it
            // keep walking towards the thing you do not want it to reach, and the way to stop it is
            // to put it down or break it, both of which drop the body.
            if (_state == NativeState.Abduct) return;

            Health attacker = FindPlayer(_health.LastAttackerId);
            if (attacker == null || !attacker.IsAlive) return;

            _target = attacker;
            _suspect = attacker.transform.position;
            _hasSuspect = true;
            _forgetAt = Time.time + _def.MemorySeconds;

            Alarm(this, attacker, _suspect);

            if (_state != NativeState.Chase && _state != NativeState.Attack) EnterChase();
        }

        static Health FindPlayer(int objectId)
        {
            if (objectId == 0) return null;

            foreach (NetworkPlayerRegistry.PlayerBody body in NetworkPlayerRegistry.Players)
            {
                if (!body.IsValid || body.Object.ObjectId != objectId) continue;
                return body.Object.GetComponent<Health>();
            }

            return null;
        }

        // ---------------------------------------------------------------- transitions

        void EnterIdle()
        {
            _state = NativeState.Idle;
            _stateUntil = Time.time + Random.Range(_def.IdleMin, _def.IdleMax);
            _swingAt = 0f;
            Stop();
        }

        void EnterPatrol()
        {
            _state = NativeState.Patrol;
            _stateUntil = Time.time + 25f;
            _swingAt = 0f;

            // Night patrols range wider, which is the ambient half of the threat: at noon they are a
            // ring around a camp you can walk past, and at midnight they are somewhere on the path.
            float radius = _def.PatrolRadius * Mathf.Lerp(1f, 2.2f, Night);
            Vector2 offset = Random.insideUnitCircle * radius;
            Vector3 wanted = _camp + new Vector3(offset.x, 0f, offset.y);

            if (!Go(wanted, _def.WalkSpeed)) EnterIdle();
        }

        void EnterInvestigate()
        {
            _state = NativeState.Investigate;
            _stateUntil = Time.time + _def.InvestigateSeconds;
            _swingAt = 0f;

            Vector3 wanted = _hasSuspect ? _suspect : transform.position;
            if (!Go(wanted, _def.RunSpeed)) EnterPatrol();
        }

        void EnterChase()
        {
            _state = NativeState.Chase;
            _swingAt = 0f;

            if (_agent != null) _agent.speed = _def.RunSpeed;
            if (_agent != null && _agent.enabled && _agent.isOnNavMesh) _agent.isStopped = false;
        }

        void EnterAttack()
        {
            if (_state == NativeState.Attack) return;

            _state = NativeState.Attack;

            // The first swing waits for the wind-up but not for the full interval: closing the
            // distance is the wind-up for the fight, and the interval belongs between blows.
            _nextAttack = Mathf.Max(_nextAttack, Time.time);
        }

        void EnterFlee()
        {
            if (_state == NativeState.Flee) return;

            _state = NativeState.Flee;
            _stateUntil = Time.time + 12f;

            // Broken, and not carrying anybody home. This is the cheapest rescue in the game: hurt
            // the carrier enough and it drops your friend and runs.
            Release();

            _target = null;
            _hasSuspect = false;
            _swingAt = 0f;

            if (!Go(_camp, _def.RunSpeed)) EnterIdle();

            if (CommandLine.HasFlag("-nativeLog"))
                Debug.Log($"[Native] {_def.Id} {ObjectId} broke and ran for camp at "
                          + $"{_health.Current:F0}/{_health.Max:F0} hp.");
        }

        // ---------------------------------------------------------------- navigation

        bool Go(Vector3 wanted, float speed)
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh) return false;
            if (!NavMesh.SamplePosition(wanted, out NavMeshHit hit, 12f, NavMesh.AllAreas)) return false;

            _agent.speed = speed;
            _agent.isStopped = false;
            return _agent.SetDestination(hit.position);
        }

        bool Arrived()
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh) return true;
            if (_agent.pathPending) return false;

            return _agent.remainingDistance <= _agent.stoppingDistance + 0.2f;
        }

        void Stop()
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh) return;

            _agent.isStopped = true;
            _agent.velocity = Vector3.zero;
        }

        void Face(Vector3 direction)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f) return;

            transform.rotation = Quaternion.RotateTowards(transform.rotation,
                                                          Quaternion.LookRotation(direction),
                                                          540f * Time.deltaTime);
        }

        // ---------------------------------------------------------------- death and loot

        void OnServerStateChanged(LifeState previous, LifeState next)
        {
            if (next != LifeState.Dead || _state == NativeState.Dead) return;

            _state = NativeState.Dead;
            _target = null;
            _hasSuspect = false;
            _despawnAt = Time.time + _corpseSeconds;

            // Dropped where it fell, which is the point: the friend you were trying to save is now
            // wherever the fight happened rather than wherever the camp is.
            Release();

            Stop();
            if (_agent != null) _agent.enabled = false;
            if (_collider != null) _collider.enabled = false;

            ServerDropLoot();
        }

        /// <summary>
        /// What they were carrying, on the ground where they fell. Same door as an animal's meat,
        /// for the same reason: a body worth walking to is a body four people can argue over.
        /// </summary>
        void ServerDropLoot()
        {
            if (_def == null || _def.Loot == null) return;

            var dropped = new List<ItemStack>();
            int lines = 0;

            foreach (LootDrop drop in _def.Loot)
            {
                if (drop == null || drop.Item == null) continue;
                if (Random.value > Mathf.Clamp01(drop.Chance)) continue;

                int count = Random.Range(drop.Low, drop.High + 1);
                if (count <= 0) continue;

                ushort index = ItemCatalog.Active != null ? ItemCatalog.Active.IndexOf(drop.Item) : (ushort)0;
                if (index == 0)
                {
                    Debug.LogWarning($"[Native] {_def.Id} drops {drop.Item.name}, which is not in the item "
                                     + "catalog; skipped.");
                    continue;
                }

                var stack = new ItemStack(index, count);

                float angle = lines * Mathf.PI * 0.7f;
                Vector3 offset = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * 0.55f;

                WorldItemSpawner.Drop(stack, transform.position + offset + Vector3.up * 0.4f,
                                      Quaternion.identity);

                dropped.Add(stack);
                lines++;
            }

            Debug.Log($"[Native] {_def.Id} killed by {_health.LastAttackerId}; dropped {Describe(dropped)}.");

            Looted?.Invoke(this, dropped);
        }

        static string Describe(List<ItemStack> stacks)
        {
            if (stacks.Count == 0) return "nothing";

            var parts = new List<string>(stacks.Count);
            foreach (ItemStack stack in stacks)
                parts.Add($"{stack.Count}x {(stack.Def != null ? stack.Def.Id : "?")}");

            return string.Join(", ", parts);
        }
    }
}
