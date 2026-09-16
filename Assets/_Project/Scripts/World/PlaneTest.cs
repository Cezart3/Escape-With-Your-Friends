using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The acceptance test for #71, run inside a real session, behind <c>-planeTest</c>. It wants
    /// <c>-scene island2 -noNatives -noAnimals</c>, and unlike every harness before it, <b>both ends
    /// of the pair run a half of it</b>.
    ///
    /// That is not symmetry for its own sake. #71's acceptance is *"progress is legible at a glance
    /// and replicated to all players"*, and the second half of that sentence cannot be checked from
    /// the machine doing the fitting. The server half hauls the three parts home and watches the
    /// holes fill; the client half stands there, touches nothing, and reports what the aeroplane in
    /// front of it looks like. If the bitmask replicates but nobody turns it into a wing, the server
    /// half still passes and the client half fails, which is exactly the bug worth catching.
    ///
    /// The wing goes in first, deliberately. A count would let you fit the wing and watch an engine
    /// appear, and a harness that fitted them in order would never notice, so the out-of-order fit
    /// is checked before the in-order ones.
    /// </summary>
    public class PlaneTest : MonoBehaviour
    {
        const float WaitForPlayers = 90f;
        const float WaitForParts = 60f;

        /// <summary>How long the client half waits for the other machine to finish hauling. Seconds.</summary>
        const float WaitForFinish = 240f;

        /// <summary>How far from the beachhead the plane may stand and still be somewhere you go. Metres.</summary>
        const float NearCamp = 120f;

        /// <summary>Same convention as PlaneAssembly's default prefix.</summary>
        const string Prefix = "Fitted.";

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-planeTest")) return;

            _started = true;

            var go = new GameObject("PlaneTest");
            DontDestroyOnLoad(go);
            go.AddComponent<PlaneTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null
                   || !(InstanceFinder.NetworkManager.IsServerStarted
                        || InstanceFinder.NetworkManager.IsClientStarted))
                yield return null;

            bool server = InstanceFinder.NetworkManager.IsServerStarted;

            // Server side only, because GameSceneLoader.Current is a server-side field: every path
            // that writes it is one the server walks, so on a pure client it is still the empty
            // string it was born as. The client half does not need it - the plane only exists on the
            // second island, so waiting for the plane below asks the same question and gets a real
            // answer on both machines.
            if (server && GameSceneLoader.Current != "Island2")
            {
                Debug.LogError("[PlaneTest] The plane is on the second island and "
                               + $"'{GameSceneLoader.Current}' is not it. Run with -scene island2. "
                               + "Nothing was checked.");
                yield break;
            }

            float deadline = Time.time + WaitForParts;
            while (Time.time < deadline && PlaneAssembly.Instance == null)
                yield return new WaitForSeconds(0.5f);

            if (PlaneAssembly.Instance == null)
            {
                Debug.LogError("[PlaneTest] There is no plane on this island. Run with -scene island2 "
                               + "after a POI bake. Nothing was checked.");
                yield break;
            }

            if (server) yield return Hauling();
            else yield return Watching();

            Report();
        }

        // ---------------------------------------------------------------- the machine doing the work

        IEnumerator Hauling()
        {
            PlayerMotor[] players = null;
            float deadline = Time.time + WaitForPlayers;

            while (Time.time < deadline)
            {
                players = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                          .Where(m => m != null && m.IsSpawned).ToArray();

                if (players.Length >= 2) break;
                yield return new WaitForSeconds(0.5f);
            }

            if (players == null || players.Length == 0)
            {
                Debug.LogError("[PlaneTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            float partsDeadline = Time.time + WaitForParts;
            while (Time.time < partsDeadline && PlanePart.All.Count < 3)
                yield return new WaitForSeconds(0.5f);

            // The second player is waited for and then given a moment, because the other half of this
            // harness has to be looking at an unfinished plane before this half starts finishing it.
            // Teleporting between the three parts takes about a second; a client still loading the
            // island would see nothing but the end state and would be right to call that a failure.
            yield return new WaitForSeconds(5f);

            PlaneAssembly plane = PlaneAssembly.Instance;
            NetworkObject who = players[0].GetComponent<NetworkObject>();

            // ---- 1. a wreck with three holes in it

            Check($"the plane wants three pieces ({plane.Needed} hole(s))", plane.Needed == 3);
            Check($"and has none of them yet ({plane.Fitted} fitted)", plane.Fitted == 0);
            Check($"so nothing is standing on it ({plane.Showing} piece(s) visible)", plane.Showing == 0);
            Check("which is not a finished aeroplane", !plane.Complete);

            Transform camp = Camp();
            float walk = camp != null ? Flat(plane.transform.position, camp.position) : -1f;
            Check($"it stands within reach of the beachhead ({walk:0}m)", camp != null && walk <= NearCamp);

            Check($"and it says what it is missing (\"{plane.Prompt}\")",
                  plane.Prompt != null && plane.Prompt.Contains("engine")
                  && plane.Prompt.Contains("wing") && plane.Prompt.Contains("propeller"));

            Check("empty hands cannot fit anything to it", !plane.ServerCanInteract(who));

            Debug.Log($"[PlaneTest] a plane with {plane.Needed} holes in it, {walk:0}m from camp, and "
                      + $"{PlanePart.All.Count} parts scattered across the island to fill them.");

            // ---- 2. the wing first, which a count would get wrong

            yield return Fit(plane, who, "wing");

            Check("the wing is in", plane.Has("wing"));
            Check("and the engine did not appear along with it", !plane.Has("engine"));
            Check("nor the propeller", !plane.Has("propeller"));
            Check($"one piece is standing on the plane ({plane.Showing})", plane.Showing == 1);
            Check("and it is the wing, not whichever hole came first", Standing(plane, "wing"));
            Check("the engine hole is still a hole", !Standing(plane, "engine"));
            Check($"the prompt has dropped it (\"{plane.Prompt}\")",
                  plane.Prompt != null && !plane.Prompt.Contains("wing"));

            Check($"two parts are left in the world ({PlanePart.All.Count})", PlanePart.All.Count == 2);
            Check("and the hands that carried it are empty again",
                  Mathf.Approximately(PlanePart.SpeedFor(who), 1f));

            Debug.Log("[PlaneTest] the wing went in first and the wing is what appeared; the plane "
                      + $"is still missing {plane.Missing()}.");

            // ---- 3. the other two

            yield return Fit(plane, who, "engine");

            Check("the engine is in", plane.Has("engine"));
            Check($"and two pieces are standing ({plane.Showing})", plane.Showing == 2);
            Check("with the plane still unfinished", !plane.Complete);
            Check("and still able to take the last one", plane.Prompt.Contains("propeller"));

            yield return Fit(plane, who, "propeller");

            Check("the propeller is in", plane.Has("propeller"));
            Check($"all three pieces are standing ({plane.Showing}/{plane.Needed})",
                  plane.Showing == plane.Needed);
            Check("and the plane is finished", plane.Complete);
            Check($"which is what it now says (\"{plane.Prompt}\")", plane.Prompt == "The plane is finished");
            Check("there is nothing left to fit to it", !plane.ServerCanInteract(who));
            Check($"no parts are left anywhere ({PlanePart.All.Count})", PlanePart.All.Count == 0);

            yield return new WaitForSeconds(0.75f);

            Check($"and the objective has moved on (\"{Objective.Text}\")",
                  Objective.Active && Objective.Text.Contains("Get in the plane"));

            Debug.Log("[PlaneTest] three parts hauled home and the plane is whole: "
                      + $"{plane.Showing}/{plane.Needed} pieces standing, nothing left to fetch.");
        }

        /// <summary>
        /// Carries the part with that label to the plane and fits it, through the same two doors a
        /// player uses. The walk is a teleport because #70 already proved the walking works, and
        /// doing it at 0.35x speed would put three real minutes into every run of this harness.
        /// </summary>
        IEnumerator Fit(PlaneAssembly plane, NetworkObject who, string label)
        {
            PlanePart part = PlanePart.All.FirstOrDefault(p => p.Label == label);
            if (part == null) { Fail($"there is a {label} on the island to fetch"); yield break; }

            PlayerMotor motor = who.GetComponent<PlayerMotor>();

            Stand(motor, part.transform.position + Vector3.back * 1.5f);
            yield return null;

            part.ServerInteract(who);
            yield return null;

            if (part.Carrier != who) { Fail($"the {label} can be lifted"); yield break; }

            // Off to one side of the airframe rather than inside it: the boxes are solid and a
            // character controller teleported into one has to be pushed back out again.
            Stand(motor, plane.transform.position + plane.transform.right * 9f);
            yield return null;

            Check($"a pair of hands holding the {label} is offered the plane",
                  plane.ServerCanInteract(who));

            plane.ServerInteract(who);
            yield return null;
            yield return null;

            Check($"and the {label} stops existing as a thing you carry", part == null || !part.IsSpawned);
        }

        // ---------------------------------------------------------------- the machine watching

        /// <summary>
        /// The whole of "replicated to all players". This half never touches a part: it looks at the
        /// aeroplane, waits, and looks again.
        /// </summary>
        IEnumerator Watching()
        {
            PlaneAssembly plane = PlaneAssembly.Instance;

            int sawAtFirst = plane.Showing;
            Check($"the client sees an unfinished plane to begin with ({sawAtFirst}/{plane.Needed})",
                  sawAtFirst < plane.Needed);

            float deadline = Time.time + WaitForFinish;
            while (Time.time < deadline && !plane.Complete)
                yield return new WaitForSeconds(0.5f);

            Check($"the client sees the plane finish ({plane.Fitted}/{plane.Needed} fitted)", plane.Complete);
            Check($"with every piece actually standing on it ({plane.Showing})",
                  plane.Showing == plane.Needed);

            foreach (string label in new[] { "engine", "wing", "propeller" })
                Check($"including the {label}", Standing(plane, label));

            Check($"and no part left lying in the client's world ({PlanePart.All.Count})",
                  PlanePart.All.Count == 0);

            yield return new WaitForSeconds(0.75f);

            Check($"the client is told where to go next (\"{Objective.Text}\")",
                  Objective.Active && Objective.Text.Contains("Get in the plane"));

            Debug.Log($"[PlaneTest] the client watched the plane go from {sawAtFirst}/{plane.Needed} "
                      + $"to {plane.Showing}/{plane.Needed} without carrying a thing.");
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Is that piece actually up on the model, as opposed to merely marked as fitted.</summary>
        static bool Standing(PlaneAssembly plane, string label)
        {
            Transform piece = plane.transform.Find(Prefix + label);
            return piece != null && piece.gameObject.activeSelf;
        }

        static Transform Camp()
        {
            // The catalog id, not the prefab's; see the same lookup in PartTest.
            foreach (Landmark landmark in Landmark.All)
                if (landmark != null && landmark.Id == "camp.base") return landmark.transform;

            return null;
        }

        static void Stand(PlayerMotor motor, Vector3 where)
            => motor.ServerTeleport(where, motor.transform.eulerAngles.y);

        static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        // ---------------------------------------------------------------- bookkeeping

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[PlaneTest] FAILED: {what}.");
        }

        void Fail(string what) => Check(what, false);

        void Report()
        {
            Debug.Log($"[PlaneTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[PlaneTest] {_failed} check(s) failed.");
        }
    }
}
