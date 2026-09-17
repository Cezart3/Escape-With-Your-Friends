using System.Collections.Generic;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// A piece of the plane, lying where it fell, and the whole of #70.
    ///
    /// Three of these are scattered across the second island at the three places that want you dead,
    /// and every one of them has to be carried home on somebody's shoulder. That is the difference
    /// between this and a boat part, which you buy at a shop and put in your pocket: a plane part is
    /// not an inventory item and deliberately cannot become one. You hold it, you walk slowly, and
    /// when a native puts a dart in you it lands in the mud and rolls back down the hill.
    ///
    /// **Why not <see cref="Carryable"/>.** That component carries *bodies*: it parents a ragdoll's
    /// hip to a socket and lets each peer simulate its own copy, which is right for a corpse and
    /// wrong for an objective. Two clients disagreeing about where a corpse landed is funny; two
    /// clients disagreeing about where the engine landed is a run that cannot be finished. So this
    /// follows <see cref="Items.WorldItem"/>'s rule instead - physics on the server, everyone else
    /// kinematic behind a NetworkTransform - and never reparents anything.
    ///
    /// **Two pairs of hands.** One person can lift a part and will crawl with it. A second person
    /// takes the other end and both of them move at nearly walking pace, which makes the fast way
    /// home a thing two people have to agree on and then stay next to each other for. There is no
    /// joint and no second socket: the helper simply has to keep up, and one who wanders off loses
    /// their grip and drops the carrier back to a crawl. The comedy is in the losing, not the rig.
    /// </summary>
    public class PlanePart : NetworkBehaviour, IInteractable
    {
        [Tooltip("What the crosshair and the objective line call it.")]
        [SerializeField] string _label = "engine";

        [Tooltip("Speed multiplier on somebody hauling this on their own. Low on purpose.")]
        [SerializeField] float _alone = 0.35f;

        [Tooltip("Speed multiplier on both of them once somebody has the other end.")]
        [SerializeField] float _shared = 0.8f;

        [Tooltip("How far the second pair of hands may drift before losing their grip. Metres.")]
        [SerializeField] float _grip = 4f;

        [Tooltip("Where it is put down, in front of the carrier's feet. Metres.")]
        [SerializeField] float _dropAhead = 1.1f;

        [Header("References")]
        [SerializeField] Rigidbody _body;
        [SerializeField] Collider _collider;

        /// <summary>Who has it on their shoulder, or null. A SyncVar because a late joiner has to see it.</summary>
        readonly SyncVar<NetworkObject> _carrier = new();

        /// <summary>Who has the other end, or null.</summary>
        readonly SyncVar<NetworkObject> _helper = new();

        /// <summary>
        /// Every part in the world right now. Three on the second island and none anywhere else,
        /// which is why <see cref="SpeedFor"/> is allowed to be a scan.
        /// </summary>
        public static readonly List<PlanePart> All = new();

        readonly List<Collider> _ignored = new();

        float _objectiveAt;

        public string Label => _label;
        public NetworkObject Carrier => _carrier.Value;
        public NetworkObject Helper => _helper.Value;
        public bool IsCarried => _carrier.Value != null;

        /// <summary>What a body holding this moves at, as a fraction of its normal speed.</summary>
        public float Drag => _helper.Value != null ? _shared : _alone;

        void Awake()
        {
            if (_body == null) _body = GetComponent<Rigidbody>();
            if (_collider == null) _collider = GetComponent<Collider>();

            _carrier.OnChange += OnHandsChanged;
            _helper.OnChange += OnHandsChanged;
        }

        void OnDestroy()
        {
            _carrier.OnChange -= OnHandsChanged;
            _helper.OnChange -= OnHandsChanged;
        }

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        public override void OnStartClient()
        {
            base.OnStartClient();

            // WorldItem's rule, for WorldItem's reason: two peers integrating the same collision
            // independently is how one crate ends up in two places. The server owns the physics and
            // everybody else watches the NetworkTransform.
            if (!IsServerStarted && _body != null) _body.isKinematic = true;
        }

        // ------------------------------------------------------------------ what the crosshair says

        /// <summary>
        /// Null when there is nothing to offer, which is what lets the Interact key fall through to
        /// everything else that wants it. See <see cref="IInteractable.Prompt"/>.
        /// </summary>
        public string Prompt
        {
            get
            {
                if (_carrier.Value == null) return $"Lift the {_label}";
                if (_helper.Value == null) return $"Take the other end of the {_label}";
                return null;
            }
        }

        public bool ServerCanInteract(NetworkObject actor)
        {
            if (!IsServerStarted || actor == null) return false;

            // PlayerInteractor refuses these before the RPC goes out and again when it lands, so
            // this is the third door on the same corridor. It is here because the headless test and
            // anything else that reaches a part without a camera come in through this one.
            var health = actor.GetComponent<Health>();
            var stun = actor.GetComponent<StunState>();
            if ((health != null && health.IsIncapacitated) || (stun != null && stun.IsStunned))
                return false;

            // Already on it. Putting it down and letting go are both ServerInteract's business.
            if (actor == _carrier.Value || actor == _helper.Value) return true;

            if (_carrier.Value != null && _helper.Value != null) return false;

            // Both hands. A friend on your shoulder and an engine in your arms is one too many.
            var carry = actor.GetComponent<CarrySystem>();
            if (carry != null && carry.IsCarrying) return false;

            foreach (PlanePart part in All)
                if (part != this && (part._carrier.Value == actor || part._helper.Value == actor))
                    return false;

            return true;
        }

        public void ServerInteract(NetworkObject actor)
        {
            if (!ServerCanInteract(actor)) return;

            if (actor == _carrier.Value) { ServerPutDown(); return; }
            if (actor == _helper.Value) { _helper.Value = null; return; }

            if (_carrier.Value == null) ServerLift(actor);
            else _helper.Value = actor;
        }

        // ------------------------------------------------------------------ server

        void ServerLift(NetworkObject actor)
        {
            _carrier.Value = actor;
            if (_body == null) return;

            _body.isKinematic = true;

            // Update writes this transform onto a shoulder every frame, and an interpolated body
            // first overwrites it from its last two physics poses - a step stale while walking, and
            // the whole jump stale after a teleport with no physics step since. The helper's grip
            // check then measured from there and let go at once, and a punch put the part down
            // there. #107 learned the same for bodies (RagdollController.SetBonesKinematic); #139
            // for parts.
            _body.interpolation = RigidbodyInterpolation.None;
        }

        /// <summary>Server only. Puts it on the ground in front of whoever was holding it.</summary>
        public void ServerPutDown()
        {
            if (!IsServerStarted || _carrier.Value == null) return;

            Transform holder = _carrier.Value.transform;
            Vector3 spot = holder.position + holder.forward * _dropAhead + Vector3.up * 0.5f;

            _carrier.Value = null;
            _helper.Value = null;

            transform.SetPositionAndRotation(spot, Quaternion.Euler(0f, holder.eulerAngles.y, 0f));

            // Physics does not follow a transform write on its own - autoSyncTransforms is off in
            // this project - so without this the solver still has the part where it was picked up,
            // and going dynamic there throws it back across the island. Same lesson as Carryable.
            Physics.SyncTransforms();

            if (_body != null)
            {
                _body.isKinematic = false;
                _body.interpolation = RigidbodyInterpolation.Interpolate;
                _body.linearVelocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }
        }

        void Update()
        {
            // On every peer, because the objective banner is local and each machine works its own
            // line out of state it already has. Everything below this is the server's.
            PointAtOne();

            if (!IsServerStarted || _carrier.Value == null) return;

            // Everything that can take a part off your shoulder is one of these: a dart, a punch, a
            // fall, or a disconnection. Polled in one place rather than subscribed to in three.
            var health = _carrier.Value.GetComponent<Health>();
            var stun = _carrier.Value.GetComponent<StunState>();

            if ((health != null && health.IsIncapacitated) || (stun != null && stun.IsStunned))
            {
                ServerPutDown();
                return;
            }

            if (_helper.Value != null
                && (_helper.Value.transform.position - transform.position).sqrMagnitude > _grip * _grip)
                _helper.Value = null;

            Transform socket = _carrier.Value.GetComponent<ICarryHolder>()?.CarrySocket;
            if (socket == null) return;

            transform.SetPositionAndRotation(socket.position, socket.rotation);
        }

        // ------------------------------------------------------------------ presentation

        void OnHandsChanged(NetworkObject previous, NetworkObject next, bool asServer)
        {
            // Runs on every peer, host included. A kinematic box riding on somebody's shoulder still
            // blocks their character controller, so whoever is holding it stops colliding with it.
            foreach (Collider other in _ignored)
                if (other != null && _collider != null) Physics.IgnoreCollision(_collider, other, false);
            _ignored.Clear();

            Ignore(_carrier.Value);
            Ignore(_helper.Value);
        }

        void Ignore(NetworkObject holder)
        {
            if (holder == null || _collider == null) return;

            foreach (Collider other in holder.GetComponentsInChildren<Collider>())
            {
                if (other == null || other.isTrigger) continue;
                Physics.IgnoreCollision(_collider, other, true);
                _ignored.Add(other);
            }
        }

        // ------------------------------------------------------------------ statics

        /// <summary>
        /// What <paramref name="body"/> moves at while holding a part, or 1 if it is holding none.
        ///
        /// ponytail: a scan over every part in the world, once per body per frame. There are three
        /// parts. Give the player body a field pointing at what it holds when there are thirty.
        /// </summary>
        public static float SpeedFor(NetworkObject body)
        {
            if (body == null) return 1f;

            foreach (PlanePart part in All)
                if (part._carrier.Value == body || part._helper.Value == body) return part.Drag;

            return 1f;
        }

        /// <summary>The part <paramref name="body"/> is holding, or null. #71 asks this at the plane.</summary>
        public static PlanePart HeldBy(NetworkObject body)
        {
            if (body == null) return null;

            foreach (PlanePart part in All)
                if (part._carrier.Value == body) return part;

            return null;
        }

        /// <summary>
        /// Points the objective banner at the nearest part still out in the world.
        ///
        /// Done from the parts themselves, at 2Hz, and only by whichever one happens to be first in
        /// the list, because <see cref="Objective"/> is one global line and three components each
        /// writing it every frame is three components fighting. Local and unreplicated, like every
        /// other objective: each peer can see where the parts are and works the same sentence out.
        /// </summary>
        void PointAtOne()
        {
            if (All.Count == 0 || All[0] != this || Time.time < _objectiveAt) return;
            _objectiveAt = Time.time + 0.5f;

            PlanePart loose = null;
            foreach (PlanePart part in All)
                if (!part.IsCarried) { loose = part; break; }

            // Nothing loose means everything is on a shoulder or already in the airframe, and from
            // there the line belongs to PlaneAssembly. Two components writing one string at 2Hz is
            // two components flickering, so this one stops rather than competing. #71.
            if (loose != null)
                Objective.Set($"Find the {loose._label} and haul it to the plane", loose.transform);
        }
    }
}
