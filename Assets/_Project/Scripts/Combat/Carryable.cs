using System;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// Something that can be picked up and hauled around — a stunned player, a corpse, later a
    /// crate or a plane part.
    ///
    /// The carrier is a SyncVar rather than an event, because "who is holding this" is state a late
    /// joiner has to see correctly. Attaching is done by parenting the hip rigidbody to the
    /// carrier's socket and making it kinematic; the alternative (a joint, or driving it with
    /// forces) fights the network transform and jitters.
    /// </summary>
    [RequireComponent(typeof(RagdollController))]
    public class Carryable : NetworkBehaviour
    {
        [Tooltip("Only carryable while ragdolled. A conscious player cannot be abducted.")]
        [SerializeField] bool _requiresRagdoll = true;

        [Tooltip("Impulse applied to the hips when thrown, along the carrier's aim direction.")]
        [SerializeField] float _throwImpulse = 12f;

        [SerializeField] float _upwardThrowBias = 0.25f;

        readonly SyncVar<NetworkObject> _carrier = new();

        RagdollController _ragdoll;
        StunState _stun;

        readonly List<Collider> _ignoredWith = new();

        public bool IsCarried => _carrier.Value != null;
        public NetworkObject Carrier => _carrier.Value;

        /// <summary>Raised on every peer when this is picked up or dropped. (carrier, or null)</summary>
        public event Action<NetworkObject> CarrierChanged;

        void Awake()
        {
            _ragdoll = GetComponent<RagdollController>();
            _stun = GetComponent<StunState>();
            _carrier.OnChange += OnCarrierChanged;
        }

        void OnDestroy() => _carrier.OnChange -= OnCarrierChanged;

        /// <summary>Server only. True if <paramref name="carrier"/> is allowed to pick this up now.</summary>
        public bool ServerCanBeCarriedBy(NetworkObject carrier)
        {
            if (!IsServerStarted || carrier == null) return false;
            if (IsCarried) return false;
            if (carrier == NetworkObject) return false;   // no self-carry
            if (_requiresRagdoll && !_ragdoll.IsRagdolled) return false;

            return true;
        }

        /// <summary>Server only. Attaches to a carrier's socket.</summary>
        public void ServerAttach(NetworkObject carrier)
        {
            if (!ServerCanBeCarriedBy(carrier)) return;

            // A carried body must not wake up and walk off mid-carry.
            if (_stun != null) _stun.SuppressRecovery = true;

            _carrier.Value = carrier;
        }

        /// <summary>
        /// Server only. Detaches. A non-zero <paramref name="throwDirection"/> throws rather than
        /// drops — the impulse is applied after the body is dynamic again.
        /// </summary>
        public void ServerDetach(Vector3 throwDirection = default)
        {
            if (!IsServerStarted || !IsCarried) return;

            _carrier.Value = null;

            if (_stun != null) _stun.SuppressRecovery = false;

            if (throwDirection.sqrMagnitude > 0f)
            {
                Vector3 direction = (throwDirection.normalized + Vector3.up * _upwardThrowBias).normalized;
                ObserversThrow(direction * _throwImpulse);
            }
        }

        [ObserversRpc(RunLocally = true)]
        void ObserversThrow(Vector3 impulse)
        {
            if (_ragdoll.HipBody != null)
                _ragdoll.HipBody.AddForce(impulse, ForceMode.Impulse);
        }

        void OnCarrierChanged(NetworkObject prev, NetworkObject next, bool asServer)
        {
            // Fires on the host as well; see Health.OnHealthChanged for why there is no guard.
            if (next != null) AttachVisual(next);
            else DetachVisual();

            CarrierChanged?.Invoke(next);
        }

        void AttachVisual(NetworkObject carrier)
        {
            var socket = carrier.GetComponent<CarrySystem>()?.CarrySocket;
            if (socket == null)
            {
                Debug.LogWarning($"[Carryable] {carrier.name} has no CarrySocket; cannot attach {name}.");
                return;
            }

            Transform hip = _ragdoll.HipBone;

            // Kinematic *before* parenting, otherwise physics fights the parent transform for a frame
            // and the body snaps across the map.
            _ragdoll.SetBonesKinematic(true);
            hip.SetParent(socket, worldPositionStays: false);
            hip.localPosition = Vector3.zero;
            hip.localRotation = Quaternion.identity;

            IgnoreCollisionsWith(carrier, true);
        }

        void DetachVisual()
        {
            Transform hip = _ragdoll.HipBone;
            hip.SetParent(transform, worldPositionStays: true);

            // Back to limp so it falls naturally, and the root follows the body down.
            _ragdoll.SetBonesKinematic(false);

            foreach (Collider other in _ignoredWith)
                if (other != null) SetIgnore(other, false);
            _ignoredWith.Clear();
        }

        void IgnoreCollisionsWith(NetworkObject carrier, bool ignore)
        {
            foreach (Collider other in carrier.GetComponentsInChildren<Collider>())
            {
                if (other == null || other.isTrigger) continue;
                SetIgnore(other, ignore);
                if (ignore) _ignoredWith.Add(other);
            }
        }

        void SetIgnore(Collider other, bool ignore)
        {
            foreach (Collider mine in GetComponentsInChildren<Collider>())
            {
                if (mine == null || mine.isTrigger) continue;
                Physics.IgnoreCollision(mine, other, ignore);
            }
        }
    }
}
