using EscapeWithYourFriends.Casino;
using EscapeWithYourFriends.Economy;
using FishNet;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// The board over a slot cabinet: what it is, what you are betting, and what the screen is
    /// doing - the running win, the free spins left, the multiplier. Top-centre, in the roulette
    /// board's place, while you stand at a cabinet. A sign, not a menu, for the roulette board's
    /// reasons: the buttons are on the cabinet and you press them by aiming.
    ///
    /// Everything it reads is replicated or replayed locally from the seed, so a player watching a
    /// friend's feature sees the same numbers climb.
    ///
    /// Under both lines, the shared jackpot. The number rolls up to the replicated pot rather than
    /// jumping, so a friend's stakes at the next cabinet read as the ticker ticking.
    /// </summary>
    public class SlotBoard
    {
        /// <summary>Close enough to be playing it. Smaller than the roulette board's reach, so it wins inside it.</summary>
        public const float Reach = 2.2f;

        const float Margin = 26f;

        static readonly Color Quiet = new(0.92f, 0.86f, 0.55f);
        static readonly Color Hot = new(1f, 0.55f, 0.2f);

        static readonly Color Gold = new(0.35f, 1f, 0.85f);

        Text _title;
        Text _line;
        Text _jackpot;
        float _shownJackpot;

        public void Build(RectTransform parent)
        {
            _title = HudFactory.Label(parent, "SlotTitle", 34, TextAnchor.UpperCenter);
            HudFactory.Anchor((RectTransform)_title.transform, new Vector2(0.5f, 1f),
                              new Vector2(0.5f, 1f), new Vector2(0f, -Margin), new Vector2(620f, 44f));

            _line = HudFactory.Label(parent, "SlotLine", 22, TextAnchor.UpperCenter);
            _line.color = Quiet;
            HudFactory.Anchor((RectTransform)_line.transform, new Vector2(0.5f, 1f),
                              new Vector2(0.5f, 1f), new Vector2(0f, -Margin - 42f), new Vector2(720f, 28f));

            _jackpot = HudFactory.Label(parent, "SlotJackpot", 26, TextAnchor.UpperCenter);
            _jackpot.color = Gold;
            HudFactory.Anchor((RectTransform)_jackpot.transform, new Vector2(0.5f, 1f),
                              new Vector2(0.5f, 1f), new Vector2(0f, -Margin - 74f), new Vector2(620f, 32f));
        }

        public void Refresh(SlotMachine machine, Wallet wallet)
        {
            if (_title == null) return;

            bool showing = machine != null && machine.IsSpawned;
            if (_title.gameObject.activeSelf != showing)
            {
                _title.gameObject.SetActive(showing);
                _line.gameObject.SetActive(showing);
                _jackpot.gameObject.SetActive(showing);
            }

            if (!showing) return;

            // Rolls up over about a second; a pot that dropped snaps straight back down.
            float pot = machine.Jackpot;
            _shownJackpot = pot < _shownJackpot
                ? pot
                : Mathf.MoveTowards(_shownJackpot, pot, Mathf.Max(1f, pot - _shownJackpot) * 3f * Time.deltaTime);
            _jackpot.text = JackpotLine(Mathf.RoundToInt(_shownJackpot));

            int localId = LocalId();

            _title.text = Title(machine);
            _title.color = machine.Animating && machine.Showing != null && machine.Showing.FreeSpinsLeft >= 0 ? Hot : Quiet;
            _line.text = Line(machine, wallet != null ? wallet.Chips : 0, localId);
        }

        static int LocalId()
        {
            var client = InstanceFinder.ClientManager;
            return client != null && client.Connection != null && client.Connection.FirstObject != null
                ? client.Connection.FirstObject.ObjectId
                : -1;
        }

        /// <summary>The cabinet nearest the camera within <see cref="Reach"/>, or null.</summary>
        public static SlotMachine Nearest(Vector3 position)
        {
            SlotMachine best = null;
            float bestDistance = Reach * Reach;

            foreach (SlotMachine machine in SlotMachine.All)
            {
                if (machine == null || !machine.IsSpawned) continue;

                float distance = (machine.transform.position - position).sqrMagnitude;
                if (distance > bestDistance) continue;

                bestDistance = distance;
                best = machine;
            }

            return best;
        }

        // ---------------------------------------------------------------- the strings

        /// <summary>The big line. Pure, like everything below, so the harness can read it headless.</summary>
        public static string Title(SlotMachine machine)
        {
            SlotFrame frame = machine.Animating ? machine.Showing : null;
            if (frame == null || frame.FreeSpinsLeft < 0) return machine.Title.ToUpperInvariant();

            string multiplier = frame.Multiplier > 0 ? $"  ·  x{frame.Multiplier}" : "";
            return $"FREE SPINS  ·  {frame.FreeSpinsLeft} left{multiplier}";
        }

        /// <summary>The ticker.</summary>
        public static string JackpotLine(int chips) => $"JACKPOT  {chips:N0}";

        /// <summary>What is going on underneath: the running win mid-spin, the card after one, the stake before.</summary>
        public static string Line(SlotMachine machine, int chips, int localId)
        {
            if (machine.Animating && machine.Showing != null && machine.Played != null)
            {
                SlotFrame frame = machine.Showing;
                int won = SlotMath.Chips(machine.Played.Bet, frame.RunningPct);
                string multiplier = frame.FreeSpinsLeft < 0 && frame.Multiplier > 0 ? $"  ·  lava x{frame.Multiplier}" : "";
                return won > 0 ? $"WIN {won}{multiplier}" : $"{machine.Played.Bet} on the reels{multiplier}";
            }

            string card = machine.Card == 1 ? "RED  ·  " : machine.Card == 2 ? "BLACK  ·  " : "";

            if (machine.Gamble > 0 && localId == machine.GamblerId)
                return $"{card}WIN {machine.Gamble}  ·  red or black doubles it, spin to keep it";

            if (machine.Card != 0 && machine.Gamble == 0)
                return $"{card}{chips} chips  ·  bet {machine.Bet}";

            string last = machine.LastWin > 0 ? $"last win {machine.LastWin}  ·  " : "";
            return $"{last}{chips} chips  ·  bet {machine.Bet}";
        }
    }
}
