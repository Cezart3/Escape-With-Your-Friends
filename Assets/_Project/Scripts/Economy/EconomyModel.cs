using System.Collections.Generic;
using EscapeWithYourFriends.Data;
using UnityEngine;

namespace EscapeWithYourFriends.Economy
{
    /// <summary>
    /// What an hour of this island is worth, read off the catalogs rather than guessed.
    ///
    /// Every other balance decision in the game is a number in an asset. This is the one place that
    /// asks what those numbers add up to: if a cast is worth eleven coins and takes eleven seconds,
    /// and a boat costs sixteen hundred, then four people reach the boat in eight minutes and the
    /// whole middle of the game does not exist. That sentence is the entire reason this file is here.
    ///
    /// **Nothing below is a constant that could have been read from an asset.** Values come from
    /// <see cref="ShopDef.PriceFor"/> - what the trader actually pays, not what the item claims to be
    /// worth - weights from <see cref="ItemDef.Weight"/>, odds from the catalogs' own tables. The only
    /// hand-written numbers are the ones no asset knows: how long a walk to the counter takes, how
    /// long it takes to find an animal, and how much of a session is actually spent earning rather
    /// than eating, walking, dying and arguing. Those are declared together at the top, with the
    /// reasoning attached, so that when the answer is wrong it is obvious which assumption to argue
    /// with.
    ///
    /// **Every activity is modelled as a trip, not as a rate.** You cannot carry more than forty
    /// kilograms, so income is not "coins per minute of fishing" but "a bag's worth of fish, divided
    /// by the time it took to fill it *and* walk it to the counter". That distinction is the
    /// difference between fishing paying sixty-seven coins a minute and paying forty-four, and it is
    /// the reason a pearl - seventy coins at fifty grams - is worth more than its price says.
    /// </summary>
    public static class EconomyModel
    {
        // ------------------------------------------------------------ the assumptions

        /// <summary>Minutes to walk a full bag to the counter, sell it, and walk back out.</summary>
        /// <remarks>
        /// The trader is by the spawn and the work is not: two hundred metres each way at a sprint is
        /// about a minute, and the counter itself is the other one.
        /// </remarks>
        public const float TraderTripMinutes = 2f;

        /// <summary>Seconds of cast and strike that no fish definition covers.</summary>
        public const float CastOverheadSeconds = 1.5f;

        /// <summary>Seconds of walking about before an animal is in front of you.</summary>
        /// <remarks>Zones are seeded across the island and animals flee, so this is the big one.</remarks>
        public const float HuntFindSeconds = 60f;

        /// <summary>Seconds from the first swing to a body on the ground.</summary>
        public const float HuntKillSeconds = 25f;

        /// <summary>Seconds spent picking a kill's drops back up off the floor.</summary>
        /// <remarks>Hunting never puts anything in your bag for you. That is the point of it.</remarks>
        public const float HuntGatherSeconds = 15f;

        /// <summary>Minutes to get from the fire to a camp and back out of it alive.</summary>
        public const float RaidTravelMinutes = 1.5f;

        /// <summary>Seconds per native: the approach, the fight, and the pockets.</summary>
        public const float RaidKillSeconds = 35f;

        /// <summary>How many bodies a camp is good for before you are waiting on respawns.</summary>
        public const int BodiesPerRaid = 6;

        /// <summary>Minutes in the kind of evening this game is actually played in.</summary>
        public const float SessionMinutes = 90f;

        /// <summary>How many people are on the island.</summary>
        public const int Players = 4;

        /// <summary>
        /// The fraction of a session actually spent earning.
        ///
        /// The rest is eating, drinking, warming up, walking somewhere, being dead, carrying somebody
        /// who is dead, and the twenty minutes after somebody finds the taser. Pitching this at one
        /// would be modelling a spreadsheet rather than an evening.
        /// </summary>
        public const float Attention = 0.55f;

        /// <summary>
        /// The fraction of income that never reaches the boat fund, because it was spent on a gun,
        /// ammunition for the gun, bandages for the consequences of the gun, and a revive.
        /// </summary>
        public const float GearShare = 0.45f;

        // ------------------------------------------------------------ the shape of an answer

