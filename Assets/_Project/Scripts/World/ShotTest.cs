using System.Collections;
using System.IO;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Player;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// <c>-shots &lt;folder&gt;</c>: screenshots of the island from the spawn, for judging the look
    /// from a terminal. Needs a real window (no <c>-nographics</c>): a camera of its own on top of the
    /// player's, four eye-level views round the spawn and one from above, then quit. Not a pass/fail
    /// harness - the verdict is a human's, or whoever reads the PNGs.
    /// </summary>
    public class ShotTest : MonoBehaviour
    {
        static bool _started;

        internal static void Begin()
        {
            string folder = CommandLine.GetString("-shots", null);
            if (_started || string.IsNullOrEmpty(folder)) return;
            _started = true;

            var go = new GameObject("ShotTest");
            DontDestroyOnLoad(go);
            go.AddComponent<ShotTest>().StartCoroutine(Run(folder));
        }

        static IEnumerator Run(string folder)
        {
            Directory.CreateDirectory(folder);

            // The island, the player and the first shadow cascade all have to have arrived.
            yield return new WaitForSeconds(12f);

            PlayerMotor player = FindAnyObjectByType<PlayerMotor>();
            Vector3 eye = (player != null ? player.transform.position : Vector3.zero) + Vector3.up * 1.7f;

            var camera = new GameObject("ShotCamera").AddComponent<Camera>();
            if (Camera.main != null) camera.CopyFrom(Camera.main);
            camera.depth = 100f;
            camera.fieldOfView = 70f;
            camera.farClipPlane = 1500f;
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = true;

            Material sky = RenderSettings.skybox;
            Debug.Log($"[ShotTest] sky {(sky != null ? sky.name + " on " + sky.shader.name : "none")}, "
                      + $"clear {camera.clearFlags}, quality {QualitySettings.names[QualitySettings.GetQualityLevel()]}.");

            for (int i = 0; i < 4; i++)
            {
                camera.transform.SetPositionAndRotation(eye, Quaternion.Euler(4f, i * 90f, 0f));
                yield return Shoot(folder, $"eye{i * 90}");
            }

            camera.transform.position = eye + new Vector3(0f, 35f, -55f);
            camera.transform.LookAt(eye);
            yield return Shoot(folder, "above");

            Debug.Log($"[ShotTest] 5 shots in {folder}.");
            Application.Quit();
        }

        static IEnumerator Shoot(string folder, string name)
        {
            // Two frames so the camera's move reaches the shadows and the post stack.
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, name + ".png"));
            yield return new WaitForSeconds(0.5f);
        }
    }
}
