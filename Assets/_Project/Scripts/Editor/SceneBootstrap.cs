using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Creates the persistent bootstrap scene and registers it in Build Settings.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.SceneBootstrap.CreateBootstrapScene
    ///
    /// Bootstrap is the scene that ships as index 0: it holds nothing gameplay-specific and later
    /// hosts the NetworkManager, so gameplay scenes can be loaded and unloaded around it.
    /// </summary>
    public static class SceneBootstrap
    {
        const string SceneDir = "Assets/_Project/Scenes";
        const string BootstrapPath = SceneDir + "/Bootstrap.unity";

        public static void CreateBootstrapScene()
        {
            Directory.CreateDirectory(SceneDir);

            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.transform.position = new Vector3(0f, 2f, -10f);
            cameraGo.AddComponent<AudioListener>();

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // A visible floor so a smoke-test build is obviously alive rather than a black screen.
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(5f, 1f, 5f);

            EditorSceneManager.SaveScene(scene, BootstrapPath);
            Debug.Log($"[SceneBootstrap] created {BootstrapPath}");

            RegisterInBuildSettings(BootstrapPath);

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static void RegisterInBuildSettings(string path)
        {
            var scenes = EditorBuildSettings.scenes.ToList();

            if (scenes.Any(s => s.path == path))
            {
                Debug.Log($"[SceneBootstrap] already in build settings: {path}");
                return;
            }

            // Bootstrap must stay at index 0 — it is what the player loads first.
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            Debug.Log($"[SceneBootstrap] build settings now: " +
                      string.Join(", ", scenes.Select(s => Path.GetFileNameWithoutExtension(s.path))));
        }
    }
}
