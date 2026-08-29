using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// Lets a player pick up, haul, and throw a <see cref="Carryable"/> — a stunned friend, a
    /// corpse, later a plane part.
    ///
    /// Clients ask; the server decides. Range and eligibility are re-checked server-side on every
    /// request, so a client cannot pick something up across the map by editing its own range value.
    ///
    /// The socket is exposed through <see cref="ICarryHolder"/> so that a vehicle seat can offer
    /// one too without pretending to be a character; see that interface for why.
    /// </summary>
    public class CarrySystem : NetworkBehaviour, ICarryHolder
    {
        [Header("References")]
        [Tooltip("Transform the carried body is parented to, usually on the character's shoulders.")]
        [SerializeField] Transform _carrySocket;

        [Tooltip("Origin for the pickup search — normally the camera or head.")]
        [SerializeField] Transform _aimOrigin;

        [Header("Rules")]
        [SerializeField] float _pickupRange = 2.5f;

        [Tooltip("Server-side slack on the range check, to forgive latency between aim and request.")]
        [SerializeField] float _serverRangeTolerance = 1.5f;

        [SerializeField] LayerMask _carryableMask = ~0;

        readonly SyncVar<Carryable> _carrying = new();

        StunState _stun;
        Health _health;

        /// <inheritdoc />
        public Transform CarrySocket => _carrySocket;
        public bool IsCarrying => _carrying.Value != null;
        public Carryable Carried => _carrying.Value;

        void Awake()
        {
            _stun = GetComponent<StunState>();
            _health = GetComponent<Health>();
        }

        void OnEnable()
        {
            // Getting punched or killed while hauling a body has to release it, or the corpse stays
            // welded to a corpse's shoulder for the rest of the run.
            if (_stun != null) _stun.StunChanged += OnStunChanged;
            if (_health != null) _health.ServerStateChanged += OnServerLifeStateChanged;
        }

        void OnDisable()
        {
            if (_stun != null) _stun.StunChanged -= OnStunChanged;
            if (_health != null) _health.ServerStateChanged -= OnServerLifeStateChanged;
        }

        void OnStunChanged(bool stunned)
        {
            if (stunned) ServerForceDrop();
        }

        void OnServerLifeStateChanged(Core.LifeState previous, Core.LifeState next)
        {
            if (next != Core.LifeState.Alive) ServerForceDrop();
        }

        /// <summary>
        /// Owner-side entry point. Call from input. Picks up whatever is aimed at, or drops what is
        /// already held.
        /// </summary>
        public void RequestPickupOrDrop()
        {
            if (!IsOwner) return;

            if (IsCarrying)
            {
                ServerDrop(Vector3.zero);
                return;
            }

            Carryable target = FindTarget();
            if (target != null)
                ServerPickup(target);
        }

        /// <summary>Owner-side. Throws the carried body along the aim direction.</summary>
        public void RequestThrow()
        {
            if (!IsOwner || !IsCarrying) return;
            ServerDrop(AimDirection());
        }

        Carryable FindTarget()
        {
            Transform origin = _aimOrigin != null ? _aimOrigin : transform;

            // A sphere cast rather than a ray: bodies on the ground are easy to miss with a thin ray,
            // and missing your friend three times in a row is not the funny kind of frustrating.
            if (!Physics.SphereCast(origin.position, 0.4f, origin.forward,
                                    out RaycastHit hit, _pickupRange, _carryableMask,
                                    QueryTriggerInteraction.Ignore))
                return null;

            return hit.collider.GetComponentInParent<Carryable>();
        }

        Vector3 AimDirection()
        {
            Transform origin = _aimOrigin != null ? _aimOrigin : transform;
            return origin.forward;
        }

        [ServerRpc]
        void ServerPickup(Carryable target)
        {
            if (target == null || IsCarrying) return;

            // A body on the floor does not get to haul another body.
            if (_health != null && _health.IsIncapacitated) return;
            if (_stun != null && _stun.IsStunned) return;

            if (!target.ServerCanBeCarriedBy(NetworkObject)) return;

            // Re-validate range on the server. The client picked the target, but it does not get to
            // decide how far away it was allowed to be.
            float maxDistance = _pickupRange + _serverRangeTolerance;
            if ((target.transform.position - transform.position).sqrMagnitude > maxDistance * maxDistance)
                return;

            target.ServerAttach(NetworkObject);
            _carrying.Value = target;
        }

        [ServerRpc]
        void ServerDrop(Vector3 throwDirection)
        {
            if (!IsCarrying) return;

            Carryable carried = _carrying.Value;
            _carrying.Value = null;
            carried.ServerDetach(throwDirection);
        }

        /// <summary>
        /// Server only. Force-drops whatever is held — used when the carrier is stunned, dies, or
        /// disconnects. Without this a body stays welded to a corpse's shoulder.
        /// </summary>
        public void ServerForceDrop()
        {
            if (!IsServerStarted || !IsCarrying) return;

            Carryable carried = _carrying.Value;
            _carrying.Value = null;
            carried.ServerDetach();
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            ServerForceDrop();
        }

        void OnDrawGizmosSelected()
        {
            Transform origin = _aimOrigin != null ? _aimOrigin : transform;
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(origin.position + origin.forward * _pickupRange, 0.4f);
        }
    }
}
