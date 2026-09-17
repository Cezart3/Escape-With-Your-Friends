using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Vehicles;
using EscapeWithYourFriends.World;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.AI
{
    /// <summary>
    /// The one you came back for. #73.
    ///
    /// Somebody was left on the first island, and the whole reason the aeroplane exists is to go and
    /// get them. They are not cargo and they are not a quest token: they walk, they get in, and they
    /// have opinions about the flying.
    ///
    /// **They are a passenger, not a parcel.** The obvious reading of "pick up the NPC" is to make
    /// them a <see cref="Combat.Carryable"/> like a corpse, and that is the expensive reading - a
    /// Carryable needs a ragdoll rig, and the carry rules only ever find a body whose Health says it
    /// is incapacitated. Somebody standing on a beach waving at you is neither. So they walk to the
    /// aeroplane on the navmesh the island already has, and they ride in the <c>CargoSocket</c>,
    /// which is the seat <see cref="Vehicle"/> already keeps for a body. No rig, no new socket, and
    /// no argument with the seat-and-ownership machinery that exists for players.
    ///
    /// **Nobody is told what to do next by a tutorial.** Each step of the chain is written by
    /// whatever component can already see that the step has happened, the same way
    /// <see cref="PlaneAssembly"/> writes its own line when the last part goes in. This one owns
    /// three: find them, get them to the aeroplane, and take them home. The objective is local and
    /// derived (see <see cref="Objective"/>), so all four players read the same line without anybody
    /// replicating a sentence.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class Castaway : NetworkBehaviour, IInteractable
    {
        /// <summary>Where they are in the rescue. Replicated: the prompt and the HUD both read it.</summary>
        public enum Stage
        {
            /// <summary>Sat where you left them, waiting to be noticed.</summary>
            Waiting = 0,

            /// <summary>On their feet and walking after whoever spoke to them.</summary>
            Following = 1,

            /// <summary>In the back of the aeroplane, holding on.</summary>
            Aboard = 2,

            /// <summary>Off the aeroplane on the far island. #74 takes it from here.</summary>
            Home = 3
        }

        [Tooltip("Metres from the aeroplane's root. Closer than this and they climb in by "
                 + "themselves. Measured from the root, so it has to clear the wing: they stop "
                 + "_followDistance behind whoever they are following, and that person is "
                 + "standing next to the fuselage, not on it.")]
        [SerializeField] float _boardRange = 6f;

        [Tooltip("Metres they keep behind whoever they are following. Close enough to not be lost, "
                 + "far enough to not be walked into.")]
        [SerializeField] float _followDistance = 2.5f;

        [Tooltip("Metres. Further than this from the person they follow and they give up and wait, "
                 + "so a player who flies off does not drag them across the island.")]
        [SerializeField] float _leashRange = 60f;

        [SerializeField] float _walkSpeed = 3.4f;

        /// <summary>The one of these on the island, for the harness and for the objective line.</summary>
        public static Castaway Instance { get; private set; }

        readonly SyncVar<int> _stage = new();

        /// <summary>Who they are walking after. Server-only: clients never need to know.</summary>
        NetworkObject _leader;

        /// <summary>The aeroplane they are riding in, or null.</summary>
        Vehicle _ride;

        NavMeshAgent _agent;
        PlaneController _flight;

        float _nextRemark;
        float _worstBank;
        string _lastRemark;

        public Stage Where => (Stage)_stage.Value;

        /// <summary>True once they are off the aeroplane on the far side. The end of #73.</summary>
        public bool Rescued => Where == Stage.Home;

        public string Prompt => Where switch
        {
            Stage.Waiting => "Tell them you have a plane",
            Stage.Aboard => "Help them down",

            // Following: they are already coming. Nothing to offer, which also takes this component
            // out of the crosshair's way - see PlayerInteractor, which skips an empty prompt.
            _ => null
        };

        void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _agent.speed = _walkSpeed;
            _agent.stoppingDistance = _followDistance;
            _agent.angularSpeed = 720f;

            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Onto the navmesh, wherever the bake put them. The POI site stands them beside the
            // wreck, and the nav report says that marker is inside the hull:
            //
            //   castaway -> camp.base: setting off 13m from the marker because the marker is
            //   inside the building
            //
            // An agent that starts off the mesh has isOnNavMesh false for good and will never take
            // a step, which is how #73's second harness run failed - they stood up, said they were
            // following, and travelled nothing. Warp is the fix rather than moving the site,
            // because every walking thing spawned on a POI can land this way.
            if (_agent.enabled && !_agent.isOnNavMesh
                && NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 12f, NavMesh.AllAreas))
                _agent.Warp(hit.position);

            Say();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // A late joiner is handed the value, never the changes that produced it, so the line has
            // to be written once on arrival as well as on every change. Same reason as PlaneAssembly.
            _stage.OnChange += OnStageChanged;
            Say();
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            _stage.OnChange -= OnStageChanged;
        }

        void OnStageChanged(int previous, int next, bool asServer) => Say();

        void Update()
        {
            if (!IsServerStarted) return;

            switch (Where)
            {
                case Stage.Following: Follow(); break;
                case Stage.Aboard: Ride(); break;
            }
        }

        // ---------------------------------------------------------------- the three steps

        public bool ServerCanInteract(NetworkObject actor)
        {
            if (!IsServerStarted || actor == null) return false;

            var health = actor.GetComponent<Combat.Health>();
            var stun = actor.GetComponent<Combat.StunState>();
            if ((health != null && health.IsIncapacitated) || (stun != null && stun.IsStunned))
                return false;

            return Where == Stage.Waiting || Where == Stage.Aboard;
        }

        public void ServerInteract(NetworkObject actor)
        {
            if (!ServerCanInteract(actor)) return;

            if (Where == Stage.Waiting)
            {
                _leader = actor;
                Enter(Stage.Following);
                Debug.Log("[Castaway] on their feet and following.");
                return;
            }

            // Aboard, and somebody opened the door. Where they are standing is where they get out,
            // and whether that is the right island is not this component's business to argue about.
            Disembark();
        }

        void Follow()
        {
            // The aeroplane first: if one is parked within arm's reach they get in without being
            // asked, because "walk to the plane and then press the key again" is a step nobody would
            // thank us for.
            Vehicle ride = NearestRide();
            if (ride != null)
            {
                Board(ride);
                return;
            }

            if (_leader == null)
            {
                Enter(Stage.Waiting);
                return;
            }

            Vector3 there = _leader.transform.position;
            float apart = Vector3.Distance(there, transform.position);

            // Left behind. They stop rather than chase forever, so the objective goes back to
            // "find them" and the player who flew off knows where to look.
            if (apart > _leashRange)
            {
                _leader = null;
                Enter(Stage.Waiting);
                Debug.Log("[Castaway] lost sight of everyone and sat back down.");
                return;
            }

            if (!_agent.enabled || !_agent.isOnNavMesh) return;

            _agent.isStopped = apart <= _followDistance;
            if (!_agent.isStopped) _agent.SetDestination(there);
        }

        Vehicle NearestRide()
        {
            foreach (Vehicle vehicle in Vehicle.All)
            {
                if (vehicle == null) continue;

                // Only something that flies. A lift to the beach in the buggy is not the rescue,
                // and climbing into the boat would strand them on the wrong side of the sea.
                if (vehicle.GetComponent<PlaneController>() == null) continue;

                if (Vector3.Distance(vehicle.transform.position, transform.position) <= _boardRange)
                    return vehicle;
            }

            return null;
        }

        void Board(Vehicle ride)
        {
            _ride = ride;
            _flight = ride.GetComponent<PlaneController>();

            Transform socket = ride.CarrySocket != null ? ride.CarrySocket : ride.transform;

            _agent.enabled = false;
            transform.SetParent(socket, worldPositionStays: false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;

            Enter(Stage.Aboard);
            Debug.Log($"[Castaway] climbed into the {ride.Label} and is holding on.");
        }

        void Ride()
        {
            if (_ride == null)
            {
                Disembark();
                return;
            }

            Remark();
        }

        void Disembark()
        {
            Transform ride = _ride != null ? _ride.transform : transform;

            transform.SetParent(null, worldPositionStays: true);

            // Out of the door, not through the fuselage. A metre to the side and a metre up, then let
            // the navmesh catch them: autoSyncTransforms is off, so the agent has to be told the
            // world has moved under it before it is switched back on.
            Vector3 door = ride.position + ride.right * 2.5f + Vector3.up * 0.5f;
            transform.position = door;
            Physics.SyncTransforms();

            if (NavMesh.SamplePosition(door, out NavMeshHit hit, 8f, NavMesh.AllAreas))
                transform.position = hit.position;

            _agent.enabled = true;
            _ride = null;
            _flight = null;

            Enter(Stage.Home);
            Debug.Log("[Castaway] is on the ground with both feet, which is all they wanted.");
        }

        void Enter(Stage stage)
        {
            _stage.Value = (int)stage;
            Say();
        }

        // ---------------------------------------------------------------- opinions

        /// <summary>
        /// The passenger commentary. It is on a cooldown and reads the flight model rather than a
        /// script, so it lands on what the pilot actually did instead of on a timer - which is the
        /// difference between a character and a loudspeaker.
        /// </summary>
        void Remark()
        {
            if (_flight == null || Time.time < _nextRemark) return;

            float bank = Mathf.Abs(_flight.Bank);
            _worstBank = Mathf.Max(_worstBank, bank);

            string line = null;

            if (!_flight.IsAirborne && _flight.Airspeed > 12f) line = "Is the runway meant to be this short?";
            else if (bank > 60f) line = "WINGS. WINGS. LEVEL THE WINGS.";
            else if (_flight.Airspeed < 14f && _flight.IsAirborne) line = "We are very slow. Is that normal?";
            else if (_flight.IsAirborne && transform.position.y < 25f) line = "I can see individual leaves.";
            else if (bank < 8f && _flight.Airspeed > 30f) line = "Oh. You can actually fly this thing.";

            // Never the same line twice running. A parked aeroplane with somebody in the back holds
            // one condition true forever, and the harness caught exactly that - sixty consecutive
            // "We are very slow. Is that normal?" - which is a stuck record, not a passenger.
            if (line == null || line == _lastRemark) return;

            _lastRemark = line;
            _nextRemark = Time.time + 6f;
            Debug.Log($"[Castaway] \"{line}\"");
        }

        // ---------------------------------------------------------------- the chain

        /// <summary>
        /// Writes this step of the objective. Derived from replicated state on every peer, so the
        /// four of you read the same sentence without anybody sending one.
        /// </summary>
        void Say()
        {
            switch (Where)
            {
                case Stage.Waiting:
                    Objective.Set("Find the one you left behind", transform);
                    break;

                case Stage.Following:
                    Vehicle ride = FindPlane();
                    Objective.Set("Get them to the plane", ride != null ? ride.transform : null);
                    break;

                case Stage.Aboard:
                    Objective.Set("Fly them home", null);
                    break;

                case Stage.Home:
                    Objective.Set("Leave this place", null);
                    break;
            }
        }

        static Vehicle FindPlane()
        {
            foreach (Vehicle vehicle in Vehicle.All)
                if (vehicle != null && vehicle.GetComponent<PlaneController>() != null) return vehicle;

            return null;
        }
    }
}
