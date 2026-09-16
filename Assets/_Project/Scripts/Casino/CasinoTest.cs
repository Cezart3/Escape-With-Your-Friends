using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.UI;
using EscapeWithYourFriends.World;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>
    /// The acceptance test for #65, behind <c>-casinoTest</c>. Wants <c>-scene island -noNatives
    /// -noAnimals</c>.
    ///
    /// The acceptance is "it reads as a casino built by people stranded on an island", which is a
    /// thing a person decides by looking at it. What a harness can hold is everything that would
    /// have to be true first, and every one of these has been wrong at some point in a build:
    ///
    /// 1. There is a room. Walls on three sides, a doorway in the fourth, a floor, a roof.
    /// 2. The table is inside it, not clipping a wall and not out on the sand - the roulette table
    ///    is placed by the POI catalogue and the building by the greybox builder, and nothing but
    ///    arithmetic keeps those two agreeing.
    /// 3. There is somewhere to sit and something to drink at, because a room with one table in it
    ///    is a room, not a casino.
    /// 4. The lights are on, there are several of them, and they are not all the same colour.
    /// 5. The board over the table says the right words in each of the three states a table can be
    ///    in, which is the half of the UI a headless run can actually read.
    /// </summary>
    public class CasinoTest : MonoBehaviour
    {
        const float WaitForWorld = 60f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-casinoTest")) return;

            _started = true;

            var go = new GameObject("CasinoTest");
            DontDestroyOnLoad(go);
            go.AddComponent<CasinoTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            Landmark casino = null;
            RouletteWheel table = null;
            PlayerMotor player = null;

            float deadline = Time.time + WaitForWorld;

            while (Time.time < deadline && (casino == null || table == null || player == null))
            {
                table ??= FindObjectsByType<RouletteWheel>(FindObjectsSortMode.None)
                          .FirstOrDefault(t => t != null && t.IsSpawned);

                // By proximity to the table rather than by id. The building's own Landmark.Id and
                // the POI catalogue's entry name are set by two different systems and need not be
                // the same string; what this suite means by "the casino" is the room the table is
                // standing in, so that is what it looks for.
                if (table != null)
                    casino = FindObjectsByType<Landmark>(FindObjectsSortMode.None)
                             .Where(l => l != null)
                             .OrderBy(l => (l.transform.position - table.transform.position).sqrMagnitude)
                             .FirstOrDefault(l => (l.transform.position - table.transform.position)
                                                  .sqrMagnitude < 25f * 25f);

                player ??= FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                           .FirstOrDefault(m => m != null && m.IsSpawned);

                if (casino == null || table == null || player == null)
                    yield return new WaitForSeconds(0.5f);
            }

            if (casino == null || table == null || player == null)
            {
                Debug.LogError($"[CasinoTest] Nothing to check: casino={casino != null}, "
                               + $"table={table != null}, player={player != null}.");
                yield break;
            }

            Debug.Log($"[CasinoTest] the table is standing in \"{casino.Id}\" ({casino.DisplayName}), "
                      + $"{Vector3.Distance(casino.transform.position, table.transform.position):0.0}m "
                      + "from its marker.");

            Room(casino);
            Furniture(casino);
            Lighting(casino);
            Fits(casino, table);

            yield return Board(table, player);

            Report();
        }

        // ---------------------------------------------------------------- is it a building

        void Room(Landmark casino)
        {
            Check("the casino has a floor", Part(casino, "Floor") != null);
            Check("and a roof", Part(casino, "Roof") != null);

            Check("three walls", Part(casino, "Wall.Back") != null && Part(casino, "Wall.Left") != null
                                && Part(casino, "Wall.Right") != null);

            Transform left = Part(casino, "Wall.FrontLeft");
            Transform right = Part(casino, "Wall.FrontRight");

            Check("and a front with a doorway in it rather than a missing wall",
                  left != null && right != null);

            if (left == null || right == null) return;

            // The gap between the inner faces of the two front stubs. Two people wide or it is a
            // funnel, and four players arrive at once.
            float gap = Mathf.Abs(right.localPosition.x - right.localScale.x * 0.5f
                                  - (left.localPosition.x + left.localScale.x * 0.5f));

            Check($"the doorway is wide enough to walk through ({gap:0.0}m)", gap >= 1.6f);
        }

        void Furniture(Landmark casino)
        {
            int stools = Parts(casino, "Stool").Count(t => !t.name.Contains("Cushion"));
            Check($"there is somewhere to sit ({stools} stools)", stools >= 4);

            Check("there is a bar", Part(casino, "Bar") != null);

            int bottles = Parts(casino, "Bar.Bottle").Count();
            Check($"with something on it ({bottles} bottles)", bottles >= 4);

            Check("and a sign over the door", Part(casino, "Sign") != null);

            Transform sign = Part(casino, "Sign");
            Check("hung by somebody who did not measure",
                  sign != null && Mathf.Abs(Mathf.DeltaAngle(sign.localEulerAngles.z, 0f)) > 1f);
        }

        void Lighting(Landmark casino)
        {
            Light[] lamps = casino.GetComponentsInChildren<Light>(includeInactive: true);

            Check($"the lights are on ({lamps.Length} of them)", lamps.Length >= 4);

            Check("none of them casts shadows, on a GPU that cannot afford it",
                  lamps.All(l => l.shadows == LightShadows.None));

            int colours = lamps.Select(l => ColorUtility.ToHtmlStringRGB(l.color)).Distinct().Count();
            Check($"and no two agree on a colour ({colours} of {lamps.Length})", colours >= 4);

            Check("something is animating them", casino.GetComponent<TackyLights>() != null);

            Debug.Log($"[CasinoTest] the room: {lamps.Length} lamps, "
                      + $"{Parts(casino, "Stool").Count(t => !t.name.Contains("Cushion"))} stools, "
                      + $"{Parts(casino, "Bar.Bottle").Count()} bottles behind the bar.");
        }

        /// <summary>
        /// The table and the building are placed by two different systems - the POI catalogue and the
        /// greybox builder - and nothing but arithmetic keeps them agreeing. A table half inside a
        /// wall is the single most likely way this issue breaks later.
        /// </summary>
        void Fits(Landmark casino, RouletteWheel table)
        {
            Vector3 local = casino.transform.InverseTransformPoint(table.transform.position);

            Check($"the table is inside the room (local {local.x:0.0}, {local.z:0.0})",
                  Mathf.Abs(local.x) < 4.2f && local.z > -3.8f && local.z < 1.8f);

            Check($"and standing on the floor, not in it ({local.y:0.00}m)",
                  Mathf.Abs(local.y) < 0.4f);

            // From the table's edge, not its middle. A gap measured to the origin of a 3.4m table
            // reads almost two metres wider than the one a player actually walks through.
            const float HalfTable = 1.7f;
            float toWall = 4.25f - Mathf.Abs(local.x) - HalfTable;

            Check($"with room to walk round it ({toWall:0.0}m from its edge to the side wall)",
                  toWall > 1f);
        }

        // ---------------------------------------------------------------- what the board says

        IEnumerator Board(RouletteWheel table, PlayerMotor player)
        {
            var wallet = player.GetComponent<Wallet>();
            wallet.ServerSetChips(500);

            player.ServerTeleport(table.transform.position + Vector3.forward * 2f, 0f);
            yield return new WaitForSeconds(0.3f);

            Check("the board appears when you are at the table",
                  CasinoBoard.Nearest(player.transform.position) == table);

            player.ServerTeleport(table.transform.position + Vector3.forward * 30f, 0f);
            yield return new WaitForSeconds(0.3f);

            Check("and not when you are across the room",
                  CasinoBoard.Nearest(player.transform.position) == null);

            player.ServerTeleport(table.transform.position + Vector3.forward * 2f, 0f);
            yield return new WaitForSeconds(0.3f);

            Check("before the first spin it shows a dash, not a zero",
                  CasinoBoard.Number(-1) == "--");

            Check("and a number once there is one", CasinoBoard.Number(17) == "17");

            string idle = CasinoBoard.Line(table, wallet.Chips);
            Check($"an empty table asks for bets (\"{idle}\")",
                  idle.Contains("PLACE YOUR BETS") && idle.Contains("500 chips"));

            BetSpot red = table.GetComponentsInChildren<BetSpot>().First(s => s.Kind == BetKind.Red);
            table.ServerSetTiming(betWindow: 3f, spinSeconds: 1f);
            red.ServerInteract(player.NetworkObject);

            yield return new WaitForSeconds(0.3f);

            string open = CasinoBoard.Line(table, wallet.Chips);
            Check($"a table with money on it says so (\"{open}\")",
                  open.Contains("BETS OPEN") && open.Contains("on the cloth"));

            Check("and the pot is replicated, not read off the server's own list",
                  table.Pot == 100);

            table.ServerCallIt();

            float giveUp = Time.time + 10f;
            while (!table.Spinning && Time.time < giveUp) yield return null;

            string closed = CasinoBoard.Line(table, wallet.Chips);
            Check($"and closes when the wheel turns (\"{closed}\")", closed.Contains("NO MORE BETS"));

            while (table.Spinning && Time.time < giveUp) yield return null;

            Check("the last number keeps its colour on the board",
                  CasinoBoard.Colour(table.Result, flashing: true)
                  != CasinoBoard.Colour(table.Result == 0 ? 1 : 0, flashing: true));

            Debug.Log($"[CasinoTest] the board: \"{CasinoBoard.Number(table.Result)}\" over "
                      + $"\"{CasinoBoard.Line(table, wallet.Chips)}\".");
        }

        // ---------------------------------------------------------------- scaffolding

        static Transform Part(Landmark casino, string name)
            => casino.GetComponentsInChildren<Transform>(includeInactive: true)
                     .FirstOrDefault(t => t.name == name);

        static System.Collections.Generic.IEnumerable<Transform> Parts(Landmark casino, string prefix)
            => casino.GetComponentsInChildren<Transform>(includeInactive: true)
                     .Where(t => t.name.StartsWith(prefix));

        void Report()
        {
            if (_failed == 0) Debug.Log($"[CasinoTest] {_passed} passed, 0 failed.");
            else Debug.LogError($"[CasinoTest] {_passed} passed, {_failed} FAILED.");
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                Debug.Log($"[CasinoTest] PASS: {what}");
            }
            else
            {
                _failed++;
                Debug.LogError($"[CasinoTest] FAIL: {what}");
            }
        }
    }
}
