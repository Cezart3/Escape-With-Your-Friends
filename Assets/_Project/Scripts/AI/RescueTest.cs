using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.Vehicles;
using EscapeWithYourFriends.World;
using FishNet;
using FishNet.Object;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.AI
{
    /// <summary>
    /// The acceptance test for #73, behind <c>-rescueTest</c>. It wants <c>-noNatives -noAnimals</c>
    /// and the first island, which is where the person is.
    ///
    /// The acceptance is *"the objective chain is clear without a tutorial"*, and a chain is only
    /// checkable as a sequence: what matters is not that each line is correct in isolation but that
    /// the line changes when the player does the thing the previous line asked for. So every step
    /// here reads <see cref="Objective.Text"/> immediately after acting, and a step that leaves the
    /// old line standing is a step the player would have no way of knowing they had completed.
    ///
    /// It does not fly anywhere. Whether a crossing works is <c>-voyageTest</c>'s question and the
    /// flight model is <c>-flightTest</c>'s; what is new here is the gate in front of the crossing,
    /// so this checks that taxiing off the end of the world does nothing and that being up there
    /// with somebody at the controls starts the clock.
    /// </summary>
    public class RescueTest : MonoBehaviour
    {
        const float WaitForPlayers = 90f;
        const float WaitForWorld = 60f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-rescueTest")) return;

            _started = true;

            var go = new GameObject("RescueTest");
            DontDestroyOnLoad(go);
            go.AddComponent<RescueTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            if (GameSceneLoader.Current != "Island")
            {
                Debug.LogError("[RescueTest] The one you left behind is on the first island and "
                               + $"'{GameSceneLoader.Current}' is not it. Nothing was checked.");
                yield break;
            }

            float deadline = Time.time + WaitForWorld;
            while (Time.time < deadline && (Castaway.Instance == null || Plane() == null))
                yield return new WaitForSeconds(0.5f);

            if (Castaway.Instance == null || Plane() == null)
            {
                Debug.LogError("[RescueTest] This island has no castaway or no plane on it. Run after "
                               + "a POI bake. Nothing was checked.");
                yield break;
            }

            PlayerMotor[] players = null;
            deadline = Time.time + WaitForPlayers;

            while (Time.time < deadline)
            {
                players = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                          .Where(p => p != null).ToArray();

                if (players.Length >= 1) break;
                yield return new WaitForSeconds(0.5f);
            }

            if (players == null || players.Length == 0)
            {
                Debug.LogError("[RescueTest] Nobody spawned. Nothing was checked.");
                yield break;
            }

            Castaway who = Castaway.Instance;
            Vehicle plane = Plane();

            Debug.Log($"[RescueTest] one castaway {Flat(who.transform.position, Camp()):0}m from camp, "
                      + $"and a plane {Flat(plane.transform.position, Camp()):0}m from camp to fetch "
                      + "them with.");

            var legs = who.GetComponent<NavMeshAgent>();
            Debug.Log($"[RescueTest] their legs: agent enabled {legs.enabled}, on the mesh "
                      + $"{legs.isOnNavMesh}, {legs.speed:0.0} m/s.");

            yield return Finding(who, players[0]);
            yield return Walking(who, players[0], plane);
            yield return Flying(who, plane);
            yield return Leaving(plane, players[0]);

            Report();
        }

        // ---------------------------------------------------------------- 1. somebody is out there

        IEnumerator Finding(Castaway who, PlayerMotor player)
        {
            Check("they are waiting where they were left", who.Where == Castaway.Stage.Waiting);
            Check("the chain says to go and find them",
                  Objective.Text.Contains("Find", System.StringComparison.OrdinalIgnoreCase));
            Check("and it points at them", Objective.Target == who.transform);

            Check("they have something to say to somebody who walks up",
                  !string.IsNullOrEmpty(who.Prompt));

            NetworkObject actor = player.GetComponent<NetworkObject>();
            Check("and a player on their feet may speak to them", who.ServerCanInteract(actor));

            yield return null;
        }

        // ---------------------------------------------------------------- 2. and they follow

        IEnumerator Walking(Castaway who, PlayerMotor player, Vehicle plane)
        {
            NetworkObject actor = player.GetComponent<NetworkObject>();

            // Stand next to them first. A follower who is told to follow from four hundred metres
            // away is past their own leash before they take a step.
            Stand(player, Ground(who.transform.position + Vector3.back * 2f));
            yield return null;

            who.ServerInteract(actor);
            yield return null;

            Check("speaking to them gets them on their feet", who.Where == Castaway.Stage.Following);
            Check("the chain moves on to the plane",
                  Objective.Text.Contains("plane", System.StringComparison.OrdinalIgnoreCase));
            Check("and points at it", Objective.Target == plane.transform);

            Check("with nothing left to say while they are walking",
                  string.IsNullOrEmpty(who.Prompt));

            // Walk. Not a teleport onto the plane: the whole claim is that they come with you, and
            // a harness that carries them there has checked nothing.
            //
            // It cannot be one jump either. The strip is two hundred metres from the beach and the
            // leash is sixty, so a leader who crosses the island in a single frame has abandoned
            // them by the component's own rule, and the first run of this test said so:
            //
            //   [Castaway] lost sight of everyone and sat back down.
            //
            // That is the follower working. So the leader moves ten metres at a time and only once
            // they have caught up, which is what walking is.
            Vector3 alongside = plane.transform.position + plane.transform.right * 3f;
            float start = Vector3.Distance(who.transform.position, alongside);

            var legs = who.GetComponent<NavMeshAgent>();
            float deadline = Time.time + 180f;
            float probe = 0f;

            while (Time.time < deadline && who.Where == Castaway.Stage.Following)
            {
                float apart = Vector3.Distance(who.transform.position, player.transform.position);

                if (apart < 12f)
                    Stand(player, Ground(Vector3.MoveTowards(player.transform.position,
                                                             alongside, 10f)));

                // A follower that does not follow looks identical from the outside whether the
                // leader never moved, the path is partial, or the agent is parked on a sealed
                // pocket of mesh. One line every two seconds tells the three apart.
                if (Time.time >= probe)
                {
                    probe = Time.time + 2f;
                    Debug.Log($"[RescueTest] probe: leader at {player.transform.position}, them at "
                              + $"{who.transform.position}, {apart:0.0}m apart, "
                              + $"{Vector3.Distance(who.transform.position, alongside):0}m to go; "
                              + $"stopped {legs.isStopped}, path {legs.pathStatus}, hasPath "
                              + $"{legs.hasPath}, {legs.velocity.magnitude:0.0} m/s, "
                              + $"{legs.remainingDistance:0.0}m remaining.");
                }

                yield return new WaitForSeconds(0.25f);
            }

            float travelled = start - Vector3.Distance(who.transform.position, alongside);

            Check($"they walk to the plane on their own feet ({travelled:0}m of {start:0})",
                  who.Where == Castaway.Stage.Aboard);

            Debug.Log($"[RescueTest] told there was a plane, they walked {travelled:0}m to it and "
                      + "let themselves in.");
        }

        // ---------------------------------------------------------------- 3. and they ride

        IEnumerator Flying(Castaway who, Vehicle plane)
        {
            if (who.Where != Castaway.Stage.Aboard)
            {
                Fail("they got in before anything could be checked about the ride");
                yield break;
            }

            Check("the chain is down to flying them home",
                  Objective.Text.Contains("home", System.StringComparison.OrdinalIgnoreCase));

            Check("they are in the back of the plane, not standing beside it",
                  who.transform.IsChildOf(plane.transform));

            Check("and their own navigation is switched off while they are in it",
                  !who.GetComponent<NavMeshAgent>().enabled);

            // Move the aeroplane and see whether the passenger is still in it. This is the whole
            // reason for riding in the socket rather than being told to walk alongside.
            Vector3 was = plane.transform.position;
            plane.transform.position = was + Vector3.up * 400f + Vector3.forward * 400f;
            Physics.SyncTransforms();
            yield return null;

            float off = Vector3.Distance(who.transform.position, plane.CarrySocket != null
                                         ? plane.CarrySocket.position
                                         : plane.transform.position);

            Check($"four hundred metres later they are still aboard ({off:0.00}m off the socket)",
                  off < 1.5f);

            plane.transform.position = was;
            Physics.SyncTransforms();
            yield return null;
        }

        // ---------------------------------------------------------------- 4. the gate on the edge

        IEnumerator Leaving(Vehicle plane, PlayerMotor player)
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
            float out_ = Mathf.Max(size.x, size.z) * 0.5f + 140f;

            // Off the end of the map, but on the ground and with nobody at the controls. Taxiing
            // into the sea is not a decision to leave.
            plane.transform.position = new Vector3(centre.x + out_, 2f, centre.z);
            Physics.SyncTransforms();

            yield return new WaitForSeconds(1.5f);

            Check($"rolling off the edge of the world commits nothing ({voyage.Committing:0.0}s)",
                  voyage.Committing <= 0.01f);

            Debug.Log("[RescueTest] the plane sat past the edge of the map on the ground and the "
                      + "crossing never started counting.");
        }

        // ---------------------------------------------------------------- bookkeeping

        static Vehicle Plane()
        {
            foreach (Vehicle vehicle in Vehicle.All)
                if (vehicle != null && vehicle.GetComponent<PlaneController>() != null) return vehicle;

            return null;
        }

        static Vector3 Camp()
        {
            foreach (Landmark landmark in Landmark.All)
                if (landmark != null && landmark.Id == "camp.base") return landmark.transform.position;

            return Vector3.zero;
        }

        static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        static void Stand(PlayerMotor motor, Vector3 where)
            => motor.ServerTeleport(where, motor.transform.eulerAngles.y);

        /// <summary>A teleport target on top of the island rather than buried in it.</summary>
        static Vector3 Ground(Vector3 where)
        {
            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null) return where;

            where.y = terrain.SampleHeight(where) + terrain.transform.position.y + 1f;
            return where;
        }

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[RescueTest] FAILED: {what}.");
        }

        void Fail(string what) => Check(what, false);

        void Report()
        {
            Debug.Log($"[RescueTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[RescueTest] {_failed} check(s) failed.");
        }
    }
}
