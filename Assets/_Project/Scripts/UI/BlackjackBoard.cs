using System.Collections.Generic;
using System.Text;
using EscapeWithYourFriends.Casino;
using EscapeWithYourFriends.Economy;
using FishNet;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// The board over the blackjack table, in the slot board's place: the dealer's cards on the big
    /// line, yours under it, and whose turn it is. The cards on the felt show only their colour, so
    /// this is where a player reads the ranks.
    /// </summary>
    public class BlackjackBoard
    {
        /// <summary>
        /// Close enough to be standing at a seat, measured from the camera to the table's foot, so
        /// about a metre and a half across the floor. The slot board's number, for its reason.
        /// </summary>
        public const float Reach = 2.2f;

        const float Margin = 26f;

        static readonly Color Quiet = new(0.92f, 0.86f, 0.55f);
        static readonly Color Hot = new(1f, 0.55f, 0.2f);

        Text _title;
        Text _line;

        public void Build(RectTransform parent)
        {
            _title = HudFactory.Label(parent, "BlackjackTitle", 34, TextAnchor.UpperCenter);
            HudFactory.Anchor((RectTransform)_title.transform, new Vector2(0.5f, 1f),
                              new Vector2(0.5f, 1f), new Vector2(0f, -Margin), new Vector2(620f, 44f));

            _line = HudFactory.Label(parent, "BlackjackLine", 22, TextAnchor.UpperCenter);
            _line.color = Quiet;
            HudFactory.Anchor((RectTransform)_line.transform, new Vector2(0.5f, 1f),
                              new Vector2(0.5f, 1f), new Vector2(0f, -Margin - 42f), new Vector2(900f, 28f));
        }

        public void Refresh(BlackjackTable table, Wallet wallet)
        {
            if (_title == null) return;

            bool showing = table != null && table.IsSpawned;
            if (_title.gameObject.activeSelf != showing)
            {
                _title.gameObject.SetActive(showing);
                _line.gameObject.SetActive(showing);
            }

            if (!showing) return;

            int localId = LocalId();

            _title.text = Title(table);
            _title.color = table.Turn >= 0 && table.Owner(table.Turn / 2) == localId ? Hot : Quiet;
            _line.text = Line(table, wallet != null ? wallet.Chips : 0, localId);
        }

        static int LocalId()
        {
            var client = InstanceFinder.ClientManager;
            return client != null && client.Connection != null && client.Connection.FirstObject != null
                ? client.Connection.FirstObject.ObjectId
                : -1;
        }

        /// <summary>The table nearest the camera within <see cref="Reach"/>, or null.</summary>
        public static BlackjackTable Nearest(Vector3 position)
        {
            BlackjackTable best = null;
            float bestDistance = Reach * Reach;

            foreach (BlackjackTable table in BlackjackTable.All)
            {
                if (table == null || !table.IsSpawned) continue;

                float distance = (table.transform.position - position).sqrMagnitude;
                if (distance > bestDistance) continue;

                bestDistance = distance;
                best = table;
            }

            return best;
        }

        // ---------------------------------------------------------------- the strings

        /// <summary>The dealer's hand, or the table's name between rounds. Pure, so the harness can read it headless.</summary>
        public static string Title(BlackjackTable table)
        {
            List<int> dealer = table.Hands[BlackjackMath.Dealer];
            if (table.Phase == BlackjackPhase.Betting || dealer.Count == 0) return "BLACKJACK  ·  PAYS 3 TO 2";

            return "DEALER  " + BlackjackMath.Describe(dealer, table.HoleDown);
        }

        /// <summary>Your hands and what they need, or how to sit down.</summary>
        public static string Line(BlackjackTable table, int chips, int localId)
        {
            int seat = table.SeatOf(localId);

            if (seat < 0)
            {
                return table.Phase == BlackjackPhase.Betting
                    ? $"{chips} chips  ·  a seat's bet button puts down {BlackjackTable.Chunk}, up to {BlackjackTable.MaxBet}"
                    : $"{chips} chips  ·  next hand when this one is paid";
            }

            if (table.Phase == BlackjackPhase.Betting)
                return $"SEAT {seat + 1}  ·  bet {table.BetOn(seat * 2)}  ·  cards when betting closes";

            var line = new StringBuilder();

            for (int hand = seat * 2; hand <= seat * 2 + 1; hand++)
            {
                int bet = table.BetOn(hand);
                if (bet <= 0) continue;

                if (line.Length > 0) line.Append("   |   ");
                if (table.Turn == hand) line.Append("> ");

                line.Append(BlackjackMath.Describe(table.Hands[hand])).Append("  ·  ");
                line.Append(table.Phase == BlackjackPhase.Done ? Outcome(table, hand, bet) : $"bet {bet}");
            }

            if (table.Turn >= 0 && table.Turn / 2 == seat) line.Append("  ·  YOUR TURN");
            return line.ToString();
        }

        static string Outcome(BlackjackTable table, int hand, int bet)
        {
            int back = table.PaidOn(hand);
            if (back == 0) return $"LOST {bet}";
            if (back == bet) return "PUSH";
            return back > bet * 2 ? $"BLACKJACK +{back - bet}" : $"WIN +{back - bet}";
        }
    }
}
