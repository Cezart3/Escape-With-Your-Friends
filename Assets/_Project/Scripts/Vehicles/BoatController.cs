using EscapeWithYourFriends.World;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// Makes a <see cref="Vehicle"/> float and drive on the sea.
    ///
    /// Same deal as <see cref="CarController"/>: the host simulates, everybody else holds the body
    /// kinematic and lets the NetworkTransform place it, and the driver's client sends quantised
    /// input rather than motion. A boat simulated on four machines is four boats in four different
    /// troughs.
    ///
    /// **Buoyancy is a spring, and the whole issue is the damper.** Each sample point pushes up in
    /// proportion to how deep it is, which is Archimedes and also a Hooke spring with stiffness
    /// `floatation * mass * g / draft`. An undamped spring at thirty hertz does not settle, it
    /// oscillates until a rounding error throws the boat into the sky - "stable in waves, does not
    /// jitter" is a statement about the damping coefficient and nothing else. So the damper is not a
    /// number somebody guessed: it is computed as a fraction of critical damping for that spring and
    /// this hull's mass, which means changing the mass or the draft cannot silently make the boat
    /// unstable.
    ///
    /// **It rights itself for free.** The centre of mass sits below the sample points, so a rolled
    /// hull has its buoyancy acting above its weight and the pair is a righting couple. That is how
    /// a real boat does it and it needs no code at all, unlike the car, which needs a timer and a
    /// hop because a car on its roof is genuinely stable.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class BoatController : NetworkBehaviour
    {
        /// <summary>
        /// Where the hull is tested against the water, in local space, written by
        /// <see cref="EditorTools.BoatBuilder"/>. Six points: bow, midships and stern in pairs. Fewer
        /// than four and the boat cannot tell pitch from roll; more than eight is paying for detail
        /// the waves do not have, since the shortest wave out there is seventeen metres long.
        /// </summary>
        [SerializeField] Vector3[] _floats = new Vector3[0];

        [Tooltip("Metres of submersion at which a float carries its full share. Also, halved, the "
                 + "depth the hull sits at when nothing is moving, because floatation is 2.")]
        [SerializeField] float _draft = 0.7f;

        [Tooltip("How many times its own weight the hull holds up when every float is fully under. "
                 + "Two means it rests at half draft and has that much again in reserve for four "
                 + "passengers and a wave.")]
        [SerializeField] float _floatation = 2f;

        [Tooltip("Fraction of critical damping for the buoyancy spring. Below about 0.5 the hull "
                 + "porpoises after every wave; at 1 it is a plank. This is the number that makes "
                 + "or breaks the whole component.")]
        [SerializeField] float _damping = 0.7f;

        [Tooltip("Newtons of thrust at full throttle.")]
        [SerializeField] float _thrust = 7000f;

        [Tooltip("Metres per second. Thrust is cut above this rather than the body being clamped.")]
        [SerializeField] float _topSpeed = 12f;

        [Tooltip("Fraction of top speed available astern.")]
        [SerializeField] float _reverseFraction = 0.4f;

        [Tooltip("Newton metres of rudder at full lock and full flow.")]
        [SerializeField] float _turnTorque = 4500f;

        [Tooltip("Metres per second at which the rudder reaches full authority. A rudder with no "
                 + "water moving past it does nothing, which is why a stopped boat cannot spin on "
                 + "the spot.")]
        [SerializeField] float _rudderSpeed = 4f;

        [Tooltip("Ns/m against motion along the hull. Small: a hull is meant to go this way.")]
        [SerializeField] float _forwardDrag = 260f;

        [Tooltip("Ns/m against motion across the hull. Large, and the reason a boat carves a turn "
                 + "instead of sliding through it like a car on ice.")]
        [SerializeField] float _lateralDrag = 2600f;

        [Tooltip("Metres aft of the centre of mass where that lateral resistance acts - the keel. "
                 + "Aft of centre is what makes the hull weathervane back into line; at centre it "
                 + "pivots like a fidget spinner, and forward of centre it is uncontrollable.")]
        [SerializeField] float _keelAft = 1.2f;

        [Tooltip("Local centre of mass. Below the floats on purpose: that is the righting couple.")]
        [SerializeField] Vector3 _centreOfMass = new(0f, -0.25f, 0f);

        Rigidbody _body;
        Vehicle _vehicle;

        /// <summary>Latest input the server is acting on. -1..1, -1..1, held.</summary>
        float _throttle, _steer;
        bool _handbrake;

        /// <summary>What the owner last sent, quantised, so an unchanged frame sends nothing.</summary>
        int _sentThrottle = int.MinValue, _sentSteer = int.MinValue;
        bool _sentHandbrake;

        /// <summary>Whether somebody was in seat 0 last step. See the release in FixedUpdate.</summary>
        bool _hadDriver;

        /// <summary>Floats under water at the end of the last step, and how deep the deepest was.</summary>
        int _wet;
        float _deepest;

        /// <summary>Metres per second along the hull's own forward. Negative is astern.</summary>
        public float ForwardSpeed => _body != null ? Vector3.Dot(_body.linearVelocity, transform.forward) : 0f;

        /// <summary>True while any part of the hull is in the water.</summary>
        public bool IsAfloat => _wet > 0;

        public float TopSpeed => _topSpeed;

        public int FloatCount => _floats.Length;

        /// <summary>
        /// What the hull is doing, for the harness. A boat that will not move is either out of the
        /// water, not being given thrust, or fighting its own drag, and those three look identical
        /// from outside.
        /// </summary>
        public string HullReport()
        {
            float heel = Vector3.Angle(transform.up, Vector3.up);

            return $"{_wet}/{_floats.Length} floats wet, deepest {_deepest:0.00}m of {_draft:0.00}m draft, "
                   + $"hull y {_body.position.y:0.00} vs sea {WaterSurface.HeightAt(_body.position):0.00}, "
                   + $"heel {heel:0.0}deg, speed {ForwardSpeed:0.0} m/s, "
                   + $"throttle {_throttle:0.00}, steer {_steer:0.00}, "
                   + $"vel {_body.linearVelocity}, ang {_body.angularVelocity}";
        }

        void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _vehicle = GetComponent<Vehicle>();

            _body.centerOfMass = _centreOfMass;

            // #62. The baked numbers, kept so an outboard can be expressed as a multiple of them.
            _stockThrust = _thrust;
            _stockTopSpeed = _topSpeed;
        }

        float _stockThrust;
        float _stockTopSpeed;

        /// <summary>
        /// Server only. A bigger outboard, from #62. Thrust and ceiling move together for the same
        /// reason they do on the car: more push against the same speed limit is a shorter run-up to
        /// a number the buyer already had.
        /// </summary>
        public void ServerTune(float power)
        {
            _thrust = _stockThrust * power;
            _topSpeed = _stockTopSpeed * power;
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            // Only the host runs the physics; see the note on CarController.OnStartNetwork.
            if (!IsServerStarted) _body.isKinematic = true;
        }

        /// <summary>Called by the generator so the float layout lives in a diff, not in a binary.</summary>
        public void Configure(Vector3[] floats) => _floats = floats;

        // ---------------------------------------------------------------- input

        /// <summary>
        /// Owner side, every frame. Same contract and the same quantisation as
        /// <see cref="CarController.OwnerDrive"/>, duplicated rather than shared: two vehicles is
        /// not yet a pattern, and an abstract base holding one RPC would be harder to read than the
        /// twenty lines it saves.
        /// </summary>
        // ponytail: duplicated input relay. Extract a base when the plane makes it three.
        public void OwnerDrive(float throttle, float steer, bool handbrake)
        {
            if (!IsOwner) return;

            int t = Mathf.RoundToInt(Mathf.Clamp(throttle, -1f, 1f) * 10f);
            int s = Mathf.RoundToInt(Mathf.Clamp(steer, -1f, 1f) * 10f);

            if (t == _sentThrottle && s == _sentSteer && handbrake == _sentHandbrake) return;

            _sentThrottle = t;
            _sentSteer = s;
            _sentHandbrake = handbrake;

            ServerDrive((sbyte)t, (sbyte)s, handbrake);
        }

        /// <summary>
        /// Quantised to a tenth and sent as two bytes. Ownership is the permission check: seat 0
        /// takes ownership of the vehicle, so a passenger calling this is refused by FishNet before
        /// it reaches here.
        /// </summary>
        [ServerRpc]
        void ServerDrive(sbyte throttle, sbyte steer, bool handbrake)
        {
            _throttle = Mathf.Clamp(throttle / 10f, -1f, 1f);
            _steer = Mathf.Clamp(steer / 10f, -1f, 1f);
            _handbrake = handbrake;
        }

        /// <summary>Server side, for anything that drives without a keyboard: the harness, for now.</summary>
        public void ServerDrive(float throttle, float steer, bool handbrake)
        {
            if (!IsServerStarted) return;

            _throttle = Mathf.Clamp(throttle, -1f, 1f);
            _steer = Mathf.Clamp(steer, -1f, 1f);
            _handbrake = handbrake;
        }

        /// <summary>Cuts the engine. Called when the driver's seat empties.</summary>
        public void ServerRelease()
        {
            if (!IsServerStarted) return;

            _throttle = 0f;
            _steer = 0f;
            _handbrake = true;
        }

        // ---------------------------------------------------------------- floating

        void FixedUpdate()
        {
            if (!IsServerStarted || _floats.Length == 0) return;

            // Losing the driver cuts the throttle once, on the transition. Same reasoning as the
            // car: "no driver" is also what a harness-driven or AI-driven boat looks like, and a
            // guard that re-cut every step would make those impossible rather than merely unusual.
            bool driven = _vehicle == null || _vehicle.Driver != null;

            if (_hadDriver && !driven) ServerRelease();
            _hadDriver = driven;

            Float();
            Drive();
        }

        void Float()
        {
            float weight = _body.mass * -Physics.gravity.y;

            // Stiffness of the whole buoyancy spring, in newtons per metre of sinking, and the
            // damping that goes with it. Derived rather than typed in, so a heavier hull or a
            // deeper draft cannot quietly leave the boat under-damped and porpoising.
            float stiffness = _floatation * weight / Mathf.Max(0.01f, _draft);
            float critical = 2f * Mathf.Sqrt(stiffness * _body.mass);
            float damper = _damping * critical / _floats.Length;

            _wet = 0;
            _deepest = 0f;

            foreach (Vector3 local in _floats)
            {
                Vector3 point = transform.TransformPoint(local);

                float submersion = WaterSurface.HeightAt(point) - point.y;
                if (submersion <= 0f) continue;

                _wet++;
                _deepest = Mathf.Max(_deepest, submersion);

                // Clamped, and this is the clamp that stops a rammed boat becoming a missile: six
                // metres under is not six times the force, it is the same force as one draft under.
                float share = Mathf.Clamp01(submersion / _draft);
                float lift = _floatation * weight / _floats.Length * share;

                // The damper acts on the vertical speed of this point rather than of the hull, which
                // is what makes it damp pitch and roll as well as heave - the points are spread out,
                // so a rocking hull has them moving in opposite directions.
                float rise = Vector3.Dot(_body.GetPointVelocity(point), Vector3.up);

                _body.AddForceAtPosition(Vector3.up * (lift - rise * damper * share), point,
                                         ForceMode.Force);
            }
        }

        void Drive()
        {
            if (_wet == 0) return;

            // Everything below scales with how much of the hull is in the water. A propeller out of
            // the water does not push and a rudder out of the water does not steer, and a boat
            // launched off a wave that kept full authority mid-air would be a flying boat.
            float bite = (float)_wet / _floats.Length;

            Vector3 velocity = _body.linearVelocity;
            Vector3 forward = transform.forward;
            Vector3 right = transform.right;

            float along = Vector3.Dot(velocity, forward);
            float across = Vector3.Dot(velocity, right);

            // Anisotropic drag is the whole difference between a boat and a raft: a hull slides
            // along itself easily and sideways barely at all, so a turn carves instead of drifting.
            _body.AddForce(-forward * (along * _forwardDrag * bite), ForceMode.Force);

            // The lateral half acts *aft* of the centre of mass rather than through it, which is the
            // whole of directional stability in one argument. A hull that is sliding sideways gets a
            // restoring moment from the water on its keel, the same way fletching straightens an
            // arrow. Applied at the centre of mass instead, it produces no moment at all and the
            // first measurement of this boat was 147 degrees a second on a 1.7 metre radius.
            _body.AddForceAtPosition(-right * (across * _lateralDrag * bite),
                                     _body.worldCenterOfMass - forward * _keelAft, ForceMode.Force);

            // The handbrake is the throttle going to neutral and the hull being left to stop itself.
            // There is nothing out here to grab on to, so there is no brake to give.
            float ceiling = _throttle >= 0f ? _topSpeed : _topSpeed * _reverseFraction;
            bool overspeed = Mathf.Abs(along) > ceiling && Mathf.Sign(along) == Mathf.Sign(_throttle);

            if (!_handbrake && !overspeed)
                _body.AddForce(forward * (_throttle * _thrust * bite), ForceMode.Force);

            // Rudder authority needs flow past it, so it follows speed through the water rather than
            // throttle. Going astern steers the other way, as a real boat does and as everybody who
            // has ever backed a trailer already resents.
            float flow = Mathf.Clamp(along / _rudderSpeed, -1f, 1f);

            _body.AddTorque(Vector3.up * (_steer * _turnTorque * flow * bite), ForceMode.Force);
        }
    }
}