        /// <summary>One way of making money, priced as a round trip to the counter.</summary>
        public readonly struct Rate
        {
            public readonly string Activity;
            public readonly string Detail;

            /// <summary>Coins in hand at the end of one full bag.</summary>
            public readonly float CoinsPerTrip;

            /// <summary>Minutes that bag took, counting the walk to the trader.</summary>
            public readonly float TripMinutes;

            /// <summary>Kilograms of the carry limit the trip actually used.</summary>
            public readonly float Kilograms;

            public Rate(string activity, string detail, float coins, float minutes, float kilograms)
            {
                Activity = activity;
                Detail = detail;
                CoinsPerTrip = Mathf.Max(0f, coins);
                TripMinutes = Mathf.Max(0f, minutes);
                Kilograms = Mathf.Max(0f, kilograms);
            }

            public float CoinsPerMinute => TripMinutes <= 0f ? 0f : CoinsPerTrip / TripMinutes;
            public float CoinsPerHour => CoinsPerMinute * 60f;

            public override string ToString()
                => $"{Activity,-10} {CoinsPerMinute,6:0.0} c/min  {CoinsPerHour,7:0} c/h  "
                   + $"({CoinsPerTrip:0} coins per {TripMinutes:0.0} min trip, {Kilograms:0.0} kg)  {Detail}";
        }

        // ------------------------------------------------------------ fishing

        /// <summary>
        /// A bag of fish, at the odds and the fight times the catalog actually states.
        ///
        /// The fight is modelled as the competent line rather than the optimal one: reel while it is
        /// calm, let go while it runs. That is the strategy the minigame is designed to teach, so it
        /// is the strategy the economy should be priced against.
        /// </summary>
        public static Rate Fishing(FishCatalog fish, ShopDef shop, float carryLimit)
        {
            if (fish == null || shop == null || fish.Count == 0) return default;

            float coins = 0f;
            float kilos = 0f;
            float seconds = 0f;

            foreach (FishDef def in fish.Fish)
            {
                if (def == null) continue;

                float chance = fish.ChanceOf(def);
                if (chance <= 0f) continue;

                float count = (def.Low + def.High) * 0.5f;
                float paid = def.Catch == null ? 0f : shop.PriceFor(def.Catch) * count;
                float weight = def.Catch == null ? 0f : def.Catch.Weight * count;

                float bite = (def.BiteSeconds.x + def.BiteSeconds.y) * 0.5f;
                float cast = bite + def.HookSeconds * 0.5f + FightSeconds(def) + CastOverheadSeconds;

                coins += chance * paid;
                kilos += chance * weight;
                seconds += chance * cast;
            }

            if (seconds <= 0f) return default;

            // A bag holds what a bag holds. Something weightless - a pearl - never fills it, so the
            // trip length is set by the fish and the pearls ride along free.
            float casts = kilos > 0.001f ? carryLimit / kilos : 40f;

            float minutes = casts * seconds / 60f + TraderTripMinutes;

            return new Rate("fishing", $"{casts:0} casts at {coins:0.0} c and {seconds:0.0} s each",
                            casts * coins, minutes, casts * kilos);
        }

        /// <summary>
        /// Seconds to land one, reeling in the gaps between its runs.
        ///
        /// Line comes in at the reel speed for the calm part of every struggle period and goes back
        /// out at the slip speed for the run. The net of those two is the speed a fish is actually
        /// beaten at, and a boot - which never runs - is simply its distance over its reel speed.
        /// </summary>
        public static float FightSeconds(FishDef def)
        {
            if (def == null) return 0f;

            float period = Mathf.Max(0.01f, def.StruggleSeconds);
            float running = Mathf.Clamp01(def.RunSeconds / period);
            float calm = 1f - running;

            float net = def.ReelSpeed * calm - def.SlipSpeed * running;

            return def.Distance / Mathf.Max(0.05f, net);
        }

        // ------------------------------------------------------------ hunting

        /// <summary>A bag of meat and hide, averaged over what the island actually has on it.</summary>
        public static Rate Hunting(AnimalCatalog animals, ShopDef shop, float carryLimit)
        {
            if (animals == null || shop == null || animals.Count == 0) return default;

            float coins = 0f;
            float kilos = 0f;
            int counted = 0;

            foreach (AnimalDef def in animals.Animals)
            {
                if (def == null || def.Loot == null) continue;

                coins += Paid(def.Loot, shop);
                kilos += Carried(def.Loot);
                counted++;
            }

            if (counted == 0) return default;

            coins /= counted;
            kilos /= counted;

            float seconds = HuntFindSeconds + HuntKillSeconds + HuntGatherSeconds;
            float kills = kilos > 0.001f ? carryLimit / kilos : 10f;
            float minutes = kills * seconds / 60f + TraderTripMinutes;

            return new Rate("hunting", $"{kills:0} kills at {coins:0} c and {seconds:0} s each",
                            kills * coins, minutes, kills * kilos);
        }

        // ------------------------------------------------------------ raiding a camp

