using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// Picks a quality level before the first frame, and says why.
    ///
    /// The project shipped with Unity's default, which is Ultra. That is the wrong end of the range
    /// for a game whose stated target is "runs on almost any PC": the first thing a player on an
    /// integrated GPU saw was a four-cascade shadow map and 2x MSAA, and the first thing they would
    /// have concluded is that the game is broken.
    ///
    /// The choice is a guess, and guesses about hardware age badly, so three things are true here on
    /// purpose. The command line beats everything, so a test can pin a level. A stored preference
    /// beats the guess, which is where the settings menu in #84 will write. And the guess logs both
    /// its answer and the facts it used, so a wrong one is a bug report rather than a mystery.
    /// </summary>
    public static class GraphicsBoot
    {
        /// <summary>Where a player's own choice lives. Written by the settings menu, never by this.</summary>
        public const string PreferenceKey = "ewyf.quality";

        // Unity's six built-in levels, mapped onto the three URP assets in Assets/_Project/Settings:
        // 0-1 use URP_Low, 2-3 URP_Medium, 4-5 URP_High. These are the three worth choosing between.
        const int Low = 1;
        const int Medium = 2;
        const int High = 4;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            int chosen = Choose(out string why, out bool asked);

            // A headless build renders nothing, so guessing at a level there only makes the smoke-test
            // logs harder to read. An explicit -quality still applies, because that is the only way to
            // test this code path at all without a screen.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null && !asked)
            {
                StartProbe();
                return;
            }

            chosen = Mathf.Clamp(chosen, 0, QualitySettings.names.Length - 1);

            // applyExpensiveChanges: this runs once, before anything is on screen, so the reload of
            // textures and shadow maps costs nothing anyone can see.
            QualitySettings.SetQualityLevel(chosen, applyExpensiveChanges: true);

            Debug.Log($"[GraphicsBoot] Quality '{QualitySettings.names[chosen]}' ({chosen}) - {why}. "
                      + $"{SystemInfo.graphicsDeviceName}, {SystemInfo.graphicsMemorySize}MB video, "
                      + $"{SystemInfo.systemMemorySize}MB system, {SystemInfo.processorCount} cores.");

            StartProbe();
        }

        static void StartProbe()
        {
            float every = CommandLine.GetFloat("-perfLog", -1f);

            // Development builds always measure, because the frame counter on the HUD needs the data
            // and the cost is one array write per frame.
            if (every <= 0f && !Debug.isDebugBuild) return;

            PerfProbe.Start(every);
        }

        static int Choose(out string why, out bool asked)
        {
            asked = false;

            string requested = CommandLine.GetString("-quality", null);
            if (!string.IsNullOrWhiteSpace(requested) && !requested.StartsWith("-"))
            {
                int named = ByName(requested);
                if (named >= 0)
                {
                    why = $"-quality {requested}";
                    asked = true;
                    return named;
                }

                Debug.LogWarning($"[GraphicsBoot] '{requested}' is not a quality level. "
                                 + $"Known: {string.Join(", ", QualitySettings.names)}, or an index.");
            }

            if (PlayerPrefs.HasKey(PreferenceKey))
            {
                why = "the player's saved choice";
                return PlayerPrefs.GetInt(PreferenceKey);
            }

            return Guess(out why);
        }

        static int ByName(string requested)
        {
            if (int.TryParse(requested, out int index)) return index;

            string[] names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
                if (string.Equals(names[i], requested, System.StringComparison.OrdinalIgnoreCase)) return i;

            return -1;
        }

        /// <summary>
        /// Integrated graphics first, because that is the case this game cares about and the one a
        /// memory figure gets wrong: an iGPU reports a slice of system RAM, which can look like a
        /// respectable amount of video memory while being a fraction of the bandwidth.
        /// </summary>
        static int Guess(out string why)
        {
            string device = SystemInfo.graphicsDeviceName ?? "";

            if (IsIntegrated(device))
            {
                why = "integrated graphics";
                return Low;
            }

            if (SystemInfo.graphicsMemorySize < 4000 || SystemInfo.systemMemorySize < 8000)
            {
                why = $"{SystemInfo.graphicsMemorySize}MB video and {SystemInfo.systemMemorySize}MB system memory";
                return Medium;
            }

            why = $"{SystemInfo.graphicsMemorySize}MB of video memory on a discrete GPU";
            return High;
        }

        static bool IsIntegrated(string device)
        {
            // Substrings rather than a model list: the list would be out of date within a year, and
            // the naming conventions - a trailing M on AMD's mobile parts, Intel's four families -
            // have been stable for far longer than any list of parts.
            string lower = device.ToLowerInvariant();

            if (lower.Contains("iris") || lower.Contains("uhd graphics") || lower.Contains("hd graphics")
                || lower.Contains("intel(r) graphics")
                || (lower.Contains("vega") && lower.Contains("graphics")))
                return true;

            // "AMD Radeon 760M Graphics", "780M", "660M" - the mobile integrated line. A discrete card
            // is a "Radeon RX 7600" and does not match.
            if (!lower.Contains("radeon")) return false;

            for (int i = 0; i < lower.Length - 3; i++)
            {
                if (!char.IsDigit(lower[i])) continue;
                if (char.IsDigit(lower[i + 1]) && char.IsDigit(lower[i + 2]) && lower[i + 3] == 'm') return true;
            }

            return false;
        }
    }
}
