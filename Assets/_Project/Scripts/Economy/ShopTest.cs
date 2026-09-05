using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.UI;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Economy
{
    /// <summary>
    /// The acceptance test for #48, run inside a real session. Server side, behind <c>-shopTest</c>.
    ///
    /// The criterion is "buy/sell round-trips correctly with 4 players shopping simultaneously", and
    /// simultaneous is the whole word. Four clients pressing buy on a shelf that holds one arrive at
    /// the server as four calls, back to back, on one thread, with nothing between them - so the test
    /// makes exactly those calls. Same argument as #44's chest race: that is not an approximation of
    /// the race, it is the race.
    ///
    /// Three properties are checked after every step, because between them they catch every way a
    /// shop can be wrong:
    ///
    /// 1. **Stock conservation.** Items on the shelf plus items in bags never changes except by a
    ///    sale, and never goes negative. A shelf that oversells shows up here immediately.
    /// 2. **The ledger identity.** Money in wallets, minus what the server minted, plus what it
    ///    burned, equals what it started with. #47 gave us the two counters; this is the only thing
    ///    that makes "did the shop print money" a question with a number for an answer.
    /// 3. **Refunds are refunds.** A purchase that half-fits gives back exactly what it could not
    ///    deliver - on the shelf *and* in the wallet - and does it through
    ///    <see cref="Wallet.ServerRefund"/> so the ledger does not read it as income.
    ///
    /// And the one that needs no runtime check at all, only saying out loud: **a request names an
    /// offer index and a count, never a price.** Look at <see cref="Trading.RequestBuy"/> - there is
    /// nowhere to put a number. A modified client can ask for the wrong thing or too much of it, and
    /// the tests below cover both, but it cannot ask for a discount, because the wire has no field
    /// for one.
    /// </summary>
    public class ShopTest : MonoBehaviour
    {
        const float WaitForSecondPlayer = 60f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-shopTest")) return;

            _started = true;

            var go = new GameObject("ShopTest");
            DontDestroyOnLoad(go);
            go.AddComponent<ShopTest>();
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
                Debug.LogError("[ShopTest] Needs two players; start a second process with "
                               + "-client -shopTest. Nothing was checked.");
                yield break;
            }

            ShopCounter counter = null;
            deadline = Time.time + 10f;

            while (Time.time < deadline && counter == null)
            {
                counter = FindObjectsByType<ShopCounter>(FindObjectsSortMode.None)
                          .FirstOrDefault(c => c != null && c.IsSpawned);

                if (counter == null) yield return new WaitForSeconds(0.5f);
            }

            if (counter == null)
            {
                Debug.LogError("[ShopTest] No counter in this scene; run ShopFactory.Build and "
                               + "TerrainGenerator.GenerateIsland -rebuildPois, then -scene island.");
                yield break;
            }

            // The host's own body first, because it is the only one this process owns and therefore
            // the only one that can exercise the client-facing request path below.
            Inventory alice = bags.FirstOrDefault(b => b.IsOwner) ?? bags[0];
            Inventory bob = bags.First(b => b != alice);

            var trading = alice.GetComponent<Trading>();
            Wallet aliceWallet = alice.GetComponent<Wallet>();
            Wallet bobWallet = bob.GetComponent<Wallet>();

            if (trading == null || aliceWallet == null || bobWallet == null)
            {
                Debug.LogError("[ShopTest] A player is missing Trading or Wallet; run "
                               + "PlayerPrefabBuilder.BuildPlayerPrefab.");
                yield break;
            }

            ItemCatalog items = alice.Catalog;
            ItemDef rope = items.Find("rope");
            ItemDef flint = items.Find("flint");
            ItemDef hatchet = items.Find("hatchet");
            ItemDef knife = items.Find("knife");
            ItemDef boatPart = items.Find("boat_part");

            if (rope == null || flint == null || hatchet == null || knife == null || boatPart == null)
            {
                Debug.LogError("[ShopTest] The item catalog is missing something; run ItemFactory.Build.");
                yield break;
            }

            // Walking there is the player's job; a harness teleports. The shop is a place, and the
            // reach check is the thing being tested, so both bodies have to actually stand at it.
            Stand(alice, counter, -1.4f);
            Stand(bob, counter, 1.4f);

            yield return new WaitForSeconds(0.3f);

            int ropeOffer = OfferOf(counter, rope);
            int hatchetOffer = OfferOf(counter, hatchet);
            int knifeOffer = OfferOf(counter, knife);
            int boatOffer = OfferOf(counter, boatPart);

            ShopDef shop = counter.Shop;

            Debug.Log($"[ShopTest] {counter.name}: {counter.Describe()}");
            Debug.Log($"[ShopTest] alice at {(alice.transform.position - counter.transform.position).magnitude:F1}m, "
                      + $"bob at {(bob.transform.position - counter.transform.position).magnitude:F1}m.");

            Check("the shelf has rope, a hatchet, a knife and a boat part",
                  ropeOffer >= 0 && hatchetOffer >= 0 && knifeOffer >= 0 && boatOffer >= 0);
            Check("both players are at the counter",
                  counter.InReach(alice.transform.position) && counter.InReach(bob.transform.position));

            // ---------------------------------------------------------------- what things cost

            Check("the shop pays half of value for rope", shop.PriceFor(rope) == 2);
            Check("and sells it for more than that", counter.OfferAt(ropeOffer).Price > shop.PriceFor(rope));
            Check("it pays nothing for a boat part", shop.PriceFor(boatPart) == 0);
            Check("and nothing for nothing", shop.PriceFor(null) == 0);

            // ---------------------------------------------------------------- a plain purchase

            Reset(alice, bob, aliceWallet, bobWallet, counter);

            int ropePrice = counter.OfferAt(ropeOffer).Price;
            int ledger = Ledger(aliceWallet, bobWallet);

            int bought = counter.ServerBuy(alice, aliceWallet, ropeOffer, 3, out string why);

            Check("three rope come across the counter", bought == 3 && why == null);
            Check("and into the bag", alice.CountOf(rope) == 3);
            Check("paid for at the shop's price", aliceWallet.Balance == 1000 - 3 * ropePrice);
            Check("an unlimited line stays unlimited", counter.Remaining(ropeOffer) < 0);
            Check("and the ledger still balances", Ledger(aliceWallet, bobWallet) == ledger);

            // ---------------------------------------------------------------- selling it back

            int paid = counter.ServerSell(alice, aliceWallet, SlotOf(alice, rope), 2, out why);

            Check("two of them sell back", paid == 2 * shop.PriceFor(rope) && why == null);
            Check("and leave the bag", alice.CountOf(rope) == 1);
            Check("the money arrives", aliceWallet.Balance == 1000 - 3 * ropePrice + paid);
            Check("and the ledger still balances", Ledger(aliceWallet, bobWallet) == ledger);
            Check("the spread means a round trip loses money", aliceWallet.Balance < 1000);

            // ---------------------------------------------------------------- what will not sell

            alice.Add(boatPart, 1);
            int before = aliceWallet.Balance;

            paid = counter.ServerSell(alice, aliceWallet, SlotOf(alice, boatPart), 1, out why);

            Check("a boat part cannot be flipped back", paid == 0 && why != null);
            Check("and stays in the bag", alice.CountOf(boatPart) == 1);
            Check("and paid nothing", aliceWallet.Balance == before);

            Check("nor can an empty slot be sold",
                  counter.ServerSell(alice, aliceWallet, EmptySlot(alice), 1, out why) == 0 && why != null);
            Check("nor a slot that does not exist",
                  counter.ServerSell(alice, aliceWallet, 999, 1, out why) == 0 && why != null);

            // ---------------------------------------------------------------- affording it

            Reset(alice, bob, aliceWallet, bobWallet, counter);
            aliceWallet.ServerSetBalance(2 * ropePrice + 3);
            ledger = Ledger(aliceWallet, bobWallet);

            bought = counter.ServerBuy(alice, aliceWallet, ropeOffer, 5, out why);

            Check("asking for more than you can afford buys what you can", bought == 2);
            Check("and spends what it cost", aliceWallet.Balance == 3);
            Check("a wallet that cannot afford one buys nothing",
                  counter.ServerBuy(alice, aliceWallet, ropeOffer, 1, out why) == 0 && why != null);
            Check("and is not emptied trying", aliceWallet.Balance == 3);
            Check("the ledger still balances", Ledger(aliceWallet, bobWallet) == ledger);

            // ---------------------------------------------------------------- the race

            Reset(alice, bob, aliceWallet, bobWallet, counter);

            int hatchetPrice = counter.OfferAt(hatchetOffer).Price;
            int shelf = counter.Remaining(hatchetOffer);
            int stock = Stock(counter, alice, bob, hatchet);
            ledger = Ledger(aliceWallet, bobWallet);

            Check("there is exactly one hatchet on the shelf", shelf == 1);

            int first = counter.ServerBuy(alice, aliceWallet, hatchetOffer, 1, out why);
            int second = counter.ServerBuy(bob, bobWallet, hatchetOffer, 1, out string secondWhy);

            Check("the first to click gets the hatchet", first == 1);
            Check("the second is told it is gone", second == 0 && secondWhy != null);
            Check("the shelf is empty, not negative", counter.Remaining(hatchetOffer) == 0);
            Check("one hatchet exists, not two", Stock(counter, alice, bob, hatchet) == stock);
            Check("only one hatchet was paid for",
                  aliceWallet.Balance == 1000 - hatchetPrice && bobWallet.Balance == 1000);
            Check("and the ledger balances", Ledger(aliceWallet, bobWallet) == ledger);

            // Four players, two knives. This is the acceptance sentence, done literally: four buy
            // calls back to back on one thread with nothing between them.
            int knifePrice = counter.OfferAt(knifeOffer).Price;
            shelf = counter.Remaining(knifeOffer);
            stock = Stock(counter, alice, bob, knife);

            int sold = 0;
            sold += counter.ServerBuy(alice, aliceWallet, knifeOffer, 1, out why);
            sold += counter.ServerBuy(bob, bobWallet, knifeOffer, 1, out why);
            sold += counter.ServerBuy(alice, aliceWallet, knifeOffer, 1, out why);
            sold += counter.ServerBuy(bob, bobWallet, knifeOffer, 1, out why);

            Check($"four clicks on a shelf of {shelf} sell {shelf}", sold == shelf);
            Check("the shelf is empty, not negative", counter.Remaining(knifeOffer) == 0);
            Check("no knife was duplicated", Stock(counter, alice, bob, knife) == stock);
            Check("and nobody paid for a knife they did not get",
                  aliceWallet.Balance + bobWallet.Balance == 2000 - hatchetPrice - sold * knifePrice);
            Check("the ledger balances after the scramble", Ledger(aliceWallet, bobWallet) == ledger);

            // Greed on a limited line clamps rather than overselling. The shelf is refilled first,
            // because "you bought all zero of them" is a test that passes for the wrong reason.
            Reset(alice, bob, aliceWallet, bobWallet, counter);
            counter.ServerRestock();
            aliceWallet.ServerSetBalance(100000);
            shelf = counter.Remaining(hatchetOffer);

            Check("a restock put one hatchet back", shelf == 1);

            bought = counter.ServerBuy(alice, aliceWallet, hatchetOffer, 999, out why);

            Check("asking for a thousand buys what is on the shelf", bought == shelf);
            Check("and leaves it empty rather than owing", counter.Remaining(hatchetOffer) == 0);

            // ---------------------------------------------------------------- a full bag

            Reset(alice, bob, aliceWallet, bobWallet, counter);
            ledger = Ledger(aliceWallet, bobWallet);

            alice.Add(flint, alice.SlotCount * flint.MaxStack);
            Check("alice's bag will not take another flint", alice.Add(flint, 1) == 1);

            before = aliceWallet.Balance;
            shelf = counter.Remaining(knifeOffer);

            bought = counter.ServerBuy(alice, aliceWallet, knifeOffer, 1, out why);

            Check("a full bag buys nothing", bought == 0 && why != null);
            Check("and is charged nothing", aliceWallet.Balance == before);
            Check("the knife goes back on the shelf", counter.Remaining(knifeOffer) == shelf);
            Check("and the refund did not read as income", Ledger(aliceWallet, bobWallet) == ledger);

            // ---------------------------------------------------------------- the wrong offer

            Reset(alice, bob, aliceWallet, bobWallet, counter);
            ledger = Ledger(aliceWallet, bobWallet);

            Check("there is no offer minus one",
                  counter.ServerBuy(alice, aliceWallet, -1, 1, out why) == 0 && why != null);
            Check("nor offer nine hundred",
                  counter.ServerBuy(alice, aliceWallet, 900, 1, out why) == 0 && why != null);
            Check("nor buying none of something",
                  counter.ServerBuy(alice, aliceWallet, ropeOffer, 0, out why) == 0);
            Check("nor a negative number of them",
                  counter.ServerBuy(alice, aliceWallet, ropeOffer, -5, out why) == 0);
            Check("none of that cost anything",
                  aliceWallet.Balance == 1000 && Ledger(aliceWallet, bobWallet) == ledger);

            // ---------------------------------------------------------------- standing too far away

            Vector3 home = bob.transform.position;
            Stand(bob, counter, 0f, away: 200f);

            yield return new WaitForSeconds(0.3f);

            Check("bob is no longer at the counter", !counter.InReach(bob.transform.position));
            Check("and cannot buy from across the island",
                  counter.ServerBuy(bob, bobWallet, ropeOffer, 1, out why) == 0 && why != null);
            Check("nor sell into it", Sell(counter, bob, bobWallet, rope) == 0);
            Check("and was charged nothing", bobWallet.Balance == 1000);

            // ---------------------------------------------------------------- the client's door

            // Everything above called the server directly. This is the path a real player takes: an
            // owner-side request, one network tick, and the server deciding on its own numbers.
            Reset(alice, bob, aliceWallet, bobWallet, counter);
            ledger = Ledger(aliceWallet, bobWallet);

            trading.RequestBuy(counter, ropeOffer, 2);

            yield return Delivered();

            Check("a request from the owner buys", alice.CountOf(rope) == 2);
            Check("at the server's price", aliceWallet.Balance == 1000 - 2 * ropePrice);

            trading.RequestSell(counter, SlotOf(alice, rope), 2);

            yield return Delivered();

            Check("and sells back through the same door", alice.CountOf(rope) == 0);
            Check("for what the shop pays", aliceWallet.Balance == 1000 - 2 * ropePrice + 2 * shop.PriceFor(rope));
            Check("with the ledger still balancing", Ledger(aliceWallet, bobWallet) == ledger);

            before = aliceWallet.Balance;

            trading.RequestBuy(counter, 900, 1);
            trading.RequestBuy(counter, ropeOffer, -3);
            trading.RequestBuy(null, ropeOffer, 1);
            trading.RequestSell(counter, 999, 1);

            yield return Delivered();

            Check("nonsense through the client door changes nothing",
                  aliceWallet.Balance == before && alice.CountOf(rope) == 0);
            Check("and the ledger is untouched by it", Ledger(aliceWallet, bobWallet) == ledger);

            // Out of reach through the client door, which is the check that matters: the client picks
            // the counter, so it is the client that can pick the wrong one.
            Vector3 aliceHome = alice.transform.position;
            Stand(alice, counter, 0f, away: 200f);

            yield return new WaitForSeconds(0.3f);

            trading.RequestBuy(counter, ropeOffer, 1);

            yield return Delivered();

            Check("a counter two hundred metres away sells nothing",
                  alice.CountOf(rope) == 0 && aliceWallet.Balance == before);

            if (!string.IsNullOrEmpty(trading.LastRefusal))
                Debug.Log($"[ShopTest] the server's answer to that: \"{trading.LastRefusal}\".");

            alice.GetComponent<PlayerMotor>()?.ServerTeleport(aliceHome, 0f);
            bob.GetComponent<PlayerMotor>()?.ServerTeleport(home, 0f);

            // ---------------------------------------------------------------- restocking

            counter.ServerRestock();

            Check("a restock puts the hatchet back on the shelf",
                  counter.Remaining(hatchetOffer) == counter.OfferAt(hatchetOffer).Stock);
            Check("and does not overfill it",
                  counter.Remaining(knifeOffer) <= counter.OfferAt(knifeOffer).Stock);

            string line = $"[ShopTest] {_passed} passed, {_failed} failed. "
                          + $"end: alice {Purse.Text(aliceWallet.Balance)}, "
                          + $"bob {Purse.Text(bobWallet.Balance)}, "
                          + $"{Wallet.Minted} minted, {Wallet.Burned} burned. "
                          + $"shelf: {counter.Describe()}";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>A ServerRpc lands a network tick later, not a frame later. See #46's harness.</summary>
        static WaitForSeconds Delivered() => new(0.3f);

        /// <summary>
        /// Puts a body at the counter, or a long way from it. The reach check reads the transform, so
        /// the only honest way to test it is to move the transform.
        /// </summary>
        static void Stand(Inventory bag, ShopCounter counter, float sideways, float away = 2f)
        {
            var motor = bag.GetComponent<PlayerMotor>();
            if (motor == null) return;

            Transform at = counter.transform;
            Vector3 spot = at.position + at.right * sideways + at.forward * away
                           + Vector3.up * 0.5f;

            motor.ServerTeleport(spot, 0f);
        }

        /// <summary>Which line on the shelf sells this, or -1.</summary>
        static int OfferOf(ShopCounter counter, ItemDef def)
        {
            for (int i = 0; i < counter.OfferCount; i++)
                if (counter.OfferAt(i).Item == def) return i;

            return -1;
        }

        static int SlotOf(Inventory bag, ItemDef def)
        {
            for (int i = 0; i < bag.SlotCount; i++)
                if (bag[i].Def == def) return i;

            return -1;
        }

        static int EmptySlot(Inventory bag)
        {
            for (int i = 0; i < bag.SlotCount; i++)
                if (bag[i].IsEmpty) return i;

            return -1;
        }

        static int Sell(ShopCounter counter, Inventory bag, Wallet wallet, ItemDef def)
        {
            int slot = SlotOf(bag, def);
            if (slot < 0) bag.Add(def, 1);

            return counter.ServerSell(bag, wallet, SlotOf(bag, def), 1, out string _);
        }

        /// <summary>Everything of a kind that exists: on the shelf and in both bags.</summary>
        static int Stock(ShopCounter counter, Inventory a, Inventory b, ItemDef def)
        {
            int offer = OfferOf(counter, def);
            int shelf = offer >= 0 ? Mathf.Max(0, counter.Remaining(offer)) : 0;

            return shelf + a.CountOf(def) + b.CountOf(def);
        }

        /// <summary>
        /// The one number that says whether the shop printed money: what the wallets hold, adjusted
        /// by what the server admits to having created and destroyed. It must never move except when
        /// the server deliberately mints or burns, and a shop that refunds by minting shows up here
        /// as a wallet total that no longer matches its own ledger.
        /// </summary>
        static int Ledger(Wallet a, Wallet b)
            => a.Balance + b.Balance - Wallet.Minted + Wallet.Burned;

        static void Reset(Inventory alice, Inventory bob, Wallet aliceWallet, Wallet bobWallet,
                          ShopCounter counter)
        {
            alice.ServerClear();
            bob.ServerClear();
            aliceWallet.ServerSetBalance(1000);
            bobWallet.ServerSetBalance(1000);
            Wallet.ResetLedger();
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[ShopTest] FAILED: {what}.");
        }
    }
}
