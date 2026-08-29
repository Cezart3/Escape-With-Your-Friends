using System;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// Host-authoritative stun. The server owns the timer and the flag; clients only mirror it and
    /// play the visual result.
    ///
    /// The flag is a SyncVar so late joiners and reconnects see the correct state, while the impulse
    /// travels as a separate RPC — impulses are one-off events, and replicating them as state would
    /// re-apply the force to anyone who joined afterwards.
    /// </summary>
    [RequireComponent(typeof(RagdollController))]
    public class StunState : NetworkBehaviour
    {
        [Tooltip("Extra seconds spent on the ground after the stun timer expires, before standing up.")]
        [SerializeField] float _getUpDelay = 0.35f;

        readonly SyncVar<bool> _isStunned = new();

        RagdollController _ragdoll;
        Health _health;

        float _serverRecoverAt;

        public bool IsStunned => _isStunned.Value;

        /// <summary>
        /// While true the server will not stand this character back up. Carrying sets it, so a body
        /// slung over someone's shoulder does not wake up and start walking mid-carry.
        /// </summary>
        public bool SuppressRecovery { get; set; }

        /// <summary>Raised on every peer when the stun flag flips. (isStunned)</summary>
        public event Action<bool> StunChanged;

        void Awake()
        {
            _ragdoll = GetComponent<RagdollController>();
            _health = GetComponent<Health>();
            _isStunned.OnChange += OnStunChanged;
        }

        void OnEnable()
        {
            if (_health != null)
            {
                _health.ServerStateChanged += OnServerLifeStateChanged;
                _health.Incapacitated += OnIncapacitated;
            }
        }

        void OnDisable()
        {
            if (_health != null)
            {
                _health.ServerStateChanged -= OnServerLifeStateChanged;
                _health.Incapacitated -= OnIncapacitated;
            }
        }

        void OnDestroy() => _isStunned.OnChange -= OnStunChanged;

        void Update()
        {
            if (!IsServerStarted || !_isStunned.Value) return;
            if (SuppressRecovery) return;

            // Downed and dead bodies stay down; only the life state can pick them back up.
            if (_health != null && _health.IsIncapacitated) return;

            if (Time.time >= _serverRecoverAt)
                _isStunned.Value = false;
        }

        /// <summary>
        /// Server only. Stuns for at least <paramref name="duration"/> seconds — an existing longer
        /// stun is never shortened, so a weak follow-up hit cannot rescue someone from a heavy one.
        /// </summary>
        public void ServerStun(float duration, Vector3 impulse = default, Vector3 hitPoint = default)
        {
            if (!IsServerStarted) return;

            if (duration > 0f)
            {
                float recoverAt = Time.time + duration + _getUpDelay;
                _serverRecoverAt = Mathf.Max(_serverRecoverAt, recoverAt);
                _isStunned.Value = true;
            }

            // The impulse goes out even with no stun duration, so that shooting a body already on the
            // ground still sends it tumbling. That is most of the appeal of a shotgun here.
            if (impulse.sqrMagnitude > 0f && (_isStunned.Value || _health == null || _health.IsIncapacitated))
                ObserversApplyImpulse(impulse, hitPoint);
        }

        /// <summary>Server only. Convenience overload driven straight from a damage event.</summary>
        public void ServerStun(in DamageInfo info)
            => ServerStun(info.StunDuration, info.Impulse, info.HitPoint);

        /// <summary>Server only. Ends the stun immediately, e.g. after a rescue.</summary>
        public void ServerClearStun()
        {
            if (!IsServerStarted) return;
            _serverRecoverAt = 0f;
            SuppressRecovery = false;
            _isStunned.Value = false;
        }

        [ObserversRpc(RunLocally = true)]
        void ObserversApplyImpulse(Vector3 impulse, Vector3 hitPoint)
        {
            // The stun flag may not have replicated yet, so make sure we are limp before pushing.
            _ragdoll.EnableRagdoll(impulse, hitPoint);
        }

        void OnStunChanged(bool prev, bool next, bool asServer)
        {
            // Fires on the host as well; see Health.OnHealthChanged for why there is no guard.
            if (next)
                _ragdoll.EnableRagdoll(Vector3.zero, Vector3.zero);
            else if (_health == null || !_health.IsIncapacitated)
                _ragdoll.DisableRagdoll();

            StunChanged?.Invoke(next);
        }

        void OnServerLifeStateChanged(LifeState previous, LifeState next)
        {
            if (next == LifeState.Alive)
            {
                // Rescued or revived. Stand back up.
                ServerClearStun();
                return;
            }

            // Downed or dead: pinned to the floor until the life state says otherwise. The recovery
            // timer must not fight it.
            _serverRecoverAt = float.MaxValue;
            _isStunned.Value = true;
        }

        void OnIncapacitated(DamageInfo info) => _ragdoll.EnableRagdoll(info.Impulse, info.HitPoint);
    }
}
