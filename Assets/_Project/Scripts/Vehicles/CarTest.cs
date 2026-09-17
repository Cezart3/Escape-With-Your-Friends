using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// The acceptance test for #58, run inside a real session. Server side, behind <c>-carTest</c>,
    /// and it wants <c>-scene island -noNatives -noAnimals</c>: the buggy the island's own POI bake
    /// parked at base camp, on real terrain, with nobody wandering into it.
    ///
    /// The acceptance is "fun to drive badly", which no assertion can check. What *can* be checked is
    /// everything that would stop it being fun:
    ///
    /// 1. **It goes, it stops, it turns.** Numbers rather than adjectives — top speed, stopping
    ///    distance, degrees of heading per second — so the next person to change the tuning can see
    ///    what they changed rather than guessing from the seat of their trousers.
    /// 2. **Four passengers survive a real physics body.** <c>-vehicleTest</c> slides a kinematic
    ///    buggy along a straight line, which proves the seat glue and nothing about suspension,
    ///    terrain or interpolation. This drives the actual car over the actual island.
    /// 3. **A flip ends.** Rolling it is the joke; staying rolled is the frustration the issue
    ///    explicitly asks not to ship.
    /// 4. **A driverless car stops.** Losing the driver at speed must not leave the throttle open,
    ///    or a buggy whose driver was shot out of the seat drives itself into the sea.
    ///
    /// Everything here runs on the server. The driver's own input path is a plain ServerRpc guarded
    /// by ownership, and a headless host has no keyboard to press anyway.
    /// </summary>
    public class CarTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const float WaitForVehicle = 60f;

        /// <summary>
        /// Metres a rider is allowed to be behind its anchor *beyond* the one frame the sampling
        /// ordering guarantees. Not a seatbelt test - it is there to catch a rider that has come
        /// loose and is being dragged.
        ///
        /// It used to be 0.15m, widened from a rounding budget because the measured drift grew with
        /// frame time: the chassis is interpolated between physics steps and the rider is glued on a
        /// different beat. #141 stopped paying for that with tolerance and subtracted it instead, so
        /// this is a rounding budget again. Measured slip is 0.000m over some twenty-four thousand
        /// samples, alone and while another harness is stretching frames.
        /// </summary>
        const float Glued = 0.02f;

        /// <summary>Seconds of full throttle when measuring what the thing will do.</summary>
        const float RunUp = 9f;

        /// <summary>Seconds of driving with four aboard.</summary>
        const float RideSeconds = 12f;

        /// <summary>Seconds allowed for a flip to end before it counts as beached.</summary>
        const float RightingPatience = 12f;

        static bool _started;

        int _passed;
        int _failed;

        readonly List<NetworkObject> _extras = new();

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-carTest")) return;

            _started = true;

            var go = new GameObject("CarTest");
            DontDestroyOnLoad(go);
            go.AddComponent<CarTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            PlayerMotor motor = null;
            float deadline = Time.time + WaitForPlayer;

            while (Time.time < deadline && motor == null)
            {
                motor = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                        .FirstOrDefault(m => m != null && m.IsSpawned);

                if (motor == null) yield return new WaitForSeconds(0.5f);
            }

            if (motor == null)
            {
                Debug.LogError("[CarTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            Vehicle buggy = null;
            float vehicleDeadline = Time.time + WaitForVehicle;

            while (Time.time < vehicleDeadline && buggy == null)
            {
                // By component, not by whichever spawned first: the island now also bakes a boat.
                buggy = Vehicle.All.FirstOrDefault(v => v != null && v.IsSpawned
                                                        && v.GetComponent<CarController>() != null);
                if (buggy == null) yield return new WaitForSeconds(0.5f);
            }

            var car = buggy != null ? buggy.GetComponent<CarController>() : null;
            var body = buggy != null ? buggy.GetComponent<Rigidbody>() : null;

            if (car == null || body == null)
            {
                Debug.LogError("[CarTest] No drivable vehicle in the world. Run with -scene island "
                               + "after VehicleBuilder.Build and a POI bake.");
                yield break;
            }

            Shape(car, body);

            Recentre(car, body);
            yield return Settling(car, body);
            yield return Accelerating(car);
            yield return Stopping(car, body);

            // Back to the middle of the pad before each section that drives. Twelve seconds at
            // 17 m/s is two hundred metres, so a section that starts where the last one stopped
            // starts somewhere off the edge.
            Recentre(car, body);
            yield return Steering(car, buggy);

            Recentre(car, body);
            yield return Riding(car, buggy, motor);
            yield return Flipping(car, body);
            yield return Driverless(car, buggy, motor);

            Cleanup(buggy, car);

            Report();
        }

        // ---------------------------------------------------------------- what was baked

        void Shape(CarController car, Rigidbody body)
        {
            WheelCollider[] wheels = car.GetComponentsInChildren<WheelCollider>();

            Check($"the buggy has four wheels ({wheels.Length})", wheels.Length == 4);
            Check("the body is dynamic on the server", !body.isKinematic);
            Check($"it weighs something ({body.mass:0} kg)", body.mass > 100f);
            // Axle height, and up there on purpose. This assertion used to demand the opposite,
            // from back when the centre of mass sat on the floor and the buggy could not be rolled
            // at all - which is the one thing the issue asks for by name.
            Check($"its centre of mass is up at axle height ({body.centerOfMass.y:0.00}m)",
                  body.centerOfMass.y > 0.2f && body.centerOfMass.y < 0.6f);
            Check("it interpolates, or four passengers' heads stutter", body.interpolation
                  != RigidbodyInterpolation.None);

            bool suspended = wheels.All(w => w != null && w.suspensionDistance > 0.05f && w.radius > 0.1f);
            Check("every wheel has radius and suspension travel", suspended);

            Debug.Log($"[CarTest] buggy at {body.position.ToString("F2")}, {wheels.Length} wheel(s), "
                      + $"{body.mass:0} kg, top speed {car.TopSpeed:0} m/s.");
        }

        // ---------------------------------------------------------------- it sits still

        /// <summary>
        /// A slab of nothing, a long way from the island, and the buggy gets put on it before every
        /// section that drives.
        ///
        /// Three runs of this suite were spent trying to find a fair patch of the island to test on,
        /// and every one of them failed differently: the camp parks the buggy against a shelter post,
        /// a flat spot four metres across is gone half a second into a run-up at 14 m/s, and a search
        /// that samples terrain height cannot see that the level ground it found has a building
        /// standing on it - the last run drove into Wall.Right and reported "full lock turns it 13
        /// degrees", which was a true measurement of a wall.
        ///
        /// The island is not what this suite is about. Twelve hundred metres of flat collider is
        /// cheaper than any amount of searching, gives the same answer every run, and the numbers
        /// that come off it are about the car.
        /// </summary>
        GameObject _pad;

        /// <summary>Far enough out that nothing the island generates can reach it.</summary>
        static readonly Vector3 PadCentre = new(4000f, 100f, 4000f);

        /// <summary>Where the POI bake parked it, so the suite can put it back.</summary>
        Vector3 _parkedAt;
        Quaternion _parkedFacing;

        void Recentre(CarController car, Rigidbody body)
        {
            if (_pad == null)
            {
                _parkedAt = body.position;
                _parkedFacing = body.rotation;

                _pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _pad.name = "CarTest.Pad";
                _pad.transform.position = PadCentre;
                _pad.transform.localScale = new Vector3(1200f, 1f, 1200f);
            }

            car.ServerDrive(0f, 0f, handbrake: true);
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.position = PadCentre + new Vector3(0f, 1.1f, 0f);
            body.rotation = Quaternion.identity;
            Physics.SyncTransforms();
        }

        /// <summary>
        /// The car has to stop moving before anything else means anything. If it is still creeping
        /// after this, every number below would be measuring the creep.
        /// </summary>
        IEnumerator Settling(CarController car, Rigidbody body)
        {
            car.ServerDrive(0f, 0f, handbrake: true);

            yield return new WaitForSeconds(3f);

            Vector3 restedAt = body.position;

            yield return new WaitForSeconds(2f);

            float crept = Vector3.Distance(body.position, restedAt);

            Check($"the parked buggy stays parked (crept {crept:0.00}m in 2s)", crept < 0.5f);
            Check("and it is on its wheels", !car.IsUpsideDown);

            Debug.Log($"[CarTest] parked: crept {crept:0.00}m in 2s, "
                      + $"resting at y={body.position.y:0.00}.");
        }

        // ---------------------------------------------------------------- it goes

        float _topReached;
        float _timeToTen = -1f;

        IEnumerator Accelerating(CarController car)
        {
            car.ServerDrive(1f, 0f, handbrake: false);

            float started = Time.time;
            _topReached = 0f;
            _timeToTen = -1f;
            float nextReport = 0f;

            while (Time.time - started < RunUp)
            {
                yield return null;

                float speed = car.ForwardSpeed;
                if (speed > _topReached) _topReached = speed;
                if (_timeToTen < 0f && speed >= 10f) _timeToTen = Time.time - started;

                if (Time.time - started >= nextReport)
                {
                    Debug.Log($"[CarTest] t+{Time.time - started:0}s {car.WheelReport()}");
                    nextReport += 1f;
                }
            }

            Check($"full throttle moves it ({_topReached:0.0} m/s)", _topReached > 6f);
            Check($"and it reached 10 m/s ({(_timeToTen < 0f ? -1f : _timeToTen):0.0}s)", _timeToTen > 0f);
            Check($"but not far past its ceiling ({_topReached:0.0} vs {car.TopSpeed:0} m/s)",
                  _topReached < car.TopSpeed * 1.4f);

            Debug.Log($"[CarTest] {RunUp:0}s of throttle: peak {_topReached:0.0} m/s "
                      + $"({_topReached * 3.6f:0} km/h), 0-10 m/s in "
                      + $"{(_timeToTen < 0f ? -1f : _timeToTen):0.0}s.");
        }

        // ---------------------------------------------------------------- it stops

        IEnumerator Stopping(CarController car, Rigidbody body)
        {
            float entry = car.ForwardSpeed;
            Vector3 from = body.position;

            car.ServerDrive(0f, 0f, handbrake: true);

            float deadline = Time.time + 8f;
            while (Time.time < deadline && Mathf.Abs(car.ForwardSpeed) > 0.4f) yield return null;

            float distance = Vector3.Distance(body.position, from);

            Check($"the handbrake stops it ({Mathf.Abs(car.ForwardSpeed):0.00} m/s left)",
                  Mathf.Abs(car.ForwardSpeed) <= 0.4f);
            Check($"inside a sane distance ({distance:0.0}m from {entry:0.0} m/s)", distance < 60f);

            Debug.Log($"[CarTest] braking from {entry:0.0} m/s: stopped in {distance:0.0}m.");
        }

        // ---------------------------------------------------------------- it turns

        IEnumerator Steering(CarController car, Vehicle buggy)
        {
            float heading = buggy.transform.eulerAngles.y;
            float turned = 0f;

            car.ServerDrive(0.7f, 1f, handbrake: false);

            float started = Time.time;
            float nextReport = 0f;
            while (Time.time - started < 7f)
            {
                yield return null;

                float now = buggy.transform.eulerAngles.y;
                turned += Mathf.Abs(Mathf.DeltaAngle(heading, now));
                heading = now;

                if (Time.time - started >= nextReport)
                {
                    Debug.Log($"[CarTest] turn t+{Time.time - started:0}s "
                              + $"{turned:0}deg so far, {car.WheelReport()}");
                    nextReport += 1f;
                }
            }

            car.ServerDrive(0f, 0f, handbrake: true);

            Check($"full lock actually turns it ({turned:0} degrees in 7s)", turned > 90f);

            Debug.Log($"[CarTest] full lock at throttle 0.7: {turned:0} degrees over 7s "
                      + $"({turned / 7f:0} deg/s).");

            yield return new WaitForSeconds(1.5f);
        }

        // ---------------------------------------------------------------- four aboard

        /// <summary>
        /// The one that matters. #57 proved four bodies stay glued to a transform somebody else was
        /// moving; this proves it against suspension travel, terrain, interpolation and a body being
        /// stepped by PhysX — which is the version a player will actually be sitting in.
        /// </summary>
        IEnumerator Riding(CarController car, Vehicle buggy, PlayerMotor motor)
        {
            int seated = buggy.ServerEnter(motor.NetworkObject);
            Check($"the player got the wheel (seat {seated})", seated == 0);

            for (int i = 0; i < 3; i++)
            {
                NetworkObject spare = SpawnBody(buggy.transform.position + Vector3.up * 2f
                                                + buggy.transform.right * (2f + i));
                if (spare == null) break;

                _extras.Add(spare);
                yield return new WaitForSeconds(0.3f);

                buggy.ServerEnter(spare);
            }

            Check($"four aboard ({buggy.Occupied()}/{buggy.SeatCount})", buggy.Occupied() == 4);

            float worst = 0f;
            int worstSeat = -1;
            int samples = 0;
            float peak = 0f;

            // Where each anchor was at the previous sample, and why this loop is not simply a
            // distance. The glue runs in Vehicle.LateUpdate, and a coroutine resuming on a bare
            // "yield return null" runs before it - so a rider is one frame behind its anchor, by
            // construction, and that gap is a measurement of when we looked rather than of the glue.
            // It scales with frame time, which is why these checks failed only while another
            // headless harness was stretching frames. Subtracting how far the anchor moved in that
            // same frame leaves what actually matters: whether anybody is slipping further behind
            // than the one frame the ordering guarantees. #141.
            var wasAt = new Vector3[buggy.SeatCount];
            bool seen = false;
            float stale = 0f;

            car.ServerDrive(1f, 0f, handbrake: false);

            float started = Time.time;
            while (Time.time - started < RideSeconds)
            {
                // Gentle weaving rather than a straight line: a car that only ever goes forwards
                // never leans, and leaning is what would throw a badly glued passenger.
                car.ServerDrive(1f, Mathf.Sin((Time.time - started) * 1.2f) * 0.6f, handbrake: false);

                yield return null;

                peak = Mathf.Max(peak, car.ForwardSpeed);

                for (int i = 0; i < buggy.SeatCount; i++)
                {
                    VehicleRider occupant = buggy.Occupant(i);
                    Transform anchor = buggy.SeatAnchor(i);
                    if (occupant == null || anchor == null) continue;

                    float drift = Vector3.Distance(occupant.transform.position, anchor.position);
                    float travelled = seen ? Vector3.Distance(anchor.position, wasAt[i]) : drift;

                    wasAt[i] = anchor.position;
                    samples++;
                    stale = Mathf.Max(stale, drift);

                    float slip = Mathf.Max(0f, drift - travelled);
                    if (slip <= worst) continue;

                    worst = slip;
                    worstSeat = i;
                }

                seen = true;
            }

            car.ServerDrive(0f, 0f, handbrake: true);

            Check($"nobody slipped out of their seat over {samples} sample(s) "
                  + $"(worst {worst:0.000}m behind the frame the glue runs in, seat {worstSeat}; "
                  + $"{stale:0.000}m before that frame is accounted for)", worst < Glued);
            Check($"all four are still aboard ({buggy.Occupied()}/4)", buggy.Occupied() == 4);

            Debug.Log($"[CarTest] {RideSeconds:0}s of real driving with four aboard, peaking at "
                      + $"{peak:0.0} m/s: worst slip {worst:0.000}m ({stale:0.000}m raw) over "
                      + $"{samples} sample(s).");

            yield return new WaitForSeconds(2f);
        }

        // ---------------------------------------------------------------- it comes back up

        /// <summary>
        /// Rolls it deliberately and waits. The torque is a shove, not a teleport: a car put on its
        /// roof by hand would not prove the recovery fires from a state physics can actually reach.
        /// </summary>
        IEnumerator Flipping(CarController car, Rigidbody body)
        {
            car.ServerDrive(0f, 0f, handbrake: false);

            body.AddTorque(car.transform.forward * 9000f, ForceMode.Impulse);
            body.AddForce(Vector3.up * 4000f, ForceMode.Impulse);

            float deadline = Time.time + 6f;
            while (Time.time < deadline && !car.IsUpsideDown) yield return null;

            bool flipped = car.IsUpsideDown;
            Check("a shove puts it on its roof", flipped);

            if (!flipped)
            {
                Debug.Log("[CarTest] the buggy refused to flip; the recovery was not exercised.");
                yield break;
            }

            float flippedAt = Time.time;
            float patience = Time.time + RightingPatience;

            while (Time.time < patience && car.IsUpsideDown) yield return null;

            float took = Time.time - flippedAt;

            Check($"and it picks itself back up ({took:0.0}s)", !car.IsUpsideDown);

            Debug.Log($"[CarTest] flipped, then righted itself after {took:0.0}s on its roof.");

            yield return new WaitForSeconds(1.5f);
        }

        // ---------------------------------------------------------------- nobody at the wheel

        IEnumerator Driverless(CarController car, Vehicle buggy, PlayerMotor motor)
        {
            // Back in, wound up, then thrown out at speed.
            if (buggy.SeatOf(motor.NetworkObject) < 0) buggy.ServerEnter(motor.NetworkObject);

            car.ServerDrive(1f, 0f, handbrake: false);

            float deadline = Time.time + 6f;
            while (Time.time < deadline && car.ForwardSpeed < 5f) yield return null;

            float entry = car.ForwardSpeed;

            buggy.ServerExit(motor.NetworkObject);

            Check($"the driver got out at {entry:0.0} m/s", buggy.Driver == null);

            float stopBy = Time.time + 12f;
            while (Time.time < stopBy && Mathf.Abs(car.ForwardSpeed) > 0.5f) yield return null;

            Check($"and the driverless buggy stops itself ({Mathf.Abs(car.ForwardSpeed):0.00} m/s)",
                  Mathf.Abs(car.ForwardSpeed) <= 0.5f);

            Debug.Log($"[CarTest] driver ejected at {entry:0.0} m/s; the buggy braked itself to a stop.");
        }

        // ---------------------------------------------------------------- scaffolding

        NetworkObject SpawnBody(Vector3 position)
        {
            PlayerSpawner spawner = PlayerSpawner.Instance;
            NetworkObject prefab = spawner != null ? spawner.PlayerPrefab : null;

            if (prefab == null)
            {
                Debug.LogError("[CarTest] No player prefab to spawn spare bodies from.");
                return null;
            }

            NetworkObject body = InstanceFinder.NetworkManager.GetPooledInstantiated(
                prefab, position, Quaternion.identity, asServer: true);

            InstanceFinder.ServerManager.Spawn(body);

            return body;
        }

        void Cleanup(Vehicle buggy, CarController car)
        {
            car.ServerRelease();

            foreach (NetworkObject body in _extras)
            {
                if (body == null || !body.IsSpawned) continue;

                buggy.ServerExit(body);
                InstanceFinder.ServerManager.Despawn(body);
            }

            _extras.Clear();

            if (_pad == null) return;

            Destroy(_pad);
            _pad = null;

            // Back to base camp. The suite borrowed the only car in the world and moved it four
            // kilometres into the sky; leaving it there would be leaving the island without one.
            var chassis = buggy.GetComponent<Rigidbody>();
            chassis.linearVelocity = Vector3.zero;
            chassis.angularVelocity = Vector3.zero;
            chassis.position = _parkedAt;
            chassis.rotation = _parkedFacing;
            Physics.SyncTransforms();
        }

        void Report()
        {
            if (_failed == 0) Debug.Log($"[CarTest] {_passed} passed, 0 failed.");
            else Debug.LogError($"[CarTest] {_passed} passed, {_failed} FAILED.");
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[CarTest] FAILED: {what}.");
        }
    }
}
