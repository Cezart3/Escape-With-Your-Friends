using EscapeWithYourFriends.Casino;
using EscapeWithYourFriends.Economy;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// The board over the roulette table, from #65. Top-centre, and only while you are standing at a
    /// table: chips in hand, what is on the cloth, whether the table is still taking bets, and the
    /// number that just came up.
    ///
    /// **It is a sign, not a menu.** Betting is done by aiming at a square and pressing the interact
    /// key (#64), so this never needs to be clickable, never needs an <c>EventSystem</c>, and never
    /// eats a mouse click the game wanted. Everything it draws is already replicated onto this peer,
    /// which is what lets a player watch somebody else's stake go on without a round trip.
    ///
    /// The last number keeps its colour - red, black, green for zero - because that is the one piece
    /// of casino iconography that carries no text, and a player who has never played roulette learns
    /// the whole board from watching it flash twice.
    /// </summary>
    public class CasinoBoard
    {
        /// <summary>How close you have to be for the board to appear. A little past the table's edge.</summary>
        public const float Reach = 6f;

        const float Margin = 26f;
        const float FlashSeconds = 1.6f;

        static readonly Color Quiet = new(0.92f, 0.86f, 0.55f);
        static readonly Color Red = new(0.86f, 0.28f, 0.24f);
        static readonly Color Black = new(0.78f, 0.78f, 0.82f);
        static readonly Color Green = new(0.35f, 0.85f, 0.45f);

        Text _number;
        Text _line;

        int _lastSeen = -1;
        float _flashUntil;

        public void Build(RectTransform parent)
        {
            _number = HudFactory.Label(parent, "CasinoNumber", 44, TextAnchor.UpperCenter);
            HudFactory.Anchor((RectTransform)_number.transform, new Vector2(0.5f, 1f),
                              new Vector2(0.5f, 1f), new Vector2(0f, -Margin), new Vector2(420f, 52f));

            _line = HudFactory.Label(parent, "CasinoLine", 22, TextAnchor.UpperCenter);
            _line.color = Quiet;
            HudFactory.Anchor((RectTransform)_line.transform, new Vector2(0.5f, 1f),
                              new Vector2(0.5f, 1f), new Vector2(0f, -Margin - 48f),
                              new Vector2(520f, 28f));
        }

        public void Refresh(RouletteWheel table, Wallet wallet)
        {
            if (_number == null) return;

            bool showing = table != null && table.IsSpawned;

            if (_number.gameObject.activeSelf != showing)
            {
                _number.gameObject.SetActive(showing);
                _line.gameObject.SetActive(showing);
            }

            if (!showing) return;

            if (table.Result != _lastSeen)
            {
                _lastSeen = table.Result;
                _flashUntil = Time.time + FlashSeconds;
            }

            _number.text = Number(table.Result);
            _number.color = Colour(table.Result, Time.time < _flashUntil);
            _line.text = Line(table, wallet != null ? wallet.Chips : 0);
        }

        /// <summary>
        /// The table nearest the local player, or null if none is close enough to be worth a board.
        /// Cheap: there is one table in the game and there will not be many.
        /// </summary>
        public static RouletteWheel Nearest(Vector3 position)
        {
            RouletteWheel best = null;
            float bestDistance = Reach * Reach;

            foreach (RouletteWheel table in Object.FindObjectsByType<RouletteWheel>(FindObjectsSortMode.None))
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

        /// <summary>The big number, or a dash before the first spin. Pure, so the harness can read it.</summary>
        public static string Number(int result) => result < 0 ? "--" : result.ToString();

        /// <summary>What colour that number is on a wheel. Green is the house.</summary>
        public static Color Colour(int result, bool flashing)
        {
            if (result < 0) return Quiet;
            if (!flashing) return Quiet;

            return result == 0 ? Green : RouletteWheel.IsRed(result) ? Red : Black;
        }

        /// <summary>
        /// The line under the number: what you hold, what is on the cloth, and whether the table is
        /// still listening. Pure and static for the same reason <see cref="Purse.Text"/> is - a
        /// headless run has no canvas, and the claim worth testing is what the words say.
        /// </summary>
        public static string Line(RouletteWheel table, int chips)
        {
            if (table == null) return string.Empty;

            string state = table.Spinning ? "NO MORE BETS"
                         : table.Pot > 0 ? "BETS OPEN"
                         : "PLACE YOUR BETS";

            string pot = table.Pot > 0 ? $"  ·  {table.Pot} on the cloth" : "";

            return $"{chips} chips{pot}  ·  {state}";
        }
    }
}
