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
    public class Native : NetworkBehaviour
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

            EnterIdle();
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            _health.ServerStateChanged -= OnServerStateChanged;
            _health.Changed -= OnHealthChanged;
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
            if (_state == NativeState.Flee) return;

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
