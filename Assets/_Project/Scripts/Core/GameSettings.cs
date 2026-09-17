using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// Every knob a player is allowed to turn, and the one place that remembers them. #84.
    ///
    /// The acceptance is two claims, and the second is the one worth guarding: settings *persist
    /// across sessions* and *actually change what they claim to*. A settings menu whose sliders move
    /// and change nothing is the single most common way this feature is shipped broken, so every
    /// value here has exactly one reader somewhere in the game and the harness checks that reader
    /// rather than the stored number.
    ///
    /// **<see cref="PlayerPrefs"/>, not the save file.** These belong to a machine, not to a run:
    /// somebody who loads an old save still wants their own sensitivity, and somebody who starts a
    /// fresh run does not want to re-do their keybinds. <see cref="World.RunSave"/> is for what the
    /// four of you earned; this is for how one of you likes to sit.
    ///
    /// **Quality is not stored here.** <see cref="GraphicsBoot"/> already owns that preference, picks
    /// a tier by guessing at the GPU on a first run, and applies it before the first scene loads.
    /// Duplicating the key would give two owners to one number and they would disagree the first time
    /// somebody passed <c>-quality</c>; so <see cref="Quality"/> is a window onto
    /// <see cref="GraphicsBoot.PreferenceKey"/>, not a second copy of it.
    ///
    /// **Nothing here is networked and nothing here should be.** How loud your game is and which key
    /// crouches is not something the other three need told, and a setting that replicated would be a
    /// setting somebody could change on your machine.
    /// </summary>
    public static class GameSettings
    {
        const string SensitivityKey = "ewyf.sensitivity";
        const string FovKey = "ewyf.fov";
        const string MasterVolumeKey = "ewyf.volume.master";
        const string VoiceVolumeKey = "ewyf.volume.voice";
        const string ColourblindKey = "ewyf.colourblind";
        const string RebindsKey = "ewyf.rebinds";
        const string WidthKey = "ewyf.screen.width";
        const string HeightKey = "ewyf.screen.height";
        const string FullscreenKey = "ewyf.screen.fullscreen";

        /// <summary>The camera's resting field of view, and the default the slider starts at.</summary>
        public const float DefaultFov = 70f;

        public const float MinFov = 60f;
        public const float MaxFov = 110f;

        public const float MinSensitivity = 0.1f;
        public const float MaxSensitivity = 5f;

        static float _sensitivity = 1f;
        static float _fov = DefaultFov;
        static float _masterVolume = 1f;
        static float _voiceVolume = 1f;
        static bool _colourblind;
        static string _rebinds = "";
        static bool _loaded;

        /// <summary>
        /// Raised after any setting changes. Most readers do not need it - the camera rig and the
        /// input reader ask for their value every frame anyway - but the player colours are applied
        /// once when a body spawns, so something has to tell them to look again.
        /// </summary>
        public static event Action Changed;

        /// <summary>A multiplier over the tuned look speed, not a replacement for it. 1 is stock.</summary>
        public static float Sensitivity
        {
            get { Load(); return _sensitivity; }
            set
            {
                Load();
                _sensitivity = Mathf.Clamp(value, MinSensitivity, MaxSensitivity);
                PlayerPrefs.SetFloat(SensitivityKey, _sensitivity);
                Save();
            }
        }

        /// <summary>Resting field of view in degrees. Sprinting still widens it from here.</summary>
        public static float Fov
        {
            get { Load(); return _fov; }
            set
            {
                Load();
                _fov = Mathf.Clamp(value, MinFov, MaxFov);
                PlayerPrefs.SetFloat(FovKey, _fov);
                Save();
            }
        }

        /// <summary>Everything the game plays, 0 to 1. Drives <see cref="AudioListener.volume"/>.</summary>
        public static float MasterVolume
        {
            get { Load(); return _masterVolume; }
            set
            {
                Load();
                _masterVolume = Mathf.Clamp01(value);
                PlayerPrefs.SetFloat(MasterVolumeKey, _masterVolume);
                AudioListener.volume = _masterVolume;
                Save();
            }
        }

        /// <summary>
        /// Your friends' voices, separately, because the one thing people reliably want quieter is
        /// each other. Multiplies the distance and state fade <see cref="Net.VoiceChat"/> already does.
        /// </summary>
        public static float VoiceVolume
        {
            get { Load(); return _voiceVolume; }
            set
            {
                Load();
                _voiceVolume = Mathf.Clamp01(value);
                PlayerPrefs.SetFloat(VoiceVolumeKey, _voiceVolume);
                Save();
            }
        }

        /// <summary>
        /// Swaps the player colours for a set that survives red-green colour blindness. The whole
        /// game identifies people by colour - the squad list, the downed markers, the tint on the
        /// body itself - so this is not decoration, it is whether two of the four are the same person.
        /// </summary>
        public static bool Colourblind
        {
            get { Load(); return _colourblind; }
            set
            {
                Load();
                _colourblind = value;
                PlayerPrefs.SetInt(ColourblindKey, value ? 1 : 0);
                Save();
            }
        }

        /// <summary>
        /// Key rebinds, as the Input System's own override JSON. Stored as one opaque string on
        /// purpose: the format is the input asset's business, and a hand-rolled map of action name to
        /// key would have to be taught about composites, modifiers and gamepads one at a time.
        /// </summary>
        public static string Rebinds
        {
            get { Load(); return _rebinds; }
            set
            {
                Load();
                _rebinds = value ?? "";
                PlayerPrefs.SetString(RebindsKey, _rebinds);
                Save();
            }
        }

        /// <summary>
        /// The quality tier, as an index into <see cref="QualitySettings.names"/>. A window onto
        /// <see cref="GraphicsBoot"/>'s preference rather than a copy of it - see the class remarks.
        /// </summary>
        public static int Quality
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(GraphicsBoot.PreferenceKey, QualitySettings.GetQualityLevel()),
                               0, Mathf.Max(0, QualitySettings.names.Length - 1));
            set
            {
                int level = Mathf.Clamp(value, 0, Mathf.Max(0, QualitySettings.names.Length - 1));
                PlayerPrefs.SetInt(GraphicsBoot.PreferenceKey, level);
                QualitySettings.SetQualityLevel(level, applyExpensiveChanges: true);
                Save();
            }
        }

        /// <summary>True when the game is running full screen. Stored, because a window is a choice.</summary>
        public static bool Fullscreen
        {
            get { Load(); return PlayerPrefs.GetInt(FullscreenKey, 1) != 0; }
            set
            {
                PlayerPrefs.SetInt(FullscreenKey, value ? 1 : 0);
                ApplyScreen();
                Save();
            }
        }

        /// <summary>The stored window size, or the current one when nothing has been chosen yet.</summary>
        public static Vector2Int Size
        {
            get
            {
                Load();
                int width = PlayerPrefs.GetInt(WidthKey, 0);
                int height = PlayerPrefs.GetInt(HeightKey, 0);

                return width > 0 && height > 0
                    ? new Vector2Int(width, height)
                    : new Vector2Int(Screen.width, Screen.height);
            }
            set
            {
                if (value.x <= 0 || value.y <= 0) return;

                PlayerPrefs.SetInt(WidthKey, value.x);
                PlayerPrefs.SetInt(HeightKey, value.y);
                ApplyScreen();
                Save();
            }
        }

        /// <summary>
        /// Applied before the first scene loads, so the camera rig and the input reader read the
        /// player's numbers on the very first frame rather than the prefab's for one of them.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            Load();

            AudioListener.volume = _masterVolume;
            ApplyScreen();

            Debug.Log($"[GameSettings] fov {_fov:0}, sensitivity {_sensitivity:0.00}x, "
                      + $"volume {_masterVolume:0.00} master / {_voiceVolume:0.00} voice, "
                      + $"colourblind {_colourblind}, "
                      + $"{(string.IsNullOrEmpty(_rebinds) ? "stock keys" : "rebound keys")}.");
        }

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            _sensitivity = PlayerPrefs.GetFloat(SensitivityKey, 1f);
            _fov = PlayerPrefs.GetFloat(FovKey, DefaultFov);
            _masterVolume = PlayerPrefs.GetFloat(MasterVolumeKey, 1f);
            _voiceVolume = PlayerPrefs.GetFloat(VoiceVolumeKey, 1f);
            _colourblind = PlayerPrefs.GetInt(ColourblindKey, 0) != 0;
            _rebinds = PlayerPrefs.GetString(RebindsKey, "");
        }

        /// <summary>
        /// Written immediately rather than at quit, for the same reason <see cref="Net.PlayerKey"/>
        /// writes its generated key immediately: the session somebody spent ten minutes rebinding in
        /// is quite likely the one that crashes.
        /// </summary>
        static void Save()
        {
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        static void ApplyScreen()
        {
            // A headless run has no screen to set, and asking for one logs a warning per launch
            // across every harness in the project.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

            Vector2Int size = Size;
            Screen.SetResolution(size.x, size.y, Fullscreen);
        }

        /// <summary>Back to stock, for the menu's one button that people actually use.</summary>
        public static void Reset()
        {
            Sensitivity = 1f;
            Fov = DefaultFov;
            MasterVolume = 1f;
            VoiceVolume = 1f;
            Colourblind = false;
            Rebinds = "";
        }

        /// <summary>Forgets what was loaded so the next read comes off PlayerPrefs again. Harness only.</summary>
        internal static void Forget() => _loaded = false;

        /// <summary>One line for the log and for the harness.</summary>
        public static string Describe()
            => $"fov {Fov:0}, sensitivity {Sensitivity:0.00}x, volume {MasterVolume:0.00}/"
               + $"{VoiceVolume:0.00}, quality {Quality}, colourblind {Colourblind}, "
               + $"{(string.IsNullOrEmpty(Rebinds) ? "stock keys" : "rebound keys")}";
    }
}
