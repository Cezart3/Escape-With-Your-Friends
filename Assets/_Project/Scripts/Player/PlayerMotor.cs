using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using FishNet.Utility.Template;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// Walking, sprinting, crouching and jumping, predicted on the owner and reconciled by the host.
    ///
    /// The owner simulates its own movement the instant it presses a key and keeps a history of what
    /// it did. The host re-runs the same inputs authoritatively and sends back the resulting state; if
    /// it differs from what the owner predicted, the owner snaps to the host result and replays every
    /// input since. When the prediction was right — which is nearly always — the replay reproduces the
    /// same position and nothing visibly happens. That is what keeps movement responsive at 100ms
    /// without letting a client teleport.
    ///
    /// Everyone else sees this body through its NetworkTransform. The NetworkObject has state
    /// forwarding switched off, which makes FishNet call
    /// <c>NetworkTransform.ConfigureForPrediction</c>: server-authoritative, not sent to the owner. So
    /// the owner is driven purely by prediction and spectators purely by interpolation, and the two
    /// never fight over the same transform.
    ///
    /// The feel is deliberately loose. Acceleration and friction rather than instant velocity, light
    /// bodies, a floaty jump: sliding past the ledge you meant to stop at is the joke, not a bug.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerMotor : TickNetworkBehaviour
    {
        [System.Flags]
        enum MoveFlags : byte
        {
            None = 0,
            Sprint = 1,
            Crouch = 2,
            Jump = 4,
        }

        /// <summary>
        /// What the owner pressed on one tick. Kept to three fields on purpose: this is sent every
        /// tick, from every player, forever.
        /// </summary>
        struct MoveData : IReplicateData
        {
            public Vector2 Move;
            public float Yaw;
            public MoveFlags Flags;

            uint _tick;

            public MoveData(Vector2 move, float yaw, MoveFlags flags)
            {
                Move = move;
                Yaw = yaw;
                Flags = flags;
                _tick = 0;
            }

            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
            public void Dispose() { }
        }

        /// <summary>
        /// Everything the replicate reads that is not in <see cref="MoveData"/>. If a value influences
        /// the next tick and is missing here, the owner and the host drift apart and the player
        /// rubber-bands — the counters matter as much as the position.
        /// </summary>
        struct MotorState : IReconcileData
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public byte TicksSinceGrounded;
            public byte TicksSinceJump;
            public bool Crouching;

            uint _tick;

            public MotorState(Vector3 position, Vector3 velocity,
                              byte ticksSinceGrounded, byte ticksSinceJump, bool crouching)
            {
                Position = position;
                Velocity = velocity;
                TicksSinceGrounded = ticksSinceGrounded;
                TicksSinceJump = ticksSinceJump;
                Crouching = crouching;
                _tick = 0;
            }

            public uint GetTick() => _tick;
            public void SetTick(uint value) => _tick = value;
            public void Dispose() { }
        }

        [Header("References")]
        [SerializeField] PlayerInputReader _input;

        [Header("Speed")]
        [SerializeField] float _walkSpeed = 4.5f;
        [SerializeField] float _sprintSpeed = 7.5f;
        [SerializeField] float _crouchSpeed = 2.2f;

        [Header("Acceleration")]
        [Tooltip("Metres per second gained per second on the ground.")]
        [SerializeField] float _groundAcceleration = 55f;

        [Tooltip("Deliberately low: you keep the momentum you jumped with.")]
        [SerializeField] float _airAcceleration = 12f;

        [Tooltip("How hard you stop when you let go. Low numbers mean you slide.")]
        [SerializeField] float _groundFriction = 30f;

        [Header("Jump and gravity")]
        [Tooltip("Peak height of a jump from standing, in metres.")]
        [SerializeField] float _jumpHeight = 1.15f;

        [Tooltip("Multiplier on real gravity. Above 1 makes the fall snappier than the rise.")]
        [SerializeField] float _gravityScale = 2.2f;

        [Tooltip("Ticks after walking off a ledge during which a jump still counts.")]
        [SerializeField] byte _coyoteTicks = 4;

        [Tooltip("Ticks before a second jump is allowed, so one press cannot fire twice.")]
        [SerializeField] byte _jumpCooldownTicks = 6;

        [SerializeField] float _terminalVelocity = 55f;

        [Header("Crouch")]
        [SerializeField] float _standHeight = 1.75f;
        [SerializeField] float _crouchHeight = 1.05f;

        [Header("Grounding")]
        [Tooltip("Downward velocity held while grounded, so the controller stays glued to slopes.")]
        [SerializeField] float _groundStick = 2f;

        CharacterController _controller;
        Health _health;
        StunState _stun;
        Carryable _carryable;
        RagdollController _ragdoll;

        Vector3 _velocity;
        byte _ticksSinceGrounded;
        byte _ticksSinceJump;
        bool _crouching;

        readonly Collider[] _overlap = new Collider[8];

        // Correction telemetry. Prediction is working when the state the host sends back for tick N
        // matches what we had already drawn for tick N, so the error has to be measured against our
        // own history and not against where we happen to stand now: an incoming state is always a
        // round trip old, and comparing it to the present would just measure the latency.
        //
        // Off unless -motorLog is passed, and owner-only.
        const int LogIntervalTicks = 60;
        const int HistoryTicks = 128;

        bool _logCorrections;
        readonly uint[] _historyTicks = new uint[HistoryTicks];
        readonly Vector3[] _historyPositions = new Vector3[HistoryTicks];

        int _reconciles;
        int _samples;
        int _ticksSinceLog;
        float _errorSum;
        float _worstError;

        /// <summary>Current velocity in world space. Read by the camera and by anything that throws us.</summary>
        public Vector3 Velocity => _velocity;

        public bool IsGrounded => _ticksSinceGrounded == 0;

        public bool IsCrouching => _crouching;

        /// <summary>Where the camera sits, in local space. Drops with the crouch.</summary>
        public float EyeHeight => (_crouching ? _crouchHeight : _standHeight) - 0.2f;

        void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _health = GetComponent<Health>();
            _stun = GetComponent<StunState>();
            _carryable = GetComponent<Carryable>();
            _ragdoll = GetComponent<RagdollController>();

            if (_input == null) _input = GetComponent<PlayerInputReader>();

            ApplyHeight(standing: true);

            // Tick builds and runs the input; PostTick captures the resulting state for the reconcile.
            // Anything sampled in Update would be a frame out of step with the simulation.
            SetTickCallbacks(TickCallback.Tick | TickCallback.PostTick);
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (!IsOwner) return;

            _logCorrections = CommandLine.HasFlag("-motorLog");

            if (_input != null) _input.Bind(transform.eulerAngles.y);
        }

        public override void OnStopClient()
        {
            if (IsOwner && _input != null) _input.Release();
            base.OnStopClient();
        }

        protected override void TimeManager_OnTick() => PerformReplicate(BuildMoveData());

        protected override void TimeManager_OnPostTick()
        {
            CreateReconcile();

            if (_logCorrections) LogCorrections();
        }

        public override void CreateReconcile()
        {
            var state = new MotorState(transform.position, _velocity,
                                       _ticksSinceGrounded, _ticksSinceJump, _crouching);

            if (_logCorrections) Remember(TimeManager.LocalTick, state.Position);

            PerformReconcile(state);
        }

        /// <summary>
        /// Samples the reader. Returns an empty struct when we do not own this body: FishNet ignores
        /// the argument in that case and feeds the replicate whatever the owner actually sent.
        /// </summary>
        MoveData BuildMoveData()
        {
            if (!IsOwner || _input == null || !_input.IsBound) return default;

            MoveFlags flags = MoveFlags.None;
            if (_input.Sprint) flags |= MoveFlags.Sprint;
            if (_input.Crouch) flags |= MoveFlags.Crouch;

            // Consumed here rather than read, so a press between two ticks is used exactly once.
            if (_input.ConsumeJump()) flags |= MoveFlags.Jump;

            return new MoveData(_input.Move, _input.Yaw, flags);
        }

        [Replicate]
        void PerformReplicate(MoveData md, ReplicateState state = ReplicateState.Invalid,
                              Channel channel = Channel.Unreliable)
        {
            var delta = (float)TimeManager.TickDelta;

            // Ragdolled bodies are moved by the physics engine, and the controller is switched off
            // underneath us. Running the motor here would fight it and throw a warning per tick.
            if (!_controller.enabled)
            {
                _velocity = Vector3.zero;
                _ticksSinceGrounded = 0;
                return;
            }

            transform.rotation = Quaternion.Euler(0f, md.Yaw, 0f);

            // Stunned, downed, dead or being carried: you still fall, you just do not steer. The
            // component states behind this are SyncVars rather than part of the reconcile, so a replay
            // uses their current value; a mispredicted tick here is corrected by the next reconcile.
            bool immobile = IsImmobilized();

            Vector2 input = immobile ? Vector2.zero : Vector2.ClampMagnitude(md.Move, 1f);
            bool wantsCrouch = !immobile && (md.Flags & MoveFlags.Crouch) != 0;
            bool wantsSprint = !immobile && (md.Flags & MoveFlags.Sprint) != 0;
            bool wantsJump = !immobile && (md.Flags & MoveFlags.Jump) != 0;

            ResolveCrouch(wantsCrouch);

            // isGrounded describes the last Move call, which is exactly the state this tick starts in.
            bool grounded = _controller.isGrounded;
            if (grounded)
            {
                _ticksSinceGrounded = 0;
                // Without a little downward push the controller floats a skin width off the ground on
                // every step and reports itself airborne every other tick.
                if (_velocity.y < 0f) _velocity.y = -_groundStick;
            }
            else if (_ticksSinceGrounded < byte.MaxValue)
            {
                _ticksSinceGrounded++;
            }

            if (_ticksSinceJump < byte.MaxValue) _ticksSinceJump++;

            float targetSpeed = _crouching
                ? _crouchSpeed
                : wantsSprint && input.y > 0.1f ? _sprintSpeed : _walkSpeed;

            Vector3 wish = transform.TransformDirection(new Vector3(input.x, 0f, input.y));
            var horizontal = new Vector3(_velocity.x, 0f, _velocity.z);

            if (input.sqrMagnitude > 0.0001f)
            {
                float acceleration = grounded ? _groundAcceleration : _airAcceleration;
                horizontal = Vector3.MoveTowards(horizontal, wish * targetSpeed, acceleration * delta);
            }
            else if (grounded)
            {
                horizontal = Vector3.MoveTowards(horizontal, Vector3.zero, _groundFriction * delta);
            }

            _velocity.x = horizontal.x;
            _velocity.z = horizontal.z;

            bool canJump = _ticksSinceGrounded <= _coyoteTicks
                           && _ticksSinceJump >= _jumpCooldownTicks
                           && !_crouching;

            if (wantsJump && canJump)
            {
                // v = sqrt(2gh): solving for the launch speed means the tuning knob is a height in
                // metres, which is a number a playtester can actually reason about.
                _velocity.y = Mathf.Sqrt(2f * Mathf.Abs(Physics.gravity.y) * _gravityScale * _jumpHeight);
                _ticksSinceGrounded = byte.MaxValue;
                _ticksSinceJump = 0;
            }

            _velocity.y = Mathf.Max(_velocity.y + Physics.gravity.y * _gravityScale * delta,
                                    -_terminalVelocity);

            _controller.Move(_velocity * delta);
        }

        [Reconcile]
        void PerformReconcile(MotorState state, Channel channel = Channel.Unreliable)
        {
            if (_logCorrections) RecordError(state.GetTick(), state.Position);

            _velocity = state.Velocity;
            _ticksSinceGrounded = state.TicksSinceGrounded;
            _ticksSinceJump = state.TicksSinceJump;

            ApplyHeight(!state.Crouching);
            _crouching = state.Crouching;

            // The controller caches its own position and will overwrite a transform written behind its
            // back. Disabling it for the assignment is the documented way to teleport one.
            bool wasEnabled = _controller.enabled;
            _controller.enabled = false;
            transform.position = state.Position;
            _controller.enabled = wasEnabled;
        }

        void Remember(uint tick, Vector3 position)
        {
            int slot = (int)(tick % HistoryTicks);

            _historyTicks[slot] = tick;
            _historyPositions[slot] = position;
        }

        /// <summary>
        /// Compares an incoming authoritative state against what we predicted for that same tick.
        /// A state older than the buffer has no sample and is counted but not measured.
        /// </summary>
        void RecordError(uint tick, Vector3 authoritative)
        {
            _reconciles++;

            int slot = (int)(tick % HistoryTicks);
            if (_historyTicks[slot] != tick) return;

            float error = Vector3.Distance(_historyPositions[slot], authoritative);

            _samples++;
            _errorSum += error;
            if (error > _worstError) _worstError = error;
        }

        /// <summary>
        /// One line every two seconds rather than one per tick: a per-tick log at 30Hz across four
        /// processes buries the exceptions we are actually reading the file for.
        /// </summary>
        void LogCorrections()
        {
            if (++_ticksSinceLog < LogIntervalTicks) return;

            float average = _samples > 0 ? _errorSum / _samples : 0f;

            Debug.Log($"[PlayerMotor] owner {OwnerId} over {LogIntervalTicks} ticks: "
                      + $"{_reconciles} reconcile(s), {_samples} measured, "
                      + $"average error {average:F4}m, worst {_worstError:F4}m, "
                      + $"at {transform.position}.");

            _ticksSinceLog = 0;
            _reconciles = 0;
            _samples = 0;
            _errorSum = 0f;
            _worstError = 0f;
        }

        bool IsImmobilized()
        {
            if (_health != null && _health.IsIncapacitated) return true;
            if (_stun != null && _stun.IsStunned) return true;
            if (_carryable != null && _carryable.IsCarried) return true;
            if (_ragdoll != null && _ragdoll.IsRagdolled) return true;

            return false;
        }

        /// <summary>
        /// Crouches immediately, stands only when there is room. Standing up into a ceiling would push
        /// the controller through it, which is both a physics bug and a free exploit.
        /// </summary>
        void ResolveCrouch(bool wantsCrouch)
        {
            if (wantsCrouch == _crouching) return;

            if (!wantsCrouch && !HasHeadroom()) return;

            _crouching = wantsCrouch;
            ApplyHeight(!wantsCrouch);
        }

        void ApplyHeight(bool standing)
        {
            float height = standing ? _standHeight : _crouchHeight;

            _controller.height = height;
            // Feet stay put: the head is what moves when you crouch.
            _controller.center = new Vector3(0f, height * 0.5f, 0f);
        }

        bool HasHeadroom()
        {
            float radius = Mathf.Max(0.01f, _controller.radius - _controller.skinWidth);
            Vector3 feet = transform.position;
            Vector3 bottom = feet + Vector3.up * (radius + _controller.skinWidth);
            Vector3 top = feet + Vector3.up * (_standHeight - radius);

            int count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius, _overlap,
                                                       ~0, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                Collider hit = _overlap[i];
                if (hit == null) continue;

                // Our own ragdoll bones live inside this capsule and would block every stand-up.
                if (hit.transform.root == transform.root) continue;

                return false;
            }

            return true;
        }
    }
}
