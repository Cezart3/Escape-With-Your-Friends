using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Player;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>
    /// The blackjack table's harness, behind <c>-blackjackTest</c>. Wants <c>-scene island -noNatives
    /// -noAnimals</c>. A second process with <c>-blackjackTest -client</c> checks what a peer that is
    /// not the server sees.
    ///
    /// 1. The rules, on paper: totals, the dealer's 17, what each finish pays, the shoe, and scripted
    ///    rounds for the peek, splits, split aces and doubles. Thousands of random rounds rebuild
    ///    from their log to the server's hands without the hole card ever leaking early.
    /// 2. Basic strategy over a fixed run of shoes loses exactly what it lost under .NET (which is
    ///    the check that Mono shuffles like .NET does) and about half a percent, as a real table does.
    /// 3. The table in the world: rigged shoes through the real buttons pay exactly what the rules
    ///    say, the ledger balances, a player who does nothing is stood, and the refusals hold.
    /// </summary>
    public class BlackjackTest : MonoBehaviour
    {
        const float WaitForWorld = 60f;

        /// <summary>Seeds <c>i * 7919 + 13</c>, one seat, 100 a hand, by the chart.</summary>
        const int GoldenRounds = 200000;

        /// <summary>What those rounds netted under .NET 8, outside Unity.</summary>
        const long GoldenNet = -113500;

        const int FuzzRounds = 5000;
        const int Watched = 3;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-blackjackTest")) return;

            _started = true;

            var go = new GameObject("BlackjackTest");
            DontDestroyOnLoad(go);
            go.AddComponent<BlackjackTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null) yield return null;

            if (!InstanceFinder.NetworkManager.IsServerStarted)
            {
                yield return Watching();
                yield break;
            }

            Paper();
            Scripted();
            yield return Fuzz();
            yield return Strategy();

            PlayerMotor player = null;
            BlackjackTable table = null;
            float deadline = Time.time + WaitForWorld;

            while (Time.time < deadline && (player == null || table == null))
            {
                player = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None).FirstOrDefault(m => m != null && m.IsSpawned);
                table = FindObjectsByType<BlackjackTable>(FindObjectsSortMode.None).FirstOrDefault(t => t.IsSpawned);
                if (player == null || table == null) yield return new WaitForSeconds(0.5f);
            }

            if (player == null || table == null)
            {
                Check("a player and a blackjack table in the world. Run BlackjackFactory.Build and bake "
                      + "the POIs with -rebuildPois", false);
                Report();
                yield break;
            }

            var wallet = player.GetComponent<Wallet>();
            NetworkObject actor = player.NetworkObject;

            Wiring(table);

            table.ServerSetTiming(0.3f, 2f, 0.05f, 0.4f);
            wallet.ServerSetBalance(500);
            wallet.ServerSetChips(5000);
            Wallet.ResetLedger();
            yield return new WaitForSeconds(0.3f);

            int money = Wallet.TotalInWallets();
            int chips = wallet.Chips;

            yield return HitAndWin(table, wallet, actor);
            yield return SplitAndDouble(table, wallet, actor);
            yield return Peeked(table, wallet, actor);
            yield return TimedOut(table, wallet, actor);

            Check($"money never moved at the table ({money} before, {Wallet.TotalInWallets()} after)",
                  Wallet.TotalInWallets() == money);
            Check("and the ledger called none of it income", Wallet.Minted == 0 && Wallet.Burned == 0 && Wallet.Exchanged == 0);
            Check($"every chip is accounted for ({Wallet.Staked} staked, {Wallet.PaidOut} paid, {wallet.Chips} held)",
                  wallet.Chips == chips - Wallet.Staked + Wallet.PaidOut);

            // Last: it sets the stack outright, which the ledger above would rightly not balance.
            yield return Refusals(table, wallet, actor);

            Report();

            yield return Exhibition(table, wallet, actor);
        }

        // ---------------------------------------------------------------- cards

        static readonly string[] Ranks = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };

        /// <summary>A card by rank. The suit walks round so a rigged hand is not all spades.</summary>
        static int C(string rank, int suit = 0) => System.Array.IndexOf(Ranks, rank) + 13 * (suit % 4);

        static int[] Shoe(params string[] ranks)
        {
            var shoe = new int[ranks.Length];
            for (int i = 0; i < ranks.Length; i++) shoe[i] = C(ranks[i], i);
            return shoe;
        }

        static List<int> Hand(params string[] ranks) => Shoe(ranks).ToList();

        static bool SameHands(List<int>[] a, List<int>[] b)
        {
            for (int h = 0; h <= BlackjackMath.Dealer; h++)
                if (!a[h].SequenceEqual(b[h])) return false;

            return true;
        }

        // ---------------------------------------------------------------- 1. on paper

        void Paper()
        {
            Check("ace-six is a soft 17", BlackjackMath.Total(Hand("A", "6"), out bool soft) == 17 && soft);
            Check("ace-six-ten is a hard 17", BlackjackMath.Total(Hand("A", "6", "10"), out soft) == 17 && !soft);
            Check("ace-ace-nine is 21", BlackjackMath.Total(Hand("A", "A", "9")) == 21);
            Check("king-queen-two is 22", BlackjackMath.Total(Hand("K", "Q", "2")) == 22);
            Check("the dealer hits 16", BlackjackMath.DealerDraws(Hand("10", "6")));
            Check("and stands on a soft 17 and a hard one",
                  !BlackjackMath.DealerDraws(Hand("A", "6")) && !BlackjackMath.DealerDraws(Hand("10", "7")));

            List<int> dealer19 = Hand("10", "9"), dealerBj = Hand("A", "K"), dealerBust = Hand("10", "6", "7");

            Check("blackjack pays 3 to 2 (100 back as 250)", BlackjackMath.Returned(100, Hand("A", "K"), true, dealer19) == 250);
            Check("blackjack against blackjack pushes", BlackjackMath.Returned(100, Hand("A", "K"), true, dealerBj) == 100);
            Check("20 beats 19 at evens", BlackjackMath.Returned(100, Hand("10", "Q"), false, dealer19) == 200);
            Check("19 against 19 pushes", BlackjackMath.Returned(100, Hand("9", "K"), false, dealer19) == 100);
            Check("a bust loses even when the dealer busts", BlackjackMath.Returned(100, Hand("K", "Q", "5"), false, dealerBust) == 0);
            Check("anything standing beats a dealer bust", BlackjackMath.Returned(100, Hand("10", "2"), false, dealerBust) == 200);
            Check("a three-card 21 loses to a dealer blackjack", BlackjackMath.Returned(100, Hand("7", "7", "7"), false, dealerBj) == 0);
            Check("an odd bet's blackjack rounds down (50 back as 125)", BlackjackMath.Returned(50, Hand("A", "J"), true, dealer19) == 125);

            int[] shoe = BlackjackMath.Shoe(12345);
            int[] counts = new int[52];
            foreach (int card in shoe) counts[card]++;

            Check($"a shoe is six whole decks ({shoe.Length} cards)", shoe.Length == 312 && counts.All(n => n == 6));
            Check("the same seed is the same shoe", shoe.SequenceEqual(BlackjackMath.Shoe(12345)));
            Check("and another seed is another", !shoe.SequenceEqual(BlackjackMath.Shoe(12346)));
        }

        void Scripted()
        {
            // Deal order with one seat: player, dealer's up card, player, hole card, then the draws.
            var peek = new BlackjackRound(Shoe("10", "A", "9", "K"), new[] { 100 });
            Check("a dealer blackjack under an ace ends the round before anybody acts",
                  peek.Turn < 0 && peek.HoleShown && peek.Returned(0) == 0 && !peek.Can(BlackjackAction.Hit));

            var both = new BlackjackRound(Shoe("A", "A", "K", "K"), new[] { 100 });
            Check("and a player blackjack against it pushes", both.Turn < 0 && both.Returned(0) == 100);

            var noPeek = new BlackjackRound(Shoe("10", "6", "9", "A", "5"), new[] { 100 });
            Check("an ace in the hole under a six is not looked at", noPeek.Turn == 0 && !noPeek.HoleShown
                  && BlackjackMath.Rebuild(noPeek.Log)[BlackjackMath.Dealer].Count == 1);

            var split = new BlackjackRound(Shoe("8", "6", "8", "10", "3", "10", "10", "9"), new[] { 100 });
            bool splitOk = split.Can(BlackjackAction.Split) && split.Cost(BlackjackAction.Split) == 100
                           && split.Act(BlackjackAction.Split);
            bool once = !split.Can(BlackjackAction.Split);
            bool two = split.Cards[0].Count == 2 && split.Cards[1].Count == 2
                       && BlackjackMath.Total(split.Cards[0]) == 11 && BlackjackMath.Total(split.Cards[1]) == 18;
            bool doubled = split.Act(BlackjackAction.Double) && split.Turn == 1;
            bool stood = split.Act(BlackjackAction.Stand) && split.Turn < 0;
            split.PlayDealer();

            Check("two eights split into 8-3 and 8-10, once", splitOk && once && two);
            Check("the first doubles to 21 on one card and the turn moves to the second", doubled && split.Bet[0] == 200
                  && BlackjackMath.Total(split.Cards[0]) == 21);
            Check($"the dealer's 16 draws to {BlackjackMath.Total(split.Cards[BlackjackMath.Dealer])} and both hands win (400 and 200)",
                  stood && split.Returned(0) == 400 && split.Returned(1) == 200);
            Check("the log rebuilds to the same hands", SameHands(BlackjackMath.Rebuild(split.Log), split.Cards));

            var aces = new BlackjackRound(Shoe("A", "9", "A", "7", "K", "5", "8"), new[] { 100 });
            aces.Act(BlackjackAction.Split);
            aces.PlayDealer();
            Check("split aces take one card each and the round moves on", aces.Turn < 0
                  && aces.Cards[0].Count == 2 && aces.Cards[1].Count == 2);
            Check("and ace-king after a split is 21 at evens, not blackjack (200, not 250)",
                  !aces.IsBlackjack(0) && aces.Returned(0) == 200);

            var bust = new BlackjackRound(Shoe("10", "5", "6", "K", "9"), new[] { 100 });
            bust.Act(BlackjackAction.Hit);
            Check("a hit to 25 ends the hand, and with nothing left standing the dealer does not draw",
                  bust.Turn < 0 && !bust.DealerPlays && bust.Returned(0) == 0);

            var late = new BlackjackRound(Shoe("5", "9", "6", "7", "2"), new[] { 100 });
            late.Act(BlackjackAction.Hit);
            Check("no double and no split on three cards", !late.Can(BlackjackAction.Double) && !late.Can(BlackjackAction.Split));
            Check("and no split of a five and a six", !new BlackjackRound(Shoe("5", "9", "6", "7"), new[] { 100 }).Can(BlackjackAction.Split));

            var four = new BlackjackRound(Shoe("2", "2", "2", "9", "3", "3", "3", "7"), new[] { 100, 0, 100, 100 });
            int[] order = four.Log.Take(7).Select(e => e / 64).ToArray();
            Check($"three seats are dealt in seat order round the table and back ({string.Join(",", order)})",
                  order.SequenceEqual(new[] { 0, 4, 6, 8, 0, 4, 6 }) && four.Turn == 0);
        }

        IEnumerator Fuzz()
        {
            var rng = new System.Random(4242);
            bool rebuilt = true, hidden = true, ends = true;

            for (int n = 0; n < FuzzRounds; n++)
            {
                if (n % 1000 == 999) yield return null;

                var bets = new int[BlackjackMath.Seats];
                for (int s = 0; s < bets.Length; s++) bets[s] = rng.Next(3) == 0 ? 0 : 50 * rng.Next(1, 11);
                if (bets.All(b => b == 0)) bets[0] = 50;

                var round = new BlackjackRound(BlackjackMath.Shoe(rng.Next()), bets);

                for (int step = 0; round.Turn >= 0 && step < 100; step++)
                {
                    if (!round.HoleShown && BlackjackMath.Rebuild(round.Log)[BlackjackMath.Dealer].Count != 1) hidden = false;
                    round.Act((BlackjackAction)rng.Next(4));
                }

                if (round.Turn >= 0) ends = false;

                round.PlayDealer();
                if (!SameHands(BlackjackMath.Rebuild(round.Log), round.Cards)) rebuilt = false;
            }

            Check($"{FuzzRounds} random rounds all end", ends);
            Check("and the hole card never reached the log while players acted", hidden);
            Check("and every one's log rebuilds to the server's hands", rebuilt);
        }

        IEnumerator Strategy()
        {
            long net = 0, staked = 0;
            int naturals = 0;
            float started = Time.realtimeSinceStartup;

            for (int i = 0; i < GoldenRounds; i++)
            {
                if (i % 20000 == 19999) yield return null;

                net += BlackjackStrategy.PlayOne(BlackjackMath.Shoe(i * 7919 + 13), 100, out BlackjackRound round);
                staked += round.Bet.Sum();
                if (round.IsBlackjack(0)) naturals++;
            }

            double rtp = 1.0 + net / (GoldenRounds * 100.0);
            Debug.Log($"[BlackjackTest] {GoldenRounds} rounds by the chart: net {net}, return {rtp:P2} of the "
                      + $"first bet, {staked} staked, blackjacks {100.0 * naturals / GoldenRounds:0.00}%, "
                      + $"{Time.realtimeSinceStartup - started:0.0}s.");

            Check($"{GoldenRounds} fixed shoes by the chart net {net}, as they did under .NET ({GoldenNet})", net == GoldenNet);
            Check($"which returns {rtp:P2}, inside 98.5%-100.5%", rtp >= 0.985 && rtp <= 1.005);
            Check($"and a blackjack comes about one hand in 21 ({100.0 * naturals / GoldenRounds:0.00}%)",
                  naturals > GoldenRounds * 0.044 && naturals < GoldenRounds * 0.051);
        }

        // ---------------------------------------------------------------- 3. in the world

        void Wiring(BlackjackTable table)
        {
            BlackjackButton[] buttons = table.GetComponentsInChildren<BlackjackButton>();
            int wanted = BlackjackMath.Seats * 5;

            Check($"the table has {wanted} buttons ({buttons.Length}), each its own NetworkObject",
                  buttons.Length == wanted && buttons.Select(b => b.NetworkObject).Distinct().Count() == wanted
                  && buttons.All(b => b.NetworkObject != table.NetworkObject && b.Table == table));

            bool every = true;
            for (int seat = 0; seat < BlackjackMath.Seats; seat++)
            foreach (BlackjackPress press in System.Enum.GetValues(typeof(BlackjackPress)))
                if (buttons.Count(b => b.Seat == seat && b.Press == press) != 1) every = false;

            Check("one of each press at every seat", every);
        }

        IEnumerator Deal(BlackjackTable table, NetworkObject actor, int[] rig, int presses)
        {
            yield return Until(table, BlackjackPhase.Betting);

            table.RiggedShoe = rig;
            BlackjackButton bet = Button(table, 0, BlackjackPress.Bet);
            for (int n = 0; n < presses; n++) bet.ServerInteract(actor);

            table.ServerCallIt();
            yield return new WaitUntil(() => table.Phase != BlackjackPhase.Betting);
        }

        IEnumerator HitAndWin(BlackjackTable table, Wallet wallet, NetworkObject actor)
        {
            int before = wallet.Chips;

            yield return Until(table, BlackjackPhase.Betting);
            table.RiggedShoe = Shoe("10", "9", "6", "7", "5", "10");

            BlackjackButton bet = Button(table, 0, BlackjackPress.Bet);
            bet.ServerInteract(actor);
            bet.ServerInteract(actor);

            Check("two presses of seat 1's bet put 100 on it and sit the player there",
                  table.BetOn(0) == 100 && table.Owner(0) == actor.ObjectId && wallet.Chips == before - 100);
            Check("who cannot take a second seat", !Button(table, 1, BlackjackPress.Bet).ServerCanInteract(actor)
                                                   && table.ServerBet(actor, 1) == 0);

            table.ServerCallIt();
            yield return new WaitUntil(() => table.Phase != BlackjackPhase.Betting);

            List<int>[] shown = table.Hands;
            Check("the deal shows the dealer's 9 and hides the 7 from everybody, the server's own copy included",
                  table.Phase == BlackjackPhase.Playing && table.HoleDown && shown[BlackjackMath.Dealer].Count == 1
                  && table.Log.All(e => e != BlackjackMath.Dealer * 64 + table.Dealt.Cards[BlackjackMath.Dealer][1]));

            string title = UI.BlackjackBoard.Title(table);
            string line = UI.BlackjackBoard.Line(table, wallet.Chips, actor.ObjectId);
            Check($"the board reads \"{title}\" over \"{line}\"", title.EndsWith("?") && line.Contains("16") && line.Contains("YOUR TURN"));

            Check("the player may hit, stand or double 16, not split it",
                  Button(table, 0, BlackjackPress.Hit).ServerCanInteract(actor)
                  && Button(table, 0, BlackjackPress.Double).ServerCanInteract(actor)
                  && !Button(table, 0, BlackjackPress.Split).ServerCanInteract(actor));

            Button(table, 0, BlackjackPress.Hit).ServerInteract(actor);

            yield return Until(table, BlackjackPhase.Done);

            Check($"a five makes 21, the dealer's 16 draws a ten and busts, and 100 comes back as 200 ({table.PaidOn(0)})",
                  table.PaidOn(0) == 200 && wallet.Chips == before + 100 && !table.HoleDown);
            Check("everybody's log rebuilds to the server's hands", SameHands(table.Hands, table.Dealt.Cards));

            line = UI.BlackjackBoard.Line(table, wallet.Chips, actor.ObjectId);
            Check($"and the board says so (\"{line}\")", line.Contains("WIN +100"));
        }

        IEnumerator SplitAndDouble(BlackjackTable table, Wallet wallet, NetworkObject actor)
        {
            int before = wallet.Chips;
            yield return Deal(table, actor, Shoe("8", "6", "8", "10", "3", "10", "10", "9"), 2);

            Button(table, 0, BlackjackPress.Split).ServerInteract(actor);
            Check("a split takes another 100 and makes two hands", wallet.Chips == before - 200
                  && table.BetOn(0) == 100 && table.BetOn(1) == 100 && table.Hands[1].Count == 2);

            Button(table, 0, BlackjackPress.Double).ServerInteract(actor);
            Check("doubling 8-3 takes 100 more, draws one card and moves on", wallet.Chips == before - 300
                  && table.BetOn(0) == 200 && table.Hands[0].Count == 3 && table.Turn == 1);

            Button(table, 0, BlackjackPress.Stand).ServerInteract(actor);
            yield return Until(table, BlackjackPhase.Done);

            Check($"21 and 18 against a dealer bust pay 400 and 200 ({table.PaidOn(0)} and {table.PaidOn(1)})",
                  table.PaidOn(0) == 400 && table.PaidOn(1) == 200 && wallet.Chips == before + 300);
        }

        IEnumerator Peeked(BlackjackTable table, Wallet wallet, NetworkObject actor)
        {
            int before = wallet.Chips;
            yield return Deal(table, actor, Shoe("10", "A", "9", "K"), 2);

            Check("a dealer blackjack skips the players' turns", table.Phase != BlackjackPhase.Playing
                  || table.Turn < 0);
            Check("and nobody may act", !Button(table, 0, BlackjackPress.Hit).ServerCanInteract(actor)
                                        && !table.ServerAct(actor, 0, BlackjackAction.Hit));

            yield return Until(table, BlackjackPhase.Done);
            Check("19 loses the 100 and only the 100", table.PaidOn(0) == 0 && wallet.Chips == before - 100);
        }

        IEnumerator TimedOut(BlackjackTable table, Wallet wallet, NetworkObject actor)
        {
            int before = wallet.Chips;
            yield return Deal(table, actor, Shoe("10", "10", "7", "7"), 2);

            float dealt = Time.time;
            yield return Until(table, BlackjackPhase.Done);

            Check($"a player who does nothing is stood after the turn runs out ({Time.time - dealt:0.0}s)",
                  Time.time - dealt >= 1.5f && table.Hands[0].Count == 2);
            Check("and 17 against 17 pushes", table.PaidOn(0) == 100 && wallet.Chips == before);
        }

        IEnumerator Refusals(BlackjackTable table, Wallet wallet, NetworkObject actor)
        {
            yield return Until(table, BlackjackPhase.Betting);

            wallet.ServerSetChips(BlackjackTable.Chunk - 1);
            Check("a stack short of a chunk cannot sit", table.ServerBet(actor, 0) == 0 && table.Owner(0) < 0);

            wallet.ServerSetChips(5000);
            for (int n = 0; n < 20; n++) table.ServerBet(actor, 0);
            Check($"a seat stops taking chips at {BlackjackTable.MaxBet} ({table.BetOn(0)})", table.BetOn(0) == BlackjackTable.MaxBet);

            table.ServerSetTiming(0.3f, 20f, 0.05f, 0.4f);
            table.RiggedShoe = Shoe("5", "9", "6", "7", "10");
            table.ServerCallIt();
            yield return Until(table, BlackjackPhase.Playing);

            Check("no bets once the cards are out", table.ServerBet(actor, 0) == 0 && table.ServerBet(actor, 2) == 0);
            Check("no playing another seat's hand", !table.ServerAct(actor, 1, BlackjackAction.Hit));

            wallet.ServerSetChips(BlackjackTable.MaxBet - 1);
            Check("no double without the chips for it", !Button(table, 0, BlackjackPress.Double).ServerCanInteract(actor)
                                                        && !table.ServerAct(actor, 0, BlackjackAction.Double)
                                                        && wallet.Chips == BlackjackTable.MaxBet - 1);

            Check("but a stand is always there", table.ServerAct(actor, 0, BlackjackAction.Stand));
            yield return Until(table, BlackjackPhase.Betting);
            Check("and the table clears for the next round", table.Owner(0) < 0 && table.BetOn(0) == 0 && table.Log.Count == 0);
        }

        /// <summary>Plays a seat by the chart until killed, so a <c>-blackjackTest -client</c> has something to watch.</summary>
        IEnumerator Exhibition(BlackjackTable table, Wallet wallet, NetworkObject actor)
        {
            table.ServerSetTiming(1f, 5f, 0.3f, 1.5f);

            while (true)
            {
                wallet.ServerSetChips(5000);
                yield return Deal(table, actor, null, 2);

                while (table.Phase == BlackjackPhase.Playing)
                {
                    if (table.Turn >= 0)
                    {
                        List<int>[] hands = table.Hands;
                        BlackjackAction advice = BlackjackStrategy.Advise(hands[table.Turn], hands[BlackjackMath.Dealer][0],
                                                                          table.BetOn(1) == 0);
                        if (!table.ServerAct(actor, 0, advice)) table.ServerAct(actor, 0, BlackjackAction.Stand);
                    }

                    yield return new WaitForSeconds(0.4f);
                }

                yield return Until(table, BlackjackPhase.Betting);
            }
        }

        // ---------------------------------------------------------------- the client's half

        /// <summary>
        /// A peer that is not the server sees only the log. While players act the dealer must show
        /// one card; once the table pays, every hand's payout must be what the rules make of the
        /// hands this client rebuilt - so nothing the payout depended on was withheld from it.
        /// </summary>
        IEnumerator Watching()
        {
            while (!InstanceFinder.NetworkManager.IsClientStarted) yield return null;

            BlackjackTable table = null;
            float deadline = Time.time + WaitForWorld;

            while (Time.time < deadline && table == null)
            {
                table = FindObjectsByType<BlackjackTable>(FindObjectsSortMode.None).FirstOrDefault(t => t.IsSpawned);
                if (table == null) yield return new WaitForSeconds(0.5f);
            }

            if (table == null)
            {
                Check("the client sees the blackjack table", false);
                Report();
                yield break;
            }

            int seen = 0;
            bool hidden = true;
            float giveUp = Time.time + 240f;

            // A round already under way when this client arrived may be half seen; skip it.
            while (table.Phase != BlackjackPhase.Betting && Time.time < giveUp) yield return null;

            while (seen < Watched && Time.time < giveUp)
            {
                while (table.Phase != BlackjackPhase.Playing && Time.time < giveUp) yield return null;

                while (table.Phase == BlackjackPhase.Playing && Time.time < giveUp)
                {
                    if (table.HoleDown && table.Hands[BlackjackMath.Dealer].Count != 1) hidden = false;
                    yield return null;
                }

                while (table.Phase != BlackjackPhase.Done && Time.time < giveUp) yield return null;
                if (Time.time >= giveUp) break;

                List<int>[] hands = table.Hands;
                bool agrees = hands[BlackjackMath.Dealer].Count >= 2;

                for (int hand = 0; hand < BlackjackMath.Hands; hand++)
                {
                    int bet = table.BetOn(hand);
                    bool blackjack = hand % 2 == 0 && table.BetOn(hand + 1) == 0 && BlackjackMath.Natural(hands[hand]);
                    if (table.PaidOn(hand) != BlackjackMath.Returned(bet, hands[hand], blackjack, hands[BlackjackMath.Dealer]))
                        agrees = false;
                }

                Check($"round {seen + 1}: dealer {BlackjackMath.Describe(hands[BlackjackMath.Dealer])}, seat 1 "
                      + $"{BlackjackMath.Describe(hands[0])}; every payout is what this client's cards make it", agrees);
                seen++;

                while (table.Phase == BlackjackPhase.Done && Time.time < giveUp) yield return null;
            }

            Check("the dealer showed one card, never two, while the players acted", hidden);
            Check($"the client watched {seen} round(s)", seen >= Watched);
            Report();
        }

        // ---------------------------------------------------------------- scaffolding

        static IEnumerator Until(BlackjackTable table, BlackjackPhase phase)
        {
            float giveUp = Time.time + 30f;
            while (table.Phase != phase && Time.time < giveUp) yield return null;
        }

        static BlackjackButton Button(BlackjackTable table, int seat, BlackjackPress press)
            => table.GetComponentsInChildren<BlackjackButton>().First(b => b.Seat == seat && b.Press == press);

        void Report()
        {
            if (_failed == 0) Debug.Log($"[BlackjackTest] {_passed} passed, 0 failed.");
            else Debug.LogError($"[BlackjackTest] {_passed} passed, {_failed} FAILED.");
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                Debug.Log($"[BlackjackTest] PASS: {what}");
            }
            else
            {
                _failed++;
                Debug.LogError($"[BlackjackTest] FAIL: {what}");
            }
        }
    }
}
