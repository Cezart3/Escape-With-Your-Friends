using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.World;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// The acceptance test for #69, run inside a real session. Server side, behind
    /// <c>-voyageTest</c>, and it wants <c>-scene island -noNatives -noAnimals</c>.
    ///
    /// The acceptance is "travel both ways works, and losing the boat is recoverable", and unlike
    /// most of this project's acceptances that is entirely checkable. Four sections, in the order a
    /// group meets them:
    ///
    /// 1. **The gate.** An unpaid-for boat refuses the seat and says how many parts it is short.
    ///    Parts go in through the same key that pours fuel in, and come out of the bag when they do.
    /// 2. **Losing it.** A wrecked hull at sea with the driver still aboard turns up at its mooring,
    ///    repaired, with the crew still on it. That is the whole of "recoverable".
    /// 3. **Out.** Past the edge of the map for a few seconds and the session is on the second
    ///    island - checked by measuring the ground under the player's feet, because
    ///    <see cref="GameSceneLoader.Current"/> is a string this code sets itself and would happily
    ///    say Island2 on an island that never loaded.
    /// 4. **Back.** The same thing in the other direction, on the hull the far island keeps, without
    ///    paying for a second boat.
    ///
    /// The one number it cannot check is whether an hour of grinding for four parts feels earned.
    /// That is <see cref="Economy.EconomyTest"/>'s arithmetic and then the playtest.
    /// </summary>
    public class VoyageTest : MonoBehaviour
    {
        const float WaitForPlayer = 90f;
        const float WaitForVehicle = 60f;

        /// <summary>Seconds the harness gives a wreck to find its way home. The component's own
        /// default is forty-five; nobody wants to watch that.</summary>
        const float RecoverSeconds = 6f;

        /// <summary>Metres past the crossing line the hull is put to leave. The line itself is the
        /// terrain's half-extent plus sixty.</summary>
        const float PastTheLine = 80f;

        /// <summary>Seconds a crossing is allowed to take, hold included.</summary>
        const float CrossingLimit = 40f;

        /// <summary>The first island is 1024m square and the second is 512. Measuring the terrain is
        /// how this suite knows where it actually is.</summary>
        const float FirstIslandSize = 1024f;
        const float SecondIslandSize = 512f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-voyageTest")) return;

            _started = true;

            var go = new GameObject("VoyageTest");
            DontDestroyOnLoad(go);
            go.AddComponent<VoyageTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            if (GameSceneLoader.Current != "Island")
            {
                Debug.LogError($"[VoyageTest] This starts on the first island and '{GameSceneLoader.Current}' "
                               + "is not it. Run with -scene island. Nothing was checked.");
                yield break;
            }

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
                Debug.LogError("[VoyageTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            Vehicle hull = null;
            float vehicleDeadline = Time.time + WaitForVehicle;

            while (Time.time < vehicleDeadline && hull == null)
            {
                hull = Boat();
                if (hull == null) yield return new WaitForSeconds(0.5f);
            }

            if (hull == null)
            {
                Debug.LogError("[VoyageTest] No boat on the first island. Run with -scene island "
                               + "after a POI bake. Nothing was checked.");
                yield break;
            }

            Gate(hull, motor);

            yield return Losing(hull, motor);

            yield return Cross("Island", "Island2", SecondIslandSize, motor);
            yield return Cross("Island2", "Island", FirstIslandSize, motor);

            Report();
        }

        // ---------------------------------------------------------------- 1. the gate

        void Gate(Vehicle hull, PlayerMotor motor)
        {
            var voyage = hull.GetComponent<BoatVoyage>();
            NetworkObject who = motor.NetworkObject;
            var bag = who.GetComponent<Inventory>();

            if (voyage == null || bag == null)
            {
                Fail("the boat has a BoatVoyage and the player has a bag");
                return;
            }

            Check($"a boat is found unfinished ({voyage.Report()})", !voyage.Seaworthy);

            bool boarded = hull.ServerCanBoard(who, out string why);
            Check("an unfinished boat refuses to be boarded", !boarded);
            Check($"and says what it is short of (\"{why}\")",
                  why != null && why.Contains("part"));
            Check("the refusal is the seat, not the throttle", hull.ServerEnter(who) < 0);

            ItemDef part = bag.Catalog != null ? bag.Catalog.Find(BoatVoyage.PartItem) : null;
            if (part == null)
            {
                Fail("the catalog has a boat part");
                return;
            }

            // One at a time, because a part weighs twelve kilos and four of them is most of what a
            // person can carry: this way the suite never assumes it can hold the whole boat at once.
            int handed = 0;
            for (int guard = 0; guard < 12 && !voyage.Seaworthy; guard++)
            {
                // Add hands back what would not fit, so a nought here is a part in the bag.
                if (bag.Add(part, 1) > 0) break;

                int slot = SlotWith(bag, part);
                if (slot < 0) break;

                bag.ServerSelect(slot);

                int before = voyage.Fitted;
                hull.ServerInteract(who);

                if (voyage.Fitted == before) break;
                handed++;
            }

            Check($"{voyage.Needed} parts finish her ({handed} handed over)",
                  voyage.Seaworthy && voyage.Fitted == voyage.Needed);
            Check("and every one of them left the bag", bag.CountOf(part) == 0);
            Check("finished, she takes a driver", hull.ServerCanBoard(who, out _));

            Debug.Log($"[VoyageTest] the boat went from \"{why}\" to {voyage.Report()} "
                      + $"on {handed} press(es) of the same key that pours fuel in.");
        }

        // ---------------------------------------------------------------- 2. losing it

        /// <summary>
        /// Wrecks the thing at sea with somebody at the wheel and waits for it to turn up at home.
        /// Inside the map on purpose: this is a sinking, not a crossing.
        /// </summary>
        IEnumerator Losing(Vehicle hull, PlayerMotor motor)
        {
            var voyage = hull.GetComponent<BoatVoyage>();
            var condition = hull.GetComponent<VehicleCondition>();
            var body = hull.GetComponent<Rigidbody>();
            NetworkObject who = motor.NetworkObject;

            if (voyage == null || condition == null || body == null) yield break;

            voyage.ServerRecoverIn(RecoverSeconds);

            Vector3 mooring = voyage.Mooring;
            Place(hull, body, Sea(-80f));

            yield return new WaitForSeconds(1f);

            int seat = hull.ServerEnter(who);
            Check($"the driver gets aboard the finished boat (seat {seat})", seat == 0);

            condition.ServerDamage(10000f, "the harness");
            Check("a hull wrecked at sea cannot drive", !condition.CanDrive);

            float adriftAt = Vector3.Distance(body.position, mooring);
            float deadline = Time.time + RecoverSeconds + 25f;

            while (Time.time < deadline && Vector3.Distance(body.position, mooring) > 6f)
                yield return null;

            float home = Vector3.Distance(body.position, mooring);

            Check($"a boat that dies {adriftAt:0}m out comes home ({home:0.0}m from its mooring)", home <= 6f);
            Check("and comes home repaired", !condition.NeedsRepair);
            Check("and fuelled", !condition.NeedsFuel);
            Check($"with the crew still on it ({hull.Occupied()} aboard)", hull.Driver != null);

            Debug.Log($"[VoyageTest] wrecked {adriftAt:0}m from the mooring, back alongside it "
                      + $"{home:0.0}m out in {RecoverSeconds:0}s, {condition.Report()}.");

            hull.ServerExit(who);
        }

        // ---------------------------------------------------------------- 3 and 4. the crossings

        /// <summary>
        /// Sails whatever hull this island keeps off the edge of the map and checks that everybody
        /// ends up standing on the other one.
        /// </summary>
        IEnumerator Cross(string from, string to, float expectedSize, PlayerMotor motor)
        {
            Vehicle hull = Boat();
            var voyage = hull != null ? hull.GetComponent<BoatVoyage>() : null;
            var body = hull != null ? hull.GetComponent<Rigidbody>() : null;

            if (hull == null || voyage == null || body == null)
            {
                Fail($"{from} has a boat to leave on");
                yield break;
            }

            Check($"the boat waiting on {from} is seaworthy ({voyage.Report()})", voyage.Seaworthy);

            NetworkObject who = motor.NetworkObject;
            Place(hull, body, Sea(PastTheLine));

            yield return new WaitForSeconds(1f);

            int seat = hull.ServerEnter(who);
            Check($"somebody is at the wheel leaving {from} (seat {seat})", seat == 0);

            float deadline = Time.time + CrossingLimit;
            while (Time.time < deadline && GameSceneLoader.Current != to) yield return null;

            Check($"{from} -> {to}: the session says it arrived", GameSceneLoader.Current == to);

            // Arriving is not instant: the island you left is unloaded after everybody is ashore,
            // and until it goes its boat is still a boat. Wait for the hull to change rather than
            // for a fixed number of frames, which is the same thing said as a guess.
            float settled = Time.time + 15f;
            while (Time.time < settled && Boat() == hull) yield return null;

            yield return null;

            Terrain ground = Terrain.activeTerrain;
            float size = ground != null && ground.terrainData != null ? ground.terrainData.size.x : 0f;

            // The string is this code's own opinion. The ground under the player is not.
            Check($"the ground under them is {to}'s ({size:0}m square, want {expectedSize:0})",
                  Mathf.Abs(size - expectedSize) < 1f);
            Check($"and they are standing in the {to} scene",
                  motor.gameObject.scene.name == to);
            Check("nobody is still sitting in a boat that stayed behind",
                  motor.GetComponent<VehicleRider>() == null
                  || !motor.GetComponent<VehicleRider>().IsSeated);

            Vehicle arrived = Boat();
            var waiting = arrived != null ? arrived.GetComponent<BoatVoyage>() : null;

            Check($"{to} keeps a boat of its own", arrived != null && arrived != hull);
            Check("and the group does not buy a second one",
                  waiting != null && waiting.Seaworthy);

            float walk = arrived != null
                ? Vector3.Distance(motor.transform.position, arrived.transform.position) : -1f;

            Debug.Log($"[VoyageTest] {from} -> {to}: ashore on {size:0}m of terrain, "
                      + $"{walk:0}m from the hull moored there, which is {waiting?.Report()}.");
        }

        // ---------------------------------------------------------------- the sea and the map

        /// <summary>
        /// A point on the water <paramref name="beyondTheLine"/> metres past the crossing line -
        /// negative for out at sea but still on this island's map. Direction is arbitrary: the line
        /// is a circle around the terrain, and outside it there is nothing but water anywhere.
        /// </summary>
        static Vector3 Sea(float beyondTheLine)
        {
            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null || terrain.terrainData == null) return Vector3.zero;

            Vector3 size = terrain.terrainData.size;
            Vector3 centre = terrain.transform.position + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f);

            // 60 is BoatVoyage's own margin. Asking the component where the line is would be nicer;
            // one number in two places is the price of not adding an accessor for it.
            float radius = Mathf.Max(size.x, size.z) * 0.5f + 60f + beyondTheLine;

            var spot = new Vector3(centre.x + radius, 0f, centre.z);
            spot.y = WaterSurface.HeightAt(spot) - 0.35f;
            return spot;
        }

        /// <summary>Puts the hull somewhere, stopped, the way <see cref="BoatTest"/> does.</summary>
        static void Place(Vehicle hull, Rigidbody body, Vector3 where)
        {
            var boat = hull.GetComponent<BoatController>();
            if (boat != null) boat.ServerDrive(0f, 0f, handbrake: true);

            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.position = where;
            body.rotation = Quaternion.identity;
        }

        /// <summary>The boat in whatever scene is loaded, or null.</summary>
        static Vehicle Boat()
            => Vehicle.All.FirstOrDefault(v => v != null && v.IsSpawned
                                               && v.GetComponent<BoatController>() != null);

        static int SlotWith(Inventory bag, ItemDef def)
        {
            for (int slot = 0; slot < bag.SlotCount; slot++)
                if (bag[slot].Def == def && bag[slot].Count > 0) return slot;

            return -1;
        }

        // ---------------------------------------------------------------- bookkeeping

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[VoyageTest] FAILED: {what}.");
        }

        void Fail(string what) => Check(what, false);

        void Report()
        {
            Debug.Log($"[VoyageTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[VoyageTest] {_failed} check(s) failed.");
        }
    }
}
