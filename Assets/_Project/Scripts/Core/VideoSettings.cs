using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// The graphics options: four presets, and every option a preset is made of, each one settable on
    /// its own. #241.
    ///
    /// **A preset is a quality level plus a row of values.** The level picks the URP asset, which
    /// carries what cannot change at run time (HDR, soft shadows, which SSAO the renderer has). The
    /// values are written over that asset when they are applied. Change any one of them and the
    /// preset reads "Custom"; the label is worked out from the values, never stored, so it cannot
    /// disagree with them.
    ///
    /// **Ultra is never guessed.** It is the streamer's preset - 4K shadows, MSAA on top of SMAA,
    /// the grass and the trees further out than the island was tuned for - and a machine that merely
    /// can run High has no business being handed it.
    ///
    /// **The URP assets are edited in place.** In a build that is the point: nothing writes them back
    /// to disk. In the Editor the same writes would outlive play mode, so the first write to each
    /// object takes a snapshot and leaving play mode puts it back.
    ///
    /// The frame cap and VSync are not part of any preset. They are about the monitor, not the GPU.
    /// </summary>
    public static class VideoSettings
    {
        const string ValuesKey = "ewyf.video";
        const string VSyncKey = "ewyf.vsync";
        const string FrameCapKey = "ewyf.framecap";

        public enum Preset { Low, Medium, High, Ultra }

        /// <summary>Everything a preset sets. Indices into the tables below, so a preset is one row.</summary>
        [Serializable]
        public struct Values
        {
            public int Scale;     // RenderScales
            public bool Fsr;      // FSR 1 upscaling, rather than bilinear, when Scale is below 1
            public int Aa;        // AaNames
            public int Shadows;   // ShadowNames
            public bool Ao;
            public int Grass;     // TierNames: how far out and how thick
            public int View;      // TierNames: trees and terrain detail
            public bool Bloom;

            public override string ToString()
                => $"scale {RenderScales[Scale]:0.##}{(Fsr ? " FSR" : "")}, AA {AaNames[Aa]}, "
                   + $"shadows {ShadowNames[Shadows]}, AO {(Ao ? "on" : "off")}, grass {TierNames[Grass]}, "
                   + $"view {TierNames[View]}, bloom {(Bloom ? "on" : "off")}";
        }

        public static readonly float[] RenderScales = { 0.5f, 0.59f, 0.67f, 0.77f, 0.87f, 1f };
        public static readonly string[] AaNames = { "Off", "SMAA", "MSAA 2x", "MSAA 4x", "MSAA 4x + SMAA" };
        public static readonly string[] ShadowNames = { "Off", "Low", "Medium", "High", "Ultra" };
        public static readonly string[] TierNames = { "Low", "Medium", "High", "Ultra" };
        public static readonly int[] FrameCaps = { 0, 30, 60, 120, 144, 165, 240 };

        // Shadows, per ShadowNames. Off is a zero distance, which is how URP itself turns a camera's
        // shadows off. Soft or hard is the level's URP asset, not this: URP keeps that setter internal.
        static readonly float[] ShadowDistance = { 0f, 40f, 80f, 150f, 250f };
        static readonly int[] ShadowResolution = { 1024, 1024, 2048, 2048, 4096 };
        static readonly int[] ShadowCascades = { 1, 1, 2, 4, 4 };

        /// <summary>The quality level each preset stands on. Ultra shares URP_High with High.</summary>
        static readonly int[] Levels = { 1, 2, 4, 5 };

        static readonly Values[] Presets =
        {
            // Integrated graphics from before RDNA 2. FSR from 77% rather than bilinear from 80%,
            // which is what made Low blurry in #201; the shadows and the grass pay for the rest.
            new() { Scale = 3, Fsr = true, Aa = 0, Shadows = 1, Ao = false, Grass = 0, View = 0, Bloom = false },
            new() { Scale = 5, Aa = 1, Shadows = 2, Ao = true, Grass = 1, View = 1, Bloom = true },

            // SMAA alone, where High used to add MSAA 2x under it: on flat-shaded geometry the second
            // pass bought almost nothing, and High has to hold 140 fps on a 4060 laptop.
            new() { Scale = 5, Aa = 1, Shadows = 3, Ao = true, Grass = 2, View = 2, Bloom = true },
            new() { Scale = 5, Aa = 4, Shadows = 4, Ao = true, Grass = 3, View = 3, Bloom = true },
        };

        static Values _values;
        static bool _loaded;

        /// <summary>True once anything here has been applied. Until then the frame rate is FishNet's.</summary>
        internal static bool Applied { get; private set; }

        /// <summary>Raised after anything here is applied, for the menu and the terrain.</summary>
        public static event Action Changed;

        public static Values Current { get { Load(); return _values; } }

        public static int LevelOf(Preset preset) => Levels[(int)preset];

        /// <summary>A preset's values. Public so the menu and the harness can compare against it.</summary>
        public static Values Of(Preset preset) => Presets[(int)preset];

        /// <summary>The preset whose values are all the current ones, or null for "Custom".</summary>
        public static Preset? Matching
        {
            get
            {
                Load();
                for (int i = Presets.Length - 1; i >= 0; i--)
                    if (Presets[i].Equals(_values) && QualitySettings.GetQualityLevel() == Levels[i])
                        return (Preset)i;
                return null;
            }
        }

        public static string PresetName => Matching?.ToString() ?? "Custom";

        /// <summary>The preset a quality level reads as, for a first run with no values stored.</summary>
        public static Preset ForLevel(int level)
            => level >= 5 ? Preset.Ultra : level >= 4 ? Preset.High : level >= 2 ? Preset.Medium : Preset.Low;

        public static bool VSync
        {
            get => PlayerPrefs.GetInt(VSyncKey, 1) != 0;
            set { PlayerPrefs.SetInt(VSyncKey, value ? 1 : 0); PlayerPrefs.Save(); ApplyFrame(); Changed?.Invoke(); }
        }

        /// <summary>Frames a second, 0 for none. Only bites with VSync off.</summary>
        public static int FrameCap
        {
            get => PlayerPrefs.GetInt(FrameCapKey, 0);
            set { PlayerPrefs.SetInt(FrameCapKey, Mathf.Max(0, value)); PlayerPrefs.Save(); ApplyFrame(); Changed?.Invoke(); }
        }

        /// <summary>
        /// Picks a preset: its quality level, then its values. The level is stored where
        /// <see cref="GraphicsBoot"/> reads it, so the next launch starts here.
        /// </summary>
        public static void Choose(Preset preset)
        {
            int level = Mathf.Min(Levels[(int)preset], QualitySettings.names.Length - 1);
            PlayerPrefs.SetInt(GraphicsBoot.PreferenceKey, level);
            QualitySettings.SetQualityLevel(level, applyExpensiveChanges: true);
            Set(Presets[(int)preset]);
        }

        /// <summary>Stores and applies one row of values. The preset label follows on its own.</summary>
        public static void Set(Values values)
        {
            _loaded = true;
            _values = Clamp(values);
            PlayerPrefs.SetString(ValuesKey, JsonUtility.ToJson(_values));
            PlayerPrefs.Save();
            Apply();
        }

        /// <summary>
        /// Called by <see cref="GraphicsBoot"/> once the level is set. A level forced from the command
        /// line brings its own preset's values, so <c>-quality High</c> means High, whatever this
        /// machine stored.
        /// </summary>
        internal static void Boot(bool forced)
        {
            if (forced)
            {
                _loaded = true;
                _values = Presets[(int)ForLevel(QualitySettings.GetQualityLevel())];
            }

            Apply();
            Debug.Log($"[VideoSettings] {PresetName}: {Current}; vsync {(VSync ? "on" : "off")}, "
                      + $"cap {(FrameCap > 0 ? FrameCap.ToString() : "none")}.");
        }

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            string json = PlayerPrefs.GetString(ValuesKey, "");
            _values = string.IsNullOrEmpty(json)
                ? Presets[(int)ForLevel(QualitySettings.GetQualityLevel())]
                : Clamp(JsonUtility.FromJson<Values>(json));
        }

        /// <summary>Forgets what was loaded so the next read comes off PlayerPrefs again. Harness only.</summary>
        internal static void Forget() => _loaded = false;

        /// <summary>Back to what a machine with nothing stored would get. Harness only.</summary>
        internal static void Clear()
        {
            PlayerPrefs.DeleteKey(ValuesKey);
            PlayerPrefs.DeleteKey(VSyncKey);
            PlayerPrefs.DeleteKey(FrameCapKey);
            PlayerPrefs.Save();
            _loaded = false;
        }

        static Values Clamp(Values v)
        {
            v.Scale = Mathf.Clamp(v.Scale, 0, RenderScales.Length - 1);
            v.Aa = Mathf.Clamp(v.Aa, 0, AaNames.Length - 1);
            v.Shadows = Mathf.Clamp(v.Shadows, 0, ShadowNames.Length - 1);
            v.Grass = Mathf.Clamp(v.Grass, 0, TierNames.Length - 1);
            v.View = Mathf.Clamp(v.View, 0, TierNames.Length - 1);
            return v;
        }

        // ---------------------------------------------------------------- the readers

        /// <summary>SMAA on the camera: AA "SMAA" or "MSAA 4x + SMAA". Read by PostProcess.</summary>
        public static bool Smaa => Current.Aa == 1 || Current.Aa == 4;

        static int Msaa => Current.Aa switch { 2 => 2, 3 => 4, 4 => 4, _ => 1 };

        /// <summary>The terrain's multipliers for grass distance, grass density, tree distance, and its pixel error.</summary>
        public static (float grass, float density, float trees, float pixelError) Terrain
        {
            get
            {
                Values v = Current;
                // Of the island's 85 m: about 40, 70, 120 and 150 m of meshed grass and clutter (#246).
                float[] grass = { 0.47f, 0.82f, 1.41f, 1.75f };
                float[] density = { 0.3f, 0.6f, 1f, 1f };
                float[] trees = { 0.6f, 0.85f, 1f, 1.25f };
                float[] error = { 10f, 7f, 5f, 3f };
                return (grass[v.Grass], density[v.Grass], trees[v.View], error[v.View]);
            }
        }

        static void Apply()
        {
            Values v = Current;

            var urp = (QualitySettings.renderPipeline ?? GraphicsSettings.defaultRenderPipeline) as UniversalRenderPipelineAsset;
            if (urp != null)
            {
                Snapshot(urp);

                urp.renderScale = RenderScales[v.Scale];
                urp.upscalingFilter = v.Fsr ? UpscalingFilterSelection.FSR : UpscalingFilterSelection.Auto;
                urp.msaaSampleCount = Msaa;
                urp.shadowDistance = ShadowDistance[v.Shadows];
                urp.mainLightShadowmapResolution = ShadowResolution[v.Shadows];
                urp.shadowCascadeCount = ShadowCascades[v.Shadows];

                foreach (ScriptableRendererData data in urp.rendererDataList)
                {
                    if (data == null) continue;
                    foreach (ScriptableRendererFeature feature in data.rendererFeatures)
                    {
                        if (feature is not ScreenSpaceAmbientOcclusion) continue;
                        Snapshot(feature);
                        feature.SetActive(v.Ao);
                    }
                }
            }

            ApplyFrame();
            World.PostProcess.Refresh();
            foreach (var terrain in UnityEngine.Object.FindObjectsByType<World.TerrainQuality>(FindObjectsSortMode.None))
                terrain.Apply();

            Changed?.Invoke();
        }

        /// <summary>After any SetQualityLevel too, which puts back the level's own vSyncCount.</summary>
        static void ApplyFrame()
        {
            Applied = true;
            TakeFrameRate(FishNet.InstanceFinder.NetworkManager);

            // Every texture sharp at a glancing angle, which is how the ground is always seen. Per
            // level, so a SetQualityLevel undoes it too. Costs next to nothing on Medium and up.
            if (QualitySettings.GetQualityLevel() >= 2) QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;

            QualitySettings.vSyncCount = VSync ? 1 : 0;
            Application.targetFrameRate = FrameCap > 0 ? FrameCap : -1;
        }

        /// <summary>
        /// FishNet sets <see cref="Application.targetFrameRate"/> to its own 500 every time a server or
        /// client starts, which undid the player's cap the moment they hosted. A zero tells it to leave
        /// the frame rate alone. Only once this class has applied something, so a headless harness
        /// with nothing stored keeps FishNet's cap rather than spinning flat out.
        /// </summary>
        internal static void TakeFrameRate(FishNet.Managing.NetworkManager manager)
        {
            if (!Applied || manager == null) return;
            manager.ClientManager.SetFrameRate(0);
            manager.ServerManager.SetFrameRate(0);
        }

#if UNITY_EDITOR
        static readonly List<(UnityEngine.Object target, string json)> Snapshots = new();

        static void Snapshot(UnityEngine.Object target)
        {
            foreach (var (taken, _) in Snapshots) if (taken == target) return;

            if (Snapshots.Count == 0)
                UnityEditor.EditorApplication.playModeStateChanged += state =>
                {
                    if (state != UnityEditor.PlayModeStateChange.ExitingPlayMode) return;
                    foreach (var (o, json) in Snapshots) if (o != null) UnityEditor.EditorJsonUtility.FromJsonOverwrite(json, o);
                    Snapshots.Clear();
                };

            Snapshots.Add((target, UnityEditor.EditorJsonUtility.ToJson(target)));
        }
#else
        static void Snapshot(UnityEngine.Object target) { }
#endif
    }
}
