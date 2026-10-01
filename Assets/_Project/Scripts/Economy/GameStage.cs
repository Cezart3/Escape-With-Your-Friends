using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using UnityEngine;

namespace EscapeWithYourFriends.Economy
{
    /// <summary>
    /// How far up the gun ladder the squad has climbed, and what that does to the price of what it
    /// hunts and catches (the game-stage half of the economy overhaul).
    ///
    /// **The stage is the reward for a better gun.** It rises the first time anybody holds a gun the
    /// trader sells for at least the next threshold, and never falls: shotgun 1, smg 2, rifle 3,
    /// machine gun 4, sniper 5. Each step multiplies what the trader pays for *drops* - anything an
    /// animal leaves or a line brings up - by half again. So the rifle pays for itself in pelts, and
    /// the climb to the sniper is a curve rather than a wall.
    ///
    /// **Only drops scale, never stock.** Anything a shop sells stays at its flat buy-back, because a
    /// box of rounds bought at 3 and sold back at 3 x 7.6 is a money printer. The drop list is read
    /// off the animal and fish catalogs, so a new species' loot is a drop without anyone listing it.
    ///
    /// Server state. Saved with the run; clients see it through <see cref="ShopCounter.Stage"/>.
    /// </summary>
    public static class GameStage
    {
        /// <summary>The shop price a held gun must reach for each stage, 1 to 5.</summary>
        public static readonly int[] Thresholds = { 2500, 8000, 25000, 90000, 250000 };

        public const float PerStage = 1.5f;

        static int _stage;
        static HashSet<ItemDef> _drops;

        /// <summary>The run's stage: the higher of what this session reached and what the save held.</summary>
        public static int Stage => Mathf.Max(_stage, RunSave.Run != null ? RunSave.Run.stage : 0);

        public static float Multiplier(int stage) => Mathf.Pow(PerStage, Mathf.Max(0, stage));

        /// <summary>The stage a gun at this shop price earns.</summary>
        public static int StageFor(int weaponPrice)
        {
            int stage = 0;
            while (stage < Thresholds.Length && weaponPrice >= Thresholds[stage]) stage++;
            return stage;
        }

        /// <summary>Server: somebody is holding a gun that sells for this. Raises the stage if it earns one.</summary>
        public static void Observe(int weaponPrice, string who)
        {
            int earned = StageFor(weaponPrice);
            if (earned <= Stage) return;

            _stage = earned;
            Debug.Log($"[GameStage] {who} raised the stage to {earned}: drops now sell at x{Multiplier(earned):0.##}.");
        }

        /// <summary>Tests only: back to a fresh run.</summary>
        internal static void Reset()
        {
            _stage = 0;
            if (RunSave.Run != null) RunSave.Run.stage = 0;
        }

        /// <summary>Whether this item is something the island gives you rather than something a shop does.</summary>
        public static bool IsDrop(ItemDef item)
        {
            if (item == null) return false;

            if (_drops == null)
            {
                _drops = new HashSet<ItemDef>();
                if (AnimalCatalog.Active != null)
                    foreach (AnimalDef animal in AnimalCatalog.Active.Animals)
                        if (animal != null && animal.Loot != null)
                            foreach (LootDrop drop in animal.Loot)
                                if (drop != null && drop.Item != null) _drops.Add(drop.Item);
                if (FishCatalog.Active != null)
                    foreach (FishDef fish in FishCatalog.Active.Fish)
                        if (fish != null && fish.Catch != null) _drops.Add(fish.Catch);

                // Before either catalog loads the set would be empty for good. Try again next time.
                if (_drops.Count == 0)
                {
                    _drops = null;
                    return false;
                }
            }

            return _drops.Contains(item);
        }

        /// <summary>What <paramref name="shop"/> pays for one <paramref name="item"/> at <paramref name="stage"/>.</summary>
        public static int SellPrice(ShopDef shop, ItemDef item, int stage)
        {
            int flat = shop != null ? shop.PriceFor(item) : 0;
            if (flat <= 0 || !IsDrop(item) || Sells(shop, item)) return flat;

            return Mathf.RoundToInt(flat * Multiplier(stage));
        }

        static bool Sells(ShopDef shop, ItemDef item)
        {
            foreach (ShopDef.Offer offer in shop.Offers)
                if (offer.Item == item) return true;
            return false;
        }
    }
}
