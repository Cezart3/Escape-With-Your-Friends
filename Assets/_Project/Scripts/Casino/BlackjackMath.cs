using System.Collections.Generic;
using System.Text;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>What a player may do with the hand whose turn it is.</summary>
    public enum BlackjackAction
    {
        Hit,
        Stand,

        /// <summary>Stake the bet again, take exactly one card, stop. Any first two cards, after a split too.</summary>
        Double,

        /// <summary>Two cards of the same value become two hands, each with the original bet. Once per seat.</summary>
        Split,
    }

    /// <summary>
    /// The rules of the blackjack table, with no Unity, no network and no clock in it: the same shape
    /// as <see cref="SlotMath"/>, so the harness can play a few hundred thousand rounds in seconds.
    ///
    /// Six decks, shuffled fresh every round from a seed with <see cref="SlotRng"/>. Blackjack pays
    /// 3:2. The dealer peeks under an ace or a ten, so a dealer blackjack only ever costs the
    /// original bet, and stands on every 17, soft ones included. A player doubles on any first two
    /// cards, after a split too, and splits any two cards of the same value once; split aces get one
    /// card each, and 21 on a split hand is 21, not blackjack. No insurance, no surrender.
    ///
    /// A card is 0-51: <c>card % 13</c> is the rank (0 the ace, 9-12 the ten, jack, queen, king),
    /// <c>card / 13</c> the suit.
    /// </summary>
    public static class BlackjackMath
    {
        public const int Decks = 6;
        public const int Seats = 4;

        /// <summary>Player hands: a seat's own at <c>2 * seat</c>, its split at <c>2 * seat + 1</c>.</summary>
        public const int Hands = Seats * 2;

        /// <summary>The dealer's hand index, after the players'.</summary>
        public const int Dealer = Hands;

        /// <summary>
        /// A log entry is <c>hand * 64 + card</c>: that card went to that hand. An entry of
        /// <c>SplitCode + hand</c> means the hand split and its second card moved to <c>hand + 1</c>.
        /// </summary>
        public const int SplitCode = (Dealer + 1) * 64;

        static readonly string[] RankNames = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };
        static readonly string[] SuitNames = { "♠", "♥", "♦", "♣" };

        public static int Rank(int card) => card % 13;
        public static int Suit(int card) => card / 13 % 4;

        /// <summary>Hearts and diamonds.</summary>
        public static bool IsRed(int card) => Suit(card) == 1 || Suit(card) == 2;

        public static int Value(int card)
        {
            int rank = Rank(card);
            return rank == 0 ? 1 : rank >= 9 ? 10 : rank + 1;
        }

        public static string Name(int card) => RankNames[Rank(card)] + SuitNames[Suit(card)];

        /// <summary>The best total: aces count 11 where that does not bust. Soft if one of them does.</summary>
        public static int Total(IReadOnlyList<int> cards, out bool soft)
        {
            int total = 0;
            bool ace = false;

            foreach (int card in cards)
            {
                total += Value(card);
                if (Rank(card) == 0) ace = true;
            }

            soft = ace && total + 10 <= 21;
            return soft ? total + 10 : total;
        }

        public static int Total(IReadOnlyList<int> cards) => Total(cards, out _);

        /// <summary>Two cards making 21. Whether it counts as blackjack is the round's to say: not after a split.</summary>
        public static bool Natural(IReadOnlyList<int> cards) => cards.Count == 2 && Total(cards) == 21;

        /// <summary>Hits 16 and under, stands on every 17.</summary>
        public static bool DealerDraws(IReadOnlyList<int> cards) => Total(cards) < 17;

        /// <summary>A shuffled shoe of <paramref name="decks"/> decks. The same seed is the same shoe on any runtime.</summary>
        public static int[] Shoe(int seed, int decks = Decks)
        {
            var shoe = new int[52 * decks];
            for (int i = 0; i < shoe.Length; i++) shoe[i] = i % 52;

            var rng = new SlotRng(seed);
            for (int i = shoe.Length - 1; i > 0; i--)
            {
                int j = rng.Range(i + 1);
                (shoe[i], shoe[j]) = (shoe[j], shoe[i]);
            }

            return shoe;
        }

        /// <summary>
        /// Chips handed back for a finished hand, stake included: 0 lost, the bet on a push, twice it
        /// on a win, two and a half times it on a blackjack.
        /// </summary>
        public static int Returned(int bet, IReadOnlyList<int> hand, bool blackjack, IReadOnlyList<int> dealer)
        {
            if (bet <= 0) return 0;

            bool dealerBlackjack = Natural(dealer);

            if (blackjack) return dealerBlackjack ? bet : bet + bet * 3 / 2;
            if (dealerBlackjack) return 0;

            int mine = Total(hand);
            if (mine > 21) return 0;

            int theirs = Total(dealer);
            if (theirs > 21 || mine > theirs) return bet * 2;
            return mine == theirs ? bet : 0;
        }

        /// <summary>The hands a log describes. What every client draws, and what the harness checks the server's hands against.</summary>
        public static List<int>[] Rebuild(IReadOnlyList<int> log)
        {
            var hands = new List<int>[Dealer + 1];
            for (int i = 0; i < hands.Length; i++) hands[i] = new List<int>();

            foreach (int entry in log)
            {
                if (entry >= SplitCode)
                {
                    int hand = entry - SplitCode;
                    if (hand < 0 || hand + 1 >= Dealer || hands[hand].Count < 2) continue;

                    hands[hand + 1].Add(hands[hand][1]);
                    hands[hand].RemoveAt(1);
                    continue;
                }

                int to = entry / 64;
                if (to >= 0 && to <= Dealer) hands[to].Add(entry % 64);
            }

            return hands;
        }

        /// <summary>"A♠ 7♥  soft 18". A hidden hole card reads as a question mark.</summary>
        public static string Describe(IReadOnlyList<int> cards, bool holeDown = false)
        {
            if (cards.Count == 0) return "";

            var text = new StringBuilder();
            foreach (int card in cards) text.Append(Name(card)).Append(' ');

            if (holeDown) return text.Append("?").ToString();

            int total = Total(cards, out bool soft);
            text.Append(' ');
            if (total > 21) text.Append("bust ");
            else if (soft) text.Append("soft ");

            return text.Append(total).ToString();
        }
    }

    /// <summary>
    /// One round at the table, from the deal to the dealer's last card. Pure, like
    /// <see cref="BlackjackMath"/>: the <see cref="BlackjackTable"/> owns the clock, the wallets and
    /// the wire, and drives this.
    ///
    /// Everything it deals goes into <see cref="Log"/> in order, except the dealer's hole card,
    /// which goes in only when it is turned over. The log is what crosses the network, so no client
    /// holds the hole card a moment before the table shows it.
    /// </summary>
    public sealed class BlackjackRound
    {
        readonly int[] _shoe;
        int _next;
        readonly bool[] _done = new bool[BlackjackMath.Hands];

        /// <summary>Every hand, the dealer's last. Server-side truth: the dealer's includes the hole card.</summary>
        public readonly List<int>[] Cards = new List<int>[BlackjackMath.Dealer + 1];

        /// <summary>Chips on each player hand, doubles included. 0 is no hand.</summary>
        public readonly int[] Bet = new int[BlackjackMath.Hands];

        /// <summary>What the table has shown so far. See the class summary.</summary>
        public readonly List<int> Log = new();

        /// <summary>The hand to act, or -1 when the players are done.</summary>
        public int Turn { get; private set; } = -1;

        public bool HoleShown { get; private set; }

        /// <summary>Deals two cards to every seat with a bet and two to the dealer, one of them face down.</summary>
        public BlackjackRound(int[] shoe, IReadOnlyList<int> seatBets)
        {
            _shoe = shoe;
            for (int i = 0; i < Cards.Length; i++) Cards[i] = new List<int>();

            for (int seat = 0; seat < BlackjackMath.Seats && seat < seatBets.Count; seat++)
                Bet[seat * 2] = System.Math.Max(0, seatBets[seat]);

            for (int pass = 0; pass < 2; pass++)
            {
                for (int seat = 0; seat < BlackjackMath.Seats; seat++)
                    if (Bet[seat * 2] > 0) Give(seat * 2);

                if (pass == 0) Give(BlackjackMath.Dealer);
                else Cards[BlackjackMath.Dealer].Add(Draw());
            }

            // The peek: under an ace or a ten, a dealer blackjack ends the round before anybody acts.
            int up = BlackjackMath.Value(Cards[BlackjackMath.Dealer][0]);
            if ((up == 1 || up == 10) && BlackjackMath.Natural(Cards[BlackjackMath.Dealer]))
            {
                Reveal();
                return;
            }

            for (int hand = 0; hand < BlackjackMath.Hands; hand++)
                if (Bet[hand] > 0 && BlackjackMath.Total(Cards[hand]) == 21) _done[hand] = true;

            Advance(0);
        }

        public bool HasSplit(int seat) => Bet[seat * 2 + 1] > 0;

        /// <summary>An unsplit two-card 21.</summary>
        public bool IsBlackjack(int hand)
            => hand % 2 == 0 && !HasSplit(hand / 2) && BlackjackMath.Natural(Cards[hand]);

        /// <summary>Extra chips <paramref name="action"/> stakes: the hand's bet for a double or a split, else nothing.</summary>
        public int Cost(BlackjackAction action)
            => Turn >= 0 && (action == BlackjackAction.Double || action == BlackjackAction.Split) ? Bet[Turn] : 0;

        public bool Can(BlackjackAction action) => Turn >= 0 && Allowed(action, Turn, Cards[Turn], HasSplit(Turn / 2));

        /// <summary>
        /// The rules on their own, for anybody holding the hands: the round, or a client that rebuilt
        /// them from the log and wants to know which buttons to offer.
        /// </summary>
        public static bool Allowed(BlackjackAction action, int hand, IReadOnlyList<int> cards, bool seatSplit)
        {
            switch (action)
            {
                case BlackjackAction.Hit:
                case BlackjackAction.Stand:
                    return true;
                case BlackjackAction.Double:
                    return cards.Count == 2;
                case BlackjackAction.Split:
                    return cards.Count == 2 && hand % 2 == 0 && !seatSplit
                           && BlackjackMath.Value(cards[0]) == BlackjackMath.Value(cards[1]);
                default:
                    return false;
            }
        }

        /// <summary>Plays <paramref name="action"/> for the hand whose turn it is. The stake for it is the caller's to have taken.</summary>
        public bool Act(BlackjackAction action)
        {
            if (!Can(action)) return false;

            int hand = Turn;

            switch (action)
            {
                case BlackjackAction.Hit:
                    Give(hand);
                    if (BlackjackMath.Total(Cards[hand]) >= 21) Finish(hand);
                    break;

                case BlackjackAction.Stand:
                    Finish(hand);
                    break;

                case BlackjackAction.Double:
                    Bet[hand] *= 2;
                    Give(hand);
                    Finish(hand);
                    break;

                case BlackjackAction.Split:
                    int second = Cards[hand][1];
                    Bet[hand + 1] = Bet[hand];
                    Cards[hand].RemoveAt(1);
                    Cards[hand + 1].Add(second);
                    Log.Add(BlackjackMath.SplitCode + hand);

                    Give(hand);
                    Give(hand + 1);

                    // Split aces take one card each and stand. Anything else that reached 21 stands too.
                    bool aces = BlackjackMath.Rank(second) == 0;
                    if (aces || BlackjackMath.Total(Cards[hand + 1]) == 21) _done[hand + 1] = true;
                    if (aces || BlackjackMath.Total(Cards[hand]) == 21) Finish(hand);
                    break;
            }

            return true;
        }

        /// <summary>Whether any hand is still waiting on the dealer's total: not bust, not a blackjack already paid on the spot.</summary>
        public bool DealerPlays
        {
            get
            {
                for (int hand = 0; hand < BlackjackMath.Hands; hand++)
                    if (Bet[hand] > 0 && BlackjackMath.Total(Cards[hand]) <= 21 && !IsBlackjack(hand)) return true;

                return false;
            }
        }

        /// <summary>Turns the hole card and draws to 17, once the players are done. Adds to the log; the table paces it out.</summary>
        public void PlayDealer()
        {
            if (Turn >= 0) return;
            if (!HoleShown) Reveal();

            if (!DealerPlays) return;
            while (BlackjackMath.DealerDraws(Cards[BlackjackMath.Dealer])) Give(BlackjackMath.Dealer);
        }

        /// <summary>Chips handed back for a hand once the dealer is done, stake included.</summary>
        public int Returned(int hand)
            => BlackjackMath.Returned(Bet[hand], Cards[hand], IsBlackjack(hand), Cards[BlackjackMath.Dealer]);

        void Finish(int hand)
        {
            _done[hand] = true;
            Advance(hand + 1);
        }

        void Advance(int from)
        {
            for (int hand = from; hand < BlackjackMath.Hands; hand++)
            {
                if (Bet[hand] <= 0 || _done[hand]) continue;
                Turn = hand;
                return;
            }

            Turn = -1;
        }

        void Reveal()
        {
            HoleShown = true;
            Turn = -1;
            Log.Add(BlackjackMath.Dealer * 64 + Cards[BlackjackMath.Dealer][1]);
        }

        // A rigged shoe in the harness can be short; a real one never comes near its end.
        int Draw() => _shoe[_next++ % _shoe.Length];

        void Give(int hand)
        {
            int card = Draw();
            Cards[hand].Add(card);
            Log.Add(hand * 64 + card);
        }
    }
}
