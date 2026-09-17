using System.Collections;
using System.Linq;
using EscapeWithYourFriends.AI;
using EscapeWithYourFriends.Casino;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.Vehicles;
using EscapeWithYourFriends.World;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Net
{
    /// <summary>
    /// The acceptance test for #92, behind <c>-achievementTest</c>: *"achievements fire reliably in
    /// multiplayer for all clients"*. A pair on <c>-scene island -noNatives -noAnimals</c>, and
    /// **both** processes take the flag, because the half that matters - a client being told - can
    /// only be checked on the client.
    ///
    /// The host earns each one through the real hook, not by calling the award itself: the guest
    /// dies eleven times, bets everything until it is gone, and is run over by the host, and then
    /// the two of them fly off with the castaway. Each suite then checks that it was told exactly
    /// what it earned and nothing it did not - the driver, not the one under the wheels; the one who
    /// died, not the one who killed them.
    ///
    /// What it cannot check is Steam. Spacewar has none of these ids, and a harness machine may have
    /// no Steam client at all.
    /// </summary>
    public class AchievementTest : MonoBehaviour
    {
        const float WaitForWorld = 60f;
        const float WaitForPlayers = 90f;

        /// <summary>How long a client waits to be told it escaped. Seconds.</summary>
        const float ClientPatience = 240f;

        /// <summary>Spins of all-on-one-number before giving up on losing. 36 in 37 lose.</summary>
        const int MaxSpins = 12;

        const float RunUp = 30f;
        static readonly Vector3 PadCentre = new(-4000f, 100f, 4000f);

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-achievementTest")) return;
            _started = true;

            var go = new GameObject("AchievementTest");
            DontDestroyOnLoad(go);
            go.AddComponent<AchievementTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null) yield return null;

            yield return InstanceFinder.NetworkManager.IsServerStarted ? Server() : Client();

            Debug.Log($"[AchievementTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[AchievementTest] {_failed} check(s) failed.");
        }

        // ---------------------------------------------------------------- the client's half

        IEnumerator Client()
        {
            float deadline = Time.time + ClientPatience;
            while (Time.time < deadline && !Achievements.Unlocked.Contains(Achievements.FirstTry))
                yield return new WaitForSeconds(0.5f);

            // Anything that was going to arrive twice has had time to.
            yield return new WaitForSeconds(3f);

            string got = string.Join(", ", Achievements.Unlocked);
            Debug.Log($"[AchievementTest] this client was told: {got}.");

            Check("died ten times, and was told", Achievements.Unlocked.Contains(Achievements.DiedTen));
            Check("lost it all at the table, and was told", Achievements.Unlocked.Contains(Achievements.LostItAll));
            Check("got out on the first try, and was told", Achievements.Unlocked.Contains(Achievements.FirstTry));
            Check("but was not the one driving", !Achievements.Unlocked.Contains(Achievements.RanOverFriend));
            Check($"and heard each once ({Achievements.Received} message(s))", Achievements.Received == 3);
        }

        // ---------------------------------------------------------------- the host's half

        IEnumerator Server()
        {
            if (GameSceneLoader.Current != "Island")
            {
                Fail($"this runs on the first island, not '{GameSceneLoader.Current}'");
                yield break;
            }

            float deadline = Time.time + WaitForWorld;
            while (Time.time < deadline && (Castaway.Instance == null || Find<PlaneController>() == null
                                            || Find<CarController>() == null || Wheel() == null))
                yield return new WaitForSeconds(0.5f);

            Vehicle plane = Find<PlaneController>();
            Vehicle buggy = Find<CarController>();
            RouletteWheel wheel = Wheel();

            if (Castaway.Instance == null || plane == null || buggy == null || wheel == null)
            {
                Fail("the island has a castaway, a plane, a buggy and a roulette table - run after a POI bake");
                yield break;
            }

            PlayerMotor host = null, guest = null;
            deadline = Time.time + WaitForPlayers;
            while (Time.time < deadline && guest == null)
            {
                PlayerMotor[] players = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                                        .Where(p => p != null && p.IsSpawned).ToArray();
                host = players.FirstOrDefault(p => p.IsOwner);
                guest = players.FirstOrDefault(p => !p.IsOwner);
                yield return new WaitForSeconds(0.5f);
            }

            if (host == null || guest == null)
            {
                Fail("a second player joined to earn things on - run the client with -achievementTest too");
                yield break;
            }

            Achievements.Awarded.Clear();
            RunSummary.Reset();

            yield return Dying(guest);
            yield return Gambling(wheel, guest);
            yield return Driving(buggy, host, guest);
            yield return Leaving(plane, host, guest);

            // The host is a client too, and its own broadcast comes round the same way.
            yield return new WaitForSeconds(1f);

            Debug.Log($"[AchievementTest] awarded: {string.Join(", ", Achievements.Awarded)}; "
                      + $"the host was told: {string.Join(", ", Achievements.Unlocked)}.");

            Check("the host was told it ran somebody over", Achievements.Unlocked.Contains(Achievements.RanOverFriend));
            Check("and that it got out first time", Achievements.Unlocked.Contains(Achievements.FirstTry));
            Check("and nothing that happened to the guest",
                  !Achievements.Unlocked.Contains(Achievements.DiedTen)
                  && !Achievements.Unlocked.Contains(Achievements.LostItAll));
        }

        IEnumerator Dying(PlayerMotor guest)
        {
            var health = guest.GetComponent<Health>();
            NetworkObject who = guest.NetworkObject;

            while (health.Deaths < Achievements.Deaths - 1) yield return Die(health);
            Check($"{health.Deaths} deaths earn nothing", Count(Achievements.DiedTen, who) == 0);

            yield return Die(health);
            Check($"death {health.Deaths} does", Count(Achievements.DiedTen, who) == 1);

            yield return Die(health);
            Check($"and death {health.Deaths} does not earn it twice", Count(Achievements.DiedTen, who) == 1);
            Check("and nobody else died enough", Count(Achievements.DiedTen) == 1);

            Stand(guest);
            yield return null;
        }

        static IEnumerator Die(Health health)
        {
            if (health.IsIncapacitated)
            {
                health.ServerRescue();
                health.ServerRevive(1f);
                yield return null;
            }

            health.ServerKill(new DamageInfo(0f, DamageType.Blunt));
            yield return null;
        }

        IEnumerator Gambling(RouletteWheel wheel, PlayerMotor guest)
        {
            var wallet = guest.GetComponent<Wallet>();
            NetworkObject who = guest.NetworkObject;

            wheel.ServerSetTiming(0.1f, 0.2f);
            wallet.ServerPayChips(10);

            int spins = 0;
            while (wallet.Chips > 0 && spins < MaxSpins)
            {
                // The lot, on one number.
                wheel.ServerPlaceBet(who, BetKind.Straight, 17, wallet.Chips);
                wheel.ServerCallIt();
                spins++;

                float until = Time.time + 5f;
                yield return new WaitForSeconds(0.1f);
                while (Time.time < until && (wheel.Spinning || wheel.Pot > 0)) yield return null;

                if (wallet.Chips > 0)
                    Check($"a spin that leaves {wallet.Chips} chips earns nothing",
                          Count(Achievements.LostItAll, who) == 0);
            }

            Check($"{spins} spin(s) and the chips are gone ({wallet.Chips})", wallet.Chips == 0);
            Check("which earns it, once", Count(Achievements.LostItAll, who) == 1);
            Check("and only them", Count(Achievements.LostItAll) == 1);
        }

        IEnumerator Driving(Vehicle buggy, PlayerMotor host, PlayerMotor guest)
        {
            var car = buggy.GetComponent<CarController>();
            var body = buggy.GetComponent<Rigidbody>();
            var impact = buggy.GetComponent<VehicleImpact>();

            GameObject pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pad.name = "AchievementTest.Pad";
            pad.transform.position = PadCentre;
            pad.transform.localScale = new Vector3(600f, 1f, 600f);

            car.ServerDrive(0f, 0f, handbrake: true);
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.position = PadCentre + new Vector3(0f, 1.1f, -6f);
            body.rotation = Quaternion.identity;
            Physics.SyncTransforms();

            guest.ServerTeleport(PadCentre + new Vector3(0f, 1.2f, RunUp), 180f);

            int seat = buggy.ServerEnter(host.NetworkObject);
            Check($"the host takes the wheel (seat {seat})", seat == 0);

            yield return new WaitForSeconds(1.5f);

            int before = impact.Hits;
            float giveUp = Time.time + 14f;
            while (Time.time < giveUp && impact.Hits == before)
            {
                car.ServerDrive(1f, 0f, handbrake: false);
                yield return null;
            }

            car.ServerDrive(0f, 0f, handbrake: true);

            Check("and drives into the guest", impact.Hits > before);
            Check("which earns the driver it", Count(Achievements.RanOverFriend, host.NetworkObject) == 1);
            Check("and not the one under the wheels", Count(Achievements.RanOverFriend, guest.NetworkObject) == 0);

            buggy.ServerExit(host.NetworkObject);
            yield return new WaitForSeconds(0.5f);

            Stand(guest);
            Destroy(pad);
            yield return null;
        }

        IEnumerator Leaving(Vehicle plane, PlayerMotor host, PlayerMotor guest)
        {
            Castaway who = Castaway.Instance;
            var flight = plane.GetComponent<PlaneController>();
            var body = plane.GetComponent<Rigidbody>();

            var assembly = plane.GetComponent<PlaneAssembly>();
            if (assembly != null) assembly.ServerFitAll();

            // Same staging as -endTest: the plane beside the castaway, the host beside them.
            plane.transform.position = who.transform.position + who.transform.right * 5f;
            if (body != null) body.linearVelocity = Vector3.zero;
            Physics.SyncTransforms();
            yield return null;

            host.ServerTeleport(who.transform.position + Vector3.back * 2f, host.transform.eulerAngles.y);
            guest.ServerTeleport(who.transform.position + Vector3.forward * 2f, guest.transform.eulerAngles.y);
            yield return null;

            who.ServerInteract(host.NetworkObject);

            float boarded = Time.time + 20f;
            while (Time.time < boarded && who.Where != Castaway.Stage.Aboard)
                yield return new WaitForSeconds(0.25f);

            Check($"the castaway is aboard ({who.Where})", who.Where == Castaway.Stage.Aboard);

            int pilot = plane.ServerEnter(host.NetworkObject);
            int passenger = plane.ServerEnter(guest.NetworkObject);
            Check($"both of them are in it (seats {pilot} and {passenger})", pilot == 0 && passenger > 0);
            Check($"and it has never come back down ({flight.Touchdowns})", flight.Touchdowns == 0);

            Terrain terrain = Terrain.activeTerrain;
            Vector3 size = terrain.terrainData.size;
            Vector3 centre = terrain.transform.position + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f);
            float out_ = Mathf.Max(size.x, size.z) * 0.5f + 160f;

            float ended = Time.time + 30f;
            while (Time.time < ended && !RunSummary.Over)
            {
                plane.transform.position = new Vector3(centre.x + out_, 300f, centre.z);
                if (body != null) body.linearVelocity = Vector3.zero;
                Physics.SyncTransforms();
                yield return new WaitForSeconds(0.25f);
            }

            Check("the run ends", RunSummary.Over);
            Check("and the pilot got out first time", Count(Achievements.FirstTry, host.NetworkObject) == 1);
            Check("and so did the passenger", Count(Achievements.FirstTry, guest.NetworkObject) == 1);
        }

        // ---------------------------------------------------------------- bookkeeping

        static void Stand(PlayerMotor player)
        {
            var health = player.GetComponent<Health>();
            if (health.IsIncapacitated)
            {
                health.ServerRescue();
                health.ServerRevive(1f);
            }

            player.GetComponent<StunState>()?.ServerClearStun();
        }

        static int Count(string id, NetworkObject body)
            => Achievements.Awarded.Count(a => a == $"{id} {body.ObjectId}");

        static int Count(string id)
            => Achievements.Awarded.Count(a => a.StartsWith(id + " "));

        static Vehicle Find<T>() where T : Component
            => Vehicle.All.FirstOrDefault(v => v != null && v.IsSpawned && v.GetComponent<T>() != null);

        static RouletteWheel Wheel()
            => FindObjectsByType<RouletteWheel>(FindObjectsSortMode.None).FirstOrDefault(w => w.IsSpawned);

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[AchievementTest] FAILED: {what}.");
        }

        void Fail(string what) => Check(what, false);
    }
}
