using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The acceptance test for #202, behind <c>-waterTest</c> with <c>-scene island</c>: the flat
    /// horizon ring has to reach the camera's far plane in every direction, so the sea runs to the
    /// horizon from any altitude, and the material has to carry the fades that hide the patch edge.
    /// Geometry and material properties only - nothing here renders a pixel.
    /// </summary>
    public class WaterTest : MonoBehaviour
    {
        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-waterTest")) return;

            _started = true;

            var go = new GameObject("WaterTest");
            DontDestroyOnLoad(go);
            go.AddComponent<WaterTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            WaterSurface water = null;
            for (float deadline = Time.time + 30f; Time.time < deadline && water == null;)
            {
                water = FindFirstObjectByType<WaterSurface>();
                if (water == null) yield return new WaitForSeconds(0.5f);
            }

            Check("the scene has a WaterSurface", water != null);
            if (water == null)
            {
                Report();
                yield break;
            }

            MeshRenderer[] pieces = water.GetComponentsInChildren<MeshRenderer>();
            MeshFilter ring = pieces.Select(p => p.GetComponent<MeshFilter>())
                                    .Where(f => f != null && f.sharedMesh != null)
                                    .OrderByDescending(f => f.sharedMesh.bounds.extents.x)
                                    .FirstOrDefault();
            Check("there is a horizon ring mesh", ring != null);

            if (ring != null)
            {
                // The ring is square and follows the camera in xz, so its half-width is the reach in
                // every direction. The far plane is the furthest anything is drawn.
                float reach = ring.sharedMesh.bounds.extents.x;
                Check($"the ring reaches the far plane ({reach:F0} m of {CameraTuning.FarPlane:F0} m; regenerate the water if not)",
                      reach >= CameraTuning.FarPlane);
                Check("the ring follows the camera", water.FollowCamera);
            }

            Material material = pieces.Length > 0 ? pieces[0].sharedMaterial : null;
            Check("the water has a material", material != null);
            // -nographics swaps every shader for the error one, whose properties read as zero.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Debug.Log("[WaterTest] no graphics device: fades unchecked; run without -nographics to check them.");
            else if (material != null)
            {
                Vector4 waves = material.GetVector("_PatchFade");
                Vector4 ripples = material.GetVector("_RippleFade");
                Check($"the waves fade out toward the patch border ({waves.x:F0} to {waves.y:F0} m)",
                      waves.y > waves.x && waves.x > 0f);
                Check($"the ripples fade out with distance, past the patch ({ripples.x:F0} to {ripples.y:F0} m)",
                      ripples.y > ripples.x && ripples.x >= waves.y);
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
            Debug.LogError($"[WaterTest] FAILED: {what}.");
        }

        void Report()
        {
            string line = $"[WaterTest] {_passed} passed, {_failed} failed.";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }
    }
}
