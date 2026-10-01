using EscapeWithYourFriends.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The global look (#79). One volume, built once, alive for the whole process.
    ///
    /// This is the single largest difference between "a low-poly game" and "a programmer's
    /// screenshot", and until now the project had none of it. URP renders in linear space and hands
    /// the result to the display untouched unless something tonemaps it, so flat-shaded colours came
    /// out exactly as authored: no highlight rolloff, no contrast, no warmth, no bloom on the sun,
    /// and blacks sitting at zero. That reads as painted rather than lit, and no amount of extra
    /// polygons fixes it.
    ///
    /// Built in code rather than as a profile asset with a Volume in every scene, for the same
    /// reason everything else here is: the scenes are generated, and a look that lives in a scene
    /// has to be re-added to each one by hand. A <c>DontDestroyOnLoad</c> global volume applies to
    /// the menu, both islands and the casino without any of them knowing about it.
    ///
    /// Priority 0. <see cref="Player.DrunkVision"/> sits at 100 and overrides whatever it names.
    /// </summary>
    public static class PostProcess
    {
        static bool _started;
        static Bloom _bloom;

        /// <summary>Everything here is off on a headless build, which has no camera to grade.</summary>
        internal static void Begin()
        {
            if (_started || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                return;

            if (CommandLine.HasFlag("-noPost")) return;

            _started = true;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "GlobalLook";

            Tonemap(profile);
            Grade(profile);
            Glow(profile);
            Edges(profile);

            var go = new GameObject("PostProcess");
            Object.DontDestroyOnLoad(go);

            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 0f;
            volume.weight = 1f;
            volume.sharedProfile = profile;

            // A volume is ignored by any camera that has not asked for post-processing, and the main
            // camera is built in code by SceneBootstrap, which never asked. Until this line the only
            // thing that asked was DrunkVision - so the grade above appeared the moment somebody got
            // drunk and not before. Every scene load, in case a scene ever brings its own camera.
            Refresh();
            SceneManager.sceneLoaded += (_, _) => EnableOnCamera();

            Debug.Log("[PostProcess] Global look on: ACES, graded, bloom, vignette.");
        }

        /// <summary>The two options that live here, bloom and SMAA, read again. Called by VideoSettings.</summary>
        internal static void Refresh()
        {
            if (!_started) return;
            _bloom.active = VideoSettings.Current.Bloom;
            EnableOnCamera();
        }

        static void EnableOnCamera()
        {
            Camera camera = Camera.main;
            if (camera == null) return;

            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            if (data == null) return;
            data.renderPostProcessing = true;

            // Edges. SMAA rather than FXAA, which blurs the whole frame to soften the stairs. Whether
            // at all is the AA option (#241); MSAA, the other half of it, is on the URP asset.
            data.antialiasing = VideoSettings.Smaa ? AntialiasingMode.SubpixelMorphologicalAntiAliasing : AntialiasingMode.None;
            data.antialiasingQuality = AntialiasingQuality.High;
        }

        /// <summary>
        /// ACES. The film curve rather than Neutral, because the island is saturated greens and blues
        /// under a hard sun and Neutral leaves those flat - the whole point is the shoulder that
        /// stops a lit surface from clipping to a solid block of colour.
        /// </summary>
        static void Tonemap(VolumeProfile profile)
        {
            var tonemapping = profile.Add<Tonemapping>(true);
            tonemapping.mode.overrideState = true;
            tonemapping.mode.value = TonemappingMode.ACES;
        }

        /// <summary>
        /// The grade. Small numbers on purpose: this is a correction, not a filter, and anything
        /// strong enough to notice in a screenshot is strong enough to be tiring in an hour.
        /// </summary>
        static void Grade(VolumeProfile profile)
        {
            var colour = profile.Add<ColorAdjustments>(true);

            // ACES darkens the midtones on the way through, so a little exposure goes back in.
            colour.postExposure.overrideState = true;
            colour.postExposure.value = 0.25f;

            colour.contrast.overrideState = true;
            colour.contrast.value = 12f;

            colour.saturation.overrideState = true;
            colour.saturation.value = 10f;

            var balance = profile.Add<WhiteBalance>(true);

            // Warm, because it is a tropical island and because a neutral white point under a blue
            // sky reads as overcast.
            balance.temperature.overrideState = true;
            balance.temperature.value = 8f;

            var split = profile.Add<ShadowsMidtonesHighlights>(true);

            // Cool shadows against the warm key. The cheapest trick in colour grading and the one
            // that most reliably stops a flat-shaded surface looking like a swatch.
            split.shadows.overrideState = true;
            split.shadows.value = new Vector4(0.92f, 0.96f, 1.08f, 0f);

            split.highlights.overrideState = true;
            split.highlights.value = new Vector4(1.04f, 1.01f, 0.95f, 0f);
        }

        /// <summary>
        /// Bloom, above 1.0 only. Thresholding at the point where a surface is actually brighter than
        /// white keeps it on the sun, the sea's specular and the casino's lights, and off every pale
        /// wall - a bloom that glows on beige is the other way to look like 2004.
        /// </summary>
        static void Glow(VolumeProfile profile)
        {
            var bloom = _bloom = profile.Add<Bloom>(true);

            bloom.threshold.overrideState = true;
            bloom.threshold.value = 1.05f;

            bloom.intensity.overrideState = true;
            bloom.intensity.value = 0.45f;

            bloom.scatter.overrideState = true;
            bloom.scatter.value = 0.62f;

            // Off. It is a second blit for a lens artefact nobody asked for, and the low quality
            // tier cannot afford it.
            bloom.highQualityFiltering.overrideState = true;
            bloom.highQualityFiltering.value = false;
        }

        /// <summary>Vignette and a trace of chromatic aberration. Both nearly off; both missed when absent.</summary>
        static void Edges(VolumeProfile profile)
        {
            var vignette = profile.Add<Vignette>(true);

            vignette.intensity.overrideState = true;
            vignette.intensity.value = 0.22f;

            vignette.smoothness.overrideState = true;
            vignette.smoothness.value = 0.45f;

            var aberration = profile.Add<ChromaticAberration>(true);
            aberration.intensity.overrideState = true;
            aberration.intensity.value = 0.06f;
        }
    }
}
