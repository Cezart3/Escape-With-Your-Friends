using EscapeWithYourFriends.Core;
using FishNet;
using FishNet.Managing.Timing;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// What time it is on the island, in a way four machines agree on without sending a single byte
    /// about it.
    ///
    /// The obvious implementation is a float on the host replicated to everyone, and it is the wrong
    /// one: it either costs a message every tick, or it costs a message occasionally and the clients
    /// visibly jump when it lands. FishNet already synchronises <see cref="TimeManager.Tick"/> across
    /// every client, including late joiners, so the time of day can simply be a function of the tick.
    /// Nothing is replicated, nothing drifts, and a player who joins an hour in sees the same sunset
    /// as everyone else because they are both reading the same counter.
    ///
    /// With no network manager at all - the editor, a batchmode test, a single-player run - it falls
    /// back to <see cref="Time.time"/>, so the sky still moves.
    /// </summary>
    public static class WorldClock
    {
        /// <summary>Length of a full day in seconds. Set from the profile before anything reads the clock.</summary>
        public static float CycleSeconds = 1200f;

        /// <summary>Where the cycle starts, 0 to 1. Same on every machine because it comes from the same asset.</summary>
        public static float StartOfDay = 0.28f;

        /// <summary>Frozen time of day, from -timeOfDay. Negative means the clock runs normally.</summary>
        static float _frozen = -1f;

        static bool _readCommandLine;

        /// <summary>
        /// Time of day in 0..1: 0 is midnight, 0.25 sunrise, 0.5 noon, 0.75 sunset. Everything that
        /// wants to know what the sky is doing asks this.
        /// </summary>
        public static float Normalized
        {
            get
            {
                ReadCommandLine();
                if (_frozen >= 0f) return _frozen;

                float cycle = Mathf.Max(1f, CycleSeconds);
                return Mathf.Repeat(StartOfDay + Elapsed / cycle, 1f);
            }
        }

        /// <summary>How many whole days have gone by. For "you survived four nights" and nothing else yet.</summary>
        public static int Day
        {
            get
            {
                float cycle = Mathf.Max(1f, CycleSeconds);
                return Mathf.FloorToInt(StartOfDay + Elapsed / cycle);
            }
        }

        /// <summary>Time of day as a 24-hour string, for logs and for the HUD when it exists.</summary>
        public static string Clock24
        {
            get
            {
                float hours = Normalized * 24f;
                int hour = Mathf.FloorToInt(hours);
                int minute = Mathf.FloorToInt((hours - hour) * 60f);
                return $"{hour:00}:{minute:00}";
            }
        }

        /// <summary>
        /// Seconds since the session started, from the shared tick where there is one. The tick is an
        /// integer count, so this is the same number on every machine to the tick, not merely close.
        /// </summary>
        public static float Elapsed
        {
            get
            {
                TimeManager time = InstanceFinder.TimeManager;
                if (time != null) return (float)(time.Tick * time.TickDelta);
                return Time.time;
            }
        }

        /// <summary>Freezes the clock, for tests and for the editor. Negative resumes it.</summary>
        public static void Freeze(float timeOfDay)
        {
            _frozen = timeOfDay < 0f ? -1f : Mathf.Repeat(timeOfDay, 1f);
            _readCommandLine = true;
        }

        static void ReadCommandLine()
        {
            if (_readCommandLine) return;
            _readCommandLine = true;

            // -timeOfDay 0.5 pins the sun at noon, which is the only way to test a lighting change
            // in a build that has no screen: the run is over before the sun has moved.
            float value = CommandLine.GetFloat("-timeOfDay", -1f);
            if (value >= 0f)
            {
                _frozen = Mathf.Repeat(value, 1f);
                Debug.Log($"[WorldClock] Frozen at {_frozen:F3} ({Clock24}) by -timeOfDay.");
            }
        }
    }
}
