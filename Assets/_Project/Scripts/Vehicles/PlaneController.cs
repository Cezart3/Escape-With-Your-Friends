using EscapeWithYourFriends.World;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// Makes a <see cref="Vehicle"/> fly, badly, on purpose. #72.
    ///
    /// Same networking as the car and the boat: the host simulates, everybody else holds the body
    /// kinematic behind a NetworkTransform, and the driver's client sends quantised input rather
    /// than motion.
    ///
    /// **The flight model is four forces and no aerodynamics.** Thrust along the nose, lift along
    /// the wing's up-vector proportional to airspeed squared, a lot of drag sideways and vertically
    /// and very little along the fuselage, and gravity, which Unity already does. That last pair is
    /// the whole trick: an aeroplane that resists moving any direction but forwards *flies where it
    /// is pointed*, which is the thing a first-time player expects and a real lift-and-drag model
    /// spends a hundred lines failing to deliver.
    ///
    /// **Angle of attack comes for free.** Lift acts along <c>transform.up</c>, so pitching the nose
    /// up tilts the lift vector back and the aeroplane climbs; there is no separate AoA term because
    /// the rotation of the aircraft *is* the term. And because lift goes with the square of airspeed,
    /// a stall is not a special case that has to be detected and handled - it is what happens on its
    /// own when the number gets small. The nose sags, the speed comes back, the nose comes up. No
    /// spin, no departure, no death: *"forgiving stall"* is a consequence of never writing the
    /// discontinuity in the first place.
    ///
    /// **Two held keys fly it.** Shift is the throttle and Ctrl is the brake, both of which the input
    /// map already had; the stick is the same Move axes that drive the car. Roll turns you, because
    /// banked lift points sideways and a little yaw is mixed in with the bank - so a player who only
    /// ever learns "left stick to turn, shift to go" can take off. Landing is left hard: there is no
    /// flare assist, no ground effect and no reverse thrust, so arriving is a matter of arriving
    /// slowly enough, and that is the joke.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class PlaneController : NetworkBehaviour
    {
        [Tooltip("Newtons at full throttle.")]
        [SerializeField] float _thrust = 9000f;

        [Tooltip("Throttle units per second. A spool-up slow enough to notice is what stops a "
                 + "player from stamping on the key and pogoing down the strip.")]
        [SerializeField] float _spool = 0.6f;

        [Tooltip("Throttle the engine idles at with no key held. Enough to taxi, not to fly.")]
        [SerializeField] float _idle = 0.12f;

        [Tooltip("Newtons of lift per (metre per second) squared, at one unit of lift coefficient. "
                 + "45 trims a 1100kg airframe level at about 30 m/s, hands off.")]
        [SerializeField] float _lift = 45f;

        [Tooltip("Degrees of angle of attack at which the wing stops gaining lift. Not losing it: "
                 + "see the note on the class about where the forgiveness comes from.")]
        [SerializeField] float _stallAngle = 15f;

        [Tooltip("Degrees the wing is bolted on at. A wing at exactly zero lifts exactly nothing, "
                 + "so without this, hands-off cruise is a slow sink and every flight is hard work.")]
        [SerializeField] float _incidence = 4f;

        [Tooltip("Biggest lift coefficient the wing will admit to, past the stall angle.")]
        [SerializeField] float _clMax = 1.4f;

        [Tooltip("Ceiling on lift, as a multiple of the aircraft's own weight. Without it a dive "
                 + "builds speed, speed builds lift, and the pull-out is a slingshot.")]
        [SerializeField] float _liftCap = 2.2f;

        [Tooltip("Newtons per (metre per second) squared against motion along the fuselage. This is "
                 + "the number that sets the top speed: thrust over it, square-rooted, is 45 m/s.")]
        [SerializeField] float _dragForward = 4.5f;

        [Tooltip("Ns/m against motion across the fuselage, and the reason it flies where it points.")]
        [SerializeField] float _dragSide = 900f;

        [Tooltip("Ns/m against motion straight up or down through the wing. Large as well: this is "
                 + "what keeps a descent a descent instead of an accelerating drop.")]
        [SerializeField] float _dragVertical = 700f;

        [Tooltip("Newton metres of elevator at full stick and full airspeed.")]
        [SerializeField] float _pitchTorque = 16000f;

        [Tooltip("Newton metres of aileron at full stick and full airspeed.")]
        [SerializeField] float _rollTorque = 30000f;

        [Tooltip("Newton metres of yaw mixed in with the bank, per unit of bank. There is no rudder "
                 + "key: banking is turning, which is one fewer thing for a first flight to know.")]
        [SerializeField] float _yawFromBank = 9000f;

        [Tooltip("Metres per second at which the controls reach full authority. Below it they fade "
                 + "out, so a parked aeroplane cannot pirouette on its nose wheel.")]
        [SerializeField] float _controlSpeed = 18f;

        [Tooltip("Newton metres of wings-levelling per unit of bank, applied only with the stick "
                 + "centred. Cheap autopilot, and the single biggest reason a beginner stays up.")]
        [SerializeField] float _selfLevel = 7000f;

        [Tooltip("Ns/m of braking drag along the ground with the brake held.")]
        [SerializeField] float _brakeDrag = 2600f;

        Rigidbody _body;
        Vehicle _vehicle;
        PlaneAssembly _assembly;

        /// <summary>Latest input the server is acting on. -1..1, -1..1, held, held.</summary>
        float _pitch, _roll;
        bool _power, _brake;

        /// <summary>What the owner last sent, quantised, so an unchanged frame sends nothing.</summary>
        int _sentPitch = int.MinValue, _sentRoll = int.MinValue;
        bool _sentPower, _sentBrake;

        bool _hadDriver;
        bool _grounded, _touching;
        int _contacts, _touches;

        /// <summary>What was under it last step, by name. Diagnostic only.</summary>
        string _under = "", _underNext = "";

        /// <summary>0..1, where the engine actually is rather than where the key is asking it to be.</summary>
        public float Throttle { get; private set; }

        /// <summary>Metres per second along the nose. Negative is backwards, which ends badly.</summary>
        public float Airspeed => _body != null ? Vector3.Dot(_body.linearVelocity, transform.forward) : 0f;

        /// <summary>True while nothing is under the belly.</summary>
        public bool IsAirborne => !_grounded;

        /// <summary>
        /// Server side. Times this aeroplane has come back down after a real flight - longer in the
        /// air than <see cref="Hop"/>, so a bounce off a bump on the take-off run does not count.
        /// Zero when the run ends means it was the first try. #92.
        /// </summary>
        public int Touchdowns { get; private set; }

        const float Hop = 3f;
        float _aloft;

        /// <summary>Degrees of bank. Signed: positive is right wing down.</summary>
        public float Bank
        {
            get
            {
                Vector3 right = transform.right;
                return -Mathf.Atan2(right.y, Vector3.ProjectOnPlane(right, Vector3.up).magnitude)
                       * Mathf.Rad2Deg;
            }
        }

        /// <summary>
        /// Degrees between where it is pointed and where it is going. The number that says whether
        /// the drag model is doing its job: a real aeroplane keeps this near zero and a brick does
        /// not.
        /// </summary>
        public float Slip
        {
            get
            {
                Vector3 v = _body != null ? _body.linearVelocity : Vector3.zero;
                return v.sqrMagnitude < 1f ? 0f : Vector3.Angle(v, transform.forward);
            }
        }

        /// <summary>False while the airframe is still missing pieces. #71 is the ignition key.</summary>
        public bool Flyable => _assembly == null || _assembly.Complete;

        /// <summary>What the aeroplane is doing, for the harness and for a bug report.</summary>
        public string FlightReport()
            => $"throttle {Throttle:0.00}, airspeed {Airspeed:0.0} m/s, alt {transform.position.y:0.0}m, "
               + $"bank {Bank:0.0}deg, slip {Slip:0.0}deg, {(_grounded ? "on the ground" : "airborne")}, "
               + $"{(Flyable ? "complete" : "missing pieces")}, vel {_body.linearVelocity}, "
               + $"{(_body.isKinematic ? "kinematic" : "dynamic")}, "
               + $"{(_body.IsSleeping() ? "asleep" : "awake")}, {_contacts} contact(s) [{_under}]";

        void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _vehicle = GetComponent<Vehicle>();
            _assembly = GetComponent<PlaneAssembly>();
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            // Only the host runs the physics; see the note on CarController.OnStartNetwork.
            if (!IsServerStarted) _body.isKinematic = true;
        }

        // ---------------------------------------------------------------- input

        /// <summary>
        /// Owner side, every frame. Same contract and quantisation as the car and the boat, with two
        /// held keys instead of one because an aeroplane needs a throttle and they were free.
        /// </summary>
        public void OwnerDrive(float pitch, float roll, bool power, bool brake)
        {
            if (!IsOwner) return;

            int p = Mathf.RoundToInt(Mathf.Clamp(pitch, -1f, 1f) * 10f);
            int r = Mathf.RoundToInt(Mathf.Clamp(roll, -1f, 1f) * 10f);

            if (p == _sentPitch && r == _sentRoll && power == _sentPower && brake == _sentBrake) return;

            _sentPitch = p;
            _sentRoll = r;
            _sentPower = power;
            _sentBrake = brake;

            ServerDrive((sbyte)p, (sbyte)r, power, brake);
        }

        /// <summary>Two bytes and two bits. Ownership is the permission check; see the boat's note.</summary>
        [ServerRpc]
        void ServerDrive(sbyte pitch, sbyte roll, bool power, bool brake)
        {
            _pitch = Mathf.Clamp(pitch / 10f, -1f, 1f);
            _roll = Mathf.Clamp(roll / 10f, -1f, 1f);
            _power = power;
            _brake = brake;
        }

        /// <summary>Server side, for anything that flies without a keyboard: the harness, for now.</summary>
        public void ServerDrive(float pitch, float roll, bool power, bool brake)
        {
            if (!IsServerStarted) return;

            _pitch = Mathf.Clamp(pitch, -1f, 1f);
            _roll = Mathf.Clamp(roll, -1f, 1f);
            _power = power;
            _brake = brake;
        }

        /// <summary>Closes the throttle and stands on the brakes. Called when the seat empties.</summary>
        public void ServerRelease()
        {
            if (!IsServerStarted) return;

            _pitch = 0f;
            _roll = 0f;
            _power = false;
            _brake = true;
        }

        // ---------------------------------------------------------------- flying

        void FixedUpdate()
        {
            if (!IsServerStarted) return;

            // Same transition-only release as the car and the boat: "no driver" is also what a
            // harness-flown aeroplane looks like, and re-cutting every step would make that
            // impossible rather than merely unusual.
            bool driven = _vehicle == null || _vehicle.Driver != null;

            if (_hadDriver && !driven) ServerRelease();
            _hadDriver = driven;

            // Contacts rather than a probe. A ray cast down from an aeroplane has to be threaded
            // between the aeroplane's own colliders, and the answer then depends on exactly where
            // the wheels were modelled; asking PhysX what is touching costs nothing and cannot be
            // wrong. One step behind, which at 50Hz nobody can tell.
            _grounded = _touching;
            _contacts = _touches;

            if (!_grounded) _aloft += Time.fixedDeltaTime;
            else
            {
                if (_aloft > Hop) Touchdowns++;
                _aloft = 0f;
            }
            _under = _underNext;
            _touching = false;
            _touches = 0;
            _underNext = "";

            Engine();
            Aerodynamics();
            Controls();
        }

        void OnCollisionStay(Collision collision)
        {
            _touching = true;
            _touches += collision.contactCount;

            if (_underNext.Length < 120)
                _underNext += (_underNext.Length > 0 ? ", " : "")
                              + $"{collision.collider.name}<-{(collision.contactCount > 0 ? collision.GetContact(0).thisCollider.name : "?")}";
        }

        void Engine()
        {
            float wanted = !Flyable ? 0f
                         : _brake ? 0f
                         : _power ? 1f
                         : _idle;

            Throttle = Mathf.MoveTowards(Throttle, wanted, _spool * Time.fixedDeltaTime);

            if (Throttle <= 0f) return;

            // PhysX puts a parked aeroplane to sleep, and an aeroplane asleep on the strip absorbs
            // full throttle without moving a centimetre. The car learnt this the same way; see the
            // note on CarController.Drive.
            _body.WakeUp();
            _body.AddForce(transform.forward * (_thrust * Throttle));
        }

        void Aerodynamics()
        {
            Vector3 local = transform.InverseTransformDirection(_body.linearVelocity);

            // Angle of attack: where the air is coming from relative to the wing, plus the few
            // degrees the wing is bolted on at. Lift then acts along the wing's own up, so pitching
            // the nose tilts the vector and changes the angle at the same time, which is both halves
            // of "lift as a function of speed and angle of attack" for the price of an Atan2.
            float aoa = Mathf.Atan2(-local.y, Mathf.Max(1f, local.z)) + _incidence * Mathf.Deg2Rad;

            // Linear up to the stall angle and then clipped. The clip is where the forgiveness
            // lives: past the angle the wing stops *gaining* lift rather than losing all of it and
            // dropping a wing, so a stall here is a sag and a sink, not a spin.
            float cl = Mathf.Clamp(aoa / (_stallAngle * Mathf.Deg2Rad), -_clMax, _clMax);

            float ceiling = _liftCap * _body.mass * -Physics.gravity.y;
            float lift = Mathf.Clamp(_lift * local.z * local.z * cl, -ceiling, ceiling);

            if (local.z > 0f) _body.AddForce(transform.up * lift);

            // Drag, in the aircraft's own axes. Along the fuselage it goes with the square of the
            // speed, which is what gives the thing a top speed at all; across it and through the
            // wing it is linear and large, which is what makes it fly where it points.
            float along = _dragForward * local.z * Mathf.Abs(local.z)
                          + (_grounded && _brake ? _brakeDrag * local.z : 0f);

            _body.AddForce(transform.forward * -along
                           + transform.right * (-local.x * _dragSide)
                           + transform.up * (-local.y * _dragVertical));
        }

        void Controls()
        {
            // No airflow, no authority. A stopped aeroplane with the stick in the corner does
            // nothing at all, which is correct and also stops a parked one from vibrating.
            //
            // Through the air, not along the nose. Airspeed is the forward component alone, and a
            // stalled aeroplane falls belly-first with nothing on it: authority went to zero exactly
            // when the elevator was the only way out, and the stick went dead until the ground
            // arrived. Falling at thirteen metres a second is thirteen metres a second of air over
            // the tail whichever way the nose happens to be pointing.
            float authority = Mathf.Clamp01(_body.linearVelocity.magnitude / _controlSpeed);
            if (authority <= 0f) return;

            _body.AddTorque(transform.right * (-_pitch * _pitchTorque * authority));
            _body.AddTorque(transform.forward * (-_roll * _rollTorque * authority));

            float bank = Bank / 45f;

            // Bank is turn. Nobody has to find a rudder key to get the nose round.
            _body.AddTorque(transform.up * (bank * _yawFromBank * authority));

            // And with the stick centred it picks itself up. ponytail: a spring with no damper,
            // which is survivable only because the rigidbody's angular damping is the damper. If
            // the wings ever start rocking, that number is the one to raise.
            if (Mathf.Abs(_roll) < 0.05f)
                _body.AddTorque(transform.forward * (bank * _selfLevel * authority));
        }
    }
}
