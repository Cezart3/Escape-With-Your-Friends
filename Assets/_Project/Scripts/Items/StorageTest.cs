using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Items
{
    /// <summary>
    /// The acceptance test for #44, run inside a real session. Server side, behind <c>-chestTest</c>.
    ///
    /// The criterion is one sentence - "no item duplication under concurrent access" - and it needs
    /// two real players, so this waits for a second body the way #42's harness does and refuses to
    /// pretend without one.
    ///
    /// **How the race is actually simulated.** Two clients pressing take on the same pile in the same
    /// tick arrive at the server as two calls, back to back, on one thread, with nothing between
    /// them. So the test makes exactly that call twice in a row on the same slot. That is not an
    /// approximation of the race; it is the race, and anything that survives it survives the real
    /// thing.
    ///
    /// Every step also checks **conservation**: the total number of items across the chest and both
    /// bags, which must never change during a transfer. Duplication makes it go up. The other bug -
    /// items voided because the destination was full and nobody put them back - makes it go down, and
    /// is the one that is easy to write and impossible to notice.
    /// </summary>
    public class StorageTest : MonoBehaviour
    {
        // Generous: the second process has a whole island to load before it can connect, and a
        // harness that gives up early turns a slow disk into a failing test.
        const float WaitForSecondPlayer = 60f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-chestTest")) return;

            _started = true;

            var go = new GameObject("StorageTest");
            DontDestroyOnLoad(go);
            go.AddComponent<StorageTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            Inventory[] bags = System.Array.Empty<Inventory>();
            float deadline = Time.time + WaitForSecondPlayer;

            while (Time.time < deadline && bags.Length < 2)
            {
                bags = FindObjectsByType<Inventory>(FindObjectsSortMode.None)
                       .Where(b => b != null && b.IsSpawned)
                       .ToArray();

                if (bags.Length < 2) yield return new WaitForSeconds(0.5f);
            }

            if (bags.Length < 2)
            {
                Debug.LogError("[StorageTest] Needs two players; start a second process with "
                               + "-client -chestTest. Nothing was checked.");
                yield break;
            }

            Storage chest = null;
            deadline = Time.time + 10f;

            while (Time.time < deadline && chest == null)
            {
                chest = FindObjectsByType<Storage>(FindObjectsSortMode.None)
                        .FirstOrDefault(s => s != null && s.IsSpawned);

                if (chest == null) yield return new WaitForSeconds(0.5f);
            }

            if (chest == null)
            {
                Debug.LogError("[StorageTest] No chest in this scene; run StorageBuilder.Build and "
                               + "TerrainGenerator.GenerateIsland -rebuildPois, then -scene island.");
                yield break;
            }

            Inventory alice = bags[0];
            Inventory bob = bags[1];

            ItemCatalog items = alice.Catalog;
            ItemDef rope = items.Find("rope");
            ItemDef plank = items.Find("plank");
            ItemDef flint = items.Find("flint");

            if (rope == null || plank == null || flint == null)
            {
                Debug.LogError("[StorageTest] The item catalog is missing something; run ItemFactory.Build.");
                yield break;
            }

            alice.ServerClear();
            bob.ServerClear();
            Empty(chest);

            Debug.Log($"[StorageTest] {chest.name}: {chest.Describe()}, two bags of "
                      + $"{alice.SlotCount} and {bob.SlotCount} slots.");

            // ---------------------------------------------------------------- the plain case

            Check("a fresh chest is empty", chest.IsEmpty && chest.SlotCount == 30);

            alice.Add(rope, 8);
            int total = Total(chest, alice, bob);

            Check("eight rope exist", total == 8);

            int moved = chest.ServerDeposit(alice, SlotOf(alice, rope));

            Check("all eight go into the chest", moved == 8 && chest.CountOf(rope) == 8);
            Check("and out of the bag", alice.CountOf(rope) == 0);
            Check("and nothing was created or lost", Total(chest, alice, bob) == total);

            moved = chest.ServerWithdraw(bob, SlotOf(chest, rope));

            Check("somebody else can take them out", moved == 8 && bob.CountOf(rope) == 8);
            Check("the chest is empty again", chest.IsEmpty);
            Check("and still nothing was created or lost", Total(chest, alice, bob) == total);

            // ---------------------------------------------------------------- the race

            bob.ServerClear();
            Empty(chest);
            chest.Add(rope, 10);

            total = Total(chest, alice, bob);
            Check("ten rope are in the chest and nowhere else", total == 10 && chest.CountOf(rope) == 10);

            int ropeSlot = SlotOf(chest, rope);

            // This is the whole issue. Both players are looking at a replicated slot holding ten rope
            // and both press take in the same tick; the server receives two calls with nothing
            // between them. Written as two statements because that is literally what it is.
            int toAlice = chest.ServerWithdraw(alice, ropeSlot, 10);
            int toBob = chest.ServerWithdraw(bob, ropeSlot, 10);

            Check($"only one of them gets the pile (alice {toAlice}, bob {toBob})",
                  toAlice + toBob == 10);
            Check("ten rope still exist, not twenty", Total(chest, alice, bob) == 10);
            Check("and the chest slot is empty", chest[ropeSlot].IsEmpty);

            // Same race, but the pile is big enough for both to get some.
            alice.ServerClear();
            bob.ServerClear();
            Empty(chest);
            chest.Add(plank, 12);

            int plankSlot = SlotOf(chest, plank);
            int firstHalf = chest.ServerWithdraw(alice, plankSlot, 7);
            int secondHalf = chest.ServerWithdraw(bob, plankSlot, 7);

            Check($"a partial take leaves the rest ({firstHalf} then {secondHalf})",
                  firstHalf == 7 && secondHalf == 5);
            Check("twelve planks still exist", Total(chest, alice, bob) == 12);

            // ---------------------------------------------------------------- nothing is voided

            alice.ServerClear();
            bob.ServerClear();
            Empty(chest);

            // A bag that cannot take one more of the thing in the chest. Filled with flint until Add
            // starts refusing, and then asked for flint - an inventory is limited by weight as well
            // as by slots, so "full" has to be asked of the item, not of the slot count.
            alice.Add(flint, alice.SlotCount * flint.MaxStack);
            Check("alice's bag will not take another flint", alice.Add(flint, 1) == 1);

            chest.Add(flint, 6);
            total = Total(chest, alice, bob);

            moved = chest.ServerWithdraw(alice, SlotOf(chest, flint), 6);

            Check("a full bag takes nothing", moved == 0);
            Check("and the flint is still in the chest, not deleted", chest.CountOf(flint) == 6);
            Check("nothing was created or lost", Total(chest, alice, bob) == total);

            // The mirror: a full chest cannot swallow what it has no room for.
            alice.ServerClear();
            Empty(chest);
            chest.Add(flint, chest.SlotCount * flint.MaxStack);

            bob.ServerClear();
            bob.Add(rope, 5);
            total = Total(chest, alice, bob);

            moved = chest.ServerDeposit(bob, SlotOf(bob, rope));

            Check("a full chest takes nothing", moved == 0);
            Check("and the rope stays in the bag, not deleted", bob.CountOf(rope) == 5);
            Check("nothing was created or lost", Total(chest, alice, bob) == total);

            // ---------------------------------------------------------------- refusals

            Empty(chest);
            alice.ServerClear();
            bob.ServerClear();

            Check("an empty slot moves nothing", chest.ServerWithdraw(alice, 0) == 0);
            Check("nor does a slot that does not exist", chest.ServerWithdraw(alice, 9999) == 0);
            Check("nor a negative one", chest.ServerWithdraw(alice, -1) == 0);
            Check("nor a bag that is not there", chest.ServerDeposit(null, 0) == 0);

            // ---------------------------------------------------------------- the key

            alice.Add(rope, 3);
            alice.ServerSelect(SlotOf(alice, rope));

            NetworkObject aliceBody = alice.NetworkObject;

            Check("the chest offers itself to a player", chest.ServerCanInteract(aliceBody));

            chest.ServerInteract(aliceBody);

            Check("full hands put the selected stack in",
                  chest.CountOf(rope) == 3 && alice.CountOf(rope) == 0);

            chest.ServerInteract(aliceBody);

            Check("empty hands take it back out",
                  alice.CountOf(rope) == 3 && chest.CountOf(rope) == 0);

            // ---------------------------------------------------------------- two chests

            Storage[] chests = FindObjectsByType<Storage>(FindObjectsSortMode.None)
                               .Where(s => s != null && s.IsSpawned)
                               .ToArray();

            if (chests.Length >= 2)
            {
                Storage other = chests.First(s => s != chest);

                Empty(chest);
                Empty(other);
                alice.ServerClear();
                alice.Add(plank, 4);

                chest.ServerDeposit(alice, SlotOf(alice, plank));

                Check("one chest does not see into another",
                      chest.CountOf(plank) == 4 && other.CountOf(plank) == 0);
            }
            else
            {
                Debug.Log("[StorageTest] Only one chest in this scene, so the independence check is "
                          + "skipped.");
            }

            // Left with something in it so the client's log has something to report.
            Empty(chest);
            alice.ServerClear();
            bob.ServerClear();
            chest.Add(rope, 4);
            chest.Add(plank, 9);

            string line = $"[StorageTest] {_passed} passed, {_failed} failed. "
                          + $"end: {chest.Describe()}";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Every item in the chest and both bags. The number that must not move during a transfer,
        /// in either direction.
        /// </summary>
        static int Total(Storage chest, Inventory a, Inventory b)
            => chest.TotalItems + Carried(a) + Carried(b);

        static int Carried(Inventory bag)
        {
            int total = 0;
            for (int i = 0; i < bag.SlotCount; i++) total += bag[i].Count;

            return total;
        }

        static void Empty(Storage chest)
        {
            for (int i = 0; i < chest.SlotCount; i++) chest.TakeSlot(i);
        }

        static int SlotOf(Inventory bag, ItemDef def)
        {
            for (int i = 0; i < bag.SlotCount; i++)
                if (bag[i].Def == def) return i;

            return -1;
        }

        static int SlotOf(Storage chest, ItemDef def)
        {
            for (int i = 0; i < chest.SlotCount; i++)
                if (chest[i].Def == def) return i;

            return -1;
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[StorageTest] FAILED: {what}.");
        }
    }
}
