using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// Makes a <see cref="Vehicle"/> drive. Four <see cref="WheelCollider"/>s, deliberately arcade
    /// tuning, and no pretence of being a simulator.
    ///
    /// **The host simulates and everybody else watches.** The wheels and the Rigidbody only run on
    /// the server; every other peer holds the body kinematic and lets the NetworkTransform place it.
    /// A car simulated on four machines at once is four cars that disagree, and the disagreement
    /// arrives as a passenger being flung through a hill — which is exactly the failure #57 was
    /// written to make impossible.
    ///
    /// The driver's client therefore sends *input*, not motion, and only when the input changes:
    /// throttle, steering and handbrake quantised to a tenth. A keyboard produces about four of those
    /// a second and a stick a few more, which is cheaper than a fixed stream at tick rate and simpler
    /// than anything predicted. Nothing here is rolled back, because nothing here is authoritative on
    /// the client in the first place.
    ///
    /// **Flipping is a feature.** The centre of mass is low enough that the thing corners without
    /// tipping at the first turn and high enough that a ramp, a rock or a friend will put it on its
    /// roof. What must not be a feature is *staying* flipped, so a car that has been upside down and
    /// stationary for a few seconds rights itself with a hop. That is the whole recovery: no button,
    /// no prompt, nothing to discover.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class CarController : NetworkBehaviour
    {
        /// <summary>
        /// Wheels in the order the generator writes them: front-left, front-right, rear-left,
        /// rear-right. The first two steer, the last two drive. An index convention rather than an axle
        /// class because the array is written by <see cref="EditorTools.VehicleBuilder"/> and read
        /// here, and those are the only two places that will ever care.
        /// </summary>
        [SerializeField] WheelCollider[] _wheels = new WheelCollider[4];

        /// <summary>The cylinders you actually see, in the same order.</summary>
        [SerializeField] Transform[] _wheelVisuals = new Transform[4];

        [Tooltip("Nm per wheel at full throttle. All four drive: a rear-drive buggy spins out of "
                 + "every corner, which is funny twice.")]
        [SerializeField] float _motorTorque = 900f;

        [Tooltip("Nm per wheel when braking or handbraking.")]
        [SerializeField] float _brakeTorque = 3000f;

        [Tooltip("Metres per second. Torque is cut above this rather than the body being clamped, so "
                 + "a hill can still overspeed it and gravity stays in charge.")]
        [SerializeField] float _topSpeed = 22f;

        [Tooltip("Fraction of top speed available in reverse.")]
        [SerializeField] float _reverseFraction = 0.35f;

        [Tooltip("Degrees of steering lock when stopped.")]
        [SerializeField] float _steerAtRest = 34f;

        [Tooltip("Degrees of steering lock at top speed. Lower, or a twitch at 80km/h is a barrel "
                 + "roll rather than a lane change.")]
        [SerializeField] float _steerAtSpeed = 11f;

        [Tooltip("Local centre of mass. Roughly axle height: low enough to corner, high enough that "
                 + "a shove or a kerb will roll it, which is the joke this vehicle exists to tell.")]
        [SerializeField] Vector3 _centreOfMass = new(0f, 0.35f, 0.1f);

        [Tooltip("Seconds upside down and stationary before the car picks itself up.")]
        [SerializeField] float _rightingDelay = 3f;

        [Tooltip("Upward nudge given when righting, so the roll does not start inside the ground.")]
        [SerializeField] float _rightingLift = 0.8f;

        Rigidbody _body;
        Vehicle _vehicle;

        /// <summary>Latest input the server is acting on. -1..1, -1..1, held.</summary>
        float _throttle, _steer;
        bool _handbrake;

        /// <summary>What the owner last sent, quantised, so an unchanged frame sends nothing.</summary>
        int _sentThrottle = int.MinValue, _sentSteer = int.MinValue;
        bool _sentHandbrake;

        /// <summary>How long the car has been on its roof and not going anywhere.</summary>
        float _upsideDownFor;

        /// <summary>Whether somebody was in seat 0 last step. See the release in FixedUpdate.</summary>
        bool _hadDriver;

        /// <summary>Hardest thing pushing back on the chassis since the last report, and how hard.</summary>
        string _hitName = "nothing";
        float _hitForce;
        Vector3 _hitPush;
        float _hitHeight;

        /// <summary>
        /// Steering angle, for the front wheels you can see. A SyncVar rather than something derived,
        /// because a client cannot work out which way the wheels are pointed from a body that is
        /// being placed for it — and wheels that do not turn make a car feel like a sled.
        /// </summary>
        readonly SyncVar<float> _shownSteer = new();

        /// <summary>Metres per second along the car's own forward. Negative is reversing.</summary>
        public float ForwardSpeed => _body != null ? Vector3.Dot(_body.linearVelocity, transform.forward) : 0f;

        /// <summary>True while the thing is closer to its roof than to its wheels.</summary>
        public bool IsUpsideDown => Vector3.Dot(transform.up, Vector3.up) < 0.1f;

        public float TopSpeed => _topSpeed;

        /// <summary>
        /// What the wheels are actually doing, for the harness. A car that will not accelerate is
        /// either not touching the ground, not being given torque, or spinning its wheels, and
        /// those three look identical from the outside.
        /// </summary>
        public string WheelReport()
        {
            int grounded = 0;
            float rpm = 0f, slip = 0f, compression = 0f, ground = 0f;

            // Read back off the colliders rather than trusting the fields that were written to
            // them. "The motor is at 900Nm" is a statement about intent; what PhysX integrates
            // is whatever is on the WheelCollider at the end of the step, and those two have no
            // obligation to agree.
            float motorSet = 0f, brakeSet = 0f, steerSet = 0f;

            foreach (WheelCollider wheel in _wheels)
            {
                if (wheel == null) continue;
                rpm += wheel.rpm;
                motorSet += wheel.motorTorque;
                brakeSet += wheel.brakeTorque;
                steerSet = Mathf.Max(steerSet, Mathf.Abs(wheel.steerAngle));

                if (!wheel.GetGroundHit(out WheelHit hit)) continue;
                grounded++;
                slip += Mathf.Abs(hit.forwardSlip);
                compression += hit.force;
                ground += hit.point.y;
            }

            // Is the chassis resting on the ground instead of on its wheels? That is the one
            // failure that looks exactly like "no traction" from every other angle: the wheels
            // are down, they are loaded, they are turning, and the car goes nowhere.
            var chassis = GetComponentInChildren<BoxCollider>();
            float belly = chassis != null ? chassis.bounds.min.y : float.NaN;
            float contact = grounded > 0 ? ground / grounded : float.NaN;

            // Whatever is actually holding the car, named and measured. From real contacts rather
            // than an overlap query: a rotated box has an axis-aligned bounds half again its own
            // size, so that query names every crate parked nearby and proves nothing. Only an
            // impulse can hold a car still against its own wheels.
            string touching = $"{_hitName} at {_hitForce:0}N, push {_hitPush.ToString("F0")}, "
                              + $"contact y {_hitHeight:0.00}";
            _hitName = "nothing";
            _hitForce = 0f;
            _hitPush = Vector3.zero;
            _hitHeight = float.NaN;

            return $"{(_body.IsSleeping() ? "ASLEEP" : "awake")}, "
                   + $"{grounded}/4 grounded, {rpm / 4f:0} rpm avg, slip {slip:0.00}, "
                   + $"load {compression:0}N of {_body.mass * -Physics.gravity.y:0}N, "
                   + $"motor {_throttle * _motorTorque:0}Nm wanted / {motorSet / 2f:0}Nm set, "
                   + $"brake {brakeSet / 4f:0}Nm set, steer {_steer:0.00} -> {steerSet:0}deg set, "
                   + $"speed {ForwardSpeed:0.0} m/s, "
                   + $"belly {belly:0.00} vs contact {contact:0.00} "
                   + $"(clearance {belly - contact:0.00}m), "
                   + $"vel {_body.linearVelocity}, ang {_body.angularVelocity}, "
                   + $"constraints {_body.constraints}, "
                   + $"touching [{touching}]";
        }

        void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _vehicle = GetComponent<Vehicle>();

            _body.centerOfMass = _centreOfMass;
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            // Only the host runs the physics. Everywhere else the NetworkTransform writes the
            // transform outright, and a dynamic body underneath it would spend every frame fighting
            // a value it cannot win against - producing jitter on the car and, through the seat
            // glue, on four passengers.
            if (IsServerStarted) return;

            _body.isKinematic = true;

            foreach (WheelCollider wheel in _wheels)
                if (wheel != null) wheel.enabled = false;
        }

        // ---------------------------------------------------------------- input

        /// <summary>
        /// Owner side, every frame. <paramref name="throttle"/> and <paramref name="steer"/> are
        /// -1..1; <paramref name="handbrake"/> is held. Sends only when the quantised value moves, so
        /// holding W costs one packet rather than thirty a second.
        /// </summary>
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
        /// takes ownership of the vehicle in <see cref="Vehicle.ServerEnter"/> and loses it on the
        /// way out, so a passenger calling this is refused by FishNet before it reaches here.
        /// </summary>
        [ServerRpc]
        void ServerDrive(sbyte throttle, sbyte steer, bool handbrake)
        {
            _throttle = Mathf.Clamp(throttle / 10f, -1f, 1f);
            _steer = Mathf.Clamp(steer / 10f, -1f, 1f);
            _handbrake = handbrake;
        }

        /// <summary>
        /// Server side, for anything that drives without a keyboard: the harness, and eventually an
        /// AI or a scripted chase. Same values, same units, no RPC.
        /// </summary>
        public void ServerDrive(float throttle, float steer, bool handbrake)
        {
            if (!IsServerStarted) return;

            _throttle = Mathf.Clamp(throttle, -1f, 1f);
            _steer = Mathf.Clamp(steer, -1f, 1f);
            _handbrake = handbrake;
        }

        /// <summary>Drops the throttle. Called when the driver's seat empties.</summary>
        public void ServerRelease()
        {
            if (!IsServerStarted) return;

            _throttle = 0f;
            _steer = 0f;
            _handbrake = true;
        }

        // ---------------------------------------------------------------- driving

        /// <summary>
        /// Records the hardest contact for <see cref="WheelReport"/>. A CharacterController or a
        /// scenery prop has no Rigidbody, which makes it infinitely heavy as far as PhysX is
        /// concerned - a car wedged against one can spin all four wheels forever and never move.
        /// </summary>
        void OnCollisionStay(Collision other)
        {
            float force = other.impulse.magnitude / Time.fixedDeltaTime;
            if (force <= _hitForce) return;

            _hitForce = force;
            _hitName = other.gameObject.name;

            // Direction matters more than magnitude. Straight up means the car is beached on its
            // belly; sideways means it is nose-into a bank. Those two need opposite fixes and are
            // indistinguishable from the magnitude alone.
            _hitPush = other.impulse / Time.fixedDeltaTime;
            _hitHeight = other.contactCount > 0 ? other.GetContact(0).point.y : float.NaN;
        }

        void FixedUpdate()
        {
            if (!IsServerStarted) return;

            // Losing the driver drops the throttle and puts the handbrake on, once. A buggy that keeps
            // its last throttle because its driver was shot out of the seat is a funny idea exactly
            // once, and after that it is a buggy somewhere in the sea.
            //
            // On the transition rather than every frame, because "no driver" is also the state of a
            // car being driven by something that is not a person - the harness now, an AI or a
            // scripted chase later - and a guard that re-braked every step would make that
            // impossible rather than merely unusual.
            bool driven = _vehicle == null || _vehicle.Driver != null;

            if (_hadDriver && !driven) ServerRelease();
            _hadDriver = driven;

            float speed = ForwardSpeed;
            float steerLock = Mathf.Lerp(_steerAtRest, _steerAtSpeed,
                                         Mathf.InverseLerp(0f, _topSpeed, Mathf.Abs(speed)));

            float steerAngle = _steer * steerLock;
            _shownSteer.Value = steerAngle;

            // Throttle against motion is a brake, not reverse. Without this, tapping S at speed puts
            // the wheels in reverse under a car still doing 20m/s, which locks them and turns a stop
            // into a slide.
            bool braking = _handbrake
                           || (_throttle > 0.05f && speed < -0.5f)
                           || (_throttle < -0.05f && speed > 0.5f);

            float ceiling = _throttle >= 0f ? _topSpeed : _topSpeed * _reverseFraction;
            bool overspeed = Mathf.Abs(speed) > ceiling && Mathf.Sign(speed) == Mathf.Sign(_throttle);

            float motor = braking || overspeed ? 0f : _throttle * _motorTorque;
            float brake = braking ? _brakeTorque : 0f;

            // Motor torque does not wake a sleeping Rigidbody. PhysX puts the chassis to sleep
            // after a few seconds parked, the wheels keep spinning against a body that is not
            // being integrated, and the car sits there with all four wheels turning and slipping
            // and nothing moving. Anything that asks the car to move has to wake it first.
            if (motor != 0f || _steer != 0f) _body.WakeUp();

            for (int i = 0; i < _wheels.Length; i++)
            {
                WheelCollider wheel = _wheels[i];
                if (wheel == null) continue;

                // The rear wheels drive and the front wheels steer, and they do not share the job.
                // With torque on all four, the front tyres spent their whole friction budget spinning
                // forwards and had none left to turn with - full lock for seven seconds moved the
                // nose 64 degrees, which is a car ploughing straight ahead with the wheels crooked.
                // Rear drive also hands it a tail that steps out, which is the entire point.
                wheel.motorTorque = i < 2 ? 0f : motor;
                wheel.brakeTorque = brake;

                // Index 0 and 1 are the front pair. See the field comment.
                if (i < 2) wheel.steerAngle = steerAngle;
            }

            Right(Time.fixedDeltaTime);
        }

        /// <summary>
        /// Puts a beached car back on its wheels. Only when it is both upside down and going nowhere,
        /// so a barrel roll that is still in progress is left alone to finish - the flip is the joke,
        /// and cutting it short would be taking the joke away.
        /// </summary>
        void Right(float dt)
        {
            if (!IsUpsideDown || _body.linearVelocity.sqrMagnitude > 1f)
            {
                _upsideDownFor = 0f;
                return;
            }

            _upsideDownFor += dt;
            if (_upsideDownFor < _rightingDelay) return;

            _upsideDownFor = 0f;

            // Keep the heading, lose the roll and the pitch. Rotating about the world up rather than
            // resetting to identity means the car points the way it was pointing, which is usually
            // the way out of wherever it landed.
            Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.ProjectOnPlane(transform.up, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;

            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;

            // Assigned rather than moved: MovePosition and MoveRotation are an interpolated sweep
            // meant for kinematic bodies, and this body is dynamic. A sweep would drag the chassis
            // through whatever it is lying against on the way up.
            _body.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
            _body.position += Vector3.up * _rightingLift;

            // Seats moved, and the riders are glued to them in LateUpdate off a transform physics has
            // not published yet. Without this the passengers spend one frame where the car used to be.
            Physics.SyncTransforms();

            Debug.Log($"[Car] {name} righted itself after {_rightingDelay:0.#}s on its roof.");
        }

        // ---------------------------------------------------------------- visuals

        void Update()
        {
            if (_wheelVisuals == null) return;

            // Derived rather than read off the WheelCollider, because the collider only turns on the
            // host - everywhere else the body is kinematic and its wheels are switched off. Speed
            // over radius is the same number the collider would have produced anyway, and it works
            // on a peer that is only being told where the car is.
            float radius = _wheels.Length > 0 && _wheels[0] != null ? _wheels[0].radius : 0.45f;
            float spin = ForwardSpeedVisual() / Mathf.Max(0.05f, radius) * Mathf.Rad2Deg * Time.deltaTime;

            for (int i = 0; i < _wheelVisuals.Length; i++)
            {
                Transform visual = _wheelVisuals[i];
                if (visual == null) continue;

                // The cylinders are laid on their side by a 90 degree roll, so the axle is local Y
                // and the steering is a yaw applied on top of it.
                float steer = i < 2 ? _shownSteer.Value : 0f;

                visual.localRotation = Quaternion.Euler(0f, 0f, 90f)
                                       * Quaternion.Euler(0f, steer, 0f)
                                       * Quaternion.Euler(0f, 0f, 0f);

                visual.Rotate(Vector3.up, spin, Space.Self);
            }
        }

        Vector3 _lastVisualPosition;
        bool _hasVisualPosition;

        /// <summary>
        /// Forward speed measured from the transform, so it also works on a peer whose body is
        /// kinematic and is being placed by the NetworkTransform.
        /// </summary>
        float ForwardSpeedVisual()
        {
            Vector3 here = transform.position;

            if (!_hasVisualPosition)
            {
                _lastVisualPosition = here;
                _hasVisualPosition = true;
                return 0f;
            }

            Vector3 delta = here - _lastVisualPosition;
            _lastVisualPosition = here;

            return Time.deltaTime > 0f ? Vector3.Dot(delta, transform.forward) / Time.deltaTime : 0f;
        }

        /// <summary>Editor-time wiring. See <see cref="EditorTools.VehicleBuilder"/>.</summary>
        public void Configure(WheelCollider[] wheels, Transform[] visuals)
        {
            _wheels = wheels;
            _wheelVisuals = visuals;
        }
    }
}
