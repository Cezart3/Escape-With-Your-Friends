using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// Parameters of an ongoing shock. One fact, so one SyncVar — the alternative is four separate
    /// ones that can arrive out of order and briefly describe a shock nobody configured.
    /// </summary>
    public struct ShockData
    {
        /// <summary>Network tick the shock ends on. Zero means not shocked.</summary>
        public uint EndTick;
        public float JitterForce;
        public float JitterInterval;
        public float CameraShake;
    }

    /// <summary>
    /// The twitching. <see cref="StunState"/> already puts a tased victim on the floor and keeps them
    /// there; this is what makes being tased read as electricity rather than as a long nap.
    ///
    /// The jitter runs locally on every peer instead of being broadcast. Ten impulses a second per
    /// victim is not worth the bandwidth, and it does not need to be: the impulse step is derived from
    /// the network tick and the random numbers come from a hash of (object id, step), so every machine
    /// rolls the same values at the same moment without a single extra packet.
    /// </summary>
    [RequireComponent(typeof(RagdollController))]
    public class ShockState : NetworkBehaviour
    {
        readonly SyncVar<ShockData> _shock = new();

        RagdollController _ragdoll;
        Health _health;

        uint _lastJitterStep;
        bool _wasShocked;

        /// <summary>Raised on every peer when the shock starts or ends. (isShocked)</summary>
        public event Action<bool> ShockChanged;

        public bool IsShocked => TimeManager != null && TimeManager.Tick < _shock.Value.EndTick;

        /// <summary>Camera shake amplitude while shocked, for the victim's own camera. Zero otherwise.</summary>
        public float CameraShake => IsShocked ? _shock.Value.CameraShake : 0f;

        /// <summary>Seconds of shock left. Drives the victim's HUD, and is valid on every peer.</summary>
        public float Remaining
        {
            get
            {
                if (TimeManager == null) return 0f;
                uint now = TimeManager.Tick;
                uint end = _shock.Value.EndTick;
                // Unsigned subtraction underflows into a very large number, so compare first.
                return now >= end ? 0f : (float)TimeManager.TicksToTime(end - now);
            }
        }

        void Awake()
        {
            _ragdoll = GetComponent<RagdollController>();
            _health = GetComponent<Health>();
        }

        void OnEnable()
        {
            if (_health != null) _health.ServerStateChanged += OnServerLifeStateChanged;
        }

        void OnDisable()
        {
            if (_health != null) _health.ServerStateChanged -= OnServerLifeStateChanged;
        }

        void OnServerLifeStateChanged(Core.LifeState previous, Core.LifeState next)
        {
            // Standing back up should not come with a leftover second of twitching.
            if (next == Core.LifeState.Alive) ServerClearShock();
        }

        /// <summary>
        /// Server only. Starts or extends a shock. A shorter shock never cuts a longer one short, for
        /// the same reason a weak punch cannot rescue someone from a heavy stun.
        /// </summary>
        public void ServerShock(float duration, float jitterForce, float jitterInterval, float cameraShake)
        {
            if (!IsServerStarted || duration <= 0f || TimeManager == null) return;

            uint endTick = TimeManager.Tick + TimeManager.TimeToTicks(duration);
            if (endTick <= _shock.Value.EndTick) return;

            _shock.Value = new ShockData
            {
                EndTick = endTick,
                JitterForce = jitterForce,
                JitterInterval = Mathf.Max(0.02f, jitterInterval),
                CameraShake = cameraShake,
            };
        }

        /// <summary>Server only. Ends the shock immediately — rescue, revive, or a dropped battery.</summary>
        public void ServerClearShock()
        {
            if (!IsServerStarted) return;
            _shock.Value = default;
        }

        void Update()
        {
            bool shocked = IsShocked;

            if (shocked != _wasShocked)
            {
                _wasShocked = shocked;
                ShockChanged?.Invoke(shocked);
            }

            if (!shocked) return;

            // Nothing to shake if the body is still animated. StunState normally has it limp by now,
            // but the stun flag and the shock data are separate replications and can land a tick apart.
            if (_ragdoll == null || !_ragdoll.IsRagdolled) return;

            ShockData data = _shock.Value;
            uint ticksPerStep = (uint)Mathf.Max(1, (int)TimeManager.TimeToTicks(data.JitterInterval));
            uint step = TimeManager.Tick / ticksPerStep;

            if (step == _lastJitterStep) return;
            _lastJitterStep = step;

            ApplyJitter(step, data.JitterForce);
        }

        void ApplyJitter(uint step, float force)
        {
            var bones = _ragdoll.Bones;
            if (bones.Count == 0 || force <= 0f) return;

            uint seed = Hash((uint)ObjectId * 2654435761u ^ step);

            Rigidbody bone = bones[(int)(seed % (uint)bones.Count)];
            if (bone == null || bone.isKinematic) return;

            // A unit vector from three more hashes. Not uniform on the sphere, and it does not need
            // to be — this is a convulsion, not a particle simulation.
            Vector3 direction = new Vector3(UnitFromHash(seed, 1u),
                                            UnitFromHash(seed, 2u),
                                            UnitFromHash(seed, 3u));

            if (direction.sqrMagnitude < 0.001f) direction = Vector3.up;

            bone.AddForce(direction.normalized * force, ForceMode.Impulse);
        }

        static float UnitFromHash(uint seed, uint salt) => Hash(seed ^ (salt * 0x9e3779b9u)) / 2147483647.5f - 1f;

        /// <summary>
        /// Integer hash (Chris Wellons' triple32). Used instead of <see cref="UnityEngine.Random"/>
        /// because every peer has to produce identical numbers from the same tick without syncing a
        /// generator state.
        /// </summary>
        static uint Hash(uint x)
        {
            x ^= x >> 17;
            x *= 0xed5ad4bbu;
            x ^= x >> 11;
            x *= 0xac4c1b51u;
            x ^= x >> 15;
            x *= 0x31848babu;
            x ^= x >> 14;
            return x;
        }
    }
}
