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

            // -shotsAt a,b: each name a landmark id or any object's name, shot from 10m off and 4m up.
            foreach (string at in CommandLine.GetString("-shotsAt", "").Split(',', System.StringSplitOptions.RemoveEmptyEntries))
            {
                Landmark mark = Landmark.All.Find(l => l.Id == at);
                GameObject thing = mark != null ? mark.gameObject : GameObject.Find(at);
                if (thing == null)
                    foreach (Transform t in FindObjectsByType<Transform>(FindObjectsSortMode.None))
                        if (t.name.Contains(at)) { thing = t.gameObject; break; }
                if (thing == null) { Debug.Log($"[ShotTest] nothing called {at}."); continue; }

                Vector3 p = thing.transform.position;
                Debug.Log($"[ShotTest] {at} is {thing.name} at {p}, active {thing.activeInHierarchy}, "
                          + $"{thing.GetComponentsInChildren<Renderer>().Length} renderers.");
                camera.transform.position = p + thing.transform.forward * 10f + Vector3.up * 4f;
                camera.transform.LookAt(p + Vector3.up);
                yield return Shoot(folder, "at_" + at);
            }

            if (CommandLine.HasFlag("-shotsUi")) yield return Screens(folder, player, camera);

            Debug.Log($"[ShotTest] 5 shots in {folder}.");
            Application.Quit();
        }

        /// <summary>
        /// <c>-shotsUi</c>: the bag, a chest and the trader, with a few things in each (#287). A
        /// headless run builds no canvas, so these shots are the only check the screens get.
        /// </summary>
        static IEnumerator Screens(string folder, PlayerMotor player, Camera camera)
        {
            if (player == null || Data.ItemCatalog.Active == null) yield break;
            Destroy(camera.gameObject);

            var bag = player.GetComponent<Items.Inventory>();
            var input = player.GetComponent<PlayerInputReader>();
            int given = 0;
            foreach (Data.ItemDef def in Data.ItemCatalog.Active.Items)
                if (def != null && def.Icon != null && def.Weight < 3f && given < 12 && bag.Add(def, Mathf.Min(def.MaxStack, 1 + given % 4)) == 0)
                    given++;

            input.BotDriven = true;
            input.BotPress("inventory");
            yield return new WaitForSeconds(1f);
            yield return Shoot(folder, "ui_bag");

            Items.Storage chest = FindAnyObjectByType<Items.Storage>();
            if (chest != null)
            {
                foreach (Data.ItemDef def in Data.ItemCatalog.Active.Items)
                    if (def != null && def.Icon != null && chest.UsedSlots < 9) chest.Add(def, def.MaxStack);
                player.ServerTeleport(chest.transform.position + chest.transform.forward * 1.5f + Vector3.up * 0.2f, 0f);
                yield return new WaitForSeconds(1f);
                yield return Shoot(folder, "ui_chest");
            }

            int n = 0;
            foreach (Economy.ShopCounter counter in FindObjectsByType<Economy.ShopCounter>(FindObjectsSortMode.None))
            {
                player.ServerTeleport(counter.transform.position + counter.transform.forward * 1.5f + Vector3.up * 0.2f, 0f);
                yield return new WaitForSeconds(1f);
                yield return Shoot(folder, $"ui_shop{n++}");
            }
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