        /// <summary>
        /// A sweep of a camp. Never weight-limited - a native carries rope and flint, not four kilos
        /// of venison - so the trip is as long as the bodies take plus the walk there and back.
        /// </summary>
        public static Rate Raiding(NativeCatalog natives, ShopDef shop)
        {
            if (natives == null || shop == null || natives.Count == 0) return default;

            float coins = 0f;
            float kilos = 0f;
            int counted = 0;

            foreach (NativeDef def in natives.Natives)
            {
                if (def == null || def.Loot == null) continue;

                coins += Paid(def.Loot, shop);
                kilos += Carried(def.Loot);
                counted++;
            }

            if (counted == 0) return default;

            coins /= counted;
            kilos /= counted;

            float minutes = RaidTravelMinutes + BodiesPerRaid * RaidKillSeconds / 60f + TraderTripMinutes;

            return new Rate("raiding", $"{BodiesPerRaid} bodies at {coins:0} c each",
                            BodiesPerRaid * coins, minutes, BodiesPerRaid * kilos);
        }

        // ------------------------------------------------------------ the two that pay nothing

        /// <summary>
        /// Picking things up off the island, which pays nothing, because nothing on the island can be
        /// picked up and sold. Coconuts are food, driftwood is scenery, and the crafting materials are
        /// bought rather than found. Stated rather than omitted: a zero here is a design gap, and a
        /// gap that is written down is a gap somebody can decide to fill.
        /// </summary>
        public static Rate Foraging() => new("foraging", "nothing on the island is worth money yet", 0f, 1f, 0f);

        /// <summary>
        /// The wheel, which is #M6 and will pay nothing on average when it exists, because a house
        /// edge is the only thing that makes a casino a casino. It is a way to turn an evening's
        /// income into either two evenings' income or nothing, which is a different feature from a
        /// way to earn.
        /// </summary>
        public static Rate Gambling() => new("gambling", "negative by construction; the wheel is #M6", 0f, 1f, 0f);

        // ------------------------------------------------------------ the boat

        /// <summary>What the whole boat costs at the counter, parts times price.</summary>
        public static int BoatCost(ShopDef shop)
        {
            if (shop == null) return 0;

            int total = 0;

            foreach (ShopDef.Offer offer in shop.Offers)
            {
                if (!offer.IsValid || offer.Item == null || offer.Item.Id != "boat_part") continue;

                total += offer.Price * Mathf.Max(1, offer.Stock);
            }

            return total;
        }

        /// <summary>
        /// Evenings to the boat, for a group of four who are also buying guns.
        ///
        /// The blend is the average of everything that pays rather than the best of them, because
        /// four people do not all do the most profitable thing at once, and the one who is fishing
        /// is usually the one who is not fighting off the thing attacking the one who is hunting.
        /// </summary>
        public static float SessionsToBoat(int boatCost, IEnumerable<Rate> rates)
        {
            float blended = Blended(rates);
            if (blended <= 0f || boatCost <= 0) return 0f;

            float perMinute = blended * Attention * Players * (1f - GearShare);
            float perSession = perMinute * SessionMinutes;

            return perSession <= 0f ? 0f : boatCost / perSession;
        }

        /// <summary>The average coins a minute across everything that pays anything at all.</summary>
        public static float Blended(IEnumerable<Rate> rates)
        {
            float total = 0f;
            int counted = 0;

            foreach (Rate rate in rates)
            {
                if (rate.CoinsPerMinute <= 0f) continue;

                total += rate.CoinsPerMinute;
                counted++;
            }

            return counted == 0 ? 0f : total / counted;
        }

        // ------------------------------------------------------------ shared arithmetic

        /// <summary>What the trader hands over for one of these, drops and odds included.</summary>
        public static float Paid(LootDrop[] loot, ShopDef shop)
        {
            if (loot == null || shop == null) return 0f;

            float total = 0f;

            foreach (LootDrop drop in loot)
            {
                if (drop.Item == null) continue;

                total += drop.Expected * shop.PriceFor(drop.Item);
            }

            return total;
        }

        /// <summary>What one of these costs you in carry limit.</summary>
        public static float Carried(LootDrop[] loot)
        {
            if (loot == null) return 0f;

            float total = 0f;

            foreach (LootDrop drop in loot)
            {
                if (drop.Item == null) continue;

                total += drop.Expected * drop.Item.Weight;
            }

            return total;
        }

        /// <summary>Coins per kilogram, which is the only honest way to compare two bags.</summary>
        public static float PerKilo(ItemDef item, ShopDef shop)
        {
            if (item == null || shop == null || item.Weight <= 0.0001f) return 0f;

            return shop.PriceFor(item) / item.Weight;
        }
    }
}
