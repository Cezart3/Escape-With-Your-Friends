using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The acceptance test for #70, run inside a real session. Server side, behind <c>-partTest</c>,
    /// and it wants <c>-scene island2 -noNatives -noAnimals</c>.
    ///
    /// The acceptance — *"hauling parts is a comedy set piece, not a fetch-quest chore"* — is a
    /// judgement a headless run cannot make. What it can do is check every mechanism the joke is
    /// built out of, because each one of them is a thing that stops being funny the moment it stops
    /// working:
    ///
    /// 1. **Three parts, out where the danger is.** Each one a long walk from the beachhead and
    ///    standing at a landmark rather than on an empty hillside.
    /// 2. **One person can lift one, and crawls.** The speed penalty is the whole design; a part you
    ///    can sprint home with is a fetch quest.
    /// 3. **A second pair of hands is worth having, and easy to lose.** Grab the other end and both
    ///    of you speed up; wander off and the carrier is back to a crawl.
    /// 4. **Both hands are both hands.** No carrying two parts, and no part while a friend is on
    ///    your shoulder.
    /// 5. **A punch drops it.** The set piece is two hundred metres of hauling undone by one dart,
    ///    so the drop is checked as carefully as the lift.
    ///
    /// The pickup goes in through <see cref="PlanePart.ServerInteract"/>, which is the same door
    /// <see cref="PlayerInteractor"/>'s RPC uses. Only the aiming is stubbed out — there is no camera
    /// in a headless run to point at anything. Same arrangement as <c>-carryTest</c>.
    /// </summary>
    public class PartTest : MonoBehaviour
    {
        const float WaitForPlayers = 90f;
        const float WaitForParts = 60f;

        /// <summary>How far from the beachhead a part has to be to count as a journey. Metres.</summary>
        const float MinFromCamp = 40f;

        /// <summary>How close to a landmark a part has to be to count as "at" it. Metres.</summary>
        const float AtALandmark = 25f;

        /// <summary>Where the helper is sent to prove that a grip can be lost. Metres.</summary>
        const float WanderOff = 20f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-partTest")) return;

            _started = true;

            var go = new GameObject("PartTest");
            DontDestroyOnLoad(go);
            go.AddComponent<PartTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            if (GameSceneLoader.Current != "Island2")
            {
                Debug.LogError($"[PartTest] The parts are on the second island and "
                               + $"'{GameSceneLoader.Current}' is not it. Run with -scene island2. "
                               + "Nothing was checked.");
                yield break;
            }

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
                Debug.LogError("[PartTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            PlayerMotor carrier = players[0];
            PlayerMotor mate = players.Length > 1 ? players[1] : null;

            float partsDeadline = Time.time + WaitForParts;
            while (Time.time < partsDeadline && PlanePart.All.Count < 3)
                yield return new WaitForSeconds(0.5f);

            if (PlanePart.All.Count == 0)
            {
                Debug.LogError("[PartTest] No plane parts on the island. Run with -scene island2 "
                               + "after a POI bake. Nothing was checked.");
                yield break;
            }

            Scattered();

            yield return Hauling(carrier, mate);

            Report();
        }

        // ---------------------------------------------------------------- 1. where they are

        void Scattered()
        {
            Check($"there are three parts ({PlanePart.All.Count} found)", PlanePart.All.Count == 3);

            var labels = PlanePart.All.Select(p => p.Label).ToArray();
            Check($"each one is a different piece ({string.Join(", ", labels)})",
                  labels.Distinct().Count() == labels.Length);

            Transform camp = Camp();
            Check("the island has a camp to haul them back to", camp != null);

            var where = new System.Collections.Generic.List<string>();

            foreach (PlanePart part in PlanePart.All)
            {
                float walk = camp != null ? Flat(part.transform.position, camp.position) : -1f;
                if (camp != null)
                    Check($"the {part.Label} is a walk from camp ({walk:0}m)", walk >= MinFromCamp);

                Landmark nearest = Landmark.Nearest(part.transform.position);
                float toIt = nearest != null ? Flat(part.transform.position, nearest.transform.position) : -1f;

                Check($"the {part.Label} is lying at {(nearest != null ? nearest.Id : "nothing")}"
                      + $" ({toIt:0}m)", nearest != null && toIt <= AtALandmark);

                Check($"nobody is holding the {part.Label} yet", !part.IsCarried);
                Check($"the crosshair offers to lift it (\"{part.Prompt}\")",
                      !string.IsNullOrEmpty(part.Prompt));

                where.Add($"{part.Label} at {(nearest != null ? nearest.Id : "nowhere")}, {walk:0}m out");
            }

            Debug.Log($"[PartTest] {string.Join("; ", where)}, all of it on foot.");

            Check($"the objective points at one of them (\"{Objective.Text}\")",
                  Objective.Active && PlanePart.All.Any(p => Objective.Text.Contains(p.Label)));
        }

        // ---------------------------------------------------------------- 2-5. carrying one

        IEnumerator Hauling(PlayerMotor carrier, PlayerMotor mate)
        {
            PlanePart part = PlanePart.All[0];
            PlanePart other = PlanePart.All.Count > 1 ? PlanePart.All[1] : null;

            NetworkObject who = carrier.GetComponent<NetworkObject>();
            Vector3 lying = part.transform.position;

            Stand(carrier, lying + Vector3.back * 1.5f);
            yield return null;

            Check($"a free hand may lift the {part.Label}", part.ServerCanInteract(who));

            part.ServerInteract(who);
            yield return null;
            yield return null;

            Check($"the {part.Label} is on somebody's shoulder", part.Carrier == who);
            Check("and it is off the ground it was lying on",
                  Flat(part.transform.position, lying) > 0.5f);

            float alone = PlanePart.SpeedFor(who);
            Check($"hauling it alone is a crawl ({alone:0.00}x)", alone < 0.6f);

            // It follows the shoulder rather than being parented to it, so "does it follow" is a
            // real question and not a tautology. One frame of lag is the design; ten metres is not.
            Transform socket = who.GetComponent<ICarryHolder>()?.CarrySocket;
            Check("the shoulder it rides on exists", socket != null);

            Stand(carrier, lying + Vector3.forward * 30f);
            yield return null;
            yield return null;

            float onShoulder = socket != null ? Vector3.Distance(part.transform.position, socket.position) : 99f;
            Check($"it comes with you ({onShoulder:0.00}m off the shoulder after 30m)", onShoulder < 1.5f);

            Debug.Log($"[PartTest] the {part.Label} went from lying on the ground to riding a shoulder "
                      + $"at {alone:0.00}x, still {onShoulder:0.00}m off it after 30m of walking.");

            // 4. Both hands.
            if (other != null)
                Check($"the same hands cannot also take the {other.Label}", !other.ServerCanInteract(who));

            // 3. The other end.
            if (mate != null)
            {
                NetworkObject second = mate.GetComponent<NetworkObject>();

                // Carry it home first. Every part lies inside the place that wants you dead, and
                // thirty metres of walking does not leave a village: #72's rebake moved the ground
                // around and a headhunter reached the second player mid-check -
                //
                //   [Native] headhunter 16 claimed 21 3.0m away; hauling to (-102.00, 11.42, -42.00).
                //
                // which is the natives working, not the carry failing. A check that stages itself in
                // an aggro radius is measuring two things and reporting one.
                Transform camp = Camp();
                if (camp != null)
                {
                    Stand(carrier, camp.position + Vector3.back * 6f);
                    yield return null;
                    yield return null;
                }

                Stand(mate, part.transform.position + Vector3.right * 1.5f);
                yield return null;

                Check("a second pair of hands is offered the other end",
                      part.Prompt != null && part.Prompt.Contains("other end"));

                part.ServerInteract(second);
                yield return null;

                Check("and takes it", part.Helper == second);
                Check("with nothing left to offer a third person", string.IsNullOrEmpty(part.Prompt));

                float shared = PlanePart.SpeedFor(who);
                Check($"two of them move much better than one ({alone:0.00}x -> {shared:0.00}x)",
                      shared > alone + 0.2f);
                Check("and the helper is slowed the same amount",
                      Mathf.Approximately(PlanePart.SpeedFor(second), shared));

                Stand(mate, part.transform.position + Vector3.right * WanderOff);
                yield return null;
                yield return null;

                Check($"a helper who wanders {WanderOff:0}m off loses their grip", part.Helper == null);
                Check("and the carrier is back to a crawl",
                      Mathf.Approximately(PlanePart.SpeedFor(who), alone));
                Check("while the helper walks away unencumbered",
                      Mathf.Approximately(PlanePart.SpeedFor(second), 1f));

                Debug.Log($"[PartTest] a second pair of hands took it from {alone:0.00}x to {shared:0.00}x, "
                          + $"and {WanderOff:0}m of wandering took it straight back to {alone:0.00}x.");
            }
            else
            {
                Fail("a second player joined to take the other end");
            }

            // 5. A punch drops it.
            Vector3 dropZone = carrier.transform.position;
            var stun = carrier.GetComponent<StunState>();

            if (stun != null)
            {
                stun.ServerStun(1.5f);
                yield return null;
                yield return null;

                Check("one punch and the part is in the mud", !part.IsCarried);
                Check($"where you were standing ({Flat(part.transform.position, dropZone):0.0}m)",
                      Flat(part.transform.position, dropZone) < 4f);
                Check("and moving under its own weight again",
                      part.GetComponent<Rigidbody>() != null && !part.GetComponent<Rigidbody>().isKinematic);
                Check("carrying nothing costs nothing", Mathf.Approximately(PlanePart.SpeedFor(who), 1f));

                Debug.Log($"[PartTest] one punch and the {part.Label} was in the mud "
                          + $"{Flat(part.transform.position, dropZone):0.0}m from where the carrier stood, "
                          + "moving under its own weight again.");

                stun.ServerClearStun();
                yield return null;
            }
            else
            {
                Fail("the carrier can be stunned at all");
            }

            // Picked back up and put down on purpose, which is the same key twice.
            Stand(carrier, part.transform.position + Vector3.back * 1.5f);
            yield return null;

            part.ServerInteract(who);
            yield return null;

            Check("it can be picked back up off the floor", part.Carrier == who);

            part.ServerInteract(who);
            yield return null;

            Check("and put down again with the same key", !part.IsCarried);
            Check($"leaving the objective still asking for one (\"{Objective.Text}\")",
                  Objective.Active && PlanePart.All.Any(p => Objective.Text.Contains(p.Label)));
        }

        // ---------------------------------------------------------------- helpers

        static Transform Camp()
        {
            // The catalog id, not the prefab's: POISpawner overwrites Landmark.Id with the entry
            // it placed, so the beachhead answers to "camp.base" and not to "BaseCamp".
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
            Debug.LogError($"[PartTest] FAILED: {what}.");
        }

        void Fail(string what) => Check(what, false);

        void Report()
        {
            Debug.Log($"[PartTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[PartTest] {_failed} check(s) failed.");
        }
    }
}
