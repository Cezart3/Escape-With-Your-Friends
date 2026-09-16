using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.World;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// The acceptance test for #59, run inside a real session. Server side, behind <c>-boatTest</c>,
    /// and it wants <c>-scene island -noNatives -noAnimals</c>.
    ///
    /// The acceptance is "stable in waves, does not jitter or sink through the water plane", and
    /// unlike "fun to drive badly" that is a thing a machine can actually check. Three numbers say
    /// all of it, and they are all measured against the sea *under the hull* rather than against a
    /// flat sea level, because the sea is not flat:
    ///
    /// 1. **Freeboard.** Deck above the water and keel below it, continuously. A deck that goes under
    ///    is sinking; a keel that comes out is flying.
    /// 2. **Heave speed.** How fast the hull moves up and down. A 62cm sea with a ten second period
    ///    moves a floating thing at about 0.2 m/s. Metres per second is a spring that is not damped,
    ///    which is exactly the failure this issue names.
    /// 3. **Heel.** How far it leans. Buoyancy acting above a low centre of mass is a righting
    ///    couple, so this is the check that the couple is the right way up.
    ///
    /// Everything runs out at (2000, 2000), a long way off the island. Same lesson #58 learned the
    /// expensive way: a suite that tests on the world is a suite that measures the world. Open sea is
    /// free — there is nothing to build, because the water is a function rather than a collider.
    /// </summary>
    public class BoatTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const float WaitForVehicle = 60f;

        /// <summary>Metres a rider is allowed to be from its anchor. Same budget as the buggy's.</summary>
        const float Glued = 0.15f;

        /// <summary>Height of the deck above the keel, matching <c>BoatBuilder.DeckHeight</c>.</summary>
        const float DeckHeight = 1f;

        /// <summary>Seconds adrift in the waves while the freeboard and heave are watched.</summary>
        const float AdriftSeconds = 20f;

        /// <summary>Seconds of full throttle when measuring what the thing will do.</summary>
        const float RunUp = 12f;

        /// <summary>Seconds of driving with four aboard.</summary>
        const float RideSeconds = 12f;

        /// <summary>Open water, far enough out that no part of the island can reach it.</summary>
        static readonly Vector3 OpenSea = new(2000f, 0f, 2000f);

        static bool _started;

        int _passed;
        int _failed;

        Vector3 _mooredAt;
        Quaternion _mooredFacing;
        bool _moored;

        readonly List<NetworkObject> _extras = new();

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-boatTest")) return;

            _started = true;

            var go = new GameObject("BoatTest");
            DontDestroyOnLoad(go);
            go.AddComponent<BoatTest>();
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
                Debug.LogError("[BoatTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            Vehicle hull = null;
            float vehicleDeadline = Time.time + WaitForVehicle;

            while (Time.time < vehicleDeadline && hull == null)
            {
                hull = Vehicle.All.FirstOrDefault(v => v != null && v.IsSpawned
                                                       && v.GetComponent<BoatController>() != null);
                if (hull == null) yield return new WaitForSeconds(0.5f);
            }

            var boat = hull != null ? hull.GetComponent<BoatController>() : null;
            var body = hull != null ? hull.GetComponent<Rigidbody>() : null;

            if (boat == null || body == null)
            {
                Debug.LogError("[BoatTest] No boat in the world. Run with -scene island after "
                               + "BoatBuilder.Build and a POI bake.");
                yield break;
            }

            // #69 changed what a boat is when nobody has paid for it. This suite measures a hull in
            // clean water two kilometres out, which is both a boat that cannot be boarded and a boat
            // that is well past the edge of the map, so: hand it its parts, and hold the crossing.
            BoatVoyage.Sailing = false;
            var voyage = hull.GetComponent<BoatVoyage>();
            if (voyage != null) voyage.ServerGrant();

            Shape(boat, body, hull);

            // From the mooring first, because "it floats up off the seabed it was baked onto" is the
            // one thing that has to work where the bake actually put it.
            yield return Surfacing(boat, body);

            Recentre(boat, body);
            yield return Adrift(boat, body);

            Recentre(boat, body);
            yield return Driving(boat);

            Recentre(boat, body);
            yield return Turning(boat, hull);

            Recentre(boat, body);
            yield return Swamped(boat, body);

            Recentre(boat, body);
            yield return Riding(boat, hull, motor);
            yield return Driverless(boat, hull, motor);

            Cleanup(hull, boat, body);

            BoatVoyage.Sailing = true;

            Report();
        }

        // ---------------------------------------------------------------- what it is

        void Shape(BoatController boat, Rigidbody body, Vehicle hull)
        {
            _mooredAt = body.position;
            _mooredFacing = body.rotation;
            _moored = true;

            Check($"the boat has floats to sample with ({boat.FloatCount})", boat.FloatCount >= 4);
            Check($"four seats ({hull.SeatCount})", hull.SeatCount == 4);
            Check($"and a hull with mass ({body.mass:0} kg)", body.mass > 1f);

            Debug.Log($"[BoatTest] boat at {body.position.ToString("F2")}, {boat.FloatCount} float(s), "
                      + $"{body.mass:0} kg, top speed {boat.TopSpeed:0} m/s.");
        }

        /// <summary>
        /// Out to open water. No pad and no search: the sea is a function of x, z and time, so there
        /// is nowhere it is not, and two kilometres out there is nothing else.
        /// </summary>
        void Recentre(BoatController boat, Rigidbody body)
        {
            boat.ServerDrive(0f, 0f, handbrake: true);
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.position = new Vector3(OpenSea.x, WaterSurface.HeightAt(OpenSea.x, OpenSea.z) - 0.35f,
                                        OpenSea.z);
            body.rotation = Quaternion.identity;
            Physics.SyncTransforms();
        }

        /// <summary>Metres the deck clears the water by, right underneath the hull.</summary>
        static float Freeboard(Rigidbody body)
            => body.position.y + DeckHeight - WaterSurface.HeightAt(body.position);

        // ---------------------------------------------------------------- it comes up

        /// <summary>
        /// The bake puts the boat on the seabed a metre or so under, because a POI is placed on the
        /// ground and the ground here is underwater. Buoyancy is what decides a boat's waterline, so
        /// this is the first thing to prove: left alone, it surfaces and stays there.
        /// </summary>
        IEnumerator Surfacing(BoatController boat, Rigidbody body)
        {
            float from = body.position.y;
            float sea = WaterSurface.HeightAt(body.position);

            boat.ServerDrive(0f, 0f, handbrake: true);

            float deadline = Time.time + 20f;
            while (Time.time < deadline && Mathf.Abs(body.linearVelocity.y) > 0.25f) yield return null;

            yield return new WaitForSeconds(2f);

            float board = Freeboard(body);

            Check($"the boat floated up off its mooring (from y {from:0.00} to {body.position.y:0.00}, "
                  + $"sea {sea:0.00})", boat.IsAfloat);

            Check($"with its deck out of the water (freeboard {board:0.00}m)", board > 0.1f);
            Check($"and its keel still in it (keel {DeckHeight - board:0.00}m under)",
                  board < DeckHeight);

            Debug.Log($"[BoatTest] moored on the seabed at y {from:0.00}, floated to {body.position.y:0.00}: "
                      + $"{boat.HullReport()}.");
        }

        // ---------------------------------------------------------------- it sits in the waves

        /// <summary>
        /// The acceptance, in one coroutine. Twenty seconds adrift with the engine off, watching the
        /// three numbers that distinguish a boat from a bug: freeboard, heave speed and heel.
        ///
        /// The sea's longest wave has a ten second period and a 34cm amplitude, so a properly damped
        /// hull moves up and down at roughly a fifth of a metre a second. Anything measured in whole
        /// metres per second is an underdamped spring, which is the failure mode this test exists for
        /// and the reason the damper in <see cref="BoatController"/> is derived rather than guessed.
        /// </summary>
        IEnumerator Adrift(BoatController boat, Rigidbody body)
        {
            boat.ServerDrive(0f, 0f, handbrake: true);

            yield return new WaitForSeconds(3f);

            float lowest = float.MaxValue, highest = float.MinValue;
            float heave = 0f, heel = 0f;
            int samples = 0;

            float started = Time.time;
            float nextReport = 0f;

            while (Time.time - started < AdriftSeconds)
            {
                yield return null;

                float board = Freeboard(body);
                lowest = Mathf.Min(lowest, board);
                highest = Mathf.Max(highest, board);
                heave = Mathf.Max(heave, Mathf.Abs(body.linearVelocity.y));
                heel = Mathf.Max(heel, Vector3.Angle(body.transform.up, Vector3.up));
                samples++;

                if (Time.time - started < nextReport) continue;

                Debug.Log($"[BoatTest] adrift t+{Time.time - started:0}s {boat.HullReport()}");
                nextReport += 5f;
            }

            Check($"the deck never went under over {samples} sample(s) (least freeboard {lowest:0.00}m)",
                  lowest > 0f);

            Check($"the keel never came out (most freeboard {highest:0.00}m of {DeckHeight:0.00}m)",
                  highest < DeckHeight);

            Check($"it rides the waves instead of bouncing on them (heave {heave:0.00} m/s)",
                  heave < 1f);

            Check($"and stays roughly upright (heel {heel:0.0} degrees)", heel < 25f);

            Debug.Log($"[BoatTest] {AdriftSeconds:0}s adrift: freeboard {lowest:0.00}-{highest:0.00}m, "
                      + $"heave up to {heave:0.00} m/s, heel up to {heel:0.0} degrees.");
        }

        // ---------------------------------------------------------------- it goes

        IEnumerator Driving(BoatController boat)
        {
            yield return new WaitForSeconds(2f);

            float peak = 0f;
            boat.ServerDrive(1f, 0f, handbrake: false);

            float started = Time.time;
            float nextReport = 0f;

            while (Time.time - started < RunUp)
            {
                yield return null;
                peak = Mathf.Max(peak, boat.ForwardSpeed);

                if (Time.time - started < nextReport) continue;

                Debug.Log($"[BoatTest] t+{Time.time - started:0}s {boat.HullReport()}");
                nextReport += 3f;
            }

            boat.ServerDrive(0f, 0f, handbrake: true);

            Check($"full throttle moves it ({peak:0.0} m/s)", peak > 5f);
            Check($"and not faster than it is meant to go ({peak:0.0} of {boat.TopSpeed:0} m/s)",
                  peak < boat.TopSpeed * 1.4f);

            // Drag is the only brake out here, so this is how long the wake takes to die.
            float coastFrom = boat.ForwardSpeed;
            float stopBy = Time.time + 20f;
            while (Time.time < stopBy && Mathf.Abs(boat.ForwardSpeed) > 0.5f) yield return null;

            Check($"and it coasts to a stop with the engine off ({Mathf.Abs(boat.ForwardSpeed):0.00} m/s)",
                  Mathf.Abs(boat.ForwardSpeed) <= 0.5f);

            Debug.Log($"[BoatTest] {RunUp:0}s of throttle: peak {peak:0.0} m/s ({peak * 3.6f:0} km/h); "
                      + $"coasted down from {coastFrom:0.0} m/s in {20f - (stopBy - Time.time):0.0}s.");
        }

        // ---------------------------------------------------------------- it turns

        /// <summary>
        /// Full lock under power. The rudder only bites with water moving past it, so this is a
        /// measurement of the flow term as much as of the torque: a boat that spun on the spot from
        /// a standstill would mean that term was wired wrong.
        /// </summary>
        IEnumerator Turning(BoatController boat, Vehicle hull)
        {
            yield return new WaitForSeconds(2f);

            boat.ServerDrive(0.8f, 0f, handbrake: false);

            float running = Time.time + 6f;
            while (Time.time < running && boat.ForwardSpeed < 5f) yield return null;

            float heading = hull.transform.eulerAngles.y;
            float turned = 0f;

            boat.ServerDrive(0.8f, 1f, handbrake: false);

            float started = Time.time;
            float nextReport = 0f;

            while (Time.time - started < 8f)
            {
                yield return null;

                float now = hull.transform.eulerAngles.y;
                turned += Mathf.Abs(Mathf.DeltaAngle(heading, now));
                heading = now;

                if (Time.time - started < nextReport) continue;

                Debug.Log($"[BoatTest] turn t+{Time.time - started:0}s {turned:0}deg so far, "
                          + $"{boat.HullReport()}");
                nextReport += 2f;
            }

            boat.ServerDrive(0f, 0f, handbrake: true);

            Check($"full lock actually turns it ({turned:0} degrees in 8s)", turned > 90f);

            // The ceiling matters as much as the floor. A hull with no directional stability spins
            // on the spot instead of carving, which passes "does it turn" and is not a boat.
            Check($"and turns rather than pirouettes ({turned / 8f:0} deg/s)", turned / 8f < 90f);

            Debug.Log($"[BoatTest] full lock at throttle 0.8: {turned:0} degrees over 8s "
                      + $"({turned / 8f:0} deg/s).");
        }

        // ---------------------------------------------------------------- it comes back up

        /// <summary>
        /// Shoves the whole hull four metres under and watches it return. This is the clamp on the
        /// buoyant force, tested: without it, four metres under a 0.7m draft would be nearly six
        /// times the boat's weight in lift and the thing would leave the sea like a cork from a
        /// bottle, which is funny once and then is a boat on the roof of the shop.
        /// </summary>
        IEnumerator Swamped(BoatController boat, Rigidbody body)
        {
            yield return new WaitForSeconds(2f);

            body.position -= Vector3.up * 4f;
            body.linearVelocity = Vector3.zero;
            Physics.SyncTransforms();

            float launched = 0f;
            float started = Time.time;

            while (Time.time - started < 15f && Freeboard(body) < 0.1f)
            {
                yield return null;
                launched = Mathf.Max(launched, body.linearVelocity.y);
            }

            float took = Time.time - started;

            Check($"a swamped hull comes back up (took {took:0.0}s)", Freeboard(body) > 0.1f);
            Check($"without being fired out of the sea (peak {launched:0.0} m/s up)", launched < 12f);

            yield return new WaitForSeconds(3f);

            Check($"and settles again (freeboard {Freeboard(body):0.00}m, "
                  + $"heave {Mathf.Abs(body.linearVelocity.y):0.00} m/s)",
                  Freeboard(body) > 0f && Freeboard(body) < DeckHeight
                  && Mathf.Abs(body.linearVelocity.y) < 1f);

            Debug.Log($"[BoatTest] pushed 4m under: surfaced in {took:0.0}s, peaking at "
                      + $"{launched:0.0} m/s upward.");
        }

        // ---------------------------------------------------------------- four aboard

        IEnumerator Riding(BoatController boat, Vehicle hull, PlayerMotor motor)
        {
            int seated = hull.ServerEnter(motor.NetworkObject);
            Check($"the player got the helm (seat {seated})", seated == 0);

            for (int i = 0; i < 3; i++)
            {
                NetworkObject spare = SpawnBody(hull.transform.position + Vector3.up * 2f
                                                + hull.transform.right * (2f + i));
                if (spare == null) break;

                _extras.Add(spare);
                yield return new WaitForSeconds(0.3f);

                hull.ServerEnter(spare);
            }

            Check($"four aboard ({hull.Occupied()}/{hull.SeatCount})", hull.Occupied() == 4);

            float worst = 0f;
            int worstSeat = -1;
            int samples = 0;
            float peak = 0f;
            float lowest = float.MaxValue;

            float started = Time.time;
            while (Time.time - started < RideSeconds)
            {
                // Weaving, because a boat that only goes straight never heels, and heeling is what
                // would throw a badly glued passenger over the side.
                boat.ServerDrive(1f, Mathf.Sin((Time.time - started) * 0.9f) * 0.8f, handbrake: false);

                yield return null;

                peak = Mathf.Max(peak, boat.ForwardSpeed);
                lowest = Mathf.Min(lowest, Freeboard(hull.GetComponent<Rigidbody>()));

                for (int i = 0; i < hull.SeatCount; i++)
                {
                    VehicleRider occupant = hull.Occupant(i);
                    Transform anchor = hull.SeatAnchor(i);
                    if (occupant == null || anchor == null) continue;

                    float drift = Vector3.Distance(occupant.transform.position, anchor.position);
                    samples++;

                    if (drift <= worst) continue;

                    worst = drift;
                    worstSeat = i;
                }
            }

            boat.ServerDrive(0f, 0f, handbrake: true);

            Check($"nobody drifted out of their seat over {samples} sample(s) "
                  + $"(worst {worst:0.000}m, seat {worstSeat})", worst < Glued);
            Check($"all four are still aboard ({hull.Occupied()}/4)", hull.Occupied() == 4);
            Check($"and four passengers did not sink it (least freeboard {lowest:0.00}m)", lowest > 0f);

            Debug.Log($"[BoatTest] {RideSeconds:0}s of driving with four aboard, peaking at "
                      + $"{peak:0.0} m/s: worst drift {worst:0.000}m over {samples} sample(s), "
                      + $"least freeboard {lowest:0.00}m.");

            yield return new WaitForSeconds(2f);
        }

        // ---------------------------------------------------------------- nobody at the helm

        IEnumerator Driverless(BoatController boat, Vehicle hull, PlayerMotor motor)
        {
            if (hull.SeatOf(motor.NetworkObject) < 0) hull.ServerEnter(motor.NetworkObject);

            boat.ServerDrive(1f, 0f, handbrake: false);

            float deadline = Time.time + 10f;
            while (Time.time < deadline && boat.ForwardSpeed < 5f) yield return null;

            float entry = boat.ForwardSpeed;

            hull.ServerExit(motor.NetworkObject);

            Check($"the driver got out at {entry:0.0} m/s", hull.Driver == null);

            float stopBy = Time.time + 25f;
            while (Time.time < stopBy && Mathf.Abs(boat.ForwardSpeed) > 0.5f) yield return null;

            Check($"and the helmless boat coasts to a stop ({Mathf.Abs(boat.ForwardSpeed):0.00} m/s)",
                  Mathf.Abs(boat.ForwardSpeed) <= 0.5f);

            Debug.Log($"[BoatTest] driver left the helm at {entry:0.0} m/s; the boat drifted to a stop.");
        }

        // ---------------------------------------------------------------- scaffolding

        NetworkObject SpawnBody(Vector3 position)
        {
            PlayerSpawner spawner = PlayerSpawner.Instance;
            NetworkObject prefab = spawner != null ? spawner.PlayerPrefab : null;

            if (prefab == null)
            {
                Debug.LogError("[BoatTest] No player prefab to spawn spare bodies from.");
                return null;
            }

            NetworkObject body = InstanceFinder.NetworkManager.GetPooledInstantiated(
                prefab, position, Quaternion.identity, asServer: true);

            InstanceFinder.ServerManager.Spawn(body);

            return body;
        }

        void Cleanup(Vehicle hull, BoatController boat, Rigidbody body)
        {
            boat.ServerRelease();

            foreach (NetworkObject spare in _extras)
            {
                if (spare == null || !spare.IsSpawned) continue;

                hull.ServerExit(spare);
                InstanceFinder.ServerManager.Despawn(spare);
            }

            _extras.Clear();

            if (!_moored) return;

            // Back to the mooring. The suite borrowed the only boat in the world and took it two
            // kilometres out to sea.
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.position = _mooredAt;
            body.rotation = _mooredFacing;
            Physics.SyncTransforms();
        }

        void Report()
        {
            if (_failed == 0) Debug.Log($"[BoatTest] {_passed} passed, 0 failed.");
            else Debug.LogError($"[BoatTest] {_passed} passed, {_failed} FAILED.");
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[BoatTest] FAILED: {what}.");
        }
    }
}
