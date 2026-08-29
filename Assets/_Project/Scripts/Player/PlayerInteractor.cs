using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// The player's end of <see cref="IInteractable"/>: finds what is being aimed at and asks the
    /// server to use it.
    ///
    /// Deliberately the same shape as <see cref="CarrySystem"/> — same sphere cast, same aim origin,
    /// same server-side range re-validation with the same slack — because they are the same gesture
    /// competing for the same key, and two different reaches would mean the prompt appears at a
    /// distance where the action then fails. The only difference is what the cast is looking for.
    ///
    /// The sphere cast is a little wider and a little longer than the carry reach on purpose: a
    /// machine is a big fixed object you walk up to, a body is a small thing on the floor. Where both
    /// are in range, <see cref="PlayerCombatInput"/> decides, and it prefers this.
    ///
    /// Nothing here trusts the client's target. <see cref="RequestInteract"/> returns whether it
    /// found something worth sending — that is the signal the input component needs to know whether
    /// to fall through to carrying — but the server re-runs every check before anything happens.
    /// </summary>
    public class PlayerInteractor : NetworkBehaviour
    {
        [Header("References")]
        [Tooltip("Origin for the search — the same eye-height transform the weapons aim from.")]
        [SerializeField] Transform _aimOrigin;

        [Header("Rules")]
        [Tooltip("How far away a machine can be used. Longer than the carry reach; see the notes.")]
        [SerializeField] float _range = 3.5f;

        [Tooltip("Fat cast, so aiming at a large object does not require aiming at its centre.")]
        [SerializeField] float _castRadius = 0.5f;

        [Tooltip("Server-side slack on the range check, to forgive latency between aim and request.")]
        [SerializeField] float _serverRangeTolerance = 1.5f;

        [SerializeField] LayerMask _mask = ~0;

        StunState _stun;
        Health _health;

        /// <summary>
        /// What the local player is aiming at, or null. Recomputed on demand rather than cached: the
        /// HUD (#106) will want it every frame and the input path wants it on a key press, and a
        /// sphere cast is cheaper than the bookkeeping to keep a cache honest.
        /// </summary>
        public IInteractable Aimed => FindTarget(out _);

        void Awake()
        {
            _stun = GetComponent<StunState>();
            _health = GetComponent<Health>();
        }

        /// <summary>
        /// Owner-side entry point. Returns true if something interactable was aimed at and a request
        /// went out — false means the caller should try the next thing on its list.
        /// </summary>
        public bool RequestInteract()
        {
            if (!IsOwner) return false;

            // A body on the floor does not get to press buttons. Checked here as well as on the
            // server so a downed player does not spam requests that will all be refused.
            if (_health != null && _health.IsIncapacitated) return false;
            if (_stun != null && _stun.IsStunned) return false;

            if (FindTarget(out NetworkObject target) == null) return false;

            ServerInteract(target);
            return true;
        }

        IInteractable FindTarget(out NetworkObject networkObject)
        {
            networkObject = null;

            Transform origin = _aimOrigin != null ? _aimOrigin : transform;

            if (!Physics.SphereCast(origin.position, _castRadius, origin.forward,
                                    out RaycastHit hit, _range, _mask,
                                    QueryTriggerInteraction.Ignore))
                return null;

            // In parents, not on the collider: a machine's hit box is a child mesh, and the component
            // that knows what the machine does sits on the networked root.
            var interactable = hit.collider.GetComponentInParent<IInteractable>();
            if (interactable == null) return null;

            // An empty prompt means the component is present but has nothing to offer — a Rescuable
            // on a player who is upright, or on a corpse, which is the Revive Machine's job. Skipping
            // those here is what lets Interact fall through to carrying: without it the interactable
            // would swallow the key on every body in the game and a corpse could never be picked up.
            if (string.IsNullOrEmpty(interactable.Prompt)) return null;

            networkObject = hit.collider.GetComponentInParent<NetworkObject>();
            return networkObject != null ? interactable : null;
        }

        /// <summary>
        /// The object is sent, not the interface: FishNet can serialise a spawned
        /// <see cref="NetworkObject"/> by id, and the server resolves the component from it. That
        /// also means a client can only ever name something that actually exists on the server.
        /// </summary>
        [ServerRpc]
        void ServerInteract(NetworkObject target)
        {
            if (target == null) return;

            if (_health != null && _health.IsIncapacitated) return;
            if (_stun != null && _stun.IsStunned) return;

            // The client picked the target; it does not get to decide how far away it was allowed
            // to be. Measured to the object's origin, with the same slack carrying uses.
            float maxDistance = _range + _serverRangeTolerance;
            if ((target.transform.position - transform.position).sqrMagnitude > maxDistance * maxDistance)
                return;

            // Children as well as the root: a machine with two panels is one NetworkObject with two
            // interactables, and the first one is the right answer until something needs otherwise.
            var interactable = target.GetComponentInChildren<IInteractable>();
            if (interactable == null) return;

            if (!interactable.ServerCanInteract(NetworkObject)) return;

            interactable.ServerInteract(NetworkObject);
        }
    }
}
