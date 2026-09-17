using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Player;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// The acceptance test for #84, behind <c>-settingsTest write</c> and <c>-settingsTest read</c>.
    ///
    /// The acceptance is two claims — settings *persist across sessions* and *actually change what
    /// they claim to* — and they need different kinds of proof. Persistence is only honestly testable
    /// across two processes, so this is one test in two halves like <c>-saveTest</c>. "Actually
    /// changes something" is tested by asking the *reader* rather than the setting: the check on
    /// volume reads <see cref="AudioListener.volume"/>, the check on colours reads what a spawned
    /// player's body is tinted with, and the check on rebinds reads the binding override sitting on a
    /// freshly spawned player's own copy of the input asset.
    ///
    /// **This one writes to <see cref="PlayerPrefs"/>, which every process on this machine shares.**
    /// It must run alone, and the read half puts everything back to stock on its way out so the next
    /// harness does not inherit somebody's 2.5x sensitivity.
    ///
    /// What it cannot check: field of view and resolution, which need a screen and a camera that a
    /// headless run does not have. Both are one-line reads of this same store, and the store is what
    /// is under test here; whether 95 degrees looks right is a playtest question anyway.
    /// </summary>
    public class SettingsTest : MonoBehaviour
    {
        const float WaitForPlayers = 90f;

        // What the write half stores and the read half expects to find in a new process.
        const float Sensitivity = 2.5f;
        const float Fov = 95f;
        const float Master = 0.42f;
        const float Voice = 0.3f;
        const string RebindAction = "Jump";
        const string RebindPath = "<Keyboard>/j";

        static bool _started;

        string _phase;
        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started) return;

            string phase = CommandLine.GetString("-settingsTest", null);
            if (string.IsNullOrWhiteSpace(phase)) return;

            _started = true;

            var go = new GameObject("SettingsTest");
            DontDestroyOnLoad(go);
            go.AddComponent<SettingsTest>()._phase = phase.Trim().ToLowerInvariant();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            PlayerInputReader reader = null;
            float deadline = Time.time + WaitForPlayers;

            while (Time.time < deadline && reader == null)
            {
                reader = FindObjectsByType<PlayerInputReader>(FindObjectsSortMode.None)
                         .FirstOrDefault(r => r != null && r.IsBound);

                if (reader == null) yield return new WaitForSeconds(0.5f);
            }

            if (reader == null)
            {
                Debug.LogError("[SettingsTest] Nobody spawned with bound input. Nothing was checked.");
                yield break;
            }

            if (_phase == "write") Writing(reader);
            else if (_phase == "read") Reading(reader);
            else Debug.LogError($"[SettingsTest] '{_phase}' is not a phase; use 'write' or 'read'.");

            Report();
        }

        // ---------------------------------------------------------------- half one: turn the knobs

        void Writing(PlayerInputReader reader)
        {
            GameSettings.Reset();

            Check($"a reset gives stock sensitivity ({GameSettings.Sensitivity:0.00})",
                  Mathf.Approximately(GameSettings.Sensitivity, 1f));
            Check($"and stock field of view ({GameSettings.Fov:0})",
                  Mathf.Approximately(GameSettings.Fov, GameSettings.DefaultFov));
            Check("and no rebinds", string.IsNullOrEmpty(GameSettings.Rebinds));
            Check("and the ordinary colours", !GameSettings.Colourblind);

            // Clamped rather than believed. A settings file is a text file somebody will edit.
            GameSettings.Sensitivity = 99f;
            Check($"an absurd sensitivity is clamped ({GameSettings.Sensitivity:0.00})",
                  Mathf.Approximately(GameSettings.Sensitivity, GameSettings.MaxSensitivity));

            GameSettings.Fov = 5f;
            Check($"and a pinhole field of view ({GameSettings.Fov:0})",
                  Mathf.Approximately(GameSettings.Fov, GameSettings.MinFov));

            GameSettings.MasterVolume = -1f;
            Check($"and a negative volume ({GameSettings.MasterVolume:0.00})",
                  Mathf.Approximately(GameSettings.MasterVolume, 0f));

            GameSettings.Sensitivity = Sensitivity;
            GameSettings.Fov = Fov;
            GameSettings.MasterVolume = Master;
            GameSettings.VoiceVolume = Voice;

            // Not "the number was stored" but "the thing the number is for moved".
            Check($"the master volume reaches the listener ({AudioListener.volume:0.00})",
                  Mathf.Approximately(AudioListener.volume, Master));

            Color was = reader.GetComponent<PlayerIdentity>() != null
                ? reader.GetComponent<PlayerIdentity>().Color
                : Color.clear;

            GameSettings.Colourblind = true;

            Check("the colourblind palette is the live one",
                  PlayerIdentity.Active == PlayerIdentity.ColourblindPalette);

            var identity = reader.GetComponent<PlayerIdentity>();
            if (identity != null)
                Check($"and this player's colour actually changed ({was} -> {identity.Color})",
                      identity.Color != was);

            GameSettings.Quality = 0;
            Check($"the quality tier is applied, not just stored ({QualitySettings.GetQualityLevel()})",
                  QualitySettings.GetQualityLevel() == 0);

            string overrides = reader.Rebind(RebindAction, RebindPath);
            GameSettings.Rebinds = overrides;

            Check($"a rebind produces overrides to store ({overrides.Length} chars)",
                  !string.IsNullOrEmpty(overrides) && overrides.Contains("/j"));
            Check("and the action on this player already carries it",
                  reader.BoundOverrides.Contains("/j"));

            Debug.Log($"[SettingsTest] wrote: {GameSettings.Describe()}.");
        }

        // ---------------------------------------------------------------- half two: a new process

        void Reading(PlayerInputReader reader)
        {
            Check($"the sensitivity survived the quit ({GameSettings.Sensitivity:0.00})",
                  Mathf.Approximately(GameSettings.Sensitivity, Sensitivity));
            Check($"and the field of view ({GameSettings.Fov:0})",
                  Mathf.Approximately(GameSettings.Fov, Fov));
            Check($"and the volumes ({GameSettings.MasterVolume:0.00}/{GameSettings.VoiceVolume:0.00})",
                  Mathf.Approximately(GameSettings.MasterVolume, Master)
                  && Mathf.Approximately(GameSettings.VoiceVolume, Voice));

            // Applied by the boot hook before the first scene, not by anything this test did.
            Check($"the listener was turned down before the game started ({AudioListener.volume:0.00})",
                  Mathf.Approximately(AudioListener.volume, Master));

            Check($"the quality tier came back ({QualitySettings.GetQualityLevel()})",
                  QualitySettings.GetQualityLevel() == 0);

            Check("the colourblind palette is still the live one",
                  GameSettings.Colourblind && PlayerIdentity.Active == PlayerIdentity.ColourblindPalette);

            // The one that matters most: this player is a fresh body in a fresh process, and nothing
            // in this test touched its input. If the key is rebound, it is because the stored JSON
            // reached the action on its own.
            Check($"and the rebound key is on a player nobody rebound ({reader.BoundOverrides.Length} chars)",
                  reader.BoundOverrides.Contains("/j"));

            Debug.Log($"[SettingsTest] resumed: {GameSettings.Describe()}.");

            // Put the machine back. PlayerPrefs are shared by every process here, and a harness that
            // left the next one on 2.5x sensitivity with a rebound jump would be a fine way to spend
            // an afternoon debugging the wrong thing.
            GameSettings.Reset();
            PlayerPrefs.DeleteKey(GraphicsBoot.PreferenceKey);
            PlayerPrefs.Save();

            Check($"and a reset puts it all back ({GameSettings.Describe()})",
                  Mathf.Approximately(GameSettings.Sensitivity, 1f)
                  && Mathf.Approximately(GameSettings.MasterVolume, 1f)
                  && string.IsNullOrEmpty(GameSettings.Rebinds)
                  && !GameSettings.Colourblind);
        }

        // ---------------------------------------------------------------- bookkeeping

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[SettingsTest] FAILED: {what}.");
        }

        void Report()
        {
            Debug.Log($"[SettingsTest] {_phase}: {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[SettingsTest] {_failed} check(s) failed.");
        }
    }
}
