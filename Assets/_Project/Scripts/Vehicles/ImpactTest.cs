using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// The acceptance test for #60, behind <c>-impactTest</c>. Wants <c>-scene island -noNatives
    /// -noAnimals</c>, same as the other vehicle suites, and the same twelve hundred metres of flat
    /// collider a long way from the island — see the note on <see cref="CarTest"/> for why nothing
    /// that measures a vehicle is measured on terrain.
    ///
    /// The issue's acceptance is "running over a friend is reliably funny and non-desyncing", and
    /// neither half is an assertion. What is checkable is the four ways it stops being either:
    ///
    /// 1. **The hit lands at all.** A 900kg chassis at 22 m/s covers 44cm per physics step, which is
    ///    wider than the person in front of it. A car that tunnels through its victim is the whole
    ///    feature silently absent.
    /// 2. **It sends them somewhere.** Metres of flight and metres of height, printed, so the next
    ///    person to move the tuning can see what they moved.
    /// 3. **It does not land twice.** A ragdoll under a moving car is a stream of fresh contacts, one
    ///    per bone; without the cooldown the first person run over takes all of them at once and dies
    ///    on the spot, which is not funny even once.
    /// 4. **Riding is not being run over.** Four passengers share a hull with the thing that hurts
    ///    people. If the seat ever registered as a hit, driving would kill the car park.
    /// </summary>
    public class ImpactTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const float WaitForVehicle = 60f;

        /// <summary>Metres between the car's nose and the victim at the start of a charge.</summary>
        const float RunUp = 60f;

        /// <summary>Seconds to watch a launched body before measuring how far it went.</summary>
        const float FlightSeconds = 4f;

        static readonly Vector3 PadCentre = new(4000f, 100f, 4000f);

        static bool _started;

        int _passed;
        int _failed;

        GameObject _pad;
        Vector3 _parkedAt;
        Quaternion _parkedFacing;

        readonly List<NetworkObject> _extras = new();

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-impactTest")) return;

            _started = true;

            var go = new GameObject("ImpactTest");
            DontDestroyOnLoad(go);
            go.AddComponent<ImpactTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            PlayerMotor driver = null;
            float deadline = Time.time + WaitForPlayer;

            while (Time.time < deadline && driver == null)
            {
                driver = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                         .FirstOrDefault(m => m != null && m.IsSpawned);

                if (driver == null) yield return new WaitForSeconds(0.5f);
            }

            if (driver == null)
            {
                Debug.LogError("[ImpactTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            Vehicle buggy = null;
            float vehicleDeadline = Time.time + WaitForVehicle;

            while (Time.time < vehicleDeadline && buggy == null)
            {
                buggy = Vehicle.All.FirstOrDefault(v => v != null && v.IsSpawned
                                                        && v.GetComponent<CarController>() != null);
                if (buggy == null) yield return new WaitForSeconds(0.5f);
            }

            var car = buggy != null ? buggy.GetComponent<CarController>() : null;
            var body = buggy != null ? buggy.GetComponent<Rigidbody>() : null;
            var impact = buggy != null ? buggy.GetComponent<VehicleImpact>() : null;

            if (car == null || body == null || impact == null)
            {
                Debug.LogError("[ImpactTest] No buggy with a VehicleImpact in the world. Re-run "
                               + "VehicleBuilder.Build and bake the POIs.");
                yield break;
            }

            Wiring(impact);

            NetworkObject spawned = SpawnBody(PadCentre + Vector3.up * 2f);
            if (spawned == null) yield break;

            _extras.Add(spawned);

            var victim = spawned.GetComponent<PlayerMotor>();
            var stun = spawned.GetComponent<StunState>();
            var health = spawned.GetComponent<Health>();
            var ragdoll = spawned.GetComponent<RagdollController>();

            if (victim == null || stun == null || health == null || ragdoll == null)
            {
                Debug.LogError("[ImpactTest] The spawned body is missing its combat components.");
                yield break;
            }

            yield return new WaitForSeconds(1f);

            yield return Runover(car, body, impact, victim, stun, health, ragdoll);
            yield return Recovery(stun, health);
            yield return Nudge(car, body, impact, victim, stun);
            yield return Riders(car, body, buggy, impact, driver, victim);

            Cleanup(buggy, car);

            Report();
        }

        // ---------------------------------------------------------------- what was baked

        void Wiring(VehicleImpact impact)
        {
            Check($"the buggy carries the thing that hurts people ({impact.MinSpeed:0.0} m/s floor)",
                  impact.MinSpeed > 0.5f && impact.MinSpeed < 8f);

            Vehicle boat = Vehicle.All.FirstOrDefault(v => v != null && v.IsSpawned
                                                           && v.GetComponent<BoatController>() != null);

            Check("and so does the boat, which is heavier and blunter",
                  boat != null && boat.GetComponent<VehicleImpact>() != null);
        }

        // ---------------------------------------------------------------- the run-over

        IEnumerator Runover(CarController car, Rigidbody body, VehicleImpact impact,
                            PlayerMotor victim, StunState stun, Health health,
                            RagdollController ragdoll)
        {
            Stage(car, body, victim, RunUp);

            yield return new WaitForSeconds(1.5f);

            int before = impact.Hits;
            Vector3 stood = ragdoll.HipBone != null ? ragdoll.HipBone.position
                                                    : victim.transform.position;
            float healthBefore = health.Current;

            car.ServerDrive(1f, 0f, handbrake: false);

            // Long enough to cover the run-up and drive well past where the victim was standing.
            float giveUp = Time.time + 14f;
            while (Time.time < giveUp && impact.Hits == before) yield return null;

            bool landed = impact.Hits > before;

            Check($"the car actually hits the person in front of it "
                  + $"(after {14f - (giveUp - Time.time):0.0}s)", landed);

            if (!landed)
            {
                car.ServerDrive(0f, 0f, handbrake: true);
                Debug.LogError($"[ImpactTest] no contact. Car reached {car.ForwardSpeed:F1} m/s and "
                               + $"ended {Vector3.Distance(body.position, stood):F1}m from the victim.");
                yield break;
            }

            Check($"at a speed worth reporting ({impact.LastSpeed:0.0} m/s)", impact.LastSpeed > 12f);
            Check($"and it hurt ({impact.LastDamage:0} of {health.Max:0} hp)",
                  healthBefore - health.Current > 10f);
            Check($"without killing them outright ({health.Current:0} hp left, {health.State})",
                  health.IsAlive);
            Check("the victim is stunned", stun.IsStunned);
            Check("and limp", ragdoll.IsRagdolled);

            car.ServerDrive(0f, 0f, handbrake: true);

            float flight = 0f;
            float apex = 0f;
            float watchUntil = Time.time + FlightSeconds;

            while (Time.time < watchUntil)
            {
                yield return null;

                Transform hip = ragdoll.HipBone;
                if (hip == null) continue;

                flight = Mathf.Max(flight, Vector3.Distance(hip.position, stood));
                apex = Mathf.Max(apex, hip.position.y - stood.y);
            }

            Check($"the body was launched ({flight:0.0}m from where it stood)", flight > 4f);
            Check($"and went over rather than under ({apex:0.0}m up)", apex > 0.8f);

            // Ceilings matter as much as floors here, the same way they do for the boat's turning
            // circle. A car that catapults a body 186 metres into the air also passes "was it
            // launched", and a launch nobody can follow with their eyes is not the joke - it is a
            // body that lands in the sea and a suite that never noticed.
            Check($"but thrown, not fired out of a cannon ({flight:0.0}m)", flight < 80f);
            Check($"and it stayed in the world it was hit in ({apex:0.0}m up)", apex < 25f);

            // The non-desyncing half, as far as one process can see it: a ragdoll that has been
            // handed a NaN or an impulse PhysX cannot integrate leaves the world entirely, and every
            // client then disagrees about where it went.
            Transform finalHip = ragdoll.HipBone;
            bool sane = finalHip != null
                        && !float.IsNaN(finalHip.position.x) && !float.IsNaN(finalHip.position.y)
                        && !float.IsNaN(finalHip.position.z)
                        && Vector3.Distance(finalHip.position, stood) < 300f;

            Check("and it is still somewhere the rest of the world can see it", sane);

            // One hit, not one per bone. The cooldown is the only thing between a run-over and an
            // instant death by two dozen contacts.
            Check($"one collision counted as one hit ({impact.Hits - before})",
                  impact.Hits - before == 1);

            Debug.Log($"[ImpactTest] run over at {impact.LastSpeed:0.0} m/s "
                      + $"({impact.LastSpeed * 3.6f:0} km/h): {impact.LastDamage:0} damage, "
                      + $"{impact.LastImpulse.magnitude:0} Ns, thrown {flight:0.0}m and {apex:0.0}m up, "
                      + $"{health.Current:0} hp left.");
        }

        // ---------------------------------------------------------------- they get back up

        IEnumerator Recovery(StunState stun, Health health)
        {
            float waited = 0f;
            float patience = 15f;

            while (waited < patience && stun.IsStunned)
            {
                yield return new WaitForSeconds(0.25f);
                waited += 0.25f;
            }

            Check($"and the stun wears off ({waited:0.0}s)", !stun.IsStunned);
            Check($"leaving them alive ({health.Current:0} hp, {health.State})", health.IsAlive);

            Debug.Log($"[ImpactTest] the victim stood back up after {waited:0.0}s on {health.Current:0} hp.");
        }

        // ---------------------------------------------------------------- the parking-speed shove

        IEnumerator Nudge(CarController car, Rigidbody body, VehicleImpact impact,
                          PlayerMotor victim, StunState stun)
        {
            Stage(car, body, victim, 3f);

            yield return new WaitForSeconds(1.5f);

            int before = impact.Hits;
            float peak = 0f;

            car.ServerDrive(0.12f, 0f, handbrake: false);

            float until = Time.time + 5f;
            while (Time.time < until)
            {
                yield return null;
                peak = Mathf.Max(peak, Mathf.Abs(car.ForwardSpeed));
            }

            car.ServerDrive(0f, 0f, handbrake: true);

            Check($"a creep stays under the impact floor ({peak:0.0} of {impact.MinSpeed:0.0} m/s)",
                  peak < impact.MinSpeed);
            Check($"so leaning on somebody does not launch them ({impact.Hits - before} hit(s))",
                  impact.Hits == before);
            Check("and they are still standing", !stun.IsStunned);

            Debug.Log($"[ImpactTest] 5s of creeping throttle peaked at {peak:0.0} m/s and hit nobody.");

            yield return new WaitForSeconds(0.5f);
        }

        // ---------------------------------------------------------------- riding is not being hit

        IEnumerator Riders(CarController car, Rigidbody body, Vehicle buggy, VehicleImpact impact,
                           PlayerMotor driver, PlayerMotor victim)
        {
            // The victim goes a long way off the pad first, so the only person the car can reach is
            // the one sitting in it.
            victim.ServerTeleport(PadCentre + new Vector3(400f, 2f, 0f), 0f);

            Park(car, body);

            yield return new WaitForSeconds(1f);

            int seated = buggy.ServerEnter(driver.NetworkObject);
            Check($"the driver got in (seat {seated})", seated >= 0);

            int before = impact.Hits;
            float peak = 0f;

            float until = Time.time + 8f;
            while (Time.time < until)
            {
                // Weaving, because a hull that never leans never presses a passenger into itself.
                car.ServerDrive(1f, Mathf.Sin(Time.time * 1.4f) * 0.6f, handbrake: false);
                yield return null;
                peak = Mathf.Max(peak, car.ForwardSpeed);
            }

            car.ServerDrive(0f, 0f, handbrake: true);

            Check($"8s of driving at up to {peak:0.0} m/s hurts nobody aboard "
                  + $"({impact.Hits - before} hit(s))", impact.Hits == before);
            Check("and the driver is still in one piece", buggy.Driver != null);

            Debug.Log($"[ImpactTest] drove {peak:0.0} m/s with a passenger: {impact.Hits - before} hit(s).");

            buggy.ServerExit(driver.NetworkObject);
        }

        // ---------------------------------------------------------------- scaffolding

        /// <summary>
        /// Car at the middle of the pad facing +Z, victim <paramref name="ahead"/> metres up the
        /// road. Everything that charges starts here, so no section inherits the last one's wreck.
        /// </summary>
        void Stage(CarController car, Rigidbody body, PlayerMotor victim, float ahead)
        {
            Park(car, body);
            victim.ServerTeleport(PadCentre + new Vector3(0f, 1.2f, ahead), 180f);
        }

        void Park(CarController car, Rigidbody body)
        {
            if (_pad == null)
            {
                _parkedAt = body.position;
                _parkedFacing = body.rotation;

                _pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _pad.name = "ImpactTest.Pad";
                _pad.transform.position = PadCentre;
                _pad.transform.localScale = new Vector3(1200f, 1f, 1200f);
            }

            car.ServerDrive(0f, 0f, handbrake: true);
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.position = PadCentre + new Vector3(0f, 1.1f, -6f);
            body.rotation = Quaternion.identity;
            Physics.SyncTransforms();
        }

        NetworkObject SpawnBody(Vector3 position)
        {
            PlayerSpawner spawner = PlayerSpawner.Instance;
            NetworkObject prefab = spawner != null ? spawner.PlayerPrefab : null;

            if (prefab == null)
            {
                Debug.LogError("[ImpactTest] No player prefab to spawn a victim from.");
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

            var chassis = buggy.GetComponent<Rigidbody>();
            chassis.linearVelocity = Vector3.zero;
            chassis.angularVelocity = Vector3.zero;
            chassis.position = _parkedAt;
            chassis.rotation = _parkedFacing;
            Physics.SyncTransforms();
        }

        void Report()
        {
            if (_failed == 0) Debug.Log($"[ImpactTest] {_passed} passed, 0 failed.");
            else Debug.LogError($"[ImpactTest] {_passed} passed, {_failed} FAILED.");
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[ImpactTest] FAILED: {what}.");
        }
    }
}
