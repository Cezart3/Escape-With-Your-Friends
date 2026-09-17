using EscapeWithYourFriends.Combat;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// What the run was, once it is over. #74.
    ///
    /// The acceptance is *"the run has a real ending, not a fade to black"*, and an ending is real
    /// when it is about the specific afternoon these four people just had. So the numbers are the
    /// ending: how many times you died, what you left at the roulette table, and how many of your
    /// friends you drove into.
    ///
    /// **Two counters, not a statistics system.** Deaths are not counted here at all, because
    /// <see cref="Health.Deaths"/> already counts them per body and summing four ints at the end is
    /// cheaper than keeping a fifth in step with them. Only the two things nothing was counting yet
    /// get a field, and each is incremented at the line where the event already happens - one in
    /// <c>VehicleImpact</c>, one in <c>BetSpot</c>.
    ///
    /// **Server truth, one delivery.** The counters only ever move on the server. They reach the
    /// other three players once, in the RPC that ends the run, rather than as four SyncVars ticking
    /// all game for a screen nobody sees until the end.
    /// </summary>
    public static class RunSummary
    {
        /// <summary>Friends put under a wheel. Players only: a boar does not count as a friend.</summary>
        public static int RanOver { get; private set; }

        /// <summary>Chips staked at the table over the whole run, win or lose.</summary>
        public static int Gambled { get; private set; }

        /// <summary>True once the aeroplane has left with everyone aboard. Set on every peer.</summary>
        public static bool Over { get; private set; }

        /// <summary>The figures as they were when it ended. Meaningless before <see cref="Over"/>.</summary>
        public static int Deaths { get; private set; }
        public static int Seconds { get; private set; }

        /// <summary>Server-side. Called from VehicleImpact when the thing it hit was a person.</summary>
        public static void ServerRanOver() => RanOver++;

        /// <summary>Server-side. Called from BetSpot as a stake goes down, win or lose.</summary>
        public static void ServerStaked(int chips)
        {
            if (chips > 0) Gambled += chips;
        }

        /// <summary>
        /// Adds up what the server knows and hands it back for delivery. Deaths are summed here
        /// rather than tracked, which is also why a player who disconnects takes their deaths with
        /// them - the alternative is a registry of people who are not in the game any more, and
        /// nobody reading the ending screen is going to audit it.
        /// </summary>
        public static (int deaths, int gambled, int ranOver, int seconds) ServerTally()
        {
            int deaths = 0;

            foreach (Health body in Object.FindObjectsByType<Health>(FindObjectsSortMode.None))
                if (body != null && body.GetComponent<Player.PlayerMotor>() != null) deaths += body.Deaths;

            return (deaths, Gambled, RanOver, Mathf.RoundToInt(Time.time));
        }

        /// <summary>
        /// Called on every peer, including the server, when the run ends. Everything that wants to
        /// know it is over reads <see cref="Over"/> rather than being told: the ending panel, the
        /// motor that stops taking input, and the harness all ask the same static.
        /// </summary>
        public static void Finish(int deaths, int gambled, int ranOver, int seconds)
        {
            if (Over) return;

            Deaths = deaths;
            Gambled = gambled;
            RanOver = ranOver;
            Seconds = seconds;
            Over = true;

            Debug.Log($"[RunSummary] Over after {seconds / 60}m {seconds % 60}s: {deaths} death(s), "
                      + $"{gambled} chips gambled, {ranOver} friend(s) run over.");
        }

        /// <summary>
        /// Back to nothing. Only the harness calls this - the game has no way to start a second run
        /// without a fresh process, and #75 is where loading a save will need to think about it.
        /// </summary>
        internal static void Reset()
        {
            RanOver = 0;
            Gambled = 0;
            Deaths = 0;
            Seconds = 0;
            Over = false;
        }
    }
}
