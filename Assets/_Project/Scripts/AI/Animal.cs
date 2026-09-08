using System.Collections.Generic;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Net;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.AI
{
    /// <summary>What the animal is doing. Server-side; clients only ever see the transform move.</summary>
    public enum AnimalState
    {
        Idle,
        Wander,
        Flee,
        Chase,
        Attack,
        Dead,
    }

    /// <summary>
    /// One animal: the first AI in this project, and deliberately the smallest one that is still a
    /// creature rather than a prop.
    ///
    /// **Everything decides on the server.** The state machine, the pathfinding, the damage and the
    /// loot roll all run on the host, and clients receive nothing but a moving transform through
    /// <c>NetworkTransform</c> plus a species index. That is the same authority split every other
    /// system here uses, and it is not negotiable for an animal: a client that could say "the boar
    /// is now attacking Bob" could also say "the boar is now dead, here is its meat".
    ///
    /// **One prefab, N species.** The body's size, colour, speed, health, reach and loot all come
    /// from an <see cref="AnimalDef"/> chosen at spawn time, so a fourth species is a row in a seed
    /// table and never a new prefab. The species index is a SyncVar set before the spawn, which is
    /// what lets a client build the right shape from the spawn message itself rather than from a
    /// follow-up RPC that would arrive a frame after the animal was already visible.
    ///
    /// The state machine is five states and reads top to bottom:
    ///
    ///   Idle   -> stands still for a few seconds, then wanders
    ///   Wander -> walks to a point near home, then idles
    ///   Flee   -> runs directly away from the nearest person (skittish species)
    ///   Chase  -> runs at the nearest person (aggressive species)
    ///   Attack -> in reach, hitting on an interval
    ///
    /// The transitions are driven by one number - distance to the nearest living player - and by one
    /// memory: <see cref="_alarmedUntil"/>. Without that memory a boar chasing you out of its react
    /// radius immediately forgets why it was running, turns round, and wanders back, which reads as a
    /// bug rather than as an animal. With it, a fright lasts a few seconds past the cause.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class Animal : NetworkBehaviour
    {
        [Header("Data")]
        [Tooltip("Every species. The prefab holds it so the index in the SyncVar can be resolved.")]
        [SerializeField] AnimalCatalog _catalog;

        [Tooltip("Used when something spawns this prefab without saying what it is. Debug convenience.")]
        [SerializeField] AnimalDef _fallback;

        [Header("Body")]
        [Tooltip("The visible box. Scaled from the species at spawn on every peer.")]
        [SerializeField] Transform _body;

        [Tooltip("A smaller box at the front, so you can tell which way it is facing.")]
        [SerializeField] Transform _head;

        [SerializeField] CapsuleCollider _collider;

        [Header("Behaviour")]
        [Tooltip("Seconds between sweeps for nearby players. Cheaper than every frame, still instant to a human.")]
        [SerializeField] float _senseInterval = 0.25f;

        [Tooltip("Seconds the carcass stays before it despawns. The loot has already dropped by then.")]
        [SerializeField] float _corpseSeconds = 8f;

        /// <summary>Which species this is. Set before the spawn, so it arrives with the object.</summary>
        readonly SyncVar<ushort> _species = new();

        NavMeshAgent _agent;
        Health _health;
        AnimalDef _def;

        AnimalState _state = AnimalState.Idle;
        Vector3 _home;
        float _stateUntil;
        float _nextSense;
        float _nextAttack;
        float _alarmedUntil;
        float _despawnAt;

        Health _target;

        /// <summary>Every animal alive on this peer. The spawner counts it; the harness reads it.</summary>
        static readonly List<Animal> _live = new();

        public static IReadOnlyList<Animal> Live => _live;

        /// <summary>What this is. Null until the species index has arrived.</summary>
        public AnimalDef Def => _def;

        public AnimalState State => _state;

        /// <summary>Who it is running from or at. Null when it has not noticed anybody.</summary>
        public Health Target => _target;

        /// <summary>Raised on the server after loot has been dropped, with what was dropped.</summary>
        public event System.Action<Animal, IReadOnlyList<ItemStack>> Butchered;

        void Awake()
        {
            _health = GetComponent<Health>();
            _agent = GetComponent<NavMeshAgent>();

            AnimalCatalog.Use(_catalog);
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            _live.Add(this);

            _species.OnChange += OnSpeciesChanged;
            Resolve(_species.Value);
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();

            _live.Remove(this);
            _species.OnChange -= OnSpeciesChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            _home = transform.position;
            _health.ServerStateChanged += OnServerStateChanged;

            EnterIdle();
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            _health.ServerStateChanged -= OnServerStateChanged;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // The agent steers the server's copy. On a pure client the transform arrives over the
            // wire, and an agent left running would fight it - the classic two-things-own-one-transform
            // bug. The host keeps its agent because there the server's copy *is* this object.
            if (!IsServerStarted && _agent != null) _agent.enabled = false;
        }

        /// <summary>
        /// Server only, and **before** <c>ServerManager.Spawn</c>. Says what this body is, which is
        /// the one thing a shared prefab cannot know about itself.
        /// </summary>
        public void ServerConfigure(AnimalDef def, Vector3 home)
        {
            if (def == null) return;

            _def = def;
            _home = home;

            AnimalCatalog catalog = _catalog != null ? _catalog : AnimalCatalog.Active;
            if (catalog != null) _species.Value = catalog.IndexOf(def);

            _health.ServerConfigure(def.MaxHealth, canBeDowned: false);

            ApplyShape(def);
            ApplyAgent(def);

            name = def.DisplayName;
        }

        void OnSpeciesChanged(ushort previous, ushort next, bool asServer) => Resolve(next);

        void Resolve(ushort index)
        {
            AnimalCatalog catalog = _catalog != null ? _catalog : AnimalCatalog.Active;
            AnimalDef def = catalog != null ? catalog.At(index) : null;

            if (def == null) def = _fallback;
            if (def == null || def == _def) return;

            _def = def;
            ApplyShape(def);
            if (IsServerStarted) ApplyAgent(def);
        }

        // ---------------------------------------------------------------- shape

        /// <summary>
        /// The greybox, built from the numbers. Runs on every peer, because a client has to draw the
        /// thing and a server has to be able to be hit at the right size.
        /// </summary>
        void ApplyShape(AnimalDef def)
        {
            Vector3 size = def.BodySize;

            if (_body != null)
            {
                _body.localScale = size;
                _body.localPosition = new Vector3(0f, size.y * 0.5f, 0f);
                Paint(_body, def.Colour);
            }

            if (_head != null)
            {
                float head = size.y * 0.55f;
                _head.localScale = new Vector3(head, head, head);
                _head.localPosition = new Vector3(0f, size.y * 0.8f, size.z * 0.5f);

                // Darker than the body, so which end is the front survives being seen from a distance
                // in fog at dusk, which is when most of this game happens.
                Paint(_head, def.Colour * 0.6f);
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

            // A property block rather than a material instance: six species would otherwise mean six
            // leaked materials per peer, and the colour is the only thing that differs.
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            block.SetColor("_BaseColor", colour);
            block.SetColor("_Color", colour);
            renderer.SetPropertyBlock(block);
        }

        void ApplyAgent(AnimalDef def)
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

            if (_state == AnimalState.Dead)
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
                case AnimalState.Idle: TickIdle(); break;
                case AnimalState.Wander: TickWander(); break;
                case AnimalState.Flee: TickFlee(); break;
                case AnimalState.Chase: TickChase(); break;
                case AnimalState.Attack: TickAttack(); break;
            }
        }

        /// <summary>
        /// Finds the nearest living player and decides whether that is a problem.
        ///
        /// Reads <see cref="NetworkPlayerRegistry"/> rather than sweeping colliders. Four entries is
        /// cheaper than any physics query, it cannot be fooled by a tree between us, and it is the
        /// same list the natives in #55 will want. The cost of that choice is honest: this animal has
        /// no line of sight and will notice you through a rock. Hearing, not seeing.
        /// </summary>
        void Sense()
        {
            Health nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (NetworkPlayerRegistry.PlayerBody body in NetworkPlayerRegistry.Players)
            {
                if (!body.IsValid) continue;

                var health = body.Object.GetComponent<Health>();
                if (health == null || !health.IsAlive) continue;

                float distance = Vector3.Distance(transform.position, body.Object.transform.position);
                if (distance >= nearestDistance) continue;

                nearest = health;
                nearestDistance = distance;
            }

            if (nearest == null || nearestDistance > _def.SenseRadius)
            {
                _target = null;
                if (Time.time >= _alarmedUntil && (_state == AnimalState.Flee || _state == AnimalState.Chase
                                                   || _state == AnimalState.Attack))
                    EnterIdle();

                return;
            }

            _target = nearest;

            bool tooClose = nearestDistance <= _def.ReactRadius;
            bool alarmed = Time.time < _alarmedUntil;

            if (!tooClose && !alarmed)
            {
                // Aware, unbothered. A deer that watches you from thirty metres is doing the right
                // thing; it only stops grazing when you close.
                if (_state == AnimalState.Flee || _state == AnimalState.Chase || _state == AnimalState.Attack)
                    EnterIdle();

                return;
            }

            if (tooClose) _alarmedUntil = Time.time + _def.CalmSeconds;

            if (_def.IsAggressive)
            {
                if (nearestDistance <= AttackReach()) EnterAttack();
                else if (_state != AnimalState.Chase) EnterChase();
            }
            else if (_state != AnimalState.Flee)
            {
                EnterFlee();
            }
        }

        /// <summary>Reach measured centre to centre, which is what the distance check has.</summary>
        float AttackReach() => _def.AttackRange + _def.BodySize.z * 0.5f;

        void TickIdle()
        {
            if (Time.time >= _stateUntil) EnterWander();
        }

        void TickWander()
        {
            if (Arrived() || Time.time >= _stateUntil) EnterIdle();
        }

        void TickFlee()
        {
            if (_target == null)
            {
                if (Time.time >= _alarmedUntil) EnterIdle();
                return;
            }

            // Re-aimed continuously rather than once on entry. A single flee destination means a deer
            // that runs past you in a straight line the moment you circle round it, which looks less
            // like fear and more like a bad script.
            if (Time.time >= _stateUntil || Arrived()) Retreat();
        }

        void TickChase()
        {
            if (_target == null)
            {
                if (Time.time >= _alarmedUntil) EnterIdle();
                return;
            }

            float distance = Vector3.Distance(transform.position, _target.transform.position);
            if (distance <= AttackReach())
            {
                EnterAttack();
                return;
            }

            if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
                _agent.SetDestination(_target.transform.position);
        }

        void TickAttack()
        {
            if (_target == null || !_target.IsAlive)
            {
                EnterIdle();
                return;
            }

            Vector3 toTarget = _target.transform.position - transform.position;
            float distance = toTarget.magnitude;

            if (distance > AttackReach() * 1.25f)
            {
                EnterChase();
                return;
            }

            Stop();
            Face(toTarget);

            if (Time.time < _nextAttack) return;
            _nextAttack = Time.time + _def.AttackInterval;

            Strike(_target, toTarget);
        }

        /// <summary>
        /// One hit. Deliberately the same shape as <c>Weapon.ApplyHit</c>: damage, then the stun
        /// component if the blow did not already put them down. A boar that hits you and does not
        /// knock you over is a boar that is not worth being afraid of, which is why the impulse and
        /// the stun are on the species rather than hard-coded to zero.
        /// </summary>
        void Strike(Health victim, Vector3 toTarget)
        {
            Vector3 direction = toTarget.sqrMagnitude > 0.001f ? toTarget.normalized : transform.forward;
            Vector3 contact = transform.position + direction * (_def.BodySize.z * 0.5f)
                              + Vector3.up * (_def.BodyHeight * 0.6f);

            var info = new DamageInfo(_def.AttackDamage, DamageType.Animal,
                                      direction * _def.AttackKnockback, contact,
                                      _def.AttackStun, ObjectId);

            bool wasStanding = victim.IsAlive;
            victim.TakeDamage(info);

            bool knockedDown = wasStanding && victim.IsIncapacitated;
            if (!knockedDown)
            {
                var stun = victim.GetComponent<StunState>();
                if (stun != null) stun.ServerStun(info);
            }

            if (CommandLine.HasFlag("-animalLog"))
                Debug.Log($"[Animal] {_def.Id} {ObjectId} hit {victim.ObjectId} for {_def.AttackDamage}, "
                          + $"victim now {victim.Current:F0} hp.");
        }

        // ---------------------------------------------------------------- transitions

        void EnterIdle()
        {
            _state = AnimalState.Idle;
            _stateUntil = Time.time + Random.Range(_def.IdleMin, _def.IdleMax);
            Stop();
        }

        void EnterWander()
        {
            _state = AnimalState.Wander;
            _stateUntil = Time.time + 20f;

            Vector2 offset = Random.insideUnitCircle * _def.WanderRadius;
            Vector3 wanted = _home + new Vector3(offset.x, 0f, offset.y);

            if (!Go(wanted, _def.WalkSpeed)) EnterIdle();
        }

        void EnterFlee()
        {
            _state = AnimalState.Flee;
            Retreat();
        }

        void Retreat()
        {
            _stateUntil = Time.time + 1.5f;

            Vector3 away = transform.position - _target.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = transform.forward;

            Vector3 wanted = transform.position + away.normalized * _def.SenseRadius;

            // A flee that fails to find ground - cornered against the sea, or on a spit - falls back
            // to sidestepping. Standing still while something walks at you is the one response no
            // animal should have.
            if (!Go(wanted, _def.RunSpeed))
            {
                Vector3 sideways = Vector3.Cross(away.normalized, Vector3.up) * _def.ReactRadius;
                if (!Go(transform.position + sideways, _def.RunSpeed)) Stop();
            }
        }

        void EnterChase()
        {
            _state = AnimalState.Chase;
            if (_agent != null) _agent.speed = _def.RunSpeed;
            if (_agent != null && _agent.enabled && _agent.isOnNavMesh) _agent.isStopped = false;
        }

        void EnterAttack()
        {
            if (_state == AnimalState.Attack) return;

            _state = AnimalState.Attack;

            // A first hit on arrival rather than after a full interval, so closing the distance is
            // itself the wind-up. The wait belongs between hits, not before the first one.
            _nextAttack = Mathf.Max(_nextAttack, Time.time);
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
                                                          360f * Time.deltaTime);
        }

        // ---------------------------------------------------------------- death and loot

        void OnServerStateChanged(LifeState previous, LifeState next)
        {
            if (next != LifeState.Dead || _state == AnimalState.Dead) return;

            _state = AnimalState.Dead;
            _target = null;
            _despawnAt = Time.time + _corpseSeconds;

            Stop();
            if (_agent != null) _agent.enabled = false;
            if (_collider != null) _collider.enabled = false;

            ServerDropLoot();
        }

        /// <summary>
        /// Rolls the table and puts the results on the ground.
        ///
        /// Dropped as real world items through the same door <see cref="ItemDropper"/> uses, rather
        /// than pushed into the killer's bag. That is the difference between hunting and a kill
        /// counter: the meat is on the ground, it has weight, somebody has to walk over and pick it
        /// up, and whoever gets there first is a conversation four players can have.
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
                    Debug.LogWarning($"[Animal] {_def.Id} drops {drop.Item.name}, which is not in the item "
                                     + "catalog; skipped.");
                    continue;
                }

                var stack = new ItemStack(index, count);

                // Spread around the carcass rather than stacked in one point, or the whole kill is a
                // single pile of colliders resolving itself into the sky.
                float angle = lines * Mathf.PI * 0.7f;
                Vector3 offset = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * 0.55f;

                WorldItemSpawner.Drop(stack, transform.position + offset + Vector3.up * 0.4f,
                                      Quaternion.identity);

                dropped.Add(stack);
                lines++;
            }

            Debug.Log($"[Animal] {_def.Id} killed by {_health.LastAttackerId}; dropped "
                      + $"{Describe(dropped)}.");

            Butchered?.Invoke(this, dropped);
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
