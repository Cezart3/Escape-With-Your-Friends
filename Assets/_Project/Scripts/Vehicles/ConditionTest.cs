using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// The acceptance test for #61, behind <c>-conditionTest</c>. Wants <c>-scene island -noNatives
    /// -noAnimals</c>, and the same twelve hundred metres of flat collider the other vehicle suites
    /// use — see the note on <see cref="CarTest"/>.
    ///
    /// "Wrecking a vehicle costs something but is never run-ending" is two assertions wearing one
    /// sentence, and the suite is built around both:
    ///
    /// 1. **It costs.** Fuel goes down at a rate somebody can plan around, and hitting things takes
    ///    integrity off in chunks worth noticing. A number that does not move is a feature that is
    ///    not there.
    /// 2. **It is never terminal.** Every failure state is reversed *in place*, by a player holding
    ///    an item, with the vehicle left exactly where it broke. The suite wrecks the buggy on
    ///    purpose and then puts it back on the road without moving it a metre.
    ///
    /// The gate is checked through <see cref="VehicleRider.Drive"/> rather than by reading
    /// <c>CanDrive</c>, because the flag being false proves nothing about whether anything reads it.
    /// It runs a positive control first for the same reason: a stalled car and a car whose input path
    /// never worked in a headless host look identical from the outside.
    /// </summary>
    public class ConditionTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const float WaitForVehicle = 60f;

        const string FuelItem = "fuel";
        const string RepairItem = "scrap_metal";

        static readonly Vector3 PadCentre = new(4000f, 100f, 4000f);

        static bool _started;

        int _passed;
        int _failed;

        GameObject _pad;
        GameObject _wall;
        Vector3 _parkedAt;
        Quaternion _parkedFacing;

        readonly List<NetworkObject> _extras = new();

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-conditionTest")) return;

            _started = true;

            var go = new GameObject("ConditionTest");
            DontDestroyOnLoad(go);
            go.AddComponent<ConditionTest>();
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
                Debug.LogError("[ConditionTest] No player ever spawned. Nothing was checked.");
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
            var condition = buggy != null ? buggy.GetComponent<VehicleCondition>() : null;

            if (car == null || body == null || condition == null)
            {
                Debug.LogError("[ConditionTest] No buggy with a VehicleCondition in the world. "
                               + "Re-run VehicleBuilder.Build and bake the POIs.");
                yield break;
            }

            var rider = driver.GetComponent<VehicleRider>();
            var bag = driver.GetComponent<Inventory>();

            if (rider == null || bag == null)
            {
                Debug.LogError("[ConditionTest] The player has no VehicleRider or no Inventory.");
                yield break;
            }

            Wiring(buggy, condition);

            yield return Burning(buggy, car, body, condition, driver, rider);
            yield return Softness(buggy, car, body, condition, driver);
            yield return Crashing(buggy, car, body, condition);
            yield return Stalling(buggy, car, body, condition, driver, rider);
            yield return Servicing(buggy, condition, driver, bag);
            yield return Driving(buggy, car, body, condition, driver, rider);

            Cleanup(buggy, car);

            Report();
        }

        // ---------------------------------------------------------------- what was baked

        void Wiring(Vehicle buggy, VehicleCondition condition)
        {
            Check($"the buggy spawns with a full tank ({condition.Fuel:0}/{condition.Tank:0}L)",
                  !condition.NeedsFuel && condition.Tank > 1f);

            Check($"and no dents ({condition.Integrity:0}/{condition.IntegrityMax:0})",
                  !condition.NeedsRepair && condition.IntegrityMax > 1f);

            Check("so it will go", condition.CanDrive);

            Vehicle boat = Vehicle.All.FirstOrDefault(v => v != null && v.IsSpawned
                                                           && v.GetComponent<BoatController>() != null);
            var afloat = boat != null ? boat.GetComponent<VehicleCondition>() : null;

            Check("the boat has a tank too", afloat != null);

            // Running dry on land is a walk home; running dry at sea is the run-ending outcome the
            // issue says never to ship, so the hull carries more.
            Check($"and a bigger one than the buggy ({(afloat != null ? afloat.Tank : 0f):0} "
                  + $"vs {condition.Tank:0}L)", afloat != null && afloat.Tank > condition.Tank);

            Debug.Log($"[ConditionTest] buggy: {condition.Report()}.");
        }

        // ---------------------------------------------------------------- it drinks

        IEnumerator Burning(Vehicle buggy, CarController car, Rigidbody body,
                            VehicleCondition condition, PlayerMotor driver, VehicleRider rider)
        {
            Park(car, body);

            int seat = buggy.ServerEnter(driver.NetworkObject);
            Check($"the driver got the wheel (seat {seat})", seat == 0);

            yield return new WaitForSeconds(1f);

            float fuelBefore = condition.Fuel;
            Vector3 from = body.position;

            // Through the rider, not through ServerDrive: the positive control for every "it refuses
            // to move" check below.
            float until = Time.time + 10f;
            while (Time.time < until)
            {
                rider.Drive(new Vector2(0f, 1f), brake: false, boost: false);
                yield return null;
            }

            rider.Drive(Vector2.zero, brake: true, boost: false);

            float driven = Vector3.Distance(body.position, from);
            float burned = fuelBefore - condition.Fuel;

            Check($"the owner's input actually drives it ({driven:0}m in 10s)", driven > 30f);
            Check($"and driving costs fuel ({burned:0.0}L)", burned > 0.05f);

            float perKm = driven > 1f ? burned / (driven / 1000f) : 0f;
            Check($"at a rate a player could plan around ({perKm:0.0} L/km)",
                  perKm > 1f && perKm < 60f);

            float range = perKm > 0.01f ? condition.Tank / perKm : 0f;
            Check($"giving a tank a useful range ({range:0.0} km on a {condition.Tank:0}L tank)",
                  range > 1f && range < 40f);

            Debug.Log($"[ConditionTest] 10s of throttle: {driven:0}m on {burned:0.0}L "
                      + $"({perKm:0.0} L/km, {range:0.0} km to a tank). {condition.Report()}.");

            yield return new WaitForSeconds(0.5f);
        }

        // ---------------------------------------------------------------- people are soft

        /// <summary>
        /// Running somebody over must not dent the car. The guard is one line, and without it every
        /// run-over - which #60 exists to make people do constantly - is also a repair bill.
        /// </summary>
        IEnumerator Softness(Vehicle buggy, CarController car, Rigidbody body,
                             VehicleCondition condition, PlayerMotor driver)
        {
            var impact = buggy.GetComponent<VehicleImpact>();
            if (impact == null) yield break;

            NetworkObject spare = SpawnBody(PadCentre + Vector3.up * 2f);
            if (spare == null) yield break;

            _extras.Add(spare);

            var victim = spare.GetComponent<PlayerMotor>();
            if (victim == null) yield break;

            Park(car, body);
            victim.ServerTeleport(PadCentre + new Vector3(0f, 1.2f, 50f), 180f);

            yield return new WaitForSeconds(1.5f);

            float before = condition.Integrity;
            int hits = impact.Hits;

            car.ServerDrive(1f, 0f, handbrake: false);

            float giveUp = Time.time + 14f;
            while (Time.time < giveUp && impact.Hits == hits) yield return null;

            car.ServerDrive(0f, 0f, handbrake: true);

            Check($"the buggy ran the spare body over ({impact.Hits - hits} hit(s))",
                  impact.Hits > hits);
            Check($"and people do not dent cars ({condition.Integrity:0} of {before:0} left)",
                  Mathf.Approximately(condition.Integrity, before));

            Debug.Log($"[ConditionTest] ran a body over at {impact.LastSpeed:0.0} m/s: "
                      + $"{condition.Report()}.");

            // Out of the way of the wall.
            victim.ServerTeleport(PadCentre + new Vector3(400f, 2f, 0f), 0f);

            yield return new WaitForSeconds(0.5f);
        }

        // ---------------------------------------------------------------- it breaks

        IEnumerator Crashing(Vehicle buggy, CarController car, Rigidbody body,
                             VehicleCondition condition)
        {
            Park(car, body);

            if (_wall == null)
            {
                _wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _wall.name = "ConditionTest.Wall";
                _wall.transform.position = PadCentre + new Vector3(0f, 4f, 70f);
                _wall.transform.localScale = new Vector3(40f, 8f, 2f);
            }

            yield return new WaitForSeconds(1f);

            float before = condition.Integrity;

            car.ServerDrive(1f, 0f, handbrake: false);

            float giveUp = Time.time + 12f;
            while (Time.time < giveUp && condition.Integrity >= before) yield return null;

            car.ServerDrive(0f, 0f, handbrake: true);

            float lost = before - condition.Integrity;

            Check($"driving into a wall costs integrity ({lost:0} of {before:0})", lost > 1f);
            Check($"a chunk rather than a scratch ({lost:0})", lost > 10f);
            Check($"but one crash is survivable ({condition.Integrity:0} left)",
                  condition.Integrity > 0f);

            Debug.Log($"[ConditionTest] hit a wall: -{lost:0} integrity. {condition.Report()}.");

            yield return new WaitForSeconds(1f);
        }

        // ---------------------------------------------------------------- it stops

        IEnumerator Stalling(Vehicle buggy, CarController car, Rigidbody body,
                             VehicleCondition condition, PlayerMotor driver, VehicleRider rider)
        {
            Park(car, body);

            condition.ServerDamage(condition.IntegrityMax, "the harness");

            yield return new WaitForSeconds(0.5f);

            Check($"enough damage wrecks it ({condition.Integrity:0})", condition.IsWrecked);
            Check("and a wreck will not drive", !condition.CanDrive);

            if (buggy.SeatOf(driver.NetworkObject) < 0) buggy.ServerEnter(driver.NetworkObject);

            Vector3 from = body.position;

            float until = Time.time + 5f;
            while (Time.time < until)
            {
                rider.Drive(new Vector2(0f, 1f), brake: false, boost: false);
                yield return null;
            }

            float crept = Vector3.Distance(body.position, from);

            Check($"and the throttle does nothing to a wreck (moved {crept:0.00}m in 5s)", crept < 2f);

            // Now the other half of the same gate, on a running but empty car. One piece of scrap,
            // not four: Servicing below has to find something left to mend, and a suite that repairs
            // the car it is about to test the repair on measures nothing.
            condition.ServerRepair();
            condition.ServerSetFuel(0f);

            yield return new WaitForSeconds(0.5f);

            Check("an empty tank stops it too", condition.IsDry && !condition.CanDrive);

            from = body.position;

            until = Time.time + 5f;
            while (Time.time < until)
            {
                rider.Drive(new Vector2(0f, 1f), brake: false, boost: false);
                yield return null;
            }

            crept = Vector3.Distance(body.position, from);

            Check($"and a dry car does not move either ({crept:0.00}m in 5s)", crept < 2f);

            Debug.Log($"[ConditionTest] wrecked, then drained: {condition.Report()}.");

            buggy.ServerExit(driver.NetworkObject);

            yield return new WaitForSeconds(0.5f);
        }

        // ---------------------------------------------------------------- it is never terminal

        /// <summary>
        /// The half of the acceptance that matters most. The buggy is broken and empty, a long way
        /// from anywhere, and a player standing next to it with the right things in their bag puts it
        /// back on the road without it being moved, towed, despawned or respawned.
        /// </summary>
        IEnumerator Servicing(Vehicle buggy, VehicleCondition condition, PlayerMotor driver,
                              Inventory bag)
        {
            ItemDef fuel = ItemCatalog.Active != null ? ItemCatalog.Active.Find(FuelItem) : null;
            ItemDef scrap = ItemCatalog.Active != null ? ItemCatalog.Active.Find(RepairItem) : null;

            Check($"the catalog has a fuel can ({FuelItem})", fuel != null);
            Check($"and something to patch a car with ({RepairItem})", scrap != null);

            if (fuel == null || scrap == null) yield break;

            // Stood next to it, wherever it broke.
            driver.ServerTeleport(buggy.transform.position + buggy.transform.right * 3f, 0f);

            bag.ServerClear();
            bag.Add(fuel, 2);
            bag.Add(scrap, 4);

            yield return new WaitForSeconds(0.5f);

            Check($"the player is carrying both ({bag.CountOf(fuel)} fuel, {bag.CountOf(scrap)} scrap)",
                  bag.Has(fuel) && bag.Has(scrap));

            // Repair first, because a car with fuel and no engine is still scrap.
            Select(bag, scrap);
            yield return new WaitForSeconds(0.3f);

            float bentAt = condition.Integrity;
            int scrapBefore = bag.CountOf(scrap);

            Check("the crosshair offers the repair",
                  buggy.Prompt != null && buggy.Prompt.Contains("Repair"));

            buggy.ServerInteract(driver.NetworkObject);

            yield return new WaitForSeconds(0.5f);

            Check($"a piece of scrap mends it ({bentAt:0} -> {condition.Integrity:0})",
                  condition.Integrity > bentAt);
            Check($"and is spent doing it ({scrapBefore} -> {bag.CountOf(scrap)})",
                  bag.CountOf(scrap) == scrapBefore - 1);
            Check("and servicing is not boarding", buggy.SeatOf(driver.NetworkObject) < 0);

            while (condition.NeedsRepair && bag.Has(scrap))
            {
                buggy.ServerInteract(driver.NetworkObject);
                yield return new WaitForSeconds(0.3f);
            }

            Select(bag, fuel);
            yield return new WaitForSeconds(0.3f);

            float dryAt = condition.Fuel;
            int cansBefore = bag.CountOf(fuel);

            Check("the crosshair offers the refuel",
                  buggy.Prompt != null && buggy.Prompt.Contains("Refuel"));

            buggy.ServerInteract(driver.NetworkObject);

            yield return new WaitForSeconds(0.5f);

            Check($"a can goes in ({dryAt:0.0} -> {condition.Fuel:0.0}L)", condition.Fuel > dryAt);
            Check($"and is spent doing it ({cansBefore} -> {bag.CountOf(fuel)})",
                  bag.CountOf(fuel) == cansBefore - 1);

            Check("so the wreck drives again", condition.CanDrive);

            Debug.Log($"[ConditionTest] repaired and refuelled by hand where it stood: "
                      + $"{condition.Report()}.");
        }

        // ---------------------------------------------------------------- and it goes

        IEnumerator Driving(Vehicle buggy, CarController car, Rigidbody body,
                            VehicleCondition condition, PlayerMotor driver, VehicleRider rider)
        {
            Park(car, body);

            if (buggy.SeatOf(driver.NetworkObject) < 0) buggy.ServerEnter(driver.NetworkObject);

            yield return new WaitForSeconds(1f);

            Vector3 from = body.position;

            float until = Time.time + 6f;
            while (Time.time < until)
            {
                rider.Drive(new Vector2(0f, 1f), brake: false, boost: false);
                yield return null;
            }

            rider.Drive(Vector2.zero, brake: true, boost: false);

            float driven = Vector3.Distance(body.position, from);

            Check($"a serviced buggy drives off under its own power ({driven:0}m in 6s)", driven > 20f);

            Debug.Log($"[ConditionTest] back on the road: {driven:0}m in 6s. {condition.Report()}.");

            buggy.ServerExit(driver.NetworkObject);
        }

        // ---------------------------------------------------------------- scaffolding

        static void Select(Inventory bag, ItemDef def)
        {
            for (int i = 0; i < bag.SlotCount; i++)
            {
                if (bag[i].Def != def) continue;

                bag.ServerSelect(i);
                return;
            }
        }

        void Park(CarController car, Rigidbody body)
        {
            if (_pad == null)
            {
                _parkedAt = body.position;
                _parkedFacing = body.rotation;

                _pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _pad.name = "ConditionTest.Pad";
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
                Debug.LogError("[ConditionTest] No player prefab to spawn spare bodies from.");
                return null;
            }

            NetworkObject spare = InstanceFinder.NetworkManager.GetPooledInstantiated(
                prefab, position, Quaternion.identity, asServer: true);

            InstanceFinder.ServerManager.Spawn(spare);

            return spare;
        }

        void Cleanup(Vehicle buggy, CarController car)
        {
            car.ServerRelease();

            foreach (NetworkObject spare in _extras)
            {
                if (spare == null || !spare.IsSpawned) continue;

                buggy.ServerExit(spare);
                InstanceFinder.ServerManager.Despawn(spare);
            }

            _extras.Clear();

            if (_wall != null)
            {
                Destroy(_wall);
                _wall = null;
            }

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
            if (_failed == 0) Debug.Log($"[ConditionTest] {_passed} passed, 0 failed.");
            else Debug.LogError($"[ConditionTest] {_passed} passed, {_failed} FAILED.");
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[ConditionTest] FAILED: {what}.");
        }
    }
}
