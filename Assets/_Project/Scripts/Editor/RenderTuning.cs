using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The three URP assets, tuned for the three machines this game expects to meet, plus the check
    /// that the world's draw distance and its fog still agree with each other.
    ///
    /// Since #241 these are the floors, not the final word: what cannot change at run time (HDR,
    /// soft shadows, the extra lights, which SSAO the renderer carries). Render scale, MSAA, the
    /// shadow map and whether SSAO runs are written over them by VideoSettings from the player's
    /// preset, before the first frame.
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

            /// <summary>0 none, 1 the cheap SSAO the 760M can carry, 2 the full one.</summary>
            public int Occlusion;
        }

        static readonly Tier[] Tiers =
        {
            // Integrated graphics. Render scale stays at 1: 0.8 upscaled bilinearly and the whole
            // image came out soft (#201). The saving is taken from the shadows instead - a 512 map,
            // one cascade, 35 metres - which is enough shadow for a game played on foot.
            new()
            {
                Path = LowPath, Hdr = false, Msaa = 1, RenderScale = 1f,
                ShadowResolution = 512, ShadowDistance = 35f, Cascades = 1,
                SoftShadows = false, ExtraLightShadows = false, LightsPerObject = 2,

                // The cheap SSAO, which the Low preset switches off at run time. It is here so the
                // AO option has something to switch on for a player who wants it.
                Occlusion = 1,
            },

            // A laptop with a real GPU, an older desktop card, or the Radeon 760M when its owner asks
            // for more than Low - the tier docs/ART-PLAN.md holds to 60 fps. Full resolution, soft
            // shadows, still no MSAA: at 1080p it costs more than it returns on a game with no thin
            // geometry.
            //
            // High's SSAO, with the depth-normals prepass. The cheap one, rebuilding normals from depth,
            // put a black grain round every edge and blocky dark pixels in the leaves (#287: the bar
            // and the trees on Medium). The prepass costs the 760M one to two milliseconds at p95 on
            // the perf route, and its worst spot still holds 9.3 ms against Medium's 16.7.
            //
            // HDR on, and it is nearly free: URP 17 renders HDR into R11G11B10, the same 32 bits a
            // pixel as LDR. Off, the ACES curve in PostProcess had nothing above 1.0 to roll off and
            // the bloom's 1.05 threshold could never be crossed - the grade was half switched off
            // on exactly the tier it was tuned for.
            new()
            {
                Path = MediumPath, Hdr = true, Msaa = 1, RenderScale = 1f,
                ShadowResolution = 2048, ShadowDistance = 80f, Cascades = 2,
                SoftShadows = true, ExtraLightShadows = false, LightsPerObject = 4, Occlusion = 2,
            },

            // Anything current. The shadow distance is 150 rather than more because the fog closes at
            // about 700 metres and shadows past that are invisible by definition. No MSAA: High's
            // preset is SMAA alone, and Ultra, which shares this asset, asks for 4x at run time.
            new()
            {
                Path = HighPath, Hdr = true, Msaa = 1, RenderScale = 1f,
                ShadowResolution = 2048, ShadowDistance = 150f, Cascades = 4,
                SoftShadows = true, ExtraLightShadows = true, LightsPerObject = 8, Occlusion = 2,
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

            var renderer = so.FindProperty("m_RendererDataList").GetArrayElementAtIndex(0)
                             .objectReferenceValue as ScriptableRendererData;
            if (renderer == null) Debug.LogError($"[RenderTuning] {asset.name} has no renderer; no SSAO settings written.");
            else
            {
                if (renderer is UniversalRendererData universal) PostProcessing(universal);
                Occlusion(renderer, tier.Occlusion);
            }

            Debug.Log($"[RenderTuning] {asset.name}: render scale {tier.RenderScale}, "
                      + $"MSAA x{tier.Msaa}, HDR {(tier.Hdr ? "on" : "off")}, shadows "
                      + $"{tier.ShadowResolution}^2 over {tier.ShadowDistance}m in {tier.Cascades} "
                      + $"cascade(s), {(tier.SoftShadows ? "soft" : "hard")}, "
                      + $"{tier.LightsPerObject} lights per object.");
        }

        /// <summary>
        /// The shaders and lookup textures every post-processing pass is built from. URP's own
        /// "create renderer" menu fills it in; <c>CreateInstance</c>, which is how ProjectSetup made
        /// these three, does not, and in URP 17 nothing reloads it later - the field has no
        /// <c>[Reload]</c> attribute. Null means the renderer builds no post-processing pass at all
        /// (UniversalRenderer: "No postProcessData means that post processes are disabled"), so the
        /// grade, the bloom and DrunkVision's blur have never been drawn on any tier, whatever the
        /// camera asked for.
        /// </summary>
        internal static PostProcessData DefaultPostProcess()
            => AssetDatabase.LoadAssetAtPath<PostProcessData>(
                UniversalRenderPipelineAsset.packagePath + "/Runtime/Data/PostProcessData.asset");

        static void PostProcessing(UniversalRendererData renderer)
        {
            if (renderer.postProcessData != null) return;

            PostProcessData data = DefaultPostProcess();
            if (data == null)
            {
                Debug.LogError($"[RenderTuning] URP's PostProcessData.asset is missing; {renderer.name} "
                               + "stays without post-processing.");
                return;
            }

            renderer.postProcessData = data;
            EditorUtility.SetDirty(renderer);
            Debug.Log($"[RenderTuning] {renderer.name} had no post-processing data; it has URP's now.");
        }

        /// <summary>
        /// Screen-space ambient occlusion, as a renderer feature on the tiers that can pay for it
        /// (docs/ART-PLAN.md §7). It is what puts a dark line where a crate meets the sand and under
        /// every eave, which is most of what separates "lit" from "painted" on flat-shaded models.
        ///
        /// Added the way URP's own inspector adds one - a sub-asset of the renderer plus an entry in
        /// the feature map - because the map is how the renderer finds it again after a reload. The
        /// settings class is internal to URP, so the fields are written by their serialized names;
        /// a URP upgrade that renames one is reported rather than silently left at a default.
        /// </summary>
        static void Occlusion(ScriptableRendererData renderer, int level)
        {
            ScreenSpaceAmbientOcclusion ssao = null;
            foreach (ScriptableRendererFeature feature in renderer.rendererFeatures)
                if (feature is ScreenSpaceAmbientOcclusion found) ssao = found;

            var data = new SerializedObject(renderer);
            SerializedProperty features = data.FindProperty("m_RendererFeatures");
            SerializedProperty map = data.FindProperty("m_RendererFeatureMap");

            if (level == 0)
            {
                if (ssao != null)
                {
                    for (int i = features.arraySize - 1; i >= 0; i--)
                    {
                        if (features.GetArrayElementAtIndex(i).objectReferenceValue != ssao) continue;
                        // Nulled first, then deleted once, the way URP's own RemoveComponent does it:
                        // whether a delete of a live reference only nulls it has changed across Unity
                        // versions, and a guessed second delete can take the next feature with it.
                        features.GetArrayElementAtIndex(i).objectReferenceValue = null;
                        features.DeleteArrayElementAtIndex(i);
                        if (i < map.arraySize) map.DeleteArrayElementAtIndex(i);
                    }

                    data.ApplyModifiedPropertiesWithoutUndo();
                    AssetDatabase.RemoveObjectFromAsset(ssao);
                    Object.DestroyImmediate(ssao, true);
                    EditorUtility.SetDirty(renderer);
                }

                Debug.Log($"[RenderTuning] SSAO: none on {renderer.name}.");
                return;
            }

            if (ssao == null)
            {
                ssao = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
                ssao.name = "ScreenSpaceAmbientOcclusion";
                AssetDatabase.AddObjectToAsset(ssao, renderer);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ssao, out string _, out long id);

                features.arraySize++;
                features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = ssao;
                map.arraySize++;
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = id;
                data.ApplyModifiedPropertiesWithoutUndo();
            }

            bool cheap = level == 1;
            var settings = new SerializedObject(ssao);

            // Cheap: a multiply over the finished opaque image rather than an input to lighting, which
            // is what lets it skip a depth prepass - the pass an integrated GPU cannot afford. Depth
            // alone, normals rebuilt from it, which is grainy at any sample count (#287), so only Low
            // carries it, for a player who switches AO on. Half resolution with four samples and the
            // Kawase blur made it worse still; the rest is High's.
            Set(settings, "m_Settings.AfterOpaque", cheap);
            Set(settings, "m_Settings.Source", cheap ? 0 : 1);          // Depth, DepthNormals
            Set(settings, "m_Settings.NormalSamples", 1);               // Medium
            Set(settings, "m_Settings.Downsample", false);
            Set(settings, "m_Settings.Samples", 1);                     // Medium (8)
            Set(settings, "m_Settings.BlurQuality", 1);                 // Medium (Gaussian)
            Set(settings, "m_Settings.Falloff", cheap ? 50f : 100f);

            // Intensity and radius stay at URP's defaults (3.0 and 0.035). How dark the corners
            // should be is a look decision for somebody with a screen, not a number to guess here.

            settings.ApplyModifiedPropertiesWithoutUndo();
            ssao.SetActive(true);
            EditorUtility.SetDirty(ssao);
            EditorUtility.SetDirty(renderer);

            Debug.Log(cheap
                ? $"[RenderTuning] SSAO on {renderer.name} (after opaque, depth, full res, 8 samples, Gaussian)."
                : $"[RenderTuning] SSAO on {renderer.name} (in lighting, depth-normals, full res, 8 samples).");
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
