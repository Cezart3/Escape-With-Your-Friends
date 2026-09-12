using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Player;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Economy
{
    /// <summary>
    /// The acceptance test for #56, run inside a real session behind <c>-economyTest</c>.
    ///
    /// The criterion is "a four-player group reaches the boat in a believable session count", and
    /// believable is a number, so this prints the number. <see cref="EconomyModel"/> does the
    /// arithmetic off the live catalogs; this asks whether the arithmetic lands anywhere a person
    /// would want to play, and then checks the model against the actual trader so the whole thing is
    /// not a spreadsheet agreeing with itself.
    ///
    /// Four questions, in order of how badly a wrong answer hurts:
    ///
    /// 1. **Is the boat the right distance away?** Sessions to the boat has to be somewhere between
    ///    one and a half and three evenings. Under that the middle of the game does not exist; over
    ///    it, it is a job.
    /// 2. **Does one activity make the others pointless?** Nothing that pays should pay more than
    ///    three times what the worst thing that pays pays. Four people who all do the same thing
    ///    because everything else is a waste of time are not playing a co-op game.
    /// 3. **Is carrying it home worth it?** Everything sellable has to beat scrap metal per
    ///    kilogram, because scrap is the floor: it is what you would have in the bag otherwise.
    /// 4. **Does the trader agree?** A bag with one animal's drops and one native's drops in it is
    ///    carried to the counter and sold, and the coins have to match what the model predicted.
    /// </summary>
    public class EconomyTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-economyTest")) return;

            _started = true;

            var go = new GameObject("EconomyTest");
            DontDestroyOnLoad(go);
            go.AddComponent<EconomyTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            Inventory bag = null;
            float deadline = Time.time + WaitForPlayer;

            while (Time.time < deadline && bag == null)
            {
                bag = FindObjectsByType<Inventory>(FindObjectsSortMode.None).FirstOrDefault();
                yield return null;
            }

            if (bag == null)
            {
                Debug.LogError("[EconomyTest] No player inventory turned up; nothing to price.");
                yield break;
            }

            ShopCounter counter = null;
            deadline = Time.time + WaitForPlayer;

            while (Time.time < deadline && counter == null)
            {
                counter = FindObjectsByType<ShopCounter>(FindObjectsSortMode.None)
                    .FirstOrDefault(c => c.Shop != null);
                yield return null;
            }

            if (counter == null)
            {
                Debug.LogError("[EconomyTest] No shop counter in the scene; run on the island.");
                yield break;
            }

            ShopDef shop = counter.Shop;
            ItemCatalog items = ItemCatalog.Active;
            FishCatalog fish = FishCatalog.Active;
            AnimalCatalog animals = AnimalCatalog.Active;
            NativeCatalog natives = NativeCatalog.Active;

            Shelf(shop, items);
            List<EconomyModel.Rate> rates = Model(fish, animals, natives, shop, bag.CarryLimit);
            Kilos(items, shop);
            Boat(shop, rates);

            yield return Counter(bag, counter, shop, animals, natives);

            Report();
        }

        // ---------------------------------------------------------------- the shelf

        /// <summary>
        /// What the trader is and is not willing to be part of. Three of these are jokes with
        /// economic consequences and the fourth is the whole goal of the game, so they get checks.
        /// </summary>
        void Shelf(ShopDef shop, ItemCatalog items)
        {
            Debug.Log($"[EconomyTest] {shop.DisplayName} buys back at {shop.BuyBackFraction:P0} "
                      + $"and stocks {shop.Count} line(s).");

            Check("the trader pays less than it charges", shop.BuyBackFraction < 1f);

            ItemDef boot = items != null ? items.Find("boot") : null;
            ItemDef pearl = items != null ? items.Find("pearl") : null;
            ItemDef part = items != null ? items.Find("boat_part") : null;

            // A boot has to be worth nothing rather than one coin. The trader floors what it pays at
            // a coin, so a value of one would still be a sale and the joke would be a consolation
            // prize instead of eight hundred grams of carry limit you swear at.
            if (boot != null) Check("a boot is worth nothing", shop.PriceFor(boot) == 0);

            // The jackpot cannot be bought. A pearl on the shelf would turn fishing's best outcome
            // into a purchase, which is the one thing it must never be.
            if (pearl != null)
            {
                Check($"a pearl sells for {shop.PriceFor(pearl)} and is worth carrying",
                      shop.PriceFor(pearl) > 0);

                Check("but the trader does not stock pearls",
                      !shop.Offers.Any(o => o.IsValid && o.Item == pearl));
            }

            // And the boat cannot be sold back, which is what stops four parts and a refund from
            // being a money printer.
            if (part != null)
            {
                Check("a boat part cannot be sold back", shop.PriceFor(part) == 0);
                Check("but the trader stocks it", shop.Offers.Any(o => o.IsValid && o.Item == part));
            }
        }

        // ---------------------------------------------------------------- the model

        /// <summary>Every way of earning, priced as a round trip, printed as a table.</summary>
        List<EconomyModel.Rate> Model(FishCatalog fish, AnimalCatalog animals, NativeCatalog natives,
                                      ShopDef shop, float carryLimit)
        {
            var rates = new List<EconomyModel.Rate>
            {
                EconomyModel.Fishing(fish, shop, carryLimit),
                EconomyModel.Hunting(animals, shop, carryLimit),
                EconomyModel.Raiding(natives, shop),
                EconomyModel.Foraging(),
                EconomyModel.Gambling(),
            };

            Debug.Log($"[EconomyTest] one player, a {carryLimit:0} kg bag, and a {EconomyModel.TraderTripMinutes:0} "
                      + "minute walk to the counter:");

            foreach (EconomyModel.Rate rate in rates)
                Debug.Log($"[EconomyTest]   {rate}");

            EconomyModel.Rate[] paying = rates.Where(r => r.CoinsPerMinute > 0f).ToArray();

            Check("something pays at all", paying.Length > 0);
            if (paying.Length == 0) return rates;

            Check($"fishing pays ({rates[0].CoinsPerMinute:0.0} c/min)", rates[0].CoinsPerMinute > 0f);
            Check($"hunting pays ({rates[1].CoinsPerMinute:0.0} c/min)", rates[1].CoinsPerMinute > 0f);
            Check($"raiding pays ({rates[2].CoinsPerMinute:0.0} c/min)", rates[2].CoinsPerMinute > 0f);

            float best = paying.Max(r => r.CoinsPerMinute);
            float worst = paying.Min(r => r.CoinsPerMinute);

            string bestName = paying.First(r => Mathf.Approximately(r.CoinsPerMinute, best)).Activity;
            string worstName = paying.First(r => Mathf.Approximately(r.CoinsPerMinute, worst)).Activity;

            Debug.Log($"[EconomyTest] the spread is {best / worst:0.00}x: {bestName} at {best:0.0} c/min "
                      + $"against {worstName} at {worst:0.0}.");

            // Three is the line because two activities within three times of each other are a choice
            // - one is safer, one is faster, one is what you do while it rains - and four people
            // doing the one thing that pays five times the others is not a choice at all.
            Check($"no way of earning buries the others ({best / worst:0.00}x, cap 3)", best <= worst * 3f);

            return rates;
        }

        // ---------------------------------------------------------------- per kilogram

        /// <summary>
        /// The only honest comparison between two bags. Twenty slots and forty kilos mean weight is
        /// what the walk to the counter actually costs, so anything worth hunting, fishing or looting
        /// has to beat the thing you would otherwise be carrying.
        /// </summary>
        void Kilos(ItemCatalog items, ShopDef shop)
        {
            if (items == null) return;

            ItemDef scrap = items.Find("scrap_metal");
            if (scrap == null) return;

            float floor = EconomyModel.PerKilo(scrap, shop);
            Debug.Log($"[EconomyTest] scrap metal is the floor at {floor:0.0} c/kg.");

            string[] spoils = { "hide", "meat_raw", "meat_cooked", "fish_raw", "fish_cooked", "pearl",
                                "feather", "rope", "flint" };

            foreach (string id in spoils)
            {
                ItemDef def = items.Find(id);
                if (def == null) continue;

                float perKilo = EconomyModel.PerKilo(def, shop);

                Debug.Log($"[EconomyTest]   {id,-12} {shop.PriceFor(def),4} c  {def.Weight,5:0.00} kg  "
                          + $"{perKilo,6:0.0} c/kg");

                Check($"{id} beats scrap metal per kilo ({perKilo:0.0} vs {floor:0.0})", perKilo > floor);
            }

            // Cooking has to be worth the walk back to a fire, or the campfire is a survival chore
            // with a crafting menu attached.
            Cooked(items, shop, "meat_raw", "meat_cooked");
            Cooked(items, shop, "fish_raw", "fish_cooked");
        }

        void Cooked(ItemCatalog items, ShopDef shop, string rawId, string cookedId)
        {
            ItemDef raw = items.Find(rawId);
            ItemDef cooked = items.Find(cookedId);
            if (raw == null || cooked == null) return;

            float before = EconomyModel.PerKilo(raw, shop);
            float after = EconomyModel.PerKilo(cooked, shop);

            Check($"cooking {rawId} pays ({before:0.0} -> {after:0.0} c/kg)", after > before);
        }

        // ---------------------------------------------------------------- the boat

        /// <summary>The question the issue actually asks, with a number for an answer.</summary>
        void Boat(ShopDef shop, List<EconomyModel.Rate> rates)
        {
            int cost = EconomyModel.BoatCost(shop);
            float blended = EconomyModel.Blended(rates);
            float sessions = EconomyModel.SessionsToBoat(cost, rates);

            float perMinute = blended * EconomyModel.Attention * EconomyModel.Players;
            float towards = perMinute * (1f - EconomyModel.GearShare);

            Debug.Log($"[EconomyTest] the boat costs {cost} coins.");
            Debug.Log($"[EconomyTest] {EconomyModel.Players} players average {blended:0.0} c/min each at "
                      + $"{EconomyModel.Attention:P0} attention: {perMinute:0} c/min as a group, "
                      + $"{towards:0} of it after gear.");

            Debug.Log($"[EconomyTest] a {EconomyModel.SessionMinutes:0} minute session puts "
                      + $"{towards * EconomyModel.SessionMinutes:0} coins in the boat fund.");

            Debug.Log($"[EconomyTest] the boat is {sessions:0.00} session(s) away.");

            Check($"the boat costs something ({cost})", cost > 0);

            // The band. Under one and a half evenings the island's middle - the guns, the upgrades,
            // the second trip to the cave - never happens, because the boat arrives first. Over three
            // it stops being a goal and starts being a shift.
            Check($"the boat is a believable distance away ({sessions:0.00} sessions, want 1.5 to 3)",
                  sessions >= 1.5f && sessions <= 3f);
        }

        // ---------------------------------------------------------------- the counter

        /// <summary>
        /// The model against the trader. One animal's drops and one native's drops go into a bag, the
        /// bag goes to the counter, and the coins that come back have to be the coins the model said.
        /// </summary>
        IEnumerator Counter(Inventory bag, ShopCounter counter, ShopDef shop, AnimalCatalog animals,
                            NativeCatalog natives)
        {
            var wallet = bag.GetComponent<Wallet>();
            if (wallet == null)
            {
                Check("the player has a wallet", false);
                yield break;
            }

            AnimalDef animal = animals != null ? animals.Animals.FirstOrDefault(a => a != null) : null;
            NativeDef native = natives != null ? natives.Natives.FirstOrDefault(n => n != null) : null;

            if (animal == null && native == null) yield break;

            bag.ServerClear();

            // Standing at the counter is the player's job and teleporting is the harness's.
            bag.transform.position = counter.transform.position + counter.transform.forward * 2f;
            yield return new WaitForSeconds(0.3f);

            int expected = Stock(bag, shop, animal != null ? animal.Loot : null)
                           + Stock(bag, shop, native != null ? native.Loot : null);

            int before = wallet.Balance;
            int paid = 0;

            // Sell the bag out, slot by slot, the way a player empties one.
            for (int slot = 0; slot < bag.SlotCount; slot++)
            {
                ItemStack stack = bag[slot];
                if (stack.IsEmpty) continue;

                paid += counter.ServerSell(bag, wallet, slot, stack.Count, out string _);
            }

            int banked = wallet.Balance - before;

            Debug.Log($"[EconomyTest] a bag of {(animal != null ? animal.Id : "-")} and "
                      + $"{(native != null ? native.Id : "-")} drops sold for {paid} coins "
                      + $"(model said {expected}).");

            Check($"the trader paid what the model predicted ({paid} vs {expected})", paid == expected);
            Check($"and the wallet actually has it ({banked})", banked == paid);

            bag.ServerClear();
        }

        /// <summary>
        /// Puts one loot table's *rounded* drops into the bag and adds up what they should fetch.
        /// Rounded rather than expected, because a bag holds whole hides.
        /// </summary>
        int Stock(Inventory bag, ShopDef shop, LootDrop[] loot)
        {
            if (loot == null) return 0;

            int worth = 0;

            foreach (LootDrop drop in loot)
            {
                if (drop.Item == null) continue;

                int count = Mathf.Max(1, Mathf.RoundToInt((drop.Low + drop.High) * 0.5f));

                // Add hands back what would not fit, not what went in.
                int added = count - bag.Add(drop.Item, count);

                worth += added * shop.PriceFor(drop.Item);
            }

            return worth;
        }

        // ---------------------------------------------------------------- plumbing

        void Check(string what, bool ok)
        {
            if (ok)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[EconomyTest] FAILED: {what}.");
        }

        void Report()
        {
            if (_failed == 0) Debug.Log($"[EconomyTest] {_passed} passed, 0 failed.");
            else Debug.LogError($"[EconomyTest] {_passed} passed, {_failed} failed.");
        }
    }
}
