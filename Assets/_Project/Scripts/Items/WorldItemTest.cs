using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Items
{
    /// <summary>
    /// The acceptance test for #42, run inside a real session. Server side, behind <c>-itemTest</c>.
    ///
    /// The criterion in the issue is "you can rob a friend's dropped loot", and that is not a claim a
    /// single process can make. It needs two players who are not the same player, one of them dropping
    /// something and the other one taking it, with both inventories checked afterwards - so this waits
    /// for a second body to connect before it runs, and says so in the log if none ever arrives.
    ///
    /// Unlike <see cref="InventoryTest"/> this has to wait for physics: a dropped stack is a rigidbody
    /// that has to fall out of the air and land before anything about it is interesting. Hence a
    /// coroutine rather than a straight-line sequence.
    /// </summary>
    public class WorldItemTest : MonoBehaviour
    {
        const float WaitForSecondPlayer = 12f;
        const float SettleTime = 1.5f;

        static bool _started;

        int _passed;
        int _failed;

        /// <summary>Created from the network bootstrap when the flag is present. Server only.</summary>
        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-itemTest")) return;

            _started = true;

            var go = new GameObject("WorldItemTest");
            DontDestroyOnLoad(go);
            go.AddComponent<WorldItemTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            // Nothing here means anything before there is a server to own the objects.
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            List<Inventory> players = null;
            float deadline = Time.time + WaitForSecondPlayer;

            while (Time.time < deadline)
            {
                players = Players();
                if (players.Count >= 2) break;

                yield return new WaitForSeconds(0.5f);
            }

            players ??= Players();

            if (players.Count == 0)
            {
                Debug.LogError("[WorldItemTest] No player inventories exist; nothing can be tested.");
                yield break;
            }

            ItemCatalog catalog = players[0].Catalog;
            ItemDef rope = catalog != null ? catalog.Find("rope") : null;
            ItemDef part = catalog != null ? catalog.Find("boat_part") : null;

            if (rope == null || part == null)
            {
                Debug.LogError("[WorldItemTest] The catalog has no rope or boat_part; run ItemFactory.Build.");
                yield break;
            }

            Inventory owner = players[0];
            var dropper = owner.GetComponent<ItemDropper>();

            if (dropper == null)
            {
                Debug.LogError("[WorldItemTest] The player prefab has no ItemDropper; run PlayerPrefabBuilder.");
                yield break;
            }

            Debug.Log($"[WorldItemTest] {players.Count} player(s) present. "
                      + (players.Count >= 2
                          ? "Theft can be tested for real."
                          : $"No second player connected within {WaitForSecondPlayer:F0}s, so the theft "
                            + "check is skipped rather than faked."));

            // ---------------------------------------------------------------- drop

            owner.Add(rope, 6);
            owner.ServerSelect(SlotOf(owner, rope));

            int before = owner.CountOf(rope);
            WorldItem dropped = dropper.ServerDropSlot(owner.SelectedSlot, thrown: false, Vector3.forward);

            Check("dropping spawns a world item", dropped != null);
            if (dropped == null) yield break;

            Check("the item carries the stack that left the bag",
                  dropped.Stack.Count == before && dropped.Stack.Def == rope);
            Check("the bag no longer has it", owner.CountOf(rope) == 0);
            Check("the item is spawned on the network", dropped.IsSpawned);

            // ---------------------------------------------------------------- physics

            Vector3 landedFrom = dropped.transform.position;
            yield return new WaitForSeconds(SettleTime);

            Check("the item still exists after settling; nothing despawns loot", dropped != null && dropped.IsSpawned);
            if (dropped == null) yield break;

            Vector3 landedAt = dropped.transform.position;
            Check("it fell under gravity rather than hanging in the air", landedAt.y < landedFrom.y);

            // ---------------------------------------------------------------- theft

            Inventory thief = players.Count >= 2 ? players[1] : null;

            if (thief != null)
            {
                dropped.ServerInteract(thief.NetworkObject);

                Check("the thief now has it", thief.CountOf(rope) == before);
                Check("the owner still does not", owner.CountOf(rope) == 0);
                Check("the pile is gone once emptied", !dropped.IsSpawned);
            }
            else
            {
                // One player: the same object taken back proves pickup, just not theft.
                dropped.ServerInteract(owner.NetworkObject);
                Check("picking it back up returns it", owner.CountOf(rope) == before);
                Check("the pile is gone once emptied", !dropped.IsSpawned);
            }

            // ---------------------------------------------------------------- partial pickup

            Inventory taker = thief ?? owner;

            // Fill the taker to its limit with something heavy, then leave a stack it cannot fully
            // take. A full bag has to take what fits and leave the rest, not refuse the pile whole.
            taker.Add(part, 10);

            var spare = new ItemStack(catalog.IndexOf(rope), 8);
            WorldItem pile = WorldItemSpawner.Drop(spare, taker.transform.position + Vector3.up,
                                                   Quaternion.identity);

            Check("a pile can be spawned without a player dropping it", pile != null);

            if (pile != null)
            {
                int ropeBefore = taker.CountOf(rope);
                float room = taker.CarryLimit - taker.Weight;
                int fits = Mathf.Clamp(Mathf.FloorToInt(room / rope.Weight), 0, spare.Count);

                pile.ServerInteract(taker.NetworkObject);

                Check($"an overloaded bag took the {fits} that fit",
                      taker.CountOf(rope) == ropeBefore + fits);

                if (fits < spare.Count)
                {
                    Check("the remainder stayed on the floor",
                          pile.IsSpawned && pile.Stack.Count == spare.Count - fits);
                    Check("nothing was created or destroyed in the split",
                          taker.CountOf(rope) - ropeBefore + pile.Stack.Count == spare.Count);
                }
            }

            // ---------------------------------------------------------------- throw

            owner.Add(rope, 2);
            int ropeSlot = SlotOf(owner, rope);

            if (ropeSlot >= 0)
            {
                // The drop cooldown is deliberate; waiting it out is part of testing it exists.
                yield return new WaitForSeconds(0.4f);

                owner.ServerSelect(ropeSlot);
                WorldItem thrown = dropper.ServerDropSlot(ropeSlot, thrown: true, owner.transform.forward);

                Check("throwing spawns an item", thrown != null);

                if (thrown != null)
                {
                    var body = thrown.GetComponent<Rigidbody>();
                    Check("a thrown item leaves with speed on it",
                          body != null && body.linearVelocity.magnitude > 1f);
                    Check("the thrower cannot instantly take it back",
                          !thrown.ServerCanInteract(owner.NetworkObject));

                    Vector3 from = thrown.transform.position;
                    yield return new WaitForSeconds(1f);

                    if (thrown != null && thrown.IsSpawned)
                    {
                        float travelled = Vector3.Distance(from, thrown.transform.position);
                        Check($"it travelled ({travelled:F1}m)", travelled > 0.5f);
                        Check("and can be taken once it lands",
                              thrown.ServerCanInteract(owner.NetworkObject));
                    }
                }
            }

            string line = $"[WorldItemTest] {_passed} passed, {_failed} failed. "
                          + $"{Live()} stack(s) on the ground. "
                          + $"owner: {owner.Describe()}"
                          + (thief != null ? $" | other: {thief.Describe()}" : "");

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

        static List<Inventory> Players()
        {
            return Object.FindObjectsByType<Inventory>(FindObjectsSortMode.None)
                         .Where(inventory => inventory != null && inventory.IsSpawned)
                         .OrderBy(inventory => inventory.OwnerId)
                         .ToList();
        }

        static int Live() => Object.FindObjectsByType<WorldItem>(FindObjectsSortMode.None).Length;

        static int SlotOf(Inventory inventory, ItemDef def)
        {
            for (int i = 0; i < inventory.SlotCount; i++)
                if (inventory[i].Def == def) return i;

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
            Debug.LogError($"[WorldItemTest] FAILED: {what}.");
        }
    }
}
