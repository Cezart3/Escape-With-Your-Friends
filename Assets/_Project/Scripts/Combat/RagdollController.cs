using System.Collections.Generic;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// Switches a humanoid between animated and fully limp.
    ///
    /// This is the v1 approach: bones are kinematic while animated, and go fully dynamic on stun or
    /// death. It is cheap, stable, and enough to make throwing your friends off a cliff work.
    ///
    /// The v2 upgrade — ConfigurableJoint drives targeting the animated pose, so a hit makes the
    /// character flail while still standing — is deliberately deferred until the surrounding systems
    /// are stable, because it is a tuning problem, not a coding one. See docs/ARCHITECTURE.md.
    ///
    /// This component does not decide *when* to ragdoll; it is driven by StunState and Health, which
    /// are host-authoritative. It runs identically on every peer so the visual result matches.
    /// </summary>
    public class RagdollController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Root bone of the physics skeleton, usually the hips.")]
        [SerializeField] Transform _hipBone;

        [SerializeField] Animator _animator;

        [Tooltip("Components disabled while limp — character controller, movement scripts, colliders.")]
        [SerializeField] Behaviour[] _disableWhileRagdolled;

        [SerializeField] Collider _standingCollider;

        [Header("Recovery")]
        [Tooltip("How far below the hips to search for ground when standing back up.")]
        [SerializeField] float _groundProbeDistance = 3f;

        [SerializeField] LayerMask _groundMask = ~0;

        readonly List<Rigidbody> _bones = new();
        readonly List<Collider> _boneColliders = new();

        bool _isRagdolled;

        public bool IsRagdolled => _isRagdolled;
        public Transform HipBone => _hipBone;

        /// <summary>
        /// The physics skeleton. Exposed so effects that push individual limbs — the taser jitter,
        /// later explosions — can pick a bone without every one of them re-walking the hierarchy.
        /// </summary>
        public IReadOnlyList<Rigidbody> Bones => _bones;

        /// <summary>The hip rigidbody, which is what carrying and throwing act on.</summary>
        public Rigidbody HipBody { get; private set; }

        void Awake()
        {
            if (_hipBone == null)
            {
                Debug.LogError($"[RagdollController] {name} has no hip bone assigned; disabling.");
                enabled = false;
                return;
            }

            if (_animator == null) _animator = GetComponentInChildren<Animator>();

            CacheBones();
            SetRagdollInternal(false);
        }

        void CacheBones()
        {
            _hipBone.GetComponentsInChildren(includeInactive: true, _bones);
            _hipBone.GetComponentsInChildren(includeInactive: true, _boneColliders);

            // The standing capsule lives on the root, not the skeleton, so it is never in this list
            // — but guard anyway in case someone parents it under the hips later.
            if (_standingCollider != null)
                _boneColliders.Remove(_standingCollider);

            HipBody = _hipBone.GetComponent<Rigidbody>();
            if (HipBody == null)
                Debug.LogError($"[RagdollController] {name}: hip bone has no Rigidbody.");
        }

        /// <summary>
        /// Goes limp and applies an impulse. <paramref name="hitPoint"/> selects which bone takes the
        /// force, so a punch to the head spins the head rather than shoving the whole body evenly.
        /// </summary>
        public void EnableRagdoll(Vector3 impulse, Vector3 hitPoint)
        {
            if (!_isRagdolled)
                SetRagdollInternal(true);

            if (impulse.sqrMagnitude <= 0f) return;

            Rigidbody target = hitPoint == Vector3.zero ? HipBody : ClosestBone(hitPoint);
            if (target != null)
                target.AddForceAtPosition(impulse, hitPoint == Vector3.zero ? target.worldCenterOfMass : hitPoint,
                                          ForceMode.Impulse);
        }

        /// <summary>
        /// Stands back up. The root is snapped to wherever the hips ended up so the character does
        /// not teleport back to where it fell from.
        /// </summary>
        public void DisableRagdoll()
        {
            if (!_isRagdolled) return;

            Vector3 hipPosition = _hipBone.position;
            SetRagdollInternal(false);
            RepositionRootUnderHips(hipPosition);
        }

        void SetRagdollInternal(bool ragdolled)
        {
            _isRagdolled = ragdolled;

            foreach (Rigidbody bone in _bones)
            {
                bone.isKinematic = !ragdolled;
                // Interpolation on kinematic bones costs time and does nothing.
                bone.interpolation = ragdolled ? RigidbodyInterpolation.Interpolate
                                               : RigidbodyInterpolation.None;
                if (ragdolled) bone.WakeUp();
            }

            foreach (Collider boneCollider in _boneColliders)
                boneCollider.enabled = ragdolled;

            if (_standingCollider != null) _standingCollider.enabled = !ragdolled;
            if (_animator != null) _animator.enabled = !ragdolled;

            foreach (Behaviour behaviour in _disableWhileRagdolled)
                if (behaviour != null) behaviour.enabled = !ragdolled;
        }

        /// <summary>
        /// Moves the root to the hips' resting spot and drops it onto the ground. Without this the
        /// character snaps back to wherever it was standing when it fell over.
        /// </summary>
        void RepositionRootUnderHips(Vector3 hipPosition)
        {
            Vector3 target = hipPosition;

            if (Physics.Raycast(hipPosition + Vector3.up * 0.1f, Vector3.down,
                                out RaycastHit hit, _groundProbeDistance, _groundMask,
                                QueryTriggerInteraction.Ignore))
            {
                target = hit.point;
            }

            // Keep facing roughly where the body ended up, flattened so the character is not tilted.
            Vector3 forward = Vector3.ProjectOnPlane(_hipBone.forward, Vector3.up);
            transform.SetPositionAndRotation(
                target,
                forward.sqrMagnitude > 0.001f ? Quaternion.LookRotation(forward) : transform.rotation);
        }

        Rigidbody ClosestBone(Vector3 worldPoint)
        {
            Rigidbody closest = HipBody;
            float best = float.MaxValue;

            foreach (Rigidbody bone in _bones)
            {
                float distance = (bone.worldCenterOfMass - worldPoint).sqrMagnitude;
                if (distance >= best) continue;
                best = distance;
                closest = bone;
            }

            return closest;
        }

        /// <summary>Server-side helper so carrying can freeze the body without disabling physics.</summary>
        public void SetBonesKinematic(bool kinematic)
        {
            foreach (Rigidbody bone in _bones)
                bone.isKinematic = kinematic;
        }
    }
}
