using System.Collections;
using System.Linq;
using EscapeWithYourFriends.AI;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.Vehicles;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The acceptance test for #74, behind <c>-endTest</c>. It wants <c>-noNatives -noAnimals</c>
    /// and the first island, which is the one you leave from.
    ///
    /// The acceptance is *"the run has a real ending, not a fade to black"*, and the difference
    /// between the two is entirely in whether the ending is about this particular run. So the
    /// checks are: does it end at the right moment, does it end rather than travel, and are the
    /// numbers on it the numbers that actually happened.
    ///
    /// It does not check the panel, because a headless build has no canvas to draw one on. What it
    /// checks is <see cref="RunSummary"/>, which is what the panel reads - the panel is a view of
    /// four integers and the four integers are the part that can be wrong.
    /// </summary>
    public class EndTest : MonoBehaviour
    {
        const float WaitForPlayers = 90f;
        const float WaitForWorld = 60f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-endTest")) return;

            _started = true;

            var go = new GameObject("EndTest");
            DontDestroyOnLoad(go);
            go.AddComponent<EndTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            if (GameSceneLoader.Current != "Island")
            {
                Debug.LogError("[EndTest] You leave from the first island and "
                               + $"'{GameSceneLoader.Current}' is not it. Nothing was checked.");
                yield break;
            }

            float deadline = Time.time + WaitForWorld;
            while (Time.time < deadline && (Castaway.Instance == null || Plane() == null))
                yield return new WaitForSeconds(0.5f);

            Vehicle plane = Plane();
            Castaway who = Castaway.Instance;

            if (who == null || plane == null)
            {
                Debug.LogError("[EndTest] This island has no castaway or no plane on it. Run after a "
                               + "POI bake. Nothing was checked.");
                yield break;
            }

            PlayerMotor[] players = null;
            deadline = Time.time + WaitForPlayers;

            while (Time.time < deadline)
            {
                players = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                          .Where(p => p != null && p.IsSpawned).ToArray();

                if (players.Length >= 1) break;
                yield return new WaitForSeconds(0.5f);
            }

            if (players == null || players.Length == 0)
            {
                Debug.LogError("[EndTest] Nobody spawned. Nothing was checked.");
                yield break;
            }

            RunSummary.Reset();

            yield return Counting(players[0]);
            yield return Gating(plane, players[0], who);
            yield return Ending(plane, players[0], who);

            Report();
        }

        // ---------------------------------------------------------------- 1. the numbers are real

        IEnumerator Counting(PlayerMotor player)
        {
            Check("nothing has ended yet", !RunSummary.Over);

            (int deaths, int gambled, int ranOver, int seconds) = RunSummary.ServerTally();
            Check($"and the tally starts at nothing ({deaths}/{gambled}/{ranOver})",
                  deaths == 0 && gambled == 0 && ranOver == 0);
            Check($"except the clock, which has been running ({seconds}s)", seconds > 0);

            // Deaths are summed off the bodies rather than counted into a field of their own, so
            // the thing worth checking is that a real death reaches the tally. This is the whole
            // reason not to keep a fifth integer: there is nothing to fall out of step.
            var health = player.GetComponent<Health>();
            if (health != null)
            {
                health.ServerKill(new DamageInfo(0f, DamageType.Blunt));
                yield return null;
                yield return null;

                Check($"a death on a body reaches the tally ({RunSummary.ServerTally().deaths})",
                      RunSummary.ServerTally().deaths >= 1);
            }
            else
            {
                Fail("the player has a Health to die from");
            }

            RunSummary.ServerStaked(250);
            RunSummary.ServerStaked(-10);
            Check($"chips staked add up and a negative stake is ignored ({RunSummary.Gambled})",
                  RunSummary.Gambled == 250);

            RunSummary.ServerRanOver();
            Check($"and a friend under the wheels counts once ({RunSummary.RanOver})",
                  RunSummary.RanOver == 1);
        }

        // ---------------------------------------------------------------- 2. it ends on purpose

        IEnumerator Gating(Vehicle plane, PlayerMotor player, Castaway who)
        {
            var voyage = plane.GetComponent<PlaneVoyage>();
            if (voyage == null)
            {
                Fail("the plane knows how to leave the island");
                yield break;
            }

            Terrain terrain = Terrain.activeTerrain;
            Vector3 size = terrain.terrainData.size;
            Vector3 centre = terrain.transform.position + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f);
            float out_ = Mathf.Max(size.x, size.z) * 0.5f + 160f;

            // Out there, high, and nobody in it. The castaway is still on the beach.
            plane.transform.position = new Vector3(centre.x + out_, 300f, centre.z);
            Physics.SyncTransforms();

            yield return new WaitForSeconds(2f);

            Check("an empty plane past the edge ends nothing", !RunSummary.Over);
            Check("and the castaway is still where they were",
                  who.Where == Castaway.Stage.Waiting);
        }

        // ---------------------------------------------------------------- 3. and then it ends

        IEnumerator Ending(Vehicle plane, PlayerMotor player, Castaway who)
        {
            var assembly = plane.GetComponent<PlaneAssembly>();
            if (assembly != null) assembly.ServerFitAll();
            yield return null;

            var flight = plane.GetComponent<PlaneController>();
            Check("the plane is whole enough to fly", flight != null && flight.Flyable);

            NetworkObject actor = player.GetComponent<NetworkObject>();

            // Revive them first: a corpse cannot fly an aeroplane, and section 1 killed this one
            // on purpose.
            var health = player.GetComponent<Health>();
            if (health != null && health.IsIncapacitated)
            {
                health.ServerRescue();
                health.ServerRevive(1f);
                yield return null;
            }

            // Park it beside them. Section 2 left it three hundred metres over open water, and
            // whether they can walk to an aeroplane is -rescueTest's question - this one is about
            // what happens once it leaves with them in it.
            var body = plane.GetComponent<Rigidbody>();
            plane.transform.position = who.transform.position + who.transform.right * 5f;
            if (body != null) body.linearVelocity = Vector3.zero;
            Physics.SyncTransforms();
            yield return null;

            player.ServerTeleport(who.transform.position + Vector3.back * 2f,
                                  player.transform.eulerAngles.y);
            yield return null;

            who.ServerInteract(actor);

            float boarded = Time.time + 20f;
            while (Time.time < boarded && who.Where != Castaway.Stage.Aboard)
                yield return new WaitForSeconds(0.25f);

            Check($"the castaway is aboard ({who.Where})", who.Where == Castaway.Stage.Aboard);

            plane.ServerEnter(actor);
            yield return null;

            Check("and somebody is flying it", plane.Driver != null);

            // Off the map and high, which is the crossing's condition. With the castaway aboard it
            // is not a crossing.
            Terrain terrain = Terrain.activeTerrain;
            Vector3 size = terrain.terrainData.size;
            Vector3 centre = terrain.transform.position + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f);
            float out_ = Mathf.Max(size.x, size.z) * 0.5f + 160f;

            string was = GameSceneLoader.Current;
            float ended = Time.time + 30f;

            while (Time.time < ended && !RunSummary.Over)
            {
                // Held out there rather than thrown once: it falls, and four seconds of falling
                // from a single teleport would put it back inside the margin before it committed.
                plane.transform.position = new Vector3(centre.x + out_, 300f, centre.z);
                if (body != null) body.linearVelocity = Vector3.zero;
                Physics.SyncTransforms();

                yield return new WaitForSeconds(0.25f);
            }

            Check("leaving the map with them aboard ends the run", RunSummary.Over);
            Check($"rather than crossing to the other island ({GameSceneLoader.Current})",
                  GameSceneLoader.Current == was);

            Check($"the ending remembers the death ({RunSummary.Deaths})", RunSummary.Deaths >= 1);
            Check($"the chips ({RunSummary.Gambled})", RunSummary.Gambled == 250);
            Check($"the friend under the wheels ({RunSummary.RanOver})", RunSummary.RanOver == 1);
            Check($"and how long it took ({RunSummary.Seconds}s)", RunSummary.Seconds > 0);

            Debug.Log($"[EndTest] the run ended after {RunSummary.Seconds}s with "
                      + $"{RunSummary.Deaths} death(s), {RunSummary.Gambled} chips gambled and "
                      + $"{RunSummary.RanOver} friend(s) run over, and nobody changed islands.");
        }

        // ---------------------------------------------------------------- bookkeeping

        static Vehicle Plane()
        {
            foreach (Vehicle vehicle in Vehicle.All)
                if (vehicle != null && vehicle.GetComponent<PlaneController>() != null) return vehicle;

            return null;
        }

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[EndTest] FAILED: {what}.");
        }

        void Fail(string what) => Check(what, false);

        void Report()
        {
            Debug.Log($"[EndTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[EndTest] {_failed} check(s) failed.");
        }
    }
}
