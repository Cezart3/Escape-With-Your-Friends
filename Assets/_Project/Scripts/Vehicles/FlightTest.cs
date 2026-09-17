using System.Collections;
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
    /// The acceptance test for #72, behind <c>-flightTest</c>. Wants <c>-scene island2 -noNatives
    /// -noAnimals</c>, and runs on the server.
    ///
    /// The acceptance is *"a first-time player can take off; landing stays hard enough to be funny"*,
    /// and the first half of that is the one a headless run can actually answer. So the take-off is
    /// flown by the stupidest autopilot that could be called a player: **hold the throttle, and pull
    /// back once it feels fast**. Two rules, no trim, no rudder, no flaps, no look-ahead. If that
    /// gets off the ground in the length of the strip, so will somebody who has never seen the game.
    ///
    /// The manoeuvring checks start from a block of air this file puts the aeroplane in, rather than
    /// from wherever the climb-out happened to end. A harness that flies into a hillside is testing
    /// the hillside, and the second island has plenty of those.
    ///
    /// The landing half is checked the other way round, by insisting it is *not* solved. Nobody can
    /// tell a headless run whether arriving is funny, but they can tell it that nothing here helps:
    /// hands off and throttle closed, the aeroplane has to still be descending and still be doing
    /// twenty metres a second when it reaches the ground. A model that flares for you has taken the
    /// joke out, so the absent feature is asserted as absent.
    /// </summary>
    public class FlightTest : MonoBehaviour
    {
        const float WaitForPlayers = 90f;
        const float WaitForParts = 60f;

        /// <summary>Airspeed at which the autopilot pulls back. Metres per second.</summary>
        const float Rotate = 20f;

        /// <summary>How long the take-off run is given before it counts as a failure. Seconds.</summary>
        const float RunwayTime = 25f;

        /// <summary>Height above the strip that counts as flying rather than bouncing. Metres.</summary>
        const float Airborne = 20f;

        static bool _started;

        int _passed;
        int _failed;

        /// <summary>Where the strip is. Every manoeuvring check is flown back over it.</summary>
        Vector3 _strip;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-flightTest")) return;

            _started = true;

            var go = new GameObject("FlightTest");
            DontDestroyOnLoad(go);
            go.AddComponent<FlightTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            if (GameSceneLoader.Current != "Island2")
            {
                Debug.LogError("[FlightTest] The plane is on the second island and "
                               + $"'{GameSceneLoader.Current}' is not it. Run with -scene island2. "
                               + "Nothing was checked.");
                yield break;
            }

            float deadline = Time.time + WaitForParts;
            while (Time.time < deadline && PlaneAssembly.Instance == null)
                yield return new WaitForSeconds(0.5f);

            if (PlaneAssembly.Instance == null)
            {
                Debug.LogError("[FlightTest] There is no plane on this island. Nothing was checked.");
                yield break;
            }

            PlaneAssembly assembly = PlaneAssembly.Instance;
            var plane = assembly.GetComponent<PlaneController>();
            var vehicle = assembly.GetComponent<Vehicle>();

            if (plane == null || vehicle == null)
            {
                Debug.LogError("[FlightTest] The plane is not a vehicle: "
                               + $"controller {(plane == null ? "missing" : "present")}, "
                               + $"vehicle {(vehicle == null ? "missing" : "present")}. "
                               + "Rebuild the prefab. Nothing was checked.");
                yield break;
            }

            PlayerMotor[] players = null;
            float playerDeadline = Time.time + WaitForPlayers;

            while (Time.time < playerDeadline)
            {
                players = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                          .Where(m => m != null && m.IsSpawned).ToArray();

                if (players.Length >= 1) break;
                yield return new WaitForSeconds(0.5f);
            }

            if (players == null || players.Length == 0)
            {
                Debug.LogError("[FlightTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            NetworkObject who = players[0].GetComponent<NetworkObject>();
            _strip = assembly.transform.position;

            yield return Grounded(assembly, plane, vehicle, who);
            yield return Repair(assembly, who);
            yield return Boarding(vehicle, assembly, who);
            yield return TakeOff(plane);
            yield return Handling(plane);
            yield return Stalling(plane);
            yield return Landing(plane);

            Report();
        }

        // ---------------------------------------------------------------- 1. a wreck does not fly

        IEnumerator Grounded(PlaneAssembly assembly, PlaneController plane, Vehicle vehicle,
                             NetworkObject who)
        {
            Check($"the plane has four seats ({vehicle.SeatCount})", vehicle.SeatCount == 4);
            Check("and it is not flyable with three holes in it", !plane.Flyable);

            Vector3 was = plane.transform.position;

            plane.ServerDrive(0f, 0f, power: true, brake: false);
            yield return new WaitForSeconds(3f);

            Check($"an unfinished plane will not spool up ({plane.Throttle:0.00} throttle)",
                  Mathf.Approximately(plane.Throttle, 0f));
            Check($"and goes nowhere ({Vector3.Distance(plane.transform.position, was):0.0}m)",
                  Vector3.Distance(plane.transform.position, was) < 2f);
            Check("still on its wheels", !plane.IsAirborne);

            plane.ServerDrive(0f, 0f, power: false, brake: true);

            Debug.Log("[FlightTest] three holes in it and full throttle held for three seconds: "
                      + $"{plane.FlightReport()}");
        }

        // ---------------------------------------------------------------- 2. the parts go in

        IEnumerator Repair(PlaneAssembly assembly, NetworkObject who)
        {
            var motor = who.GetComponent<PlayerMotor>();

            foreach (string label in new[] { "engine", "wing", "propeller" })
            {
                PlanePart part = PlanePart.All.FirstOrDefault(p => p.Label == label);
                if (part == null) { Fail($"there is a {label} to fetch"); continue; }

                motor.ServerTeleport(part.transform.position + Vector3.back * 1.5f,
                                     motor.transform.eulerAngles.y);
                yield return null;

                part.ServerInteract(who);
                yield return null;

                motor.ServerTeleport(assembly.transform.position + assembly.transform.right * 9f,
                                     motor.transform.eulerAngles.y);
                yield return null;

                assembly.ServerInteract(who);
                yield return null;
            }

            Check($"the plane can be made whole ({assembly.Fitted}/{assembly.Needed})",
                  assembly.Complete);
        }

        // ---------------------------------------------------------------- 3. getting in

        IEnumerator Boarding(Vehicle vehicle, PlaneAssembly assembly, NetworkObject who)
        {
            // Two interactables on one object, and this is the pair of answers that tells them
            // apart: with the holes filled the assembly has nothing to offer and the cockpit does.
            Check("a finished plane stops offering to take parts",
                  string.IsNullOrEmpty(assembly.Prompt));
            Check($"and offers a seat instead (\"{vehicle.Prompt}\")",
                  !string.IsNullOrEmpty(vehicle.Prompt));
            Check("which the empty-handed may take", vehicle.ServerCanInteract(who));
            Check("while the assembly refuses them", !assembly.ServerCanInteract(who));

            int seat = vehicle.ServerEnter(who);
            yield return null;

            Check($"the first one in gets the left-hand seat ({seat})", seat == 0);

            var rider = who.GetComponent<VehicleRider>();
            Check("and is the one flying it", rider != null && rider.IsDriving);
        }

        // ---------------------------------------------------------------- 4. the whole acceptance

        IEnumerator TakeOff(PlaneController plane)
        {
            Check("a finished plane will fly", plane.Flyable);

            yield return Probe(plane);

            Vector3 start = plane.transform.position;
            float began = Time.time;
            float best = 0f;
            bool rotated = false;

            while (Time.time - began < RunwayTime)
            {
                // The entire autopilot. Hold the throttle; once it feels fast, pull back.
                float pitch = plane.Airspeed >= Rotate ? 0.5f : 0f;
                if (pitch > 0f) rotated = true;

                plane.ServerDrive(pitch, 0f, power: true, brake: false);

                best = Mathf.Max(best, plane.transform.position.y - start.y);
                if (best >= Airborne) break;

                yield return new WaitForSeconds(0.1f);
            }

            float run = Vector3.Distance(
                new Vector3(start.x, 0f, start.z),
                new Vector3(plane.transform.position.x, 0f, plane.transform.position.z));

            if (!rotated)
                Debug.LogError($"[FlightTest] it never reached {Rotate:0} m/s: {plane.FlightReport()}");

            Check("holding the throttle got it moving", rotated);
            Check($"and two rules got it off the ground ({best:0}m up in {run:0}m)", best >= Airborne);
            Check("with nothing under the wheels", plane.IsAirborne);
            Check($"pointing more or less where it is going ({plane.Slip:0}deg of slip)",
                  plane.Slip < 25f);

            Debug.Log($"[FlightTest] throttle held, back pressure at {Rotate:0} m/s, and it was "
                      + $"{best:0}m up {run:0}m down the strip: {plane.FlightReport()}");
        }

        // ---------------------------------------------------------------- 5. flying it

        IEnumerator Handling(PlaneController plane)
        {
            yield return Block(plane, 220f, 34f);

            plane.ServerDrive(0f, 0f, power: true, brake: false);
            yield return new WaitForSeconds(3f);

            float heading = plane.transform.eulerAngles.y;

            Check($"hands off, it flies where it points ({plane.Slip:0}deg of slip)", plane.Slip < 20f);

            // A bank is a turn. There is no rudder key, so this is the only way round.
            plane.ServerDrive(0f, 0.7f, power: true, brake: false);
            yield return new WaitForSeconds(4f);

            float bank = plane.Bank;
            float turned = Mathf.Abs(Mathf.DeltaAngle(heading, plane.transform.eulerAngles.y));

            Check($"stick right puts the right wing down ({bank:0}deg of bank)", bank > 12f);
            Check($"and the nose comes round with it ({turned:0}deg)", turned > 25f);

            plane.ServerDrive(0f, 0f, power: true, brake: false);
            yield return new WaitForSeconds(5f);

            Check($"and letting go picks the wings back up ({plane.Bank:0}deg)",
                  Mathf.Abs(plane.Bank) < 12f);

            Debug.Log($"[FlightTest] four seconds of right stick was {bank:0}deg of bank and "
                      + $"{turned:0}deg of turn, and five seconds of nothing put it back to "
                      + $"{plane.Bank:0}deg: {plane.FlightReport()}");
        }

        // ---------------------------------------------------------------- 6. the forgiving bit

        IEnumerator Stalling(PlaneController plane)
        {
            yield return Block(plane, 320f, 30f);

            // Throttle closed, stick all the way back: the classic way to fall out of the sky.
            plane.ServerDrive(1f, 0f, power: false, brake: false);

            float worstBank = 0f;
            float worstSpin = 0f;

            for (int i = 0; i < 60; i++)
            {
                worstBank = Mathf.Max(worstBank, Mathf.Abs(plane.Bank));
                worstSpin = Mathf.Max(worstSpin,
                                      plane.GetComponent<Rigidbody>().angularVelocity.magnitude);
                yield return new WaitForSeconds(0.1f);
            }

            float slow = plane.Airspeed;

            Check($"the throttle closed and the stick back, it slows down ({slow:0} m/s)", slow < 26f);
            Check($"and it does not depart ({worstBank:0}deg of bank at worst)", worstBank < 75f);
            Check($"nor spin ({worstSpin:0.0} rad/s at worst)", worstSpin < 2.5f);
            Check("it is going down, which is the correct outcome", plane.transform.position.y < 320f);

            // And it comes back: nose down, power on. The way out of a stall in this model is the
            // way out of a real one, which is the only reason it is worth checking at all.
            plane.ServerDrive(-0.4f, 0f, power: true, brake: false);
            yield return new WaitForSeconds(8f);

            Check($"and pushing the nose down gets the speed back ({slow:0} -> {plane.Airspeed:0} m/s)",
                  plane.Airspeed > slow + 5f);

            Debug.Log($"[FlightTest] held at the stall for six seconds it reached {slow:0} m/s with "
                      + $"{worstBank:0}deg of bank and never spun; nose down for eight got it back to "
                      + $"{plane.Airspeed:0} m/s.");
        }

        // ---------------------------------------------------------------- 7. landing stays hard

        IEnumerator Landing(PlaneController plane)
        {
            yield return Block(plane, 150f, 30f);

            // Throttle closed, hands off. Nobody is flying it now, and nothing in the model is
            // going to fly it for them.
            plane.ServerDrive(0f, 0f, power: false, brake: false);

            float top = plane.transform.position.y;
            float bottom = top;
            var body = plane.GetComponent<Rigidbody>();

            float sink = 0f;
            float over = 0f;

            // Sampled inside the loop and kept, because the numbers that matter are the ones from
            // the last moment it was still flying. Read after the arrival they are all zero, which
            // is a true statement about a stationary aeroplane and no statement at all about landing.
            for (int i = 0; i < 300 && plane.IsAirborne; i++)
            {
                sink = -body.linearVelocity.y;
                over = plane.Airspeed;
                bottom = plane.transform.position.y;

                yield return new WaitForSeconds(0.1f);
            }

            float fell = top - bottom;

            Check($"an unattended plane comes down ({fell:0}m)", fell > 15f);
            Check($"and is still going down when it gets there ({sink:0.0} m/s)", sink > 1f);

            // The absent feature, asserted as absent. A flight model that flares for you - that
            // levels off and settles on its wheels near the ground - has taken the joke out, and
            // arriving at a run over rough ground at twenty metres a second is the joke.
            Check($"with no flare to slow it down ({over:0} m/s over the fence)", over > 14f);

            Debug.Log($"[FlightTest] throttle closed and hands off, it dropped {fell:0}m and was "
                      + $"still doing {over:0} m/s forward and {sink:0.0} m/s down at the end. "
                      + "Nobody is landing that by accident.");
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Asks PhysX directly what it thinks this rigidbody is, and then shoves it. Here because a
        /// dynamic, awake, unconstrained body that answers nine thousand newtons with a velocity of
        /// exactly zero is not a flight model problem, and guessing which of the dozen fields that
        /// could be was costing more runs than printing all of them.
        /// </summary>
        IEnumerator Probe(PlaneController plane)
        {
            var body = plane.GetComponent<Rigidbody>();
            Vector3 start = plane.transform.position;

            Terrain ground = Terrain.activeTerrain;
            float under = ground == null ? 0f
                        : ground.SampleHeight(start) + ground.transform.position.y;

            Debug.Log($"[FlightTest] probe: root at {start}, strip under it {under:0.00}m, "
                      + $"so it stands {start.y - under:0.00}m proud of the ground.");

            foreach (Collider piece in plane.GetComponentsInChildren<Collider>())
            {
                if (!piece.enabled || !piece.gameObject.activeInHierarchy) continue;

                Bounds box = piece.bounds;
                float below = ground == null ? 0f
                            : ground.SampleHeight(box.center) + ground.transform.position.y;

                PhysicsMaterial skin = piece.sharedMaterial;

                Debug.Log($"[FlightTest] probe: {piece.name} bottom at {box.min.y:0.00}, ground "
                          + $"{below:0.00}, clearance {box.min.y - below:0.00}m, friction "
                          + (skin == null ? "DEFAULT (0.6)" : $"{skin.staticFriction:0.00} static, "
                            + $"{skin.dynamicFriction:0.00} dynamic, {skin.frictionCombine}") + ".");
            }

            Debug.Log($"[FlightTest] probe: mass {body.mass:0}, linearDamping {body.linearDamping}, "
                      + $"angularDamping {body.angularDamping}, constraints {body.constraints}, "
                      + $"kinematic {body.isKinematic}, detectCollisions {body.detectCollisions}, "
                      + $"interpolation {body.interpolation}, collisionDetection {body.collisionDetectionMode}, "
                      + $"excludeLayers {body.excludeLayers.value}, includeLayers {body.includeLayers.value}, "
                      + $"automaticCentreOfMass {body.automaticCenterOfMass}, centreOfMass {body.centerOfMass}, "
                      + $"maxDepenetration {body.maxDepenetrationVelocity:0.00}, "
                      + $"simulationMode {Physics.simulationMode}, layer {plane.gameObject.layer}");

            body.AddForce(plane.transform.forward * 10f, ForceMode.VelocityChange);
            yield return new WaitForFixedUpdate();

            Debug.Log($"[FlightTest] probe: shoved to 10 m/s and one fixed step later it is at "
                      + $"{body.linearVelocity.magnitude:0.00} m/s ({body.linearVelocity}).");

            yield return new WaitForSeconds(1f);

            Debug.Log($"[FlightTest] probe: a second after the shove it has travelled "
                      + $"{Vector3.Distance(start, plane.transform.position):0.00}m, {plane.FlightReport()}");
        }

        /// <summary>
        /// Puts the aeroplane in a known block of air, straight and level and already at speed. Used
        /// to start each manoeuvring check from the same place instead of from wherever the last one
        /// left off - and well above the island, because a harness that flies into a hill is not
        /// testing the flight model.
        /// </summary>
        IEnumerator Block(PlaneController plane, float altitude, float speed)
        {
            var body = plane.GetComponent<Rigidbody>();

            // Back over the strip, not wherever the last check left it: an aeroplane that has spent
            // four seconds in a turn is a long way from where it started, and the second island is
            // only five hundred metres across.
            var above = new Vector3(_strip.x, altitude, _strip.z);

            plane.ServerDrive(0f, 0f, power: false, brake: false);

            body.position = above;
            body.rotation = Quaternion.Euler(0f, plane.transform.eulerAngles.y, 0f);
            plane.transform.SetPositionAndRotation(above, body.rotation);

            // autoSyncTransforms is off in this project; without this the solver still has the
            // aeroplane where it was and the first step after the move is fought by a contact that
            // is not there any more.
            Physics.SyncTransforms();

            body.linearVelocity = plane.transform.forward * speed;
            body.angularVelocity = Vector3.zero;

            yield return new WaitForSeconds(0.5f);
        }

        // ---------------------------------------------------------------- bookkeeping

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[FlightTest] FAILED: {what}.");
        }

        void Fail(string what) => Check(what, false);

        void Report()
        {
            Debug.Log($"[FlightTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[FlightTest] {_failed} check(s) failed.");
        }
    }
}
