using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// The acceptance test for #201, behind <c>-qualityTest</c>: no quality tier may render below
    /// full resolution and stretch it back up with the default bilinear filter, which is what made
    /// the Low tier look blurry. A tier below 1.0 is fine only if it upscales with FSR.
    ///
    /// Reads the URP asset each quality level points at, so it needs no scene and no player.
    /// </summary>
    public class QualityTest : MonoBehaviour
    {
        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-qualityTest")) return;

            _started = true;

            var go = new GameObject("QualityTest");
            DontDestroyOnLoad(go);
            go.AddComponent<QualityTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            yield return null;

            Check("there are quality levels", QualitySettings.count > 0);

            for (int i = 0; i < QualitySettings.count; i++)
            {
                var asset = QualitySettings.GetRenderPipelineAssetAt(i) as UniversalRenderPipelineAsset;
                string name = QualitySettings.names[i];
                Check($"'{name}' has a URP asset", asset != null);
                if (asset == null) continue;

                bool sharp = asset.renderScale >= 1f
                             || asset.upscalingFilter == UpscalingFilterSelection.FSR;
                Check($"'{name}' is sharp (scale {asset.renderScale}, filter {asset.upscalingFilter})", sharp);
            }

            Report();
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[QualityTest] FAILED: {what}.");
        }

        void Report()
        {
            string line = $"[QualityTest] {_passed} passed, {_failed} failed.";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }
    }
}
