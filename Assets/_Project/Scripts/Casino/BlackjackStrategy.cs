using System.Collections.Generic;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>
    /// Basic strategy for this table's rules (six decks, dealer stands on soft 17, double after
    /// split, no surrender): the chart a careful player follows. The harness plays it for a few
    /// hundred thousand rounds to show the table leaves the house the half a percent a real one does,
    /// not five percent or minus five.
    /// </summary>
    public static class BlackjackStrategy
    {
        public static BlackjackAction Advise(IReadOnlyList<int> cards, int dealerUp, bool canSplit)
        {
            int up = BlackjackMath.Value(dealerUp);
            if (up == 1) up = 11;

            bool two = cards.Count == 2;

            if (canSplit && two && BlackjackMath.Value(cards[0]) == BlackjackMath.Value(cards[1]))
            {
                int pair = BlackjackMath.Value(cards[0]);
                bool split = pair switch
                {
                    1 or 8 => true,
                    9 => up <= 9 && up != 7,
                    7 => up <= 7,
                    6 => up <= 6,
                    4 => up == 5 || up == 6,
                    2 or 3 => up <= 7,
                    _ => false,
                };

                if (split) return BlackjackAction.Split;
            }

            int total = BlackjackMath.Total(cards, out bool soft);

            if (soft)
            {
                if (total >= 19) return BlackjackAction.Stand;

                if (total == 18)
                {
                    if (up >= 3 && up <= 6) return two ? BlackjackAction.Double : BlackjackAction.Stand;
                    return up >= 9 ? BlackjackAction.Hit : BlackjackAction.Stand;
                }

                int from = total == 17 ? 3 : total >= 15 ? 4 : 5;
                return two && up >= from && up <= 6 ? BlackjackAction.Double : BlackjackAction.Hit;
            }

            if (total >= 17) return BlackjackAction.Stand;
            if (total >= 13) return up <= 6 ? BlackjackAction.Stand : BlackjackAction.Hit;
            if (total == 12) return up >= 4 && up <= 6 ? BlackjackAction.Stand : BlackjackAction.Hit;
            if (total == 11) return two && up <= 10 ? BlackjackAction.Double : BlackjackAction.Hit;
            if (total == 10) return two && up <= 9 ? BlackjackAction.Double : BlackjackAction.Hit;
            if (total == 9) return two && up >= 3 && up <= 6 ? BlackjackAction.Double : BlackjackAction.Hit;
            return BlackjackAction.Hit;
        }

        /// <summary>
        /// One seat plays a whole round by the chart at <paramref name="bet"/>. Returns chips back
        /// minus chips staked. <paramref name="round"/> is left finished, dealer played.
        /// </summary>
        public static int PlayOne(int[] shoe, int bet, out BlackjackRound round)
        {
            round = new BlackjackRound(shoe, new[] { bet });
            int up = round.Cards[BlackjackMath.Dealer][0];

            while (round.Turn >= 0)
            {
                BlackjackAction advice = Advise(round.Cards[round.Turn], up, !round.HasSplit(round.Turn / 2));
                if (!round.Act(advice)) round.Act(BlackjackAction.Hit);
            }

            round.PlayDealer();

            int net = 0;
            for (int hand = 0; hand < BlackjackMath.Hands; hand++) net += round.Returned(hand) - round.Bet[hand];
            return net;
        }
    }
}
