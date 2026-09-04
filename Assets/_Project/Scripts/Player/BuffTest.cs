using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// The acceptance test for #45, run inside a real session. Server side, behind <c>-buffTest</c>.
    ///
    /// The criterion is that eating "hooks into the same BuffDef system the casino alcohol will use",
    /// so the test deliberately does both halves with the same code path: it eats a coconut, and then
    /// it applies the <c>drunk</c> buff that nothing in the game hands out yet. If the second one
    /// works through <see cref="BuffState.Apply"/> with no consumable involved, #M6 is an asset rather
    /// than a system - which is the whole claim.
    ///
    /// It also checks the three things that are easy to get wrong and invisible in a log: that a use
    /// takes time and can be interrupted, that an interrupted use does not spend the item, and that
    /// the three stacking rules do three different things.
    /// </summary>
    public class BuffTest : MonoBehaviour
    {
        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-buffTest")) return;

            _started = true;

            var go = new GameObject("BuffTest");
            DontDestroyOnLoad(go);
            go.AddComponent<BuffTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            BuffState buffs = null;
            float deadline = Time.time + 10f;

            while (Time.time < deadline && buffs == null)
            {
                buffs = FindObjectsByType<BuffState>(FindObjectsSortMode.None)
                        .FirstOrDefault(b => b != null && b.IsSpawned);

                if (buffs == null) yield return new WaitForSeconds(0.5f);
            }

            if (buffs == null)
            {
                Debug.LogError("[BuffTest] No player has BuffState; run PlayerPrefabBuilder.");
                yield break;
            }

            var inventory = buffs.GetComponent<Inventory>();
            var use = buffs.GetComponent<ItemUse>();
            var stats = buffs.GetComponent<SurvivalStats>();
            var health = buffs.GetComponent<Health>();

            BuffCatalog catalog = buffs.Catalog;
            ItemCatalog items = inventory != null ? inventory.Catalog : null;

            if (catalog == null || items == null || use == null || stats == null)
            {
                Debug.LogError("[BuffTest] The player is missing a catalog, an inventory or ItemUse; "
                               + "run BuffFactory.Build and PlayerPrefabBuilder.");
                yield break;
            }

            ItemDef coconut = items.Find("coconut");
            ItemDef bottle = items.Find("water_bottle");
            ItemDef empty = items.Find("empty_bottle");
            ItemDef bandage = items.Find("bandage");
            ItemDef plank = items.Find("plank");

            BuffDef drunk = catalog.Find("drunk");
            BuffDef bandaged = catalog.Find("bandaged");
            BuffDef fed = catalog.Find("coconut_water");

            if (coconut == null || bottle == null || bandage == null || plank == null
                || drunk == null || bandaged == null || fed == null)
            {
                Debug.LogError("[BuffTest] The catalogs are missing something; run ItemFactory.Build "
                               + "then BuffFactory.Build.");
                yield break;
            }

            Debug.Log($"[BuffTest] start: {buffs.Describe()}, {stats.Describe()}");

            Check("nothing is affecting a fresh player", buffs.Count == 0);
            Check("and the multipliers are all neutral",
                  Mathf.Approximately(buffs.SpeedMultiplier, 1f)
                  && Mathf.Approximately(buffs.DamageTakenMultiplier, 1f)
                  && Mathf.Approximately(buffs.StaminaCostMultiplier, 1f));

            Check("five items are consumable and a plank is not",
                  coconut.Consumable && bottle.Consumable && bandage.Consumable && !plank.Consumable);

            // ---------------------------------------------------------------- eating

            stats.ServerFeed(hunger: -60f, thirst: -60f);

            float hungerBefore = stats.Hunger;
            float thirstBefore = stats.Thirst;

            inventory.Add(coconut, 2);
            int coconutSlot = SlotOf(inventory, coconut);

            Check("a coconut can be picked up", coconutSlot >= 0);
            if (coconutSlot < 0) yield break;

            Check("using it starts a use", use.ServerBeginUse(coconutSlot));
            Check("which takes time rather than happening instantly", use.Busy);
            Check("and the coconut has not been spent yet", inventory.CountOf(coconut) == 2);

            yield return new WaitForSeconds(coconut.UseSeconds + 0.4f);

            Check("the use finishes", !use.Busy);
            Check("one coconut is gone", inventory.CountOf(coconut) == 1);
            Check($"hunger went up ({hungerBefore:F0} -> {stats.Hunger:F0})", stats.Hunger > hungerBefore);
            Check($"thirst went up ({thirstBefore:F0} -> {stats.Thirst:F0})", stats.Thirst > thirstBefore);
            Check("and the buff is running", buffs.Has(fed) && buffs.Remaining(fed) > 0f);

            // ---------------------------------------------------------------- interruption

            inventory.Add(bandage, 1);
            int bandageSlot = SlotOf(inventory, bandage);

            Check("a bandage use starts", use.ServerBeginUse(bandageSlot));

            yield return new WaitForSeconds(0.3f);

            use.ServerCancel();

            Check("cancelling stops the use", !use.Busy);
            Check("an interrupted use costs the time, not the bandage",
                  inventory.CountOf(bandage) == 1);
            Check("and applies nothing", !buffs.Has(bandaged));

            // ---------------------------------------------------------------- stacking

            // Ignore: the second application while the first runs does nothing at all.
            Check("a bandage applies", buffs.Apply(bandaged));
            float remaining = buffs.Remaining(bandaged);

            Check("a second one while it runs is refused", !buffs.Apply(bandaged));
            Check("and did not extend the first",
                  Mathf.Abs(buffs.Remaining(bandaged) - remaining) < 0.2f);

            // Refresh: applied twice, one entry, timer reset.
            buffs.Apply(fed);
            int countBefore = buffs.Count;
            buffs.Apply(fed);

            Check("a Refresh buff applied twice is still one entry", buffs.Count == countBefore);

            // Stack: applied twice, two entries, effects multiplied.
            buffs.Apply(drunk);
            float oneDrink = buffs.SpeedMultiplier;
            buffs.Apply(drunk);

            Check("a Stack buff applied twice is two entries", buffs.Count == countBefore + 2);
            Check($"and its multipliers multiply ({oneDrink:F3} -> {buffs.SpeedMultiplier:F3})",
                  buffs.SpeedMultiplier < oneDrink);

            // ---------------------------------------------------------------- the casino's half

            Check($"the drunk buff slows you down (x{drunk.SpeedMultiplier:F2})",
                  drunk.SpeedMultiplier < 1f);
            Check($"softens hits (x{drunk.DamageTakenMultiplier:F2})", drunk.DamageTakenMultiplier < 1f);
            Check($"and hazes the screen ({drunk.Haze:F2}) for #M6 to read", buffs.Haze > 0f);

            // The damage multiplier has to actually reach Health, or the casino's rum is a number in
            // an asset nobody reads.
            if (health != null)
            {
                float before = health.Current;
                health.TakeDamage(new DamageInfo(20f, DamageType.Blunt));
                float taken = before - health.Current;

                Check($"and Health actually scales incoming damage (20 asked, {taken:F1} landed)",
                      taken > 0f && taken < 19.9f);
            }

            buffs.Clear(drunk);
            Check("a buff can be ended early", buffs.Count == countBefore + 1);

            // ---------------------------------------------------------------- what is left behind

            inventory.Add(bottle, 1);
            int bottleSlot = SlotOf(inventory, bottle);

            Check("drinking a bottle starts", use.ServerBeginUse(bottleSlot));

            yield return new WaitForSeconds(bottle.UseSeconds + 0.4f);

            Check("the full bottle is gone", inventory.CountOf(bottle) == 0);
            Check("and an empty one is in the bag",
                  empty != null && inventory.CountOf(empty) == 1);

            // ---------------------------------------------------------------- refusals

            inventory.Add(plank, 1);
            Check("a plank cannot be eaten", !use.ServerBeginUse(SlotOf(inventory, plank)));

            Check("nor can an empty slot", !use.ServerBeginUse(inventory.SlotCount - 1));
            Check("nor a slot that does not exist", !use.ServerBeginUse(9999));

            // ---------------------------------------------------------------- expiry

            buffs.ClearAll();
            Check("everything can be cleared at once", buffs.Count == 0);
            Check("and the multipliers go back to neutral",
                  Mathf.Approximately(buffs.SpeedMultiplier, 1f)
                  && Mathf.Approximately(buffs.DamageTakenMultiplier, 1f));

            string line = $"[BuffTest] {_passed} passed, {_failed} failed. "
                          + $"end: {buffs.Describe()} | {inventory.Describe()}";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

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
            Debug.LogError($"[BuffTest] FAILED: {what}.");
        }
    }
}
