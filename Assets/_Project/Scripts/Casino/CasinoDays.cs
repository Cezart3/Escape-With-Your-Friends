using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.World;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>The games on the casino floor, in the order they open.</summary>
    public enum CasinoGame
    {
        Roulette,
        Blackjack,
        Sevens,
        Volcano,
        Reef,
        Fruit,
    }

    /// <summary>
    /// Gamble With Your Friends' pacing: the casino opens with one table and gets a new game every
    /// day, and every day its chips are bigger. A day is twenty minutes (<see cref="WorldClock"/>),
    /// so a three-hour run sees a new reason to walk in roughly every time it walks past.
    ///
    /// **Limits grow by the day, not by the stage.** Half again per day, the same x1.5 that
    /// <see cref="GameStage"/> pays drops at, capped at <see cref="TopDay"/>. Roulette chips, the
    /// blackjack bet, the slot stakes and the cage's exchange chunk are all the base number times
    /// <see cref="Scale"/>, rounded to three figures.
    ///
    /// The day carries across sessions through <see cref="RunSave"/>; a client reads it off the
    /// cage (<see cref="Cashier"/>), which mirrors it the way the shop counter mirrors the stage.
    /// </summary>
    public static class CasinoDays
    {
        /// <summary>The day each <see cref="CasinoGame"/> opens, 0 being the first.</summary>
        static readonly int[] Opens = { 0, 1, 2, 5, 4, 3 };

        /// <summary>Past this day the chips stop growing: 1.5^10 is 57 times the opening stakes.</summary>
        public const int TopDay = 10;

        static int _carried = -1;

        /// <summary>The day as the cage last told this client. Ignored on the server.</summary>
        internal static int Mirror = -1;

        /// <summary>Harness only: pretend it is this day.</summary>
        internal static int TestDay = -1;

        /// <summary>Harness only: every game open whatever the day, for tests of the games themselves.</summary>
        internal static bool AllOpen;

        /// <summary>Days the run has lived, the saved ones included. 0 is the first.</summary>
        public static int Day
        {
            get
            {
                if (TestDay >= 0) return TestDay;
                if (!InstanceFinder.IsServerStarted) return Mathf.Max(0, Mirror);

                // Read once: by the next save, Run.day is this very number and adding it again
                // would count the session twice.
                if (_carried < 0 && RunSave.Run != null) _carried = RunSave.Run.day;
                return Mathf.Max(0, _carried) + WorldClock.Day;
            }
        }

        /// <summary>A new server in the same process: read the saved days again.</summary>
        internal static void NewSession() => _carried = -1;

        public static int OpensOn(CasinoGame game) => Opens[(int)game];

        public static bool IsOpen(CasinoGame game) => AllOpen || Day >= OpensOn(game);

        public static float Scale => GameStage.Multiplier(Mathf.Min(Day, TopDay));

        /// <summary>A base stake at today's limits.</summary>
        public static int Scaled(int chips) => WeaponMods.Nice(chips * Scale);

        /// <summary>What a closed game says instead of its prompt. Days count from 1 on screen.</summary>
        public static string Closed(string title, CasinoGame game)
            => $"{title} opens on day {OpensOn(game) + 1} (today is day {Day + 1})";

        public static CasinoGame Of(SlotKind kind) => kind switch
        {
            SlotKind.Volcano => CasinoGame.Volcano,
            SlotKind.Reef => CasinoGame.Reef,
            SlotKind.Fruit => CasinoGame.Fruit,
            _ => CasinoGame.Sevens,
        };
    }
}
