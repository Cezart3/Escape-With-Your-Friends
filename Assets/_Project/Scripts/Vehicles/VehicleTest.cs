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
    /// The acceptance test for #57, run inside a real session. Server side, behind
    /// <c>-vehicleTest</c>, and it wants <c>-scene island -noNatives -noAnimals</c>: the buggy the
    /// island's own POI bake parked at base camp, and nobody wandering into it.
    ///
    /// The acceptance is "four players ride one vehicle without desync or getting flung out on
    /// join", which is three separate claims and only one of them is about seats:
    ///
    /// 1. **Four fit, and the fifth is told no.** A seat list that silently double-books is a seat
    ///    list that will put two people in the driver's chair the first time two of them press the
    ///    key on the same frame.
    /// 2. **A rider does not drift.** The whole design rests on the glue in
    ///    <see cref="Vehicle.LateUpdate"/> being the last thing to write a rider's transform, so the
    ///    vehicle is driven a couple of hundred metres and every rider is measured against its own
    ///    anchor - not against the car, which would pass even if all four were stacked in one seat.
    /// 3. **Getting out puts you back.** On the ground, at your own door, with a controller that
    ///    works - and with the vehicle's ownership released if you were the one driving.
    ///
    /// Four bodies means four bodies: the harness spawns three more from the same prefab the spawner
    /// uses rather than approximating a passenger with an empty GameObject. They have no owner, which
    /// is exactly what a body whose player is still loading looks like.
    /// </summary>
    public class VehicleTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const float WaitForVehicle = 60f;

        /// <summary>Metres a rider is allowed to be from its anchor. Not a tolerance; a rounding budget.</summary>
        const float Glued = 0.02f;

        /// <summary>Metres the buggy is driven while the riders are watched.</summary>
        const float DriveDistance = 180f;

        /// <summary>Metres per second it is driven at, which is faster than anybody will ever drive it.</summary>
        const float DriveSpeed = 30f;

        static bool _started;

        int _passed;
        int _failed;

        readonly List<NetworkObject> _extras = new();

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-vehicleTest")) return;

            _started = true;

            var go = new GameObject("VehicleTest");
            DontDestroyOnLoad(go);
            go.AddComponent<VehicleTest>();
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
                Debug.LogError("[VehicleTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            // The POI spawner puts the buggy in after the server starts. Worth waiting for rather
            // than racing: this test is the only thing in the project that knows it exists.
            Vehicle buggy = null;
            float vehicleDeadline = Time.time + WaitForVehicle;

            while (Time.time < vehicleDeadline && buggy == null)
            {
                buggy = Vehicle.All.FirstOrDefault(v => v != null && v.IsSpawned);
                if (buggy == null) yield return new WaitForSeconds(0.5f);
            }

            if (buggy == null)
            {
                Debug.LogError("[VehicleTest] No vehicle in the world. Run with -scene island after "
                               + "VehicleBuilder.Build and a POI bake with -rebuildPois.");
                yield break;
            }

            var rider = motor.GetComponent<VehicleRider>();
            var health = motor.GetComponent<Health>();
            var carryable = motor.GetComponent<Carryable>();

            if (rider == null || health == null)
            {
                Debug.LogError("[VehicleTest] The player body has no VehicleRider or Health. "
                               + "Rebuild the player prefab.");
                yield break;
            }

            Shape(buggy);

            yield return Boarding(buggy, motor, rider, health);
            yield return Filling(buggy, motor, rider);
            yield return Driving(buggy);
            yield return Leaving(buggy, motor, rider);
            yield return Refusals(buggy, motor, rider, health, carryable);
            yield return Cargo(buggy);
            yield return Sweeping(buggy, motor, rider, health);

            Cleanup(buggy);

            Report();
        }

        // ---------------------------------------------------------------- what was baked

        /// <summary>
        /// The prefab, before anybody touches it. Every one of these is a thing the builder can get
        /// wrong silently, and every one of them turns into a body stuck in the floor at runtime.
        /// </summary>
        void Shape(Vehicle buggy)
        {
            Check($"the {buggy.Label} has four seats ({buggy.SeatCount})", buggy.SeatCount == 4);

            Check("a carried body has somewhere to ride", buggy.CarrySocket != null);

            Check("nobody starts aboard", buggy.Occupied() == 0);

            var doors = new List<Vector3>();

            for (int i = 0; i < buggy.SeatCount; i++)
            {
                Vector3 seat = buggy.SeatAnchor(i) != null ? buggy.SeatAnchor(i).position : Vector3.zero;
                Transform door = buggy.SeatExit(i);

                Check($"seat {i} has an anchor", buggy.SeatAnchor(i) != null);
                Check($"seat {i} has a door", door != null);

                if (door == null) continue;

                doors.Add(door.position);

                // Outside the chassis, or getting out means standing in the car you just left.
                float across = Vector3.Distance(door.position, seat);
                Check($"seat {i}'s door is clear of the seat ({across:0.00}m)", across > 0.8f);
            }

            // Pairwise, because two doors on the same spot is how four people leaving at once end up
            // inside one another and the depenetration fires them across the island.
            for (int a = 0; a < doors.Count; a++)
            for (int b = a + 1; b < doors.Count; b++)
            {
                float apart = Vector3.Distance(doors[a], doors[b]);
                Check($"doors {a} and {b} are different places ({apart:0.00}m)", apart > 0.9f);
            }

            Debug.Log($"[VehicleTest] one {buggy.Label} at {buggy.transform.position}, "
                      + $"{buggy.SeatCount} seat(s), cargo socket "
                      + $"{(buggy.CarrySocket != null ? "wired" : "missing")}.");
        }

        // ---------------------------------------------------------------- getting in

        IEnumerator Boarding(Vehicle buggy, PlayerMotor motor, VehicleRider rider, Health health)
        {
            Stand(motor, health);
            yield return Settled();

            // Through the interactable rather than through ServerEnter: the key press is the thing
            // under test, and a test that called the internal method would never notice the day
            // somebody forgets to implement ServerInteract.
            Check("the buggy offers itself to a player on foot",
                  buggy.ServerCanInteract(motor.NetworkObject));

            buggy.ServerInteract(motor.NetworkObject);
            yield return Settled();

            Check("the player is aboard", rider.IsSeated);
            Check("in the driver's seat, being the first one in", rider.Seat == 0);
            Check("and is therefore driving", rider.IsDriving);
            Check("the vehicle agrees", buggy.SeatOf(motor.NetworkObject) == 0);
            Check("one of four seats is taken", buggy.Occupied() == 1);
            Check("the vehicle's Driver is that body", buggy.Driver == rider);

            // The whole point of the ownership transfer: #58's input arrives from the connection
            // that owns the object, so the object has to change hands when the wheel does.
            Check("the driver's connection owns the buggy",
                  buggy.Owner != null && buggy.Owner == motor.Owner);

            var controller = motor.GetComponent<CharacterController>();
            Check("the rider's CharacterController is switched off",
                  controller != null && !controller.enabled);

            Transform anchor = buggy.SeatAnchor(0);
            float off = anchor != null ? Vector3.Distance(motor.transform.position, anchor.position) : 99f;
            Check($"the body sits on the anchor ({off:0.000}m)", off < Glued);
        }

        /// <summary>
        /// Three more bodies from the same prefab a real player gets, then a fourth that must be
        /// refused. Ownerless on purpose: that is what a body looks like while its player is still
        /// loading the island, and it is the shape the acceptance is worried about.
        /// </summary>
        IEnumerator Filling(Vehicle buggy, PlayerMotor motor, VehicleRider rider)
        {
            for (int i = 0; i < 4; i++)
            {
                NetworkObject body = SpawnBody(buggy.transform.position
                                               + buggy.transform.right * (3f + i));
                if (body == null) break;

                _extras.Add(body);
            }

            Check("four spare bodies exist to put in it", _extras.Count == 4);

            yield return Settled();

            for (int i = 0; i < _extras.Count && i < 3; i++)
            {
                int seat = buggy.ServerEnter(_extras[i]);
                Check($"spare body {i} took seat {seat}", seat == i + 1);
            }

            yield return Settled();

            Check("the buggy is full", buggy.IsFull && buggy.Occupied() == 4);

            if (_extras.Count == 4)
            {
                bool allowed = buggy.ServerCanBoard(_extras[3], out string why);
                Check("a fifth passenger is refused", !allowed);
                Check($"and told why ({why})", !string.IsNullOrEmpty(why));

                int seat = buggy.ServerEnter(_extras[3]);
                Check("and does not end up in a seat anyway", seat < 0);
                Check("the count did not move", buggy.Occupied() == 4);
            }

            // Every seat holds somebody different. A list that double-books would pass every count
            // above and put two people in one chair.
            var seen = new HashSet<VehicleRider>();
            for (int i = 0; i < buggy.SeatCount; i++)
            {
                VehicleRider occupant = buggy.Occupant(i);
                Check($"seat {i} holds somebody", occupant != null);

                if (occupant != null) Check($"seat {i} holds somebody new", seen.Add(occupant));
            }

            Check("the driver is still the player", buggy.Driver == rider);
            Check("and the player is still in seat 0", buggy.SeatOf(motor.NetworkObject) == 0);
        }

        // ---------------------------------------------------------------- the ride

        /// <summary>
        /// Drives it. The buggy is kinematic in #57 and there is no engine yet, so the harness moves
        /// it - which is fine, because what is under test is whether four bodies stay where they were
        /// put while the transform underneath them changes every frame.
        /// </summary>
        IEnumerator Driving(Vehicle buggy)
        {
            Vector3 start = buggy.transform.position;
            Vector3 heading = buggy.transform.forward;

            float worst = 0f;
            int worstSeat = -1;
            int samples = 0;

            float travelled = 0f;

            while (travelled < DriveDistance)
            {
                float step = DriveSpeed * Time.deltaTime;
                travelled += step;

                // Straight, level and above the terrain: a buggy driven into a hill would be a test
                // of the terrain collider, which is not what this is measuring.
                buggy.transform.position += heading * step;

                yield return null;

                // Measured after the frame the vehicle moved in, which is the frame the glue had to
                // catch up in. Sampling before it would be measuring our own ordering.
                for (int i = 0; i < buggy.SeatCount; i++)
                {
                    VehicleRider occupant = buggy.Occupant(i);
                    Transform anchor = buggy.SeatAnchor(i);
                    if (occupant == null || anchor == null) continue;

                    float drift = Vector3.Distance(occupant.transform.position, anchor.position);
                    samples++;

                    if (drift <= worst) continue;

                    worst = drift;
                    worstSeat = i;
                }
            }

            float moved = Vector3.Distance(buggy.transform.position, start);

            Check($"the buggy actually went somewhere ({moved:0}m)", moved > DriveDistance * 0.9f);
            Check($"nobody drifted out of their seat over {samples} sample(s) "
                  + $"(worst {worst:0.000}m, seat {worstSeat})", worst < Glued);

            Check("all four are still aboard", buggy.Occupied() == 4);

            Debug.Log($"[VehicleTest] {moved:0}m at {DriveSpeed:0} m/s with four aboard: "
                      + $"worst drift {worst:0.000}m over {samples} sample(s).");
        }

        // ---------------------------------------------------------------- getting out

        IEnumerator Leaving(Vehicle buggy, PlayerMotor motor, VehicleRider rider)
        {
            Transform door = buggy.SeatExit(0);
            Vector3 expected = door != null ? door.position : Vector3.zero;

            buggy.ServerInteract(motor.NetworkObject);
            yield return Settled();

            Check("the player is out", !rider.IsSeated);
            Check("the seat is free", buggy.Occupant(0) == null && buggy.Occupied() == 3);
            Check("and nobody owns the buggy any more", buggy.Owner == null || !buggy.Owner.IsValid);

            var controller = motor.GetComponent<CharacterController>();
            Check("the CharacterController is back", controller != null && controller.enabled);

            // Horizontal only: the door anchor is at the vehicle's floor height and the exit is
            // dropped onto whatever ground is under it, so the heights are meant to differ.
            Vector3 flat = motor.transform.position - expected;
            flat.y = 0f;

            Check($"the player is standing at their own door ({flat.magnitude:0.00}m)",
                  flat.magnitude < 1.5f);

            Check("and is not inside the buggy",
                  Vector3.Distance(motor.transform.position, buggy.transform.position) > 1f);

            // The thing that matters more than any of it: a body that left a car can walk.
            Vector3 before = motor.transform.position;
            motor.ServerTeleport(before + Vector3.up * 0.5f, motor.transform.eulerAngles.y);

            yield return Settled();

            Check("and can be moved again",
                  Vector3.Distance(motor.transform.position, before) > 0.01f);

            // Back in for the rest of the cases, which all want a body in a seat.
            buggy.ServerInteract(motor.NetworkObject);
            yield return Settled();

            Check("and can get straight back in", rider.IsSeated && rider.Seat == 0);
        }

        // ---------------------------------------------------------------- what is refused

        IEnumerator Refusals(Vehicle buggy, PlayerMotor motor, VehicleRider rider,
                             Health health, Carryable carryable)
        {
            Check("a body already aboard cannot board again",
                  !buggy.ServerCanBoard(motor.NetworkObject, out _));

            buggy.ServerExit(motor.NetworkObject);
            yield return Settled();

            // Down, which in this project also means limp: StunState ragdolls anything that stops
            // being Alive. Both refusals are checked off the one state because that is the only way
            // a real player ever reaches them.
            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));
            yield return Settled();

            Check("a downed player cannot climb into a car",
                  !buggy.ServerCanBoard(motor.NetworkObject, out string floored));

            Check($"for the reason you would expect ({floored})",
                  floored != null && floored.Contains("floor"));

            Check("and pressing the key does nothing", buggy.ServerEnter(motor.NetworkObject) < 0);

            if (carryable != null && _extras.Count > 3)
            {
                // On somebody's shoulder, which is the interesting one: a carried body that could
                // board would be teleported out of its carrier's arms by the seat and left dangling
                // between the two.
                carryable.ServerAttach(_extras[3]);
                yield return Settled();

                Check("the spare body picked them up", carryable.IsCarried);

                bool allowed = buggy.ServerCanBoard(motor.NetworkObject, out string held);
                Check("a carried player cannot climb into a car", !allowed);

                // The reason matters as much as the refusal: both this and the downed case are true
                // of a body in somebody's arms, and a car that says the wrong one is a car whose log
                // sends you looking at the wrong system.
                Check($"and is told the more specific of the two reasons ({held})",
                      held != null && held.Contains("carrying"));

                carryable.ServerDetach();
                yield return Settled();
            }

            Stand(motor, health);
            yield return Settled();

            Check("and once they are upright again they can",
                  buggy.ServerCanBoard(motor.NetworkObject, out _));
        }

        // ---------------------------------------------------------------- the bed

        /// <summary>
        /// A vehicle is an <see cref="ICarryHolder"/>, which is the whole reason that interface
        /// exists (#24's note said so). A dead friend rides in the back rather than in a chair.
        /// </summary>
        IEnumerator Cargo(Vehicle buggy)
        {
            if (_extras.Count < 4) yield break;

            NetworkObject freight = _extras[3];
            var carryable = freight.GetComponent<Carryable>();
            var ragdoll = freight.GetComponent<RagdollController>();
            var health = freight.GetComponent<Health>();

            if (carryable == null || ragdoll == null || health == null) yield break;

            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));
            yield return Settled();

            int before = buggy.Occupied();

            carryable.ServerAttach(buggy.NetworkObject);
            yield return Settled();

            Check("a body can be loaded into the back of the buggy", carryable.IsCarried);
            Check("and the buggy is what is holding it", carryable.Carrier == buggy.NetworkObject);

            if (carryable.IsCarried && buggy.CarrySocket != null && ragdoll.HipBone != null)
            {
                float off = Vector3.Distance(ragdoll.HipBone.position, buggy.CarrySocket.position);
                Check($"and it is riding in the bed rather than beside it ({off:0.00}m)", off < 0.5f);
            }

            Check("loading cargo does not cost a seat", buggy.Occupied() == before);

            carryable.ServerDetach();
            yield return Settled();

            Check("and it can be taken back out", !carryable.IsCarried);

            health.ServerRescue();
            yield return Settled();
        }

        // ---------------------------------------------------------------- consequences

        /// <summary>
        /// A passenger who bleeds out mid-drive. Without the sweep they ride around forever as a
        /// statue with their controller switched off, which is the kind of bug that only shows up
        /// once somebody is having fun.
        /// </summary>
        IEnumerator Sweeping(Vehicle buggy, PlayerMotor motor, VehicleRider rider, Health health)
        {
            if (health == null) yield break;

            if (!rider.IsSeated)
            {
                buggy.ServerEnter(motor.NetworkObject);
                yield return Settled();
            }

            Check("the player is aboard before we break them", rider.IsSeated);

            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));
            yield return Settled();

            Check("a player who goes down in a seat is dumped out of it", !rider.IsSeated);
            Check("and the seat is free for somebody else", buggy.Occupant(0) == null);

            // Not checked while they are still limp: the ragdoll switches the controller off for its
            // own reasons, and asserting on it here would be asserting on #17's contract rather than
            // on this one. What matters is that the seat gave it back.
            Stand(motor, health);
            yield return Settled();

            var controller = motor.GetComponent<CharacterController>();
            Check("and once they are up, their controller works again",
                  controller != null && controller.enabled);

            Check("and they can get back in", buggy.ServerEnter(motor.NetworkObject) >= 0);

            yield return Settled();

            buggy.ServerExit(motor.NetworkObject);
            yield return Settled();
        }

        // ---------------------------------------------------------------- plumbing

        NetworkObject SpawnBody(Vector3 position)
        {
            PlayerSpawner spawner = PlayerSpawner.Instance;
            NetworkObject prefab = spawner != null ? spawner.PlayerPrefab : null;

            if (prefab == null)
            {
                Debug.LogError("[VehicleTest] No player prefab to spawn spare bodies from.");
                return null;
            }

            NetworkObject body = InstanceFinder.NetworkManager.GetPooledInstantiated(
                prefab, position, Quaternion.identity, asServer: true);

            InstanceFinder.ServerManager.Spawn(body);

            return body;
        }

        void Cleanup(Vehicle buggy)
        {
            foreach (NetworkObject body in _extras)
            {
                if (body == null || !body.IsSpawned) continue;

                buggy.ServerExit(body);
                InstanceFinder.ServerManager.Despawn(body);
            }

            _extras.Clear();
        }

        static void Stand(PlayerMotor motor, Health health)
        {
            if (health != null)
            {
                if (health.IsDead) health.ServerRevive(1f);
                else if (health.IsDowned) health.ServerRescue();

                health.Heal(health.Max);
            }

            var stun = motor.GetComponent<StunState>();
            if (stun != null) stun.ServerClearStun();
        }

        /// <summary>A SyncVar written this frame is read next tick, not next frame. See #46.</summary>
        static WaitForSeconds Settled() => new(0.3f);

        void Report()
        {
            string line = $"[VehicleTest] {_passed} passed, {_failed} failed.";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[VehicleTest] FAILED: {what}.");
        }
    }
}
