using System;

namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// Reads launch arguments.
    ///
    /// The whole development loop here is a terminal launching several builds at once, so more than
    /// one system needs to know what it was started with: the bootstrap needs the connection mode,
    /// the input reader needs to know it is being driven by a test script, the motor needs to know
    /// whether to log. Each of them parsing <c>Environment.GetCommandLineArgs</c> separately means
    /// three slightly different parsers and three places a flag can be spelled wrong.
    ///
    /// The array is fetched once. It cannot change while the process is running.
    /// </summary>
    public static class CommandLine
    {
        static string[] _args;

        static string[] Args => _args ??= Environment.GetCommandLineArgs();

        public static bool HasFlag(string flag)
        {
            foreach (string arg in Args)
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        public static string GetString(string key, string fallback)
        {
            string[] args = Args;

            // Stop one short: a key in the last position has no value after it.
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];

            return fallback;
        }

        public static int GetInt(string key, int fallback)
        {
            string raw = GetString(key, null);
            return raw != null && int.TryParse(raw, out int value) ? value : fallback;
        }

        public static float GetFloat(string key, float fallback)
        {
            string raw = GetString(key, null);
            return raw != null
                   && float.TryParse(raw, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out float value)
                ? value
                : fallback;
        }
    }
}
