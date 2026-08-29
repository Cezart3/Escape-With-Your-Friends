using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// The view a dead player gets. Owner only.
    ///
    /// Dying leaves the body exactly where it fell, because that is the whole point of the death loop
    /// — somebody has to come and get it. But a first-person camera locked inside a ragdoll's skull is
    /// a face full of dirt and nothing else, and a player who cannot see anything stops caring what
    /// happens to their corpse. So death pulls the view out to third person, orbiting the body, and
    /// the player watches their friends argue about whether hauling them back is worth the money.
    ///
    /// This is a *second* CinemachineCamera at a higher priority than <see cref="PlayerCameraRig"/>'s,
    /// exactly as that class's note says it should be. Nothing in the rig knows this exists: the rig
    /// keeps tracking the head bone at priority 10, this sits at 20 while it lives, and the Brain
    /// blends between them. Reviving destroys this one and the view blends back on its own. The ghost
    /// (#26) turned out to need exactly what this file predicted: one call to <see cref="Follow"/>
    /// with somebody else's transform, and the same blend carries the player there.
    ///
    /// What it follows depends on whether the dead player can move. With no <see cref="GhostController"/>
    /// it tracks the hip bone rather than the body root, because once the ragdoll takes over the root
    /// stops moving and the corpse slides away from it, and damping is deliberately heavy — a hip
    /// being punted down a hill is not something to follow tightly. With a ghost it tracks the ghost
    /// instead, lightly damped and bound to its rotation, which is the difference between watching a
    /// corpse and flying a camera.
    /// </summary>
    public class DeathCamera : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] Health _health;
        [SerializeField] RagdollController _ragdoll;

        [Tooltip("Optional. When the dead player can fly, the view rides the ghost instead of the hip.")]
        [SerializeField] GhostController _ghost;

        [Header("Framing")]
        [Tooltip("Metres behind the body. World space, so a tumbling ragdoll does not spin the camera.")]
        [SerializeField] float _distance = 3.5f;

        [Tooltip("Metres above the body. Enough to see over it at the friends walking up.")]
        [SerializeField] float _height = 2.2f;

        [SerializeField] float _fieldOfView = 60f;

        [Tooltip("Heavy on purpose: a corpse being kicked around should not drag the camera with it.")]
        [SerializeField] float _positionDamping = 0.8f;

        [Tooltip("How lazily the camera re-centres the body on screen.")]
        [SerializeField] float _aimDamping = 0.6f;

        [Tooltip("Chasing a ghost is a free camera; heavy damping there just feels like lag.")]
        [SerializeField] float _ghostDamping = 0.25f;

        [Header("Priority")]
        [Tooltip("Must beat PlayerCameraRig's 10, and lose to anything that should override death.")]
        [SerializeField] int _priority = 20;

        CinemachineCamera _camera;
        CinemachineFollow _follow;
        Transform _followed;

        /// <summary>True while the death view is the active camera.</summary>
        public bool IsActive => _camera != null;

        /// <summary>What the death view is currently watching. Null when it is not running.</summary>
        public Transform Following => _followed;

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Only the dead player's own screen changes. Everyone else is watching the body from
            // wherever they happen to be standing, which is the joke.
            if (!IsOwner)
            {
                enabled = false;
                return;
            }

            if (_health != null) _health.StateChanged += OnLifeStateChanged;

            // Late joiners to their own corpse: a body can be spawned already dead by the time the
            // client sees it, and then no state change is ever raised.
            if (_health != null && _health.IsDead) FollowDefault();
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            if (_health != null) _health.StateChanged -= OnLifeStateChanged;

            // Not parented to the body, same as the rig's camera, so despawning would otherwise leave
            // the highest-priority view in the scene pointing at a destroyed transform.
            Teardown();
        }

        /// <summary>
        /// Points the death view at something and builds it if it is not already up. Passing null
        /// tears it down. This is the seam the ghost (#26) drives: spectating a friend is this call
        /// with their hip bone, and the Brain blends the player across.
        /// </summary>
        public void Follow(Transform target, bool chase = false)
        {
            if (target == null)
            {
                Teardown();
                return;
            }

            if (_camera == null) Build();

            _followed = target;
            _camera.Target.TrackingTarget = target;
            _camera.Target.CustomLookAtTarget = false; // Aim at whatever we are following.

            // Two different things are being followed and they want opposite bindings. A corpse hip
            // rotates freely, so its offset has to be world space or the camera cartwheels with it.
            // A ghost only ever rotates the way the player is looking, so locking the offset to it
            // is exactly what a free camera should do: the view sits behind where you are aiming.
            _follow.TrackerSettings.BindingMode =
                chase ? BindingMode.LockToTargetWithWorldUp : BindingMode.WorldSpace;
            _follow.TrackerSettings.PositionDamping =
                Vector3.one * (chase ? _ghostDamping : _positionDamping);
        }

        void OnLifeStateChanged(LifeState previous, LifeState next)
        {
            if (next == LifeState.Dead) FollowDefault();
            else Teardown();
        }

        /// <summary>
        /// The ghost when there is one to fly, because a player who can move should be watching where
        /// they are going rather than where they died. Otherwise the hip bone, and the body root as a
        /// dull last resort.
        /// </summary>
        void FollowDefault()
        {
            if (_ghost != null && _ghost.Root != null)
            {
                Follow(_ghost.Root, chase: true);
                return;
            }

            Follow(_ragdoll != null && _ragdoll.HipBone != null ? _ragdoll.HipBone : transform);
        }

        void Build()
        {
            var go = new GameObject($"DeathCamera (owner {OwnerId})");
            _camera = go.AddComponent<CinemachineCamera>();
            _camera.Lens.FieldOfView = _fieldOfView;
            _camera.Priority.Value = _priority;

            _follow = go.AddComponent<CinemachineFollow>();

            // Both settings are overwritten by Follow the moment a target arrives; these are only
            // what the camera looks like for the one frame between construction and being aimed.
            _follow.TrackerSettings.BindingMode = BindingMode.WorldSpace;
            _follow.TrackerSettings.PositionDamping = Vector3.one * _positionDamping;
            _follow.FollowOffset = new Vector3(0f, _height, -_distance);

            var composer = go.AddComponent<CinemachineRotationComposer>();
            composer.Damping = new Vector2(_aimDamping, _aimDamping);

            Debug.Log($"[DeathCamera] Owner {OwnerId} is dead; third-person view at priority {_priority}.");
        }

        void Teardown()
        {
            if (_camera != null) Destroy(_camera.gameObject);

            _camera = null;
            _follow = null;
            _followed = null;
        }
    }
}
