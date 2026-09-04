using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.World;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The three URP assets, tuned for the three machines this game expects to meet, plus the check
    /// that the world's draw distance and its fog still agree with each other.
    ///
    /// Written as a batchmode command rather than done by hand in the inspector for the usual reason:
    /// a renderer setting somebody clicked once is a setting nobody can review. Every number below is
    /// in a diff, next to the sentence explaining it.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.RenderTuning.Apply
    /// </summary>
    internal static class RenderTuning
    {
        const string LowPath = "Assets/_Project/Settings/URP_Low.asset";
        const string MediumPath = "Assets/_Project/Settings/URP_Medium.asset";
        const string HighPath = "Assets/_Project/Settings/URP_High.asset";

        // Unity's six built-in levels. 0-1 use URP_Low, 2-3 URP_Medium, 4-5 URP_High.
        const int ShippedDefault = 2;

        /// <summary>
        /// Every setting worth arguing about, in one struct so the three tiers can be read side by
        /// side rather than reconstructed from three inspector windows.
        /// </summary>
        struct Tier
        {
            public string Path;
            public bool Hdr;
            public int Msaa;              // 1, 2, 4 or 8. Samples, not an enum.
            public float RenderScale;
            public int ShadowResolution;
            public float ShadowDistance;
            public int Cascades;
            public bool SoftShadows;
            public bool ExtraLightShadows;
            public int LightsPerObject;
        }

        static readonly Tier[] Tiers =
        {
            // Integrated graphics. Render scale is the single biggest lever on an iGPU - 0.8 is 64%
            // of the pixels for a softness nobody notices at 1080p - and one cascade over 45 metres
            // is enough shadow for a game played on foot.
            new()
            {
                Path = LowPath, Hdr = false, Msaa = 1, RenderScale = 0.8f,
                ShadowResolution = 1024, ShadowDistance = 45f, Cascades = 1,
                SoftShadows = false, ExtraLightShadows = false, LightsPerObject = 2,
            },

            // A laptop with a real GPU, or an older desktop card. Full resolution, soft shadows,
            // still no MSAA: at 1080p it costs more than it returns on a game with no thin geometry.
            new()
            {
                Path = MediumPath, Hdr = false, Msaa = 1, RenderScale = 1f,
                ShadowResolution = 2048, ShadowDistance = 80f, Cascades = 2,
                SoftShadows = true, ExtraLightShadows = false, LightsPerObject = 4,
            },

            // Anything current. The shadow distance is 150 rather than more because the fog closes at
            // about 700 metres and shadows past that are invisible by definition.
            new()
            {
                Path = HighPath, Hdr = true, Msaa = 2, RenderScale = 1f,
                ShadowResolution = 2048, ShadowDistance = 150f, Cascades = 4,
                SoftShadows = true, ExtraLightShadows = true, LightsPerObject = 8,
            },
        };

        public static void Apply()
        {
            foreach (Tier tier in Tiers) Write(tier);

            QualitySettings.SetQualityLevel(ShippedDefault, applyExpensiveChanges: false);
            CheckPlatformDefault();

            Verify();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// What a *build* starts at, which is not the same thing as the editor's current level.
        ///
        /// Unity keeps a default per platform in QualitySettings, and only that one reaches a player;
        /// setting the editor's level and shipping is how a build ends up on Ultra while every
        /// screenshot in the office looks fine. There is no scripting API for the per-platform map, so
        /// the value lives in the asset and this checks it - a wrong number here is invisible until
        /// somebody with an integrated GPU opens the game.
        /// </summary>
        static void CheckPlatformDefault()
        {
            const string path = "ProjectSettings/QualitySettings.asset";
            string wanted = $"Standalone: {ShippedDefault}";

            string text = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
            if (text.Contains(wanted))
            {
                Debug.Log($"[RenderTuning] Builds start on '{QualitySettings.names[ShippedDefault]}' "
                          + $"({ShippedDefault}). GraphicsBoot overrides it per machine at run time; "
                          + "this is only the floor.");
                return;
            }

            var match = System.Text.RegularExpressions.Regex.Match(text, @"Standalone: (\d+)");
            Debug.LogError($"[RenderTuning] {path} starts standalone builds on quality level "
                           + $"{(match.Success ? match.Groups[1].Value : "?")}, not {ShippedDefault}. "
                           + $"Set 'Standalone: {ShippedDefault}' under m_PerPlatformDefaultQuality; "
                           + "there is no API for it.");
        }

        static void Write(Tier tier)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(tier.Path);
            if (asset == null)
            {
                Debug.LogError($"[RenderTuning] {tier.Path} is missing; nothing was tuned for that tier.");
                return;
            }

            var so = new SerializedObject(asset);

            // Serialized names rather than the public API: most of these are get-only properties on
            // UniversalRenderPipelineAsset, and the ones that are not are inconsistent about whether
            // the setter marks the asset dirty.
            Set(so, "m_SupportsHDR", tier.Hdr);
            Set(so, "m_MSAA", tier.Msaa);
            Set(so, "m_RenderScale", tier.RenderScale);
            Set(so, "m_MainLightShadowmapResolution", tier.ShadowResolution);
            Set(so, "m_ShadowDistance", tier.ShadowDistance);
            Set(so, "m_ShadowCascadeCount", tier.Cascades);
            Set(so, "m_SoftShadowsSupported", tier.SoftShadows);
            Set(so, "m_AdditionalLightShadowsSupported", tier.ExtraLightShadows);
            Set(so, "m_AdditionalLightsPerObjectLimit", tier.LightsPerObject);

            // True for every tier, and each one is a whole render pass that this game does not use.
            // The water shader was written to avoid the depth texture on purpose - see Water.shader -
            // and turning it on here would quietly undo that decision.
            Set(so, "m_RequireDepthTexture", false);
            Set(so, "m_RequireOpaqueTexture", false);
            Set(so, "m_UseSRPBatcher", true);

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);

            Debug.Log($"[RenderTuning] {asset.name}: render scale {tier.RenderScale}, "
                      + $"MSAA x{tier.Msaa}, HDR {(tier.Hdr ? "on" : "off")}, shadows "
                      + $"{tier.ShadowResolution}^2 over {tier.ShadowDistance}m in {tier.Cascades} "
                      + $"cascade(s), {(tier.SoftShadows ? "soft" : "hard")}, "
                      + $"{tier.LightsPerObject} lights per object.");
        }

        static void Set(SerializedObject so, string path, bool value)
        {
            SerializedProperty property = so.FindProperty(path);
            if (property == null) { Missing(so, path); return; }
            property.boolValue = value;
        }

        static void Set(SerializedObject so, string path, int value)
        {
            SerializedProperty property = so.FindProperty(path);
            if (property == null) { Missing(so, path); return; }
            property.intValue = value;
        }

        static void Set(SerializedObject so, string path, float value)
        {
            SerializedProperty property = so.FindProperty(path);
            if (property == null) { Missing(so, path); return; }
            property.floatValue = value;
        }

        static void Missing(SerializedObject so, string path)
            => Debug.LogError($"[RenderTuning] {so.targetObject.name} has no '{path}'. URP renamed a "
                              + "field; that setting is now whatever it happened to be.");

        /// <summary>
        /// Does the fog still hide the edge of the world?
        ///
        /// Three numbers have to stay in order: the distance at which exponential-squared fog is
        /// effectively opaque, the camera's far plane, and the outer edge of the water's horizon ring.
        /// If the fog ever reaches further than the far plane, the sea ends in mid-air. Nobody would
        /// change the fog curve thinking about the camera, which is exactly why this is checked by a
        /// machine.
        /// </summary>
        static void Verify()
        {
            var sky = AssetDatabase.LoadAssetAtPath<DayNightProfile>("Assets/_Project/Data/DayNight.asset");
            var island = AssetDatabase.LoadAssetAtPath<IslandProfile>("Assets/_Project/Data/Island.asset");
            if (sky == null || island == null) return;

            // exp(-(density * d)^2) = 0.02 is where fog has swallowed 98% of what is behind it.
            const float opaque = 1.978f;

            float thinnest = float.MaxValue;
            for (int i = 0; i <= 20; i++)
            {
                float density = sky.FogDensity.Evaluate(i / 20f);
                if (density > 0.0001f) thinnest = Mathf.Min(thinnest, density);
            }

            float reach = opaque / thinnest;
            float far = CameraTuning.FarPlane;

            var report = new List<string>
            {
                $"fog opaque at {reach:F0}m (thinnest density {thinnest:F4})",
                $"camera far plane {far:F0}m",
                $"water horizon out to {island.WaterHorizon:F0}m",
            };

            if (reach > far)
                Debug.LogError($"[RenderTuning] The fog reaches further than the camera does - "
                               + string.Join(", ", report) + ". The world will end in mid-air.");
            else if (reach > island.WaterHorizon)
                Debug.LogError($"[RenderTuning] The fog outruns the sea - " + string.Join(", ", report)
                               + ". The horizon will show the skybox meeting nothing.");
            else
                Debug.Log("[RenderTuning] Draw distance agrees with the fog: " + string.Join(", ", report) + ".");
        }
    }
}
