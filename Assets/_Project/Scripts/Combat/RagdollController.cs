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

        [Tooltip("How far above the hips the ground probe starts, so a body settled slightly inside "
                 + "the floor still casts from outside it.")]
        [SerializeField] float _groundProbeRise = 1f;

        [Tooltip("Height of the second probe, dropped from overhead when the first finds nothing. "
                 + "Covers a body that ended up under the floor.")]
        [SerializeField] float _rescueProbeHeight = 200f;

        [SerializeField] LayerMask _groundMask = ~0;

        readonly List<Rigidbody> _bones = new();
        readonly List<Collider> _boneColliders = new();

        // The pose the skeleton was authored in, captured once, restored every time the body stands
        // back up. See SetRagdollInternal.
        readonly List<Vector3> _restPositions = new();
        readonly List<Quaternion> _restRotations = new();

        bool _isRagdolled;

        public bool IsRagdolled => _isRagdolled;
        public Transform HipBone => _hipBone;

        /// <summary>
        /// The physics skeleton. Exposed so effects that push individual limbs — the taser jitter,
        /// later explosions — can pick a bone without every one of them re-walking the hierarchy.
        /// </summary>
        public IReadOnlyList<Rigidbody> Bones => _bones;

        [Tooltip("Share of a hit's impulse that goes into the bone that was struck, rather than into "
                 + "the whole body. High values flail without moving; low values slide without "
                 + "flailing. See EnableRagdoll.")]
        [Range(0f, 1f)]
        [SerializeField] float _localImpulseShare = 0.3f;

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

            foreach (Rigidbody bone in _bones)
            {
                _restPositions.Add(bone.transform.localPosition);
                _restRotations.Add(bone.transform.localRotation);
            }
        }

        /// <summary>
        /// Puts every bone back where the rig had it.
        ///
        /// Physics writes bone transforms in world space, so a body that has been thrown leaves its
        /// skeleton splayed across the arena in *local* coordinates too. An Animator running a clip
        /// overwrites that on the next frame and nobody notices - which is why this was missing until
        /// #50, and why the bug it caused was so strange: the first throw of a session worked and
        /// every one after it did nothing. The root was teleported home while the skeleton stayed five
        /// metres downrange, still holding the offset, and the next hit went into a knot of bones
        /// interpenetrating each other and the floor, where the solver's depenetration ate it.
        ///
        /// A character with no animation is not a hypothetical here: it is the greybox player the
        /// headless tests use, and it will be every character during an art pass.
        /// </summary>
        void RestoreRestPose()
        {
            for (int i = 0; i < _bones.Count && i < _restPositions.Count; i++)
            {
                Rigidbody bone = _bones[i];
                if (bone == null) continue;

                bone.transform.localPosition = _restPositions[i];
                bone.transform.localRotation = _restRotations[i];
            }
        }

        /// <summary>
        /// Goes limp and applies an impulse, split two ways.
        ///
        /// A share goes into the bone nearest <paramref name="hitPoint"/>, which is what makes a hit
        /// *read* as a hit: a punch to the head spins the head, a bat to the legs takes the legs out.
        /// The rest is spread across every bone in proportion to its mass, which is what makes the
        /// body actually *travel*.
        ///
        /// Both halves are needed, and #50 is where that became obvious. All of the impulse into one
        /// bone whips a limb convincingly while the person stays roughly where they were standing -
        /// a 2 kg forearm takes the whole blow and the other 54 kg get dragged along by joints. All of
        /// it spread evenly gives a body that slides without flailing, which reads as a bug. The split
        /// gives a person who flails *and* leaves.
        /// </summary>
        public void EnableRagdoll(Vector3 impulse, Vector3 hitPoint)
        {
            if (!_isRagdolled)
                SetRagdollInternal(true);

            if (impulse.sqrMagnitude <= 0f) return;

            Rigidbody local = hitPoint == Vector3.zero ? HipBody : ClosestBone(hitPoint);

            if (local != null)
            {
                Vector3 at = hitPoint == Vector3.zero ? local.worldCenterOfMass : hitPoint;
                local.AddForceAtPosition(impulse * _localImpulseShare, at, ForceMode.Impulse);
            }

            float spread = 1f - _localImpulseShare;
            if (spread <= 0f) return;

            // By mass, so every bone comes away at the same speed and the body leaves as one piece
            // rather than being torn apart by its own joints.
            float total = 0f;
            foreach (Rigidbody bone in _bones)
                if (bone != null) total += bone.mass;

            if (total <= 0f) return;

            foreach (Rigidbody bone in _bones)
            {
                if (bone == null) continue;

                bone.AddForce(impulse * (spread * bone.mass / total), ForceMode.Impulse);
            }
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

                // A limb accelerated by a punch covers more ground in one 50Hz step than the floor is
                // thick, and discrete detection samples only the end of the step, so it lands under
                // the world. Speculative rather than ContinuousDynamic: it is the cheap variant, it
                // is the only continuous mode a kinematic body is allowed, and the failure it has —
                // stopping slightly short of a surface — is invisible on a limp arm.
                bone.collisionDetectionMode = ragdolled ? CollisionDetectionMode.ContinuousSpeculative
                                                        : CollisionDetectionMode.Discrete;

                if (ragdolled) bone.WakeUp();
            }

            // Kinematic again, so writing the transforms is safe and the solver will not fight it.
            if (!ragdolled) RestoreRestPose();

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

            // Starting the probe above the hips rather than at them matters: a body that has settled
            // with its hips a few centimetres inside the floor would otherwise cast from below the
            // surface, find nothing, and be stood up exactly where it was — inside the ground.
            if (Physics.Raycast(hipPosition + Vector3.up * _groundProbeRise, Vector3.down,
                                out RaycastHit hit, _groundProbeRise + _groundProbeDistance, _groundMask,
                                QueryTriggerInteraction.Ignore))
            {
                target = hit.point;
            }
            else if (Physics.Raycast(hipPosition + Vector3.up * _rescueProbeHeight, Vector3.down,
                                     out hit, _rescueProbeHeight * 2f, _groundMask,
                                     QueryTriggerInteraction.Ignore))
            {
                // Nothing within reach below: the body is under the world, or fell off the edge of it.
                // Drop from well overhead to find the surface of whatever is in this column instead of
                // standing the player up where they are and letting them fall forever. See #110.
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

        /// <summary>
        /// Freezes the skeleton without disabling physics, which is what carrying needs: the bones
        /// stop solving but the colliders stay in the world.
        ///
        /// Interpolation goes off with it, and that is not a detail. Every bone is an interpolated
        /// rigidbody so a ragdoll does not step at 50 Hz on a 144 Hz screen - but interpolation
        /// writes the transform from the *last two physics poses*, which overwrites the position a
        /// parent transform just gave it. A body carried on somebody's shoulder therefore trails
        /// behind the carrier instead of riding it: #107 measured a native hauling at 3.3 m/s and the
        /// body it was carrying moving at 0.8, and putting it down seven metres behind where the
        /// native was standing. While something else owns the pose, nothing here may interpolate it.
        /// </summary>
        public void SetBonesKinematic(bool kinematic)
        {
            foreach (Rigidbody bone in _bones)
            {
                bone.isKinematic = kinematic;
                bone.interpolation = kinematic
                    ? RigidbodyInterpolation.None
                    : RigidbodyInterpolation.Interpolate;
            }
        }

        /// <summary>
        /// Moves the whole skeleton so the hips land on <paramref name="hipTarget"/>, keeping every
        /// bone's pose and every joint's configuration by shifting them all by the same offset.
        ///
        /// A limp body is not where its root transform says it is, so anything that relocates a player
        /// — the fall guard today, a teleporter later — has to carry the skeleton along or the player
        /// is moved while the thing everyone can see stays behind.
        /// </summary>
        public void TeleportSkeleton(Vector3 hipTarget)
        {
            Vector3 offset = hipTarget - _hipBone.position;

            foreach (Rigidbody bone in _bones)
            {
                bone.position += offset;
                bone.linearVelocity = Vector3.zero;
                bone.angularVelocity = Vector3.zero;
            }
        }
    }
}
