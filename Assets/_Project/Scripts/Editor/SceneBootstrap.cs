using System.IO;
using System.Linq;
using EscapeWithYourFriends.Net;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Managing.Timing;
using FishNet.Transporting.Tugboat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Builds the persistent bootstrap scene and registers it in Build Settings.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.SceneBootstrap.CreateBootstrapScene
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.SceneBootstrap.EnsureNetworkManager
    ///
    /// Bootstrap is the scene that ships as index 0: it holds nothing gameplay-specific and hosts the
    /// NetworkManager, so gameplay scenes can be loaded and unloaded around it.
    ///
    /// The scene is built by script rather than by hand for the same reason the island will be
    /// generated rather than sculpted — a scene that only exists as a binary someone assembled once
    /// cannot be reviewed in a diff or rebuilt from a terminal.
    /// </summary>
    public static class SceneBootstrap
    {
        const string SceneDir = "Assets/_Project/Scenes";
        const string BootstrapPath = SceneDir + "/Bootstrap.unity";

        /// <summary>Tick rate for the whole game. 30Hz — see docs/ARCHITECTURE.md.</summary>
        const ushort TickRate = 30;

        const ushort DefaultPort = 7770;

        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

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

            BuildNetworkManager();

            EditorSceneManager.SaveScene(scene, BootstrapPath);
            Debug.Log($"[SceneBootstrap] created {BootstrapPath}");

            RegisterInBuildSettings(BootstrapPath);

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Adds the NetworkManager to the existing Bootstrap scene if it is missing. Separate entry
        /// point so the network rig can be re-applied without wiping a scene that has been edited by
        /// hand since.
        /// </summary>
        public static void EnsureNetworkManager()
        {
            var scene = EditorSceneManager.OpenScene(BootstrapPath, OpenSceneMode.Single);

            if (Object.FindFirstObjectByType<NetworkManager>() != null)
            {
                Debug.Log("[SceneBootstrap] NetworkManager already present; nothing to do.");
            }
            else
            {
                BuildNetworkManager();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, BootstrapPath);
                Debug.Log($"[SceneBootstrap] NetworkManager added to {BootstrapPath}");
            }

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Creates the NetworkManager rig. FishNet adds its own sub-managers (Server, Client, Time,
        /// Scene, Observer, Prediction, ...) in <c>Awake</c>, so the only things that have to exist
        /// up front are the manager itself, a transport, and the tick rate.
        /// </summary>
        static void BuildNetworkManager()
        {
            var go = new GameObject("NetworkManager");

            var manager = go.AddComponent<NetworkManager>();

            // FishNet can find this itself at runtime, but only by scanning the project. Assigning it
            // here writes the reference into the scene, where it can be seen in a diff.
            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null) Debug.LogWarning($"[SceneBootstrap] missing {PrefabObjectsPath}");
            else manager.SpawnablePrefabs = prefabs;

            // Tugboat is a placeholder. Shipping runs over the Steam transport, so there is no port
            // forwarding and no dedicated server, but Steam needs a running client and an app id,
            // which makes it useless for headless terminal tests. Tugboat is what lets four instances
            // be launched from a script today. See #13.
            //
            // TransportManager picks up whichever transport sits on this object, so nothing further
            // has to be wired.
            var transport = go.AddComponent<Tugboat>();
            transport.SetPort(DefaultPort);
            transport.SetMaximumClients(8);

            // The tick rate FishNet ships with happens to already be 30, but leaving it implicit means
            // a package update could quietly change how fast the whole game simulates.
            var timeManager = go.AddComponent<TimeManager>();
            timeManager.SetTickRate(TickRate);

            go.AddComponent<NetworkBootstrap>();

            Debug.Log($"[SceneBootstrap] NetworkManager: Tugboat on {DefaultPort}, tick {TickRate}Hz.");
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
