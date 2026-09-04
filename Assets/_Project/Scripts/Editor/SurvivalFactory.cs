using System.IO;
using EscapeWithYourFriends.Data;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Creates the survival profile asset if it is missing, and reports what its numbers mean in
    /// minutes rather than in points per second.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.SurvivalFactory.Build
    ///
    /// Same rule as <see cref="ItemFactory"/>: it creates, it never overwrites. #40's acceptance is
    /// "annoying enough to drive behaviour but never tedious", which is a judgement made in play - so
    /// once the asset exists its numbers belong to whoever is tuning it, and rerunning this must not
    /// undo their afternoon.
    ///
    /// The report exists because the rates are unreadable as written. Nobody has an opinion about
    /// 0.067 points per second; everybody has an opinion about "you get thirsty in 25 minutes, and a
    /// day lasts 20".
    /// </summary>
    public static class SurvivalFactory
    {
        const string Folder = "Assets/_Project/Data";
        const string ProfilePath = Folder + "/Survival.asset";

        public static void Build()
        {
            SurvivalProfile profile = Ensure();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Report(profile);

            if (Application.isBatchMode) EditorApplication.Exit(profile != null ? 0 : 1);
        }

        /// <summary>The profile, creating it with the script defaults if it does not exist yet.</summary>
        internal static SurvivalProfile Ensure()
        {
            var profile = AssetDatabase.LoadAssetAtPath<SurvivalProfile>(ProfilePath);
            if (profile != null) return profile;

            Directory.CreateDirectory(Folder);

            profile = ScriptableObject.CreateInstance<SurvivalProfile>();
            AssetDatabase.CreateAsset(profile, ProfilePath);

            Debug.Log($"[SurvivalFactory] Created {ProfilePath} with the script defaults.");
            return profile;
        }

        /// <summary>
        /// Everything the rates mean, in units a playtester can argue with. Cross-referenced against
        /// the twenty-minute day, because "thirsty in 25 minutes" only means something next to it.
        /// </summary>
        static void Report(SurvivalProfile profile)
        {
            if (profile == null)
            {
                Debug.LogError($"[SurvivalFactory] {ProfilePath} could not be created.");
                return;
            }

            float day = World.WorldClock.CycleSeconds / 60f;

            float hunger = Minutes(SurvivalProfile.Max, profile.HungerDrain);
            float hungerRun = Minutes(SurvivalProfile.Max, profile.HungerDrain + profile.HungerSprintDrain);
            float thirst = Minutes(SurvivalProfile.Max, profile.ThirstDrain);
            float thirstRun = Minutes(SurvivalProfile.Max, profile.ThirstDrain + profile.ThirstSprintDrain);

            float sprint = SurvivalProfile.Max / Mathf.Max(0.01f, profile.StaminaSprintDrain);
            float refill = SurvivalProfile.Max / Mathf.Max(0.01f, profile.StaminaRecovery);

            float freeze = (SurvivalProfile.Max - profile.WarmthWater)
                           / Mathf.Max(0.01f, profile.WarmthLossRate);
            float thaw = (SurvivalProfile.Max - profile.WarmthWater)
                         / Mathf.Max(0.01f, profile.WarmthGainRate);

            float worst = profile.StarvationDamage + profile.DehydrationDamage + profile.HypothermiaDamage;

            Debug.Log($"[SurvivalFactory] A day is {day:F0} minutes. "
                      + $"Hunger empties in {hunger:F0} min walking, {hungerRun:F0} running. "
                      + $"Thirst in {thirst:F0} min walking, {thirstRun:F0} running.");

            Debug.Log($"[SurvivalFactory] Stamina: {sprint:F1}s of sprint, {refill:F1}s to refill "
                      + $"(after a {profile.StaminaRecoveryDelay:F1}s pause), {profile.StaminaJumpCost:F0} "
                      + $"per jump, sprint needs {profile.StaminaSprintThreshold:F0} to start.");

            Debug.Log($"[SurvivalFactory] Warmth settles at {profile.WarmthDay:F0} by day, "
                      + $"{profile.WarmthNight:F0} at night, {profile.WarmthWater:F0} in the sea - "
                      + $"{freeze:F0}s to freeze in the water, {thaw:F0}s to recover out of it.");

            Debug.Log($"[SurvivalFactory] Empty costs {profile.StarvationDamage:F1} hp/s hungry, "
                      + $"{profile.DehydrationDamage:F1} thirsty, {profile.HypothermiaDamage:F1} freezing: "
                      + $"{100f / Mathf.Max(0.01f, profile.DehydrationDamage):F0}s from full health on "
                      + $"thirst alone, {100f / Mathf.Max(0.01f, worst):F0}s with all three.");
        }

        static float Minutes(float amount, float perSecond)
            => perSecond <= 0f ? float.PositiveInfinity : amount / perSecond / 60f;
    }
}
