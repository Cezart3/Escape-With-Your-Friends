using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Player;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// The acceptance test for #62, behind <c>-vehicleUpgradeTest</c>. Wants <c>-scene island -noNatives
    /// -noAnimals</c> and the same flat pad the other vehicle suites build for themselves.
    ///
    /// "Upgrades measurably change handling and are worth the money" is a measurement, not an
    /// opinion, so the suite drives the buggy before and after each part and compares numbers:
    /// metres covered and top speed for the engine, degrees of yaw for the tyres, litres and
    /// integrity for the other two. Reading the multiplier back off the component would prove only
    /// that a float was stored.
    ///
    /// Two of those numbers took a run each to get right, and both mistakes were the same mistake:
    /// a measurement that moved for a reason other than the thing being measured. See
    /// <see cref="Corner"/> for the cornering one, and <see cref="Armour"/> for the other.
    ///
    /// **The order the parts go on is load-bearing.** The engine raises top speed, and the car's
    /// steering lock is interpolated against top speed - so a car with a bigger engine steers
    /// *harder* at any given speed, and a cornering test run after fitting one would credit the
    /// tyres with the engine's work. Corners are measured first, tyres are fitted first, and the
    /// engine goes on afterwards.
    /// </summary>
    public class VehicleUpgradeTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const float WaitForVehicle = 60f;

        static readonly Vector3 PadCentre = new(4000f, 100f, 4000f);

        static bool _started;

        int _passed;
        int _failed;

        GameObject _pad;
        Vector3 _parkedAt;
        Quaternion _parkedFacing;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-vehicleUpgradeTest")) return;

            _started = true;

            var go = new GameObject("VehicleUpgradeTest");
            DontDestroyOnLoad(go);
            go.AddComponent<VehicleUpgradeTest>();
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
                Debug.LogError("[VehicleUpgradeTest] No player ever spawned. Nothing was checked.");
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
            var upgrades = buggy != null ? buggy.GetComponent<VehicleUpgrades>() : null;

            if (car == null || body == null || condition == null || upgrades == null)
            {
                Debug.LogError("[VehicleUpgradeTest] No buggy with VehicleUpgrades in the world. "
                               + "Re-run VehicleBuilder.Build and bake the POIs.");
                yield break;
            }

            var bag = driver.GetComponent<Inventory>();

            if (bag == null)
            {
                Debug.LogError("[VehicleUpgradeTest] The player has no Inventory.");
                yield break;
            }

            Shelf(upgrades);

            driver.ServerTeleport(buggy.transform.position + buggy.transform.right * 3f, 0f);
            bag.ServerClear();

            yield return Tyres(buggy, car, body, upgrades, driver, bag);
            yield return Engine(buggy, car, body, upgrades, driver, bag);
            yield return Tank(buggy, condition, upgrades, driver, bag);
            yield return Armour(buggy, condition, upgrades, driver, bag);
            yield return Refusals(buggy, upgrades, driver, bag);

            Cleanup(buggy, car);

            Report();
        }

        // ---------------------------------------------------------------- what the trader stocks

        void Shelf(VehicleUpgrades upgrades)
        {
            Check($"the buggy accepts four parts ({upgrades.Fits.Count})", upgrades.Fits.Count == 4);

            foreach (VehiclePart part in System.Enum.GetValues(typeof(VehiclePart)))
            {
                Check($"a stock buggy has nothing fitted in the {part} slot",
                      upgrades.TierOf(part) == 0);

                VehicleUpgradeDef def = upgrades.Fits.FirstOrDefault(f => f != null && f.Part == part);

                Check($"and something to put in it ({(def != null ? def.DisplayName : "nothing")})",
                      def != null);

                if (def == null) continue;

                ItemDef item = Item(def.ItemId);

                Check($"which is a real item you can be sold ({def.ItemId})", item != null);
                Check($"and costs real money ({(item != null ? item.Value : 0)})",
                      item != null && item.Value > 0);
            }

            Vehicle boat = Vehicle.All.FirstOrDefault(v => v != null && v.IsSpawned
                                                           && v.GetComponent<BoatController>() != null);
            var afloat = boat != null ? boat.GetComponent<VehicleUpgrades>() : null;

            Check("the boat takes parts too", afloat != null);
            Check("but not tyres",
                  afloat != null && afloat.Fits.All(f => f == null || f.Part != VehiclePart.Tyres));

            Debug.Log($"[VehicleUpgradeTest] stock buggy: {upgrades.Report()}");
        }

        // ---------------------------------------------------------------- grip

        IEnumerator Tyres(Vehicle buggy, CarController car, Rigidbody body, VehicleUpgrades upgrades,
                          PlayerMotor driver, Inventory bag)
        {
            yield return Park(car, body);

            float stockYaw = 0f, stockSlide = 0f, stockSpeed = 0f;
            yield return Corner(car, body, (yaw, slide, speed) =>
            {
                stockYaw = yaw;
                stockSlide = slide;
                stockSpeed = speed;
            });

            Debug.Log($"[VehicleUpgradeTest] stock tyres: {stockYaw:0} degrees of yaw in 6s at "
                      + $"{stockSpeed:0.0} m/s, {stockSlide:0.00} m/s of sideways slide.");

            yield return Fit(buggy, upgrades, driver, bag, "tyre_kit", VehiclePart.Tyres);

            yield return Park(car, body);

            float fittedYaw = 0f, fittedSlide = 0f, fittedSpeed = 0f;
            yield return Corner(car, body, (yaw, slide, speed) =>
            {
                fittedYaw = yaw;
                fittedSlide = slide;
                fittedSpeed = speed;
            });

            // Both runs have to have been the same run for the comparison to mean anything.
            Check($"the two corners were driven at the same speed "
                  + $"({stockSpeed:0.0} vs {fittedSpeed:0.0} m/s)",
                  Mathf.Abs(stockSpeed - fittedSpeed) < 1.5f);

            Check($"grippy tyres turn the buggy harder ({stockYaw:0} -> {fittedYaw:0} degrees in 6s)",
                  fittedYaw > stockYaw * 1.05f);

            // The sideways number is logged and not asserted on, because it is not slip. Lateral
            // velocity at the centre of mass on a circle is mostly kinematic - yaw rate times the
            // distance back to the rear axle - so a car that corners tighter reads *higher* on it
            // while gripping better, which is exactly what the second run of this suite measured
            // (1.09 -> 1.56 m/s while the circle drew 9% tighter). It stays in the log because it
            // is the number that explains a strange yaw reading, not because it grades one.
            Debug.Log($"[VehicleUpgradeTest] grippy tyres: {fittedYaw:0} degrees of yaw in 6s at "
                      + $"{fittedSpeed:0.0} m/s ({fittedYaw / Mathf.Max(1f, stockYaw):0.00}x), "
                      + $"{fittedSlide:0.00} m/s lateral at the hub "
                      + $"(was {stockSlide:0.00}, kinematic - see the note in the suite).");
        }

        // ---------------------------------------------------------------- power

        IEnumerator Engine(Vehicle buggy, CarController car, Rigidbody body, VehicleUpgrades upgrades,
                           PlayerMotor driver, Inventory bag)
        {
            yield return Park(car, body);

            float stockRun = 0f, stockPeak = 0f;
            yield return Straight(car, body, (run, peak) => { stockRun = run; stockPeak = peak; });

            float stockCeiling = car.TopSpeed;

            Debug.Log($"[VehicleUpgradeTest] stock engine: {stockRun:0}m in 8s, peaked at {stockPeak:0.0} m/s.");

            yield return Fit(buggy, upgrades, driver, bag, "engine_kit", VehiclePart.Engine);

            Check($"a tuned engine raises the ceiling ({stockCeiling:0.0} -> {car.TopSpeed:0.0} m/s)",
                  car.TopSpeed > stockCeiling * 1.2f);

            yield return Park(car, body);

            float fittedRun = 0f, fittedPeak = 0f;
            yield return Straight(car, body, (run, peak) => { fittedRun = run; fittedPeak = peak; });

            Check($"and it covers more ground ({stockRun:0} -> {fittedRun:0}m in 8s)",
                  fittedRun > stockRun * 1.15f);

            Check($"going faster to do it ({stockPeak:0.0} -> {fittedPeak:0.0} m/s)",
                  fittedPeak > stockPeak * 1.15f);

            Debug.Log($"[VehicleUpgradeTest] tuned engine: {fittedRun:0}m in 8s "
                      + $"({fittedRun / Mathf.Max(1f, stockRun):0.00}x), peaked at {fittedPeak:0.0} m/s.");
        }

        // ---------------------------------------------------------------- range

        IEnumerator Tank(Vehicle buggy, VehicleCondition condition, VehicleUpgrades upgrades,
                         PlayerMotor driver, Inventory bag)
        {
            float stockTank = condition.Tank;
            float fuel = condition.Fuel;

            yield return Fit(buggy, upgrades, driver, bag, "tank_kit", VehiclePart.Tank);

            Check($"a long-range tank holds more ({stockTank:0} -> {condition.Tank:0}L)",
                  condition.Tank > stockTank * 1.2f);

            Check($"without pouring free petrol in ({fuel:0.0} -> {condition.Fuel:0.0}L)",
                  condition.Fuel <= fuel + 0.01f);

            Check("so a bigger tank is a tank worth filling", condition.NeedsFuel);

            Debug.Log($"[VehicleUpgradeTest] long-range tank: {condition.Report()}.");
        }

        // ---------------------------------------------------------------- panels

        IEnumerator Armour(Vehicle buggy, VehicleCondition condition, VehicleUpgrades upgrades,
                           PlayerMotor driver, Inventory bag)
        {
            // Dented first, on purpose: armour bolted to a bent car has to hand the extra integrity
            // over rather than just raising the number it is measured against.
            condition.ServerDamage(50f, "the harness");

            yield return new WaitForSeconds(0.3f);

            float stockMax = condition.IntegrityMax;
            float bent = condition.Integrity;

            yield return Fit(buggy, upgrades, driver, bag, "armour_kit", VehiclePart.Armour);

            Check($"armour raises what the buggy can take ({stockMax:0} -> {condition.IntegrityMax:0})",
                  condition.IntegrityMax > stockMax * 1.2f);

            Check($"and the plate is on the car, not in the catalogue "
                  + $"({bent:0} -> {condition.Integrity:0})",
                  condition.Integrity > bent + 1f);

            // The crash that took 65 off a stock buggy in #61's suite is the yardstick, and it has to
            // be measured from full or it is not the same crash: the first run of this hit a car
            // still carrying the 50 points of damage above, and then compared what was left against
            // a fraction of the new maximum. Two dents are not one crash.
            while (condition.NeedsRepair) condition.ServerRepair();

            float before = condition.Integrity;
            condition.ServerDamage(65f, "the harness");

            Check($"so the crash that left a stock buggy on 35 is now barely a dent "
                  + $"({before:0} -> {condition.Integrity:0} of {condition.IntegrityMax:0})",
                  condition.Integrity > 35f * 1.5f);

            Debug.Log($"[VehicleUpgradeTest] bolt-on armour: {condition.Report()}.");

            while (condition.NeedsRepair) condition.ServerRepair();
        }

        // ---------------------------------------------------------------- what it will not do

        IEnumerator Refusals(Vehicle buggy, VehicleUpgrades upgrades, PlayerMotor driver, Inventory bag)
        {
            ItemDef engine = Item("engine_kit");
            ItemDef fuel = Item("fuel");

            if (engine == null || fuel == null) yield break;

            bag.ServerClear();
            bag.Add(engine, 1);
            bag.Add(fuel, 1);

            yield return new WaitForSeconds(0.3f);

            Select(bag, engine);
            yield return new WaitForSeconds(0.3f);

            Check("a second engine will not go on top of the first", upgrades.Fit(engine) == null);
            Check("and the crosshair does not offer it",
                  buggy.Prompt == null || !buggy.Prompt.Contains("Fit"));

            int before = bag.CountOf(engine);
            buggy.ServerInteract(driver.NetworkObject);

            yield return new WaitForSeconds(0.4f);

            Check($"so pressing the key does not eat the part ({before} -> {bag.CountOf(engine)})",
                  bag.CountOf(engine) == before);

            Check("a fuel can is not a part", upgrades.Fit(fuel) == null);

            if (buggy.SeatOf(driver.NetworkObject) >= 0) buggy.ServerExit(driver.NetworkObject);

            Debug.Log($"[VehicleUpgradeTest] fully fitted buggy: {upgrades.Report()}");
        }

        // ---------------------------------------------------------------- measuring

        /// <summary>Full throttle, straight, from rest. Metres covered and the best speed seen.</summary>
        IEnumerator Straight(CarController car, Rigidbody body, System.Action<float, float> result)
        {
            yield return new WaitForSeconds(1f);

            Vector3 from = body.position;
            float peak = 0f;

            car.ServerDrive(1f, 0f, handbrake: false);

            float until = Time.time + 8f;
            while (Time.time < until)
            {
                peak = Mathf.Max(peak, body.linearVelocity.magnitude);
                yield return null;
            }

            car.ServerDrive(0f, 0f, handbrake: true);

            result(Vector3.Distance(body.position, from), peak);

            yield return new WaitForSeconds(0.5f);
        }

        /// <summary>Metres per second the cornering test holds the buggy at.</summary>
        const float CornerSpeed = 10f;

        /// <summary>
        /// Six seconds on full lock **at a held speed**. Returns degrees turned, the average
        /// sideways speed, and the speed it actually held.
        ///
        /// The speed cap is the whole test. The first version of this ran at full throttle, and
        /// grippier tyres came out *worse* on both numbers - 425 degrees of yaw against 395 -
        /// because forward grip is grip too: the fitted car reached a higher speed in the same six
        /// seconds, and a faster car on a steering lock that tightens with speed draws a wider
        /// circle. What looked like a failed upgrade was a measurement of acceleration wearing a
        /// cornering costume. Held at ten metres per second both cars are well past what their
        /// tyres can hold at full lock, so what is left is the thing being bought.
        /// </summary>
        IEnumerator Corner(CarController car, Rigidbody body,
                           System.Action<float, float, float> result)
        {
            yield return new WaitForSeconds(1f);

            // Up to speed in a straight line first, so the run-up is not part of the sample.
            float giveUp = Time.time + 10f;
            while (body.linearVelocity.magnitude < CornerSpeed && Time.time < giveUp)
            {
                car.ServerDrive(1f, 0f, handbrake: false);
                yield return null;
            }

            float yaw = 0f;
            float slide = 0f;
            float speed = 0f;
            int samples = 0;
            float last = body.rotation.eulerAngles.y;

            float until = Time.time + 6f;
            while (Time.time < until)
            {
                float now = body.rotation.eulerAngles.y;
                yaw += Mathf.Abs(Mathf.DeltaAngle(last, now));
                last = now;

                float carrying = body.linearVelocity.magnitude;

                slide += Mathf.Abs(Vector3.Dot(body.linearVelocity, body.transform.right));
                speed += carrying;
                samples++;

                car.ServerDrive(carrying < CornerSpeed ? 1f : 0f, 1f, handbrake: false);

                yield return null;
            }

            car.ServerDrive(0f, 0f, handbrake: true);

            result(yaw, samples > 0 ? slide / samples : 0f, samples > 0 ? speed / samples : 0f);

            yield return new WaitForSeconds(0.5f);
        }

        // ---------------------------------------------------------------- scaffolding

        /// <summary>
        /// Buys nothing: puts the part in the bag, selects it and presses the interact key standing
        /// next to the vehicle, which is the whole fitting interaction.
        /// </summary>
        IEnumerator Fit(Vehicle buggy, VehicleUpgrades upgrades, PlayerMotor driver, Inventory bag,
                        string itemId, VehiclePart part)
        {
            ItemDef item = Item(itemId);

            Check($"the catalog has a {itemId}", item != null);
            if (item == null) yield break;

            driver.ServerTeleport(buggy.transform.position + buggy.transform.right * 3f, 0f);

            bag.ServerClear();
            bag.Add(item, 1);

            yield return new WaitForSeconds(0.3f);

            Select(bag, item);

            yield return new WaitForSeconds(0.3f);

            Check($"the crosshair offers to fit the {item.DisplayName}",
                  buggy.Prompt != null && buggy.Prompt.Contains("Fit"));

            buggy.ServerInteract(driver.NetworkObject);

            yield return new WaitForSeconds(0.5f);

            Check($"one press bolts it on ({part} tier {upgrades.TierOf(part)})",
                  upgrades.TierOf(part) == 1);

            Check($"and the part is spent doing it ({bag.CountOf(item)} left)",
                  bag.CountOf(item) == 0);

            Check("and fitting is not boarding", buggy.SeatOf(driver.NetworkObject) < 0);

            // Out of the way before the next measured run: a fitter left standing where the buggy
            // is about to drive a circle is a fitter who gets run over, and #60 is not on trial here.
            driver.ServerTeleport(PadCentre + new Vector3(300f, 2f, 0f), 0f);
        }

        static ItemDef Item(string id)
            => ItemCatalog.Active != null ? ItemCatalog.Active.Find(id) : null;

        static void Select(Inventory bag, ItemDef def)
        {
            for (int i = 0; i < bag.SlotCount; i++)
            {
                if (bag[i].Def != def) continue;

                bag.ServerSelect(i);
                return;
            }
        }

        IEnumerator Park(CarController car, Rigidbody body)
        {
            if (_pad == null)
            {
                _parkedAt = body.position;
                _parkedFacing = body.rotation;

                _pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _pad.name = "VehicleUpgradeTest.Pad";
                _pad.transform.position = PadCentre;
                _pad.transform.localScale = new Vector3(1200f, 1f, 1200f);
            }

            car.ServerDrive(0f, 0f, handbrake: true);
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.position = PadCentre + new Vector3(0f, 1.1f, -6f);
            body.rotation = Quaternion.identity;
            Physics.SyncTransforms();

            yield return new WaitForSeconds(0.5f);
        }

        void Cleanup(Vehicle buggy, CarController car)
        {
            car.ServerRelease();

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
            if (_failed == 0) Debug.Log($"[VehicleUpgradeTest] {_passed} passed, 0 failed.");
            else Debug.LogError($"[VehicleUpgradeTest] {_passed} passed, {_failed} FAILED.");
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[VehicleUpgradeTest] FAILED: {what}.");
        }
    }
}
