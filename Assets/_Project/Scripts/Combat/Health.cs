using System;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// Host-authoritative health and life state. Every mutation happens on the server; clients only
    /// observe the replicated values and react to events.
    ///
    /// Nothing here is callable from a client by design — there is no ServerRpc that lets a player
    /// declare damage. Weapons ask the server to resolve a hit, the server validates it, and the
    /// server calls <see cref="TakeDamage"/>. That is what stops a modified client from one-shotting
    /// the lobby.
    ///
    /// Zero health means <see cref="LifeState.Downed"/>, not dead. A bleed-out timer then runs, and
    /// only when it expires does the player actually die. The timer is stored as a network tick
    /// rather than a local timestamp so every peer counts down to the same moment and the HUD can
    /// show a number your friends agree with.
    /// </summary>
    public class Health : NetworkBehaviour
    {
        [SerializeField] float _maxHealth = 100f;

        [Tooltip("Seconds spent downed before actually dying. The rescue window.")]
        [SerializeField] float _bleedOutSeconds = 90f;

        [Tooltip("Health restored when picked up off the ground while downed.")]
        [Range(0.05f, 1f)]
        [SerializeField] float _rescueHealthFraction = 0.35f;

        /// <summary>Grace period after spawning, rescuing or reviving during which damage is ignored.</summary>
        [SerializeField] float _spawnInvulnerability = 2f;

        readonly SyncVar<float> _current = new();
        readonly SyncVar<LifeState> _state = new();

        /// <summary>Network tick at which a downed player dies. Meaningless unless downed.</summary>
        readonly SyncVar<uint> _bleedOutEndTick = new();

        /// <summary>
        /// How many times this character has died this run. Drives the Revive Machine's price (#25):
        /// the friend who keeps dying gets more expensive, which is the argument the game wants
        /// people to have. Counts deaths, not downs — being helped off the floor is free.
        /// </summary>
        readonly SyncVar<int> _deaths = new();

        float _invulnerableUntil;

        /// <summary>Server-side memory of the blow that put this character down, for kill attribution.</summary>
        DamageInfo _lastBlow;

        public float Max => _maxHealth;
        public float Current => _current.Value;
        public float Normalized => _maxHealth > 0f ? _current.Value / _maxHealth : 0f;

        public LifeState State => _state.Value;
        public bool IsAlive => _state.Value == LifeState.Alive;
        public bool IsDowned => _state.Value == LifeState.Downed;
        public bool IsDead => _state.Value == LifeState.Dead;

        /// <summary>Deaths this run. Replicated, so the HUD and the shop can both quote a price.</summary>
        public int Deaths => _deaths.Value;

        /// <summary>Downed or dead — on the ground either way, so carry and stun treat them alike.</summary>
        public bool IsIncapacitated => _state.Value != LifeState.Alive;

        /// <summary>Fired on every peer when health changes. (previous, current)</summary>
        public event Action<float, float> Changed;

        /// <summary>Fired on every peer when the life state changes. (previous, next)</summary>
        public event Action<LifeState, LifeState> StateChanged;

        /// <summary>
        /// Server-only, fired before the replicated callback so authoritative systems — native
        /// abduction, loot drops, score — react before anything visual does. (previous, next)
        /// </summary>
        public event Action<LifeState, LifeState> ServerStateChanged;

        /// <summary>
        /// Fired on every peer when this character goes down or dies outright, carrying the blow that
        /// did it. This is what launches the ragdoll, so it has to reach clients with the impulse
        /// intact — a SyncVar callback cannot carry a struct, hence the separate broadcast.
        /// </summary>
        public event Action<DamageInfo> Incapacitated;

        void Awake()
        {
            _current.OnChange += OnHealthChanged;
            _state.OnChange += OnStateChanged;
        }

        void OnDestroy()
        {
            _current.OnChange -= OnHealthChanged;
            _state.OnChange -= OnStateChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _current.Value = _maxHealth;
            _state.Value = LifeState.Alive;
            _invulnerableUntil = Time.time + _spawnInvulnerability;
        }

        void Update()
        {
            if (!IsServerStarted || _state.Value != LifeState.Downed) return;
            if (TimeManager == null || TimeManager.Tick < _bleedOutEndTick.Value) return;

            // Bled out. Attribution survives, but no impulse — the body is already on the floor.
            ServerKill(new DamageInfo(0f, _lastBlow.Type, attackerId: _lastBlow.AttackerId));
        }

        /// <summary>
        /// Seconds left before a downed character dies. Zero when not downed. Valid on every peer,
        /// because the deadline is a network tick rather than a local timestamp.
        /// </summary>
        public float BleedOutRemaining
        {
            get
            {
                if (_state.Value != LifeState.Downed || TimeManager == null) return 0f;

                uint now = TimeManager.Tick;
                uint end = _bleedOutEndTick.Value;
                if (now >= end) return 0f;

                return (float)TimeManager.TicksToTime(end - now);
            }
        }

        /// <summary>Bleed-out progress from 1 (just went down) to 0 (dead), for radial HUD elements.</summary>
        public float BleedOutNormalized
            => _bleedOutSeconds > 0f ? Mathf.Clamp01(BleedOutRemaining / _bleedOutSeconds) : 0f;

        /// <summary>
        /// Applies damage. Server only — calling this on a client does nothing but log.
        /// Returns true if the damage was actually applied.
        /// </summary>
        public bool TakeDamage(DamageInfo info)
        {
            if (!IsServerStarted)
            {
                Debug.LogWarning($"[Health] TakeDamage called on a client for {name}; ignored.");
                return false;
            }

            // Already on the ground. Shooting a downed body still knocks it around, through the stun
            // impulse, but it cannot speed up the bleed-out timer.
            if (_state.Value != LifeState.Alive || info.Amount <= 0f) return false;
            if (Time.time < _invulnerableUntil) return false;

            float previous = _current.Value;
            _current.Value = Mathf.Max(0f, previous - info.Amount);

            if (_current.Value <= 0f)
                ServerDown(info);

            return true;
        }

        /// <summary>Server only. Heals without exceeding <see cref="Max"/>. Does not pick anyone up.</summary>
        public void Heal(float amount)
        {
            if (!IsServerStarted || _state.Value != LifeState.Alive || amount <= 0f) return;
            _current.Value = Mathf.Min(_maxHealth, _current.Value + amount);
        }

        /// <summary>
        /// Server only. Puts the character on the ground and starts the bleed-out timer. Called
        /// automatically when health reaches zero; exposed for scripted knockdowns.
        /// </summary>
        public void ServerDown(DamageInfo info)
        {
            if (!IsServerStarted || _state.Value != LifeState.Alive) return;

            _current.Value = 0f;
            _lastBlow = info;
            _bleedOutEndTick.Value = TimeManager != null
                ? TimeManager.Tick + TimeManager.TimeToTicks(_bleedOutSeconds)
                : 0u;

            SetState(LifeState.Downed, info);
        }

        /// <summary>
        /// Server only. Kills outright, skipping the downed state. For kill volumes, drowning, and
        /// anything else where a rescue window would be absurd.
        /// </summary>
        public void ServerKill(DamageInfo info)
        {
            if (!IsServerStarted || _state.Value == LifeState.Dead) return;

            _current.Value = 0f;
            if (_state.Value == LifeState.Alive) _lastBlow = info;

            SetState(LifeState.Dead, info);
        }

        /// <summary>
        /// Server only. Picks a downed character back up — the cheap outcome, awarded for reaching
        /// them in time. Returns false if they were not downed.
        /// </summary>
        public bool ServerRescue()
        {
            if (!IsServerStarted || _state.Value != LifeState.Downed) return false;

            _current.Value = Mathf.Clamp(_maxHealth * _rescueHealthFraction, 1f, _maxHealth);
            _invulnerableUntil = Time.time + _spawnInvulnerability;
            SetState(LifeState.Alive, default);
            return true;
        }

        /// <summary>
        /// Server only. Brings a dead body back. This is the expensive path — the Revive Machine
        /// charges for it, and the price is the caller's problem, not this component's.
        /// </summary>
        public bool ServerRevive(float healthFraction = 0.5f)
        {
            if (!IsServerStarted || _state.Value != LifeState.Dead) return false;

            _current.Value = Mathf.Clamp(_maxHealth * healthFraction, 1f, _maxHealth);
            _invulnerableUntil = Time.time + _spawnInvulnerability;
            SetState(LifeState.Alive, default);
            return true;
        }

        void SetState(LifeState next, DamageInfo blow)
        {
            LifeState previous = _state.Value;
            if (previous == next) return;

            // Before the state is published, so anything reading Deaths off the state change sees
            // the count that includes this death.
            if (next == LifeState.Dead) _deaths.Value++;

            ServerStateChanged?.Invoke(previous, next);
            _state.Value = next;

            if (next != LifeState.Alive)
                ObserversIncapacitated(blow.Impulse, blow.HitPoint, (byte)blow.Type);
        }

        [ObserversRpc(RunLocally = true)]
        void ObserversIncapacitated(Vector3 impulse, Vector3 hitPoint, byte damageType)
            => Incapacitated?.Invoke(new DamageInfo(0f, (DamageType)damageType, impulse, hitPoint));

        void OnHealthChanged(float prev, float next, bool asServer)
        {
            // Not guarded against running on a host. FishNet raises this once per machine, not
            // once per perspective: a server-side write invokes it with asServer true and never
            // repeats it as the client, so skipping the asServer invoke means a host never sees
            // its own changes at all.
            Changed?.Invoke(prev, next);
        }

        void OnStateChanged(LifeState prev, LifeState next, bool asServer)
        {
            // Fires on the host as well; see Health.OnHealthChanged for why there is no guard.
            StateChanged?.Invoke(prev, next);
        }
    }
}
