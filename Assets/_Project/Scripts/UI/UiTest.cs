using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// The acceptance test for #46, run inside a real session. Server side, behind <c>-uiTest</c>.
    ///
    /// **What a headless run can and cannot prove about a UI.** The criterion is "usable with a
    /// keyboard/mouse layout, readable at 1080p", and half of that is a human sitting in front of a
    /// screen - it belongs to the #29 playtest and no amount of automation replaces it. What a
    /// terminal *can* check is everything the screen is made of that is not pixels:
    ///
    /// - every item in the game produces a tooltip that names it, weighs it and says what it does,
    ///   because a grid of squares with no icon art is unreadable the moment one tooltip is blank;
    /// - the weight readout says the right thing, including when you are overloaded;
    /// - the hotbar wraps in both directions, which is the wheel's whole behaviour;
    /// - and a drag from bag to chest and back is exactly the transfer #44 already proved safe, with
    ///   a server-side reach check that a client cannot talk its way past.
    ///
    /// That last one is the part worth automating hardest. The UI only ever shows a chest within
    /// reach, but that is a courtesy: the request names a chest by <c>NetworkObject</c>, so a client
    /// could name any chest on the island. The test walks away and asks anyway.
    /// </summary>
    public class UiTest : MonoBehaviour
    {
        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-uiTest")) return;

            _started = true;

            var go = new GameObject("UiTest");
            DontDestroyOnLoad(go);
            go.AddComponent<UiTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            Inventory bag = null;
            float deadline = Time.time + 20f;

            while (Time.time < deadline && bag == null)
            {
                bag = FindObjectsByType<Inventory>(FindObjectsSortMode.None)
                      .FirstOrDefault(b => b != null && b.IsSpawned && b.IsOwner);

                if (bag == null) yield return new WaitForSeconds(0.5f);
            }

            if (bag == null)
            {
                Debug.LogError("[UiTest] No owned player inventory; nothing was checked.");
                yield break;
            }

            ItemCatalog items = bag.Catalog;
            if (items == null)
            {
                Debug.LogError("[UiTest] The player has no item catalog; run ItemFactory.Build.");
                yield break;
            }

            Debug.Log($"[UiTest] {items.Count} items in the catalog, bag of {bag.SlotCount} slots "
                      + $"and {bag.CarryLimit:0.#} kg.");

            // ---------------------------------------------------------------- tooltips

            int described = 0;
            int consumables = 0;

            for (ushort i = 1; i <= items.Count; i++)
            {
                ItemDef def = items.At(i);
                if (def == null) continue;

                string text = ItemTooltip.Describe(def, 3);

                bool named = text.Contains(string.IsNullOrWhiteSpace(def.DisplayName)
                                               ? def.Id
                                               : def.DisplayName);

                bool weighed = def.Weight <= 0f ? text.Contains("weighs nothing") : text.Contains("kg");

                if (!named || !weighed)
                {
                    Debug.LogError($"[UiTest] '{def.Id}' has an unusable tooltip: \"{text}\"");
                    _failed++;
                    continue;
                }

                if (def.Consumable)
                {
                    consumables++;
                    if (!text.Contains("Use ("))
                    {
                        Debug.LogError($"[UiTest] '{def.Id}' is consumable but its tooltip does not "
                                       + $"say what using it does: \"{text}\"");
                        _failed++;
                        continue;
                    }
                }

                described++;
            }

            Check($"every item has a readable tooltip ({described} of {items.Count})",
                  described == items.Count);
            Check($"and the {consumables} consumables say what they do", consumables >= 4);

            ItemDef coconut = items.Find("coconut");
            ItemDef plank = items.Find("plank");
            ItemDef rope = items.Find("rope");

            if (coconut == null || plank == null || rope == null)
            {
                Debug.LogError("[UiTest] The catalog is missing something; run ItemFactory.Build.");
                yield break;
            }

            string coconutText = ItemTooltip.Describe(coconut, 2);
            Check("a stack tooltip shows the total weight", coconutText.Contains("kg each"));
            Check("and the buff it applies", coconutText.Contains("coconut") || coconutText.Contains("Use ("));
            Check("a plank tooltip does not offer a use",
                  !ItemTooltip.Describe(plank).Contains("Use ("));

            // ---------------------------------------------------------------- weight readout

            bag.ServerClear();
            Check("an empty bag reads zero", Hotbar.WeightText(bag).StartsWith("0"));
            Check("and is not overloaded", !bag.Overloaded);

            bag.Add(plank, 20);
            string loaded = Hotbar.WeightText(bag);

            Check($"a loaded bag reads its weight ({loaded})",
                  loaded.Contains("/") && loaded.Contains("kg"));

            bag.Add(plank, 400);
            Check($"an overloaded bag says so ({Hotbar.WeightText(bag)})",
                  bag.Overloaded && Hotbar.WeightText(bag).Contains("overloaded"));

            bag.ServerClear();

            // ---------------------------------------------------------------- the hotbar

            bag.ServerSelect(0);
            Check("selection starts at the first slot", bag.SelectedSlot == 0);

            bag.ServerSelect(Inventory.HotbarSlots - 1);
            Check("the last slot is reachable", bag.SelectedSlot == Inventory.HotbarSlots - 1);

            bag.ServerSelect(Inventory.HotbarSlots);
            Check("one past the end wraps to the first", bag.SelectedSlot == 0);

            bag.ServerSelect(-1);
            Check("and one before the first wraps to the last",
                  bag.SelectedSlot == Inventory.HotbarSlots - 1);

            bag.ServerSelect(0);

            // ---------------------------------------------------------------- drag to a chest

            Storage chest = Storage.NearestInReach(bag.transform.position);

            if (chest == null)
            {
                chest = FindObjectsByType<Storage>(FindObjectsSortMode.None)
                        .FirstOrDefault(s => s != null && s.IsSpawned);

                if (chest != null) Teleport(bag, chest.transform.position + chest.transform.right * 2f);
            }

            if (chest == null)
            {
                Debug.Log("[UiTest] No chest in this scene, so the transfer half is skipped. Run with "
                          + "-scene island.");
            }
            else
            {
                for (int i = 0; i < chest.SlotCount; i++) chest.TakeSlot(i);

                // Asked of this chest rather than of the nearest one: the camp has two chests
                // barely two metres apart, and "the nearest is the one I picked" is a coin toss that
                // says nothing about reach.
                Check("the chest is in reach once you are standing at it",
                      chest.InReach(bag.transform.position));

                bag.Add(rope, 10);
                bag.RequestStore(chest, SlotOf(bag, rope), 10);

                // A ServerRpc is delivered on the next tick, not the next frame - even on a host,
                // where the two ends are the same process. At 30Hz that is a third of a second, and a
                // harness that waits one frame at 500fps reads the state before the request lands.
                yield return Delivered();

                Check("dragging a stack into the chest stores it",
                      chest.CountOf(rope) == 10 && bag.CountOf(rope) == 0);

                bag.RequestTake(chest, SlotOf(chest, rope), 4);

                yield return Delivered();

                Check("dragging part of it back takes only that part",
                      bag.CountOf(rope) == 4 && chest.CountOf(rope) == 6);

                // The check the UI cannot make for the server. Walking away and asking anyway is
                // exactly what a modified client would do.
                Vector3 near = bag.transform.position;
                Teleport(bag, near + Vector3.right * 200f);

                yield return null;

                Check("the chest is out of reach from two hundred metres",
                      !chest.InReach(bag.transform.position));

                bag.RequestTake(chest, SlotOf(chest, rope), 6);

                yield return Delivered();

                Check("and the server refuses the transfer anyway",
                      chest.CountOf(rope) == 6 && bag.CountOf(rope) == 4);

                Teleport(bag, near);

                yield return null;

                Check("walking back makes it work again",
                      Storage.NearestInReach(bag.transform.position) == chest);

                bag.RequestTake(chest, SlotOf(chest, rope), 6);

                yield return Delivered();

                Check("the rest comes back", bag.CountOf(rope) == 10 && chest.IsEmpty);
            }

            // ---------------------------------------------------------------- moving inside the bag

            bag.ServerClear();
            bag.Add(plank, 12);

            int from = SlotOf(bag, plank);
            bag.ServerMove(from, 9);

            Check("a stack can be dragged to an empty slot",
                  bag[9].Count == 12 && bag[from].IsEmpty);

            bag.ServerSplit(9, 10);

            Check("shift-drag splits it in half",
                  bag[9].Count == 6 && bag[10].Count == 6);

            bag.ServerMove(10, 9);

            Check("and dragging one half back merges them", bag[9].Count == 12 && bag[10].IsEmpty);

            string line = $"[UiTest] {_passed} passed, {_failed} failed. "
                          + $"end: {Hotbar.WeightText(bag)} | {bag.Describe()}";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

        /// <summary>Long enough for a ServerRpc to have been sent, received and run.</summary>
        static WaitForSeconds Delivered() => new(0.3f);

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

        static void Teleport(Inventory bag, Vector3 to)
        {
            var controller = bag.GetComponent<CharacterController>();

            if (controller != null) controller.enabled = false;
            bag.transform.position = to;
            if (controller != null) controller.enabled = true;

            Physics.SyncTransforms();
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[UiTest] FAILED: {what}.");
        }
    }
}
