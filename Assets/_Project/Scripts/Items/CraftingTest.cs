using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.World;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Items
{
    /// <summary>
    /// The acceptance test for #43, run inside a real session. Server side, behind <c>-craftTest</c>.
    ///
    /// The criterion is that "the tier-1 progression (tools, campfire, water filter) is craftable", so
    /// the test walks the actual progression rather than crafting one thing and calling it proven:
    ///
    ///   by hand   -> a bandage, and then a campfire
    ///   the fire  -> raw fish becomes cooked fish, because the thing you built is now a station
    ///   the bench -> a hatchet, and then a water filter
    ///   the filter-> an empty bottle becomes a full one
    ///
    /// Every step but the first is only possible because the step before it existed, which is the
    /// difference between a crafting system and a list of recipes.
    ///
    /// It also checks the three rules that are invisible in a log and expensive to get wrong: that
    /// inputs are still in the bag while the timer runs, that cancelling costs nothing, and that a
    /// recipe whose station is not nearby is refused.
    ///
    /// Needs <c>-scene island</c> for the bench half: the camp's bench is a POI, and the arena has no
    /// stations at all. The bench half is skipped rather than failed without one, so the test still
    /// says something useful in the arena.
    /// </summary>
    public class CraftingTest : MonoBehaviour
    {
        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-craftTest")) return;

            _started = true;

            var go = new GameObject("CraftingTest");
            DontDestroyOnLoad(go);
            go.AddComponent<CraftingTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            Crafting crafting = null;
            float deadline = Time.time + 15f;

            while (Time.time < deadline && crafting == null)
            {
                crafting = FindObjectsByType<Crafting>(FindObjectsSortMode.None)
                           .FirstOrDefault(c => c != null && c.IsSpawned);

                if (crafting == null) yield return new WaitForSeconds(0.5f);
            }

            if (crafting == null)
            {
                Debug.LogError("[CraftingTest] No player has Crafting; run PlayerPrefabBuilder.");
                yield break;
            }

            var inventory = crafting.GetComponent<Inventory>();
            var stats = crafting.GetComponent<SurvivalStats>();

            RecipeCatalog recipes = crafting.Catalog;
            ItemCatalog items = inventory != null ? inventory.Catalog : null;

            if (recipes == null || items == null)
            {
                Debug.LogError("[CraftingTest] The player has no recipe or item catalog; run "
                               + "RecipeFactory.Build then PlayerPrefabBuilder.");
                yield break;
            }

            ItemDef cloth = items.Find("cloth");
            ItemDef plank = items.Find("plank");
            ItemDef flint = items.Find("flint");
            ItemDef scrap = items.Find("scrap_metal");
            ItemDef rope = items.Find("rope");
            ItemDef bandage = items.Find("bandage");
            ItemDef hatchet = items.Find("hatchet");
            ItemDef fishRaw = items.Find("fish_raw");
            ItemDef fishCooked = items.Find("fish_cooked");
            ItemDef empty = items.Find("empty_bottle");
            ItemDef full = items.Find("water_bottle");

            RecipeDef makeBandage = recipes.Find("bandage");
            RecipeDef makeCampfire = recipes.Find("campfire");
            RecipeDef cookFish = recipes.Find("cook_fish");
            RecipeDef makeHatchet = recipes.Find("hatchet");
            RecipeDef makeFilter = recipes.Find("water_filter");
            RecipeDef fillBottle = recipes.Find("fill_bottle");

            if (makeBandage == null || makeCampfire == null || cookFish == null || makeHatchet == null
                || makeFilter == null || fillBottle == null)
            {
                Debug.LogError("[CraftingTest] The recipe catalog is missing the tier-1 set; run "
                               + "RecipeFactory.Build.");
                yield break;
            }

            Debug.Log($"[CraftingTest] {recipes.Count} recipes, "
                      + $"{CraftingStation.CountOf(CraftStation.Bench)} bench(es), "
                      + $"{CraftingStation.CountOf(CraftStation.Fire)} fire(s), "
                      + $"{CraftingStation.CountOf(CraftStation.Filter)} filter(s) in the world.");

            // ---------------------------------------------------------------- the catalog itself

            Check("the catalog has the tier-1 set", recipes.Count >= 12);
            Check("index 0 is nobody", recipes.At((ushort)0) == null);
            Check("every recipe round-trips through its index",
                  recipes.Recipes.All(r => recipes.At(recipes.IndexOf(r)) == r));
            Check("a campfire is a structure and a bandage is not",
                  makeCampfire.MakesStructure && !makeBandage.MakesStructure);
            Check("the four stations are all represented",
                  recipes.For(CraftStation.Hand).Count > 0 && recipes.For(CraftStation.Fire).Count > 0
                  && recipes.For(CraftStation.Bench).Count > 0
                  && recipes.For(CraftStation.Filter).Count > 0);

            // ---------------------------------------------------------------- by hand

            inventory.ServerClear();
            Check("an empty-handed player cannot make a bandage", !crafting.CanCraft(makeBandage, out _));

            inventory.Add(cloth, 2);
            Check("two cloth is enough", crafting.CanCraft(makeBandage, out _));
            Check("a hand recipe needs no station anywhere",
                  CraftingStation.InRange(CraftStation.Hand, crafting.transform.position));

            Check("the craft starts", crafting.ServerBeginCraft(makeBandage));
            Check("and the player is busy", crafting.Busy);
            Check("a second craft cannot start on top of it", !crafting.ServerBeginCraft(makeBandage));

            yield return new WaitForSeconds(0.5f);

            // The rule that keeps an interrupted craft from costing anything, and the rule that makes
            // a cancelled craft impossible to duplicate with.
            Check("the cloth is still in the bag while the timer runs", inventory.CountOf(cloth) == 2);
            Check("and the bandage does not exist yet", inventory.CountOf(bandage) == 0);
            Check("the progress bar has moved", crafting.Progress > 0f && crafting.Progress < 1f);

            yield return new WaitForSeconds(makeBandage.Seconds + 0.4f);

            Check("the bandage is made", inventory.CountOf(bandage) == 1);
            Check("and the cloth is gone", inventory.CountOf(cloth) == 0);
            Check("and the player is idle again", !crafting.Busy);

            // ---------------------------------------------------------------- cancelling costs nothing

            inventory.Add(cloth, 2);
            Check("a second bandage starts", crafting.ServerBeginCraft(makeBandage));

            yield return new WaitForSeconds(0.4f);

            crafting.ServerCancel();

            Check("cancelling stops it", !crafting.Busy);

            yield return new WaitForSeconds(makeBandage.Seconds + 0.4f);

            Check("a cancelled craft makes nothing", inventory.CountOf(bandage) == 1);
            Check("and spends nothing", inventory.CountOf(cloth) == 2);

            // ---------------------------------------------------------------- the campfire

            Check("a fire recipe is refused with no fire nearby",
                  !crafting.CanCraft(cookFish, out string noFire) && noFire != null);

            inventory.Add(plank, 4);
            inventory.Add(flint, 1);

            int firesBefore = CraftingStation.CountOf(CraftStation.Fire);

            Check("the campfire can be built by hand", crafting.ServerBeginCraft(makeCampfire));

            yield return new WaitForSeconds(makeCampfire.Seconds + 0.8f);

            Check("a fire now exists", CraftingStation.CountOf(CraftStation.Fire) == firesBefore + 1);
            Check("the planks and the flint were spent",
                  inventory.CountOf(plank) == 0 && inventory.CountOf(flint) == 0);
            Check("a structure leaves nothing in the bag", inventory.CountOf(bandage) == 1);

            // ---------------------------------------------------------------- and the fire is a station

            Check("standing at your own fire unlocks cooking",
                  CraftingStation.InRange(CraftStation.Fire, crafting.transform.position));

            inventory.Add(fishRaw, 1);
            Check("cooking starts at the fire you just built", crafting.ServerBeginCraft(cookFish));

            yield return new WaitForSeconds(cookFish.Seconds + 0.5f);

            Check("the fish is cooked", inventory.CountOf(fishCooked) == 1);
            Check("and the raw one is gone", inventory.CountOf(fishRaw) == 0);

            // ---------------------------------------------------------------- and it is warm

            if (stats != null)
            {
                stats.ServerFeed(warmth: -80f);
                float coldAt = stats.Warmth;

                yield return new WaitForSeconds(1f);

                // Warmth recovers on its own during the day at 3.5/s; a fire adds twelve more. Eight
                // in a second cannot happen without the fire, which is what this is actually asking.
                float gained = stats.Warmth - coldAt;
                Check($"the fire is warm ({gained:F1} warmth in a second)", gained > 8f);
            }

            // ---------------------------------------------------------------- the bench half

            CraftingStation bench = CraftingStation.Nearest(CraftStation.Bench, crafting.transform.position);

            if (bench == null)
            {
                Debug.Log("[CraftingTest] No bench in this scene, so the bench half is skipped. Run "
                          + "with -scene island for the camp's bench.");
            }
            else
            {
                Check("the bench is refused from across the island",
                      !CraftingStation.InRange(CraftStation.Bench,
                                               bench.transform.position + Vector3.right * 500f));

                Teleport(crafting, bench.transform.position + bench.transform.forward * 2f);

                yield return null;

                Check("standing at the bench unlocks it",
                      CraftingStation.InRange(CraftStation.Bench, crafting.transform.position));

                inventory.Add(scrap, 3);
                inventory.Add(plank, 3);
                inventory.Add(rope, 1);
                inventory.Add(cloth, 2);

                Check("the hatchet starts at the bench", crafting.ServerBeginCraft(makeHatchet));

                yield return new WaitForSeconds(makeHatchet.Seconds + 0.5f);

                Check("the hatchet is made", inventory.CountOf(hatchet) == 1);

                int filtersBefore = CraftingStation.CountOf(CraftStation.Filter);

                Check("the water filter starts at the bench", crafting.ServerBeginCraft(makeFilter));

                yield return new WaitForSeconds(makeFilter.Seconds + 0.8f);

                Check("a filter now exists",
                      CraftingStation.CountOf(CraftStation.Filter) == filtersBefore + 1);

                // ------------------------------------------------------------ and the filter is a station

                Check("standing at your own filter unlocks it",
                      CraftingStation.InRange(CraftStation.Filter, crafting.transform.position));

                inventory.Add(empty, 1);
                Check("filling a bottle starts", crafting.ServerBeginCraft(fillBottle));

                yield return new WaitForSeconds(fillBottle.Seconds + 0.5f);

                Check("the bottle is full", inventory.CountOf(full) == 1);
                Check("and the empty one is gone", inventory.CountOf(empty) == 0);
            }

            // ---------------------------------------------------------------- refusals

            Check("an unknown index makes nothing", !crafting.ServerBeginCraft((ushort)0));
            Check("nor does one past the end", !crafting.ServerBeginCraft((ushort)60000));

            string line = $"[CraftingTest] {_passed} passed, {_failed} failed. "
                          + $"end: {inventory.Describe()} | {crafting.Describe()}";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

        /// <summary>
        /// A CharacterController writes the transform back every frame, so moving a player by hand
        /// means turning it off for a frame. The same trick the spawn point uses.
        /// </summary>
        static void Teleport(Crafting crafting, Vector3 to)
        {
            var controller = crafting.GetComponent<CharacterController>();

            if (controller != null) controller.enabled = false;
            crafting.transform.position = to;
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
            Debug.LogError($"[CraftingTest] FAILED: {what}.");
        }
    }
}
