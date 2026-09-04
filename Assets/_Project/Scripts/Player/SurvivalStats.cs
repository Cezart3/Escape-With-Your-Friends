using System;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.World;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// Hunger, thirst, stamina and warmth. Ticked on the host, replicated to everyone.
    ///
    /// The acceptance criterion is "annoying enough to drive behaviour but never tedious", and the
    /// three decisions that carry that are all about *shape* rather than rate:
    ///
    /// - **Hunger and thirst are clocks; warmth is an equilibrium.** Hunger drains to zero and stays
    ///   there until you eat, because that is a chore the game wants you to plan around. Warmth moves
    ///   toward whatever the environment says it should be - hot in the day, cold at night, lethal in
    ///   the sea - and comes back on its own when you get out. A warmth bar that only ever fell would
    ///   mean carrying firewood everywhere, which is the definition of tedious.
    /// - **Stamina limits a burst, not a journey.** Eight seconds of sprint and a fast refill: it
    ///   decides whether you can outrun the thing chasing you, not whether you can cross the island.
    /// - **Empty hurts slowly.** Roughly a hundred seconds from full health to downed on one empty
    ///   bar. Long enough to get to a coconut, short enough that ignoring it is a decision.
    ///
    /// Everything mutates on the server. There is no RPC that lets a client say it ate something -
    /// #45's consumables call in from the server side after the server has taken the item.
    ///
    /// Stamina is read by <see cref="PlayerMotor"/> inside a predicted tick, from a SyncVar rather
    /// than from reconciled state. That is the same trade <c>IsImmobilized</c> already makes: a
    /// mispredicted tick at the moment stamina runs out is corrected by the next reconcile, and the
    /// alternative - putting four survival floats into every replicate - would cost more bandwidth
    /// than a rare one-tick correction is worth.
    /// </summary>
    public class SurvivalStats : NetworkBehaviour
    {
        [Tooltip("Every rate behind these bars. Shared asset; see SurvivalProfile for the numbers.")]
        [SerializeField] SurvivalProfile _profile;

        [Tooltip("Log a line a second describing every stat. Also enabled by -statLog.")]
        [SerializeField] bool _log;

        // Half a second is the send interval for the three slow bars. They move by less than a point
        // in that time, so anything faster is bandwidth spent on a number nobody can see change.
        readonly SyncVar<float> _hunger = new(new SyncTypeSettings(0.5f));
        readonly SyncVar<float> _thirst = new(new SyncTypeSettings(0.5f));
        readonly SyncVar<float> _warmth = new(new SyncTypeSettings(0.5f));

        // Stamina is the exception: it drains in eight seconds, the bar is watched while it happens,
        // and the motor gates sprinting on it. A tenth of a second is what that costs.
        readonly SyncVar<float> _stamina = new(new SyncTypeSettings(0.1f));

        Health _health;
        BuffState _buffs;

        float _recoveryAllowedAt;
        float _nextDamageAt;
        float _nextLogAt;
        bool _sprinting;

        /// <summary>Fired on every peer whenever a bar changes. The HUD redraws off this.</summary>
        public event Action Changed;

        public float Hunger => _hunger.Value;
        public float Thirst => _thirst.Value;
        public float Stamina => _stamina.Value;
        public float Warmth => _warmth.Value;

        public float HungerFraction => _hunger.Value / SurvivalProfile.Max;
        public float ThirstFraction => _thirst.Value / SurvivalProfile.Max;
        public float StaminaFraction => _stamina.Value / SurvivalProfile.Max;
        public float WarmthFraction => _warmth.Value / SurvivalProfile.Max;

        public SurvivalProfile Profile => _profile;

        /// <summary>
        /// Whether sprinting is allowed to *start*. Read by the motor on every peer, which is why the
        /// threshold is above zero: a player at one point of stamina who could start sprinting would
        /// sprint for a single tick, stop, and do it again forever.
        /// </summary>
        public bool CanSprint => _profile == null || _stamina.Value >= _profile.StaminaSprintThreshold;

        /// <summary>Whether sprinting may *continue*. Anything above empty keeps you going.</summary>
        public bool CanKeepSprinting => _profile == null || _stamina.Value > 0f;

        void Awake()
        {
            _health = GetComponent<Health>();
            _buffs = GetComponent<BuffState>();

            _hunger.OnChange += OnBarChanged;
            _thirst.OnChange += OnBarChanged;
            _stamina.OnChange += OnBarChanged;
            _warmth.OnChange += OnBarChanged;
        }

        void OnDestroy()
        {
            _hunger.OnChange -= OnBarChanged;
            _thirst.OnChange -= OnBarChanged;
            _stamina.OnChange -= OnBarChanged;
            _warmth.OnChange -= OnBarChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Full on spawn, including after a revive. Waking up from the Revive Machine starving
            // would be a punishment for having been helped.
            _hunger.Value = SurvivalProfile.Max;
            _thirst.Value = SurvivalProfile.Max;
            _stamina.Value = SurvivalProfile.Max;
            _warmth.Value = SurvivalProfile.Max;

            _log |= CommandLine.HasFlag("-statLog");
            _nextDamageAt = Time.time + 1f;
            _nextLogAt = Time.time + 1f;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (!IsServerStarted) _log = CommandLine.HasFlag("-statLog");
        }

        void OnBarChanged(float older, float newer, bool asServer)
        {
            if (asServer && IsClientStarted) return;

            Changed?.Invoke();
        }

        void Update()
        {
            if (IsServerStarted) ServerTick(Time.deltaTime);

            if (_log) LogTick();
        }

        // ---------------------------------------------------------------- the tick

        void ServerTick(float delta)
        {
            if (_profile == null || delta <= 0f) return;

            // The dead do not get hungry. Downed players do - the bleed-out timer is the thing that
            // matters while you are on the floor, and having thirst tick alongside it changes nothing
            // except the number your friends read when they pick you up.
            if (_health != null && _health.IsDead) return;

            bool sprinting = IsSprinting();

            // Punish first, against the bars as they stood at the end of last frame. Running it last
            // meant warmth was already climbing back off zero by the time it was read - at 490 frames
            // a second the recovery rate puts it above zero within one frame, so hypothermia could
            // never fire at all. Evaluating before the ticks also reads correctly: you do not stop
            // being hypothermic the instant you step out of the sea.
            Punish();

            TickDrain(delta, sprinting);
            TickStamina(delta, sprinting);
            TickWarmth(delta);
        }

        void TickDrain(float delta, bool sprinting)
        {
            float hunger = _profile.HungerDrain + (sprinting ? _profile.HungerSprintDrain : 0f);
            float thirst = _profile.ThirstDrain + (sprinting ? _profile.ThirstSprintDrain : 0f);

            _hunger.Value = Mathf.Max(0f, _hunger.Value - hunger * delta);
            _thirst.Value = Mathf.Max(0f, _thirst.Value - thirst * delta);
        }

        void TickStamina(float delta, bool sprinting)
        {
            if (sprinting)
            {
                // A buff can make running cheaper or dearer. Multiplied rather than added, so a buff
                // that halves the cost keeps halving it however the profile is retuned.
                float cost = _profile.StaminaSprintDrain
                             * (_buffs != null ? _buffs.StaminaCostMultiplier : 1f);

                _stamina.Value = Mathf.Max(0f, _stamina.Value - cost * delta);
                _recoveryAllowedAt = Time.time + _profile.StaminaRecoveryDelay;
                return;
            }

            if (Time.time < _recoveryAllowedAt || _stamina.Value >= SurvivalProfile.Max) return;

            // Starving and parched recover more slowly. This is the one place hunger and thirst have
            // a mechanical effect *before* they empty, which is what makes them worth watching rather
            // than something you only notice when the damage starts.
            float worst = Mathf.Min(HungerFraction, ThirstFraction);
            float scale = 1f - _profile.StaminaStarvationPenalty
                             * Mathf.Clamp01(1f - worst / Mathf.Max(0.01f, _profile.LowThreshold));

            _stamina.Value = Mathf.Min(SurvivalProfile.Max,
                                       _stamina.Value + _profile.StaminaRecovery * scale * delta);
        }

        void TickWarmth(float delta)
        {
            float target = TargetWarmth();
            float current = _warmth.Value;

            if (Mathf.Approximately(current, target)) return;

            float rate = target < current ? _profile.WarmthLossRate : _profile.WarmthGainRate;
            _warmth.Value = Mathf.MoveTowards(current, target, rate * delta);
        }

        /// <summary>
        /// Where warmth is heading right now. Water wins over night, because the sea at noon is still
        /// the sea, and the two stacking would make a night swim a death sentence with no counterplay.
        /// </summary>
        float TargetWarmth()
        {
            if (WaterSurface.IsSubmerged(transform.position + Vector3.up * 0.9f))
                return _profile.WarmthWater;

            return IsNight() ? _profile.WarmthNight : _profile.WarmthDay;
        }

        /// <summary>
        /// Night as the fraction of the cycle centred on midnight. WorldClock.Normalized is 0 at
        /// midnight and 0.5 at noon, so "within half the night fraction of zero" is the whole of it.
        /// </summary>
        bool IsNight()
        {
            float t = WorldClock.Normalized;
            float half = _profile.NightFraction * 0.5f;

            return t < half || t > 1f - half;
        }

        void Punish()
        {
            if (_health == null || !_health.IsAlive) return;
            if (Time.time < _nextDamageAt) return;

            _nextDamageAt = Time.time + _profile.DamageInterval;

            float amount = 0f;
            if (_hunger.Value <= 0f) amount += _profile.StarvationDamage;
            if (_thirst.Value <= 0f) amount += _profile.DehydrationDamage;
            if (_warmth.Value <= 0f) amount += _profile.HypothermiaDamage;

            if (amount <= 0f) return;

            // Environment rather than a named type per cause: the ragdoll and the kill feed care
            // about who hit you, and nobody hit you. What killed you is in the log and on the HUD.
            _health.TakeDamage(new DamageInfo(amount * _profile.DamageInterval, DamageType.Environment));
        }

        bool IsSprinting()
        {
            // The server reads the owner's replicated sprint state through the motor rather than the
            // input reader, which only exists on the owner. A body nobody is driving is not sprinting.
            if (_health != null && _health.IsIncapacitated) return false;

            return _sprinting;
        }

        /// <summary>
        /// Told by <see cref="PlayerMotor"/> on the server, once per tick, whether this body is
        /// actually running. Pushed rather than pulled because the motor is the only thing that knows
        /// the difference between holding shift and moving forward while holding shift.
        /// </summary>
        [Server]
        public void ServerReportSprinting(bool sprinting) => _sprinting = sprinting;

        /// <summary>Spends stamina for a jump. Called by the motor on the server.</summary>
        [Server]
        public void ServerSpendJump()
        {
            if (_profile == null) return;

            _stamina.Value = Mathf.Max(0f, _stamina.Value - _profile.StaminaJumpCost);
            _recoveryAllowedAt = Time.time + _profile.StaminaRecoveryDelay;
        }

        // ---------------------------------------------------------------- what feeds them

        /// <summary>
        /// Eat, drink, warm up. #45's consumables call these after the server has taken the item; a
        /// negative amount is a valid way for a hazard to cost you.
        /// </summary>
        [Server]
        public void ServerFeed(float hunger = 0f, float thirst = 0f, float warmth = 0f,
                               float stamina = 0f)
        {
            _hunger.Value = Clamp(_hunger.Value + hunger);
            _thirst.Value = Clamp(_thirst.Value + thirst);
            _warmth.Value = Clamp(_warmth.Value + warmth);
            _stamina.Value = Clamp(_stamina.Value + stamina);
        }

        static float Clamp(float value) => Mathf.Clamp(value, 0f, SurvivalProfile.Max);

        // ---------------------------------------------------------------- diagnostics

        void LogTick()
        {
            if (Time.time < _nextLogAt) return;

            _nextLogAt = Time.time + 1f;

            string peer = IsServerStarted ? "host" : "client";
            Debug.Log($"[SurvivalStats] {peer} owner {OwnerId}: {Describe()}");
        }

        /// <summary>One line for the log and the test. Reads the same on every peer, which is the point.</summary>
        public string Describe()
        {
            string low = Low();

            return $"food {_hunger.Value:F0} water {_thirst.Value:F0} stam {_stamina.Value:F0} "
                   + $"warm {_warmth.Value:F0}"
                   + (low.Length > 0 ? $" [{low}]" : "");
        }

        string Low()
        {
            if (_profile == null) return "";

            float limit = _profile.LowThreshold;
            string text = "";

            if (HungerFraction <= limit) text += "hungry ";
            if (ThirstFraction <= limit) text += "thirsty ";
            if (WarmthFraction <= limit) text += "freezing ";

            return text.TrimEnd();
        }

        /// <summary>Bake time only.</summary>
        public void Configure(SurvivalProfile profile) => _profile = profile;
    }
}
