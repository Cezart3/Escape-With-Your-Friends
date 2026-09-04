using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using UnityEngine;

namespace EscapeWithYourFriends.Items
{
    /// <summary>
    /// The acceptance test for #41, run inside the game rather than beside it.
    ///
    /// There is no test framework in this project and adding one to check a stacking rule would be
    /// the wrong trade. What is needed is proof that the rules hold *in a real session* - server
    /// authoritative, replicated to a real client, in a build - and that is exactly what this does:
    /// on <c>-invTest</c> the server runs a scripted sequence against the first inventory it sees and
    /// checks the result of every step, while every client logs its own replicated copy.
    ///
    /// If the two descriptions match at the end, the inventory replicates. If every check passes, the
    /// rules hold. Both facts land in the smoke-test log next to everything else.
    /// </summary>
    internal static class InventoryTest
    {
        static bool _ran;
        static int _passed;
        static int _failed;

        /// <summary>Server side. The first inventory to start gets the sequence run against it.</summary>
        internal static void Offer(Inventory inventory)
        {
            if (_ran || inventory == null || !CommandLine.HasFlag("-invTest")) return;

            _ran = true;
            Run(inventory);
        }

        /// <summary>Client side. Logs what actually arrived, which is the half the server cannot prove.</summary>
        internal static void Watch(Inventory inventory)
        {
            if (inventory == null || !CommandLine.HasFlag("-invTest")) return;

            // Only when it actually differs. FishNet holds replicated changes until the start
            // callbacks have run, so a burst of them arrives together describing the same end state,
            // and twenty identical lines say nothing the first one did not.
            string last = null;
            inventory.Changed += () =>
            {
                string now = inventory.Describe();
                if (now == last) return;

                last = now;
                Debug.Log($"[InventoryTest] client sees: {now}");
            };
        }

        static void Run(Inventory inventory)
        {
            ItemCatalog catalog = inventory.Catalog;
            if (catalog == null)
            {
                Debug.LogError("[InventoryTest] The inventory has no catalog; nothing can be tested.");
                return;
            }

            ItemDef rope = Need(catalog, "rope");
            ItemDef hatchet = Need(catalog, "hatchet");
            ItemDef part = Need(catalog, "boat_part");
            if (rope == null || hatchet == null || part == null) return;

            _passed = 0;
            _failed = 0;

            Debug.Log($"[InventoryTest] start: {inventory.Describe()}");

            // Empty to begin with, and every slot in the valid state.
            Check("starts empty", inventory.CountOf(rope) == 0 && inventory.Weight < 0.01f);
            Check("slots are valid", AllValid(inventory));

            // Stacking. Rope stacks to ten, so fifteen is a full stack and a part-full one.
            Check("add 3 rope", inventory.Add(rope, 3) == 0 && inventory.CountOf(rope) == 3);
            Check("add 12 more rope", inventory.Add(rope, 12) == 0 && inventory.CountOf(rope) == 15);
            Check("15 rope is two slots", Occupied(inventory) == 2);
            Check("no slot exceeds the stack limit", WithinStackLimits(inventory));

            // Partial stacks are filled before empty ones, so the full stack is first.
            Check("first slot filled to the limit", inventory[0].Count == rope.MaxStack);

            // Non-stackables take a slot each.
            Check("add a hatchet", inventory.Add(hatchet, 1) == 0);
            Check("hatchet took its own slot", Occupied(inventory) == 3);

            // Merge: dragging the full stack onto the part-full one moves what fits and no more.
            inventory.ServerMove(0, 1);
            Check("merge tops up the target", inventory[1].Count == rope.MaxStack);
            Check("merge leaves the remainder", inventory[0].Count == 5);
            Check("merge conserved the rope", inventory.CountOf(rope) == 15);

            // Split: five becomes two and three, into an empty slot.
            inventory.ServerSplit(0, 7);
            Check("split moved half", inventory[7].Count == 2 && inventory[0].Count == 3);
            Check("split conserved the rope", inventory.CountOf(rope) == 15);

            // Swap: two different kinds change places.
            ItemStack before0 = inventory[0];
            ItemStack before7 = inventory[7];
            inventory.ServerMove(0, 2);
            Check("swap exchanged the slots", inventory[2].Equals(before0));

            // A client cannot ask for a slot that does not exist, so neither can a hostile one.
            inventory.ServerMove(-1, 3);
            inventory.ServerMove(0, 9999);
            Check("out-of-range moves change nothing", inventory[7].Equals(before7) && AllValid(inventory));

            // Weight. Boat parts are heavy on purpose; the limit has to bite before the slots run out.
            float limit = inventory.CarryLimit;
            int refused = inventory.Add(part, 20);
            Check("the carry limit refuses the rest", refused > 0);
            Check("the carry limit was not exceeded", inventory.Weight <= limit + 0.001f);
            Check("but it did take some", inventory.CountOf(part) > 0);
            Check("slots were still free", Occupied(inventory) < inventory.SlotCount);

            // Removal, from wherever it happens to be.
            int taken = inventory.Remove(rope, 12);
            Check("removed twelve rope", taken == 12 && inventory.CountOf(rope) == 3);

            ItemStack lifted = inventory.TakeSlot(IndexOfKind(inventory, hatchet));
            Check("took the hatchet out", lifted.Count == 1 && inventory.CountOf(hatchet) == 0);

            Check("everything is still valid", AllValid(inventory) && WithinStackLimits(inventory));

            string line = $"[InventoryTest] {_passed} passed, {_failed} failed. "
                          + $"server holds: {inventory.Describe()}";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

        static void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[InventoryTest] FAILED: {what}.");
        }

        static ItemDef Need(ItemCatalog catalog, string id)
        {
            ItemDef def = catalog.Find(id);
            if (def == null) Debug.LogError($"[InventoryTest] The catalog has no '{id}'; run ItemFactory.Build.");
            return def;
        }

        static int Occupied(Inventory inventory)
        {
            int used = 0;
            for (int i = 0; i < inventory.SlotCount; i++)
                if (!inventory[i].IsEmpty) used++;

            return used;
        }

        static bool AllValid(Inventory inventory)
        {
            for (int i = 0; i < inventory.SlotCount; i++)
                if (!inventory[i].Valid) return false;

            return true;
        }

        static bool WithinStackLimits(Inventory inventory)
        {
            for (int i = 0; i < inventory.SlotCount; i++)
            {
                ItemStack stack = inventory[i];
                if (stack.IsEmpty) continue;

                ItemDef def = stack.Def;
                if (def == null || stack.Count > def.MaxStack) return false;
            }

            return true;
        }

        /// <summary>First slot holding this kind, or -1. Never 0 as a fallback: slot 0 is a real slot.</summary>
        static int IndexOfKind(Inventory inventory, ItemDef def)
        {
            for (int i = 0; i < inventory.SlotCount; i++)
                if (inventory[i].Def == def) return i;

            return -1;
        }
    }
}
