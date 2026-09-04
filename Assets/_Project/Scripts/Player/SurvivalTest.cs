using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.World;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// The acceptance test for #40, run inside a real session. Server side, behind <c>-statTest</c>.
    ///
    /// "Annoying enough to drive behaviour but never tedious" is a judgement made in play, not a thing
    /// a test can assert. What a test *can* assert is that the numbers behind that judgement are the
    /// ones the profile says they are - that the rates match the asset, that thirst really does bite
    /// before hunger, that stamina gates a sprint and comes back, that warmth follows the environment
    /// rather than only ever falling, and that empty actually hurts. Then the tuning argument is about
    /// the asset, which is exactly where it belongs.
    ///
    /// Rates are measured against the clock rather than trusted: a drain that silently ran at twice
    /// the profile's rate would still look correct in every log line taken on its own.
    /// </summary>
    public class SurvivalTest : MonoBehaviour
    {
        const float MeasureSeconds = 3f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-statTest")) return;

            _started = true;

            var go = new GameObject("SurvivalTest");
            DontDestroyOnLoad(go);
            go.AddComponent<SurvivalTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            SurvivalStats stats = null;
            float deadline = Time.time + 10f;

            while (Time.time < deadline && stats == null)
            {
                stats = FindObjectsByType<SurvivalStats>(FindObjectsSortMode.None)
                        .FirstOrDefault(s => s != null && s.IsSpawned);

                if (stats == null) yield return new WaitForSeconds(0.5f);
            }

            if (stats == null)
            {
                Debug.LogError("[SurvivalTest] No player has SurvivalStats; run PlayerPrefabBuilder.");
                yield break;
            }

            SurvivalProfile profile = stats.Profile;
            if (profile == null)
            {
                Debug.LogError("[SurvivalTest] The player has no survival profile; run SurvivalFactory.Build.");
                yield break;
            }

            var health = stats.GetComponent<Health>();

            Debug.Log($"[SurvivalTest] start: {stats.Describe()} at {WorldClock.Clock24}, "
                      + $"day {WorldClock.Day}.");

            // Near-full rather than exactly full: the session has been running for a few seconds by
            // the time this finds a body, and under -botMove that body has already been sprinting.
            Check("everything starts full",
                  stats.Hunger > SurvivalProfile.Max - 2f
                  && stats.Thirst > SurvivalProfile.Max - 2f
                  && stats.Stamina > SurvivalProfile.Max - 25f
                  && stats.Warmth > SurvivalProfile.Max - 2f);

            // ---------------------------------------------------------------- drain rates

            float hungerFrom = stats.Hunger;
            float thirstFrom = stats.Thirst;
            float measuredFrom = Time.time;

            yield return new WaitForSeconds(MeasureSeconds);

            float elapsed = Time.time - measuredFrom;
            float hungerLost = hungerFrom - stats.Hunger;
            float thirstLost = thirstFrom - stats.Thirst;

            // A 25% band. The bots sprint for half of every cycle, which legitimately raises the rate,
            // so this asks whether the drain is in the right decade rather than to four decimals.
            Check($"hunger drains at about the profile rate ({hungerLost / elapsed:F3}/s vs "
                  + $"{profile.HungerDrain:F3})",
                  Within(hungerLost / elapsed, profile.HungerDrain, profile.HungerSprintDrain));

            Check($"thirst drains faster than hunger ({thirstLost:F2} vs {hungerLost:F2} in "
                  + $"{elapsed:F1}s)", thirstLost > hungerLost);

            // ---------------------------------------------------------------- stamina

            stats.ServerFeed(stamina: -SurvivalProfile.Max);

            Check("stamina can be spent to empty", stats.Stamina <= 0.01f);
            Check("an empty player cannot start a sprint", !stats.CanSprint);
            Check("nor keep one going", !stats.CanKeepSprinting);

            // Recovery is delayed by design, so this waits past the delay before expecting anything.
            yield return new WaitForSeconds(profile.StaminaRecoveryDelay + 1.5f);

            Check($"stamina recovers on its own ({stats.Stamina:F0})", stats.Stamina > 5f);
            Check("but a sprint still needs a real reserve",
                  stats.CanSprint == stats.Stamina >= profile.StaminaSprintThreshold);

            stats.ServerFeed(stamina: SurvivalProfile.Max);
            Check("and it cannot go over full", Mathf.Approximately(stats.Stamina, SurvivalProfile.Max));

            // ---------------------------------------------------------------- warmth

            stats.ServerFeed(warmth: -SurvivalProfile.Max);
            Check("warmth can be driven to zero", stats.Warmth <= 0.01f);

            float healthBefore = health != null ? health.Current : 0f;

            // Held at zero, because damage is only applied to a bar that is *still* empty when the
            // once-a-second tick comes round. Letting it recover and then checking for damage was the
            // first version of this and it failed correctly: on land, warmth climbs back out of the
            // danger zone within a frame, so nothing ever hurt. Freezing has to be a state you are
            // stuck in - which, on this island, means being in the water.
            float freezingFrom = Time.time;
            while (Time.time - freezingFrom < 2.5f)
            {
                stats.ServerFeed(warmth: -SurvivalProfile.Max);
                yield return null;
            }

            if (health != null)
            {
                Check($"being held at zero warmth costs health ({healthBefore:F0} -> "
                      + $"{health.Current:F0})", health.Current < healthBefore);
            }

            // Now let go and watch it come back on its own. Warmth is an equilibrium, not a clock,
            // and that is the property that keeps it from being a chore.
            bool submerged = WaterSurface.IsSubmerged(stats.transform.position + Vector3.up * 0.9f);
            float warmthFrom = stats.Warmth;

            yield return new WaitForSeconds(1.5f);

            Check($"warmth recovers toward the environment ({warmthFrom:F0} -> {stats.Warmth:F0}, "
                  + $"{(submerged ? "in water" : "on land")})",
                  submerged ? stats.Warmth <= warmthFrom + 0.1f : stats.Warmth > warmthFrom);

            Check($"and the sea is the one place it can reach zero ({profile.WarmthWater:F0} in "
                  + $"water vs {profile.WarmthNight:F0} at night)",
                  profile.WarmthWater <= 0f && profile.WarmthNight > 0f);

            // ---------------------------------------------------------------- empty hurts

            stats.ServerFeed(thirst: -SurvivalProfile.Max);
            Check("thirst can be driven to zero", stats.Thirst <= 0.01f);

            if (health != null)
            {
                float from = health.Current;
                float at = Time.time;

                yield return new WaitForSeconds(3f);

                float lost = from - health.Current;
                float perSecond = lost / (Time.time - at);

                // Warmth is climbing back at the same time, so the floor is dehydration alone and the
                // ceiling is everything empty at once. Anything outside that is a real disagreement.
                float floor = profile.DehydrationDamage * 0.5f;
                float ceiling = profile.DehydrationDamage + profile.HypothermiaDamage
                                + profile.StarvationDamage + 0.5f;

                Check($"dehydration costs about {profile.DehydrationDamage:F1} hp/s (measured "
                      + $"{perSecond:F2})", perSecond >= floor && perSecond <= ceiling);

                Check("but it downs you rather than killing you outright", !health.IsDead);
            }

            // ---------------------------------------------------------------- feeding

            stats.ServerFeed(hunger: 40f, thirst: 60f);

            Check("eating and drinking put it back", stats.Thirst >= 55f && stats.Hunger > 0f);

            stats.ServerFeed(hunger: SurvivalProfile.Max * 2f);
            Check("and cannot overfill", Mathf.Approximately(stats.Hunger, SurvivalProfile.Max));

            string line = $"[SurvivalTest] {_passed} passed, {_failed} failed. "
                          + $"end: {stats.Describe()}"
                          + (health != null ? $", health {health.Current:F0}/{health.Max:F0}" : "");

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

        /// <summary>Within a band that allows the sprint surcharge but nothing like a doubled rate.</summary>
        static bool Within(float measured, float expected, float allowance)
            => measured >= expected * 0.75f && measured <= expected + allowance + 0.01f;

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[SurvivalTest] FAILED: {what}.");
        }
    }
}
