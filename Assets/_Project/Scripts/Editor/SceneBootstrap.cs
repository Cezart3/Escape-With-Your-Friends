using System.IO;
using System.Linq;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.UI;
using EscapeWithYourFriends.World;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Managing.Transporting;
using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Transporting;
using FishNet.Transporting.Multipass;
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
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.SceneBootstrap.EnsureTransports
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.SceneBootstrap.EnsureWorldSpawner
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.SceneBootstrap.EnsureHud
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

        const string PlayerPrefabPath = "Assets/_Project/Prefabs/Player.prefab";

        const string ReviveMachinePrefabPath = "Assets/_Project/Prefabs/ReviveMachine.prefab";

        /// <summary>
        /// Where the Revive Machine stands in the greybox arena: far enough from the spawn ring that
        /// hauling a body there is a walk, close enough that the walk is not the content.
        /// Rotated to face the spawn point, so its bay and its exit both point at where people are.
        /// </summary>
        static readonly Vector3 ReviveMachinePosition = new(0f, 0f, 14f);
        static readonly Vector3 ReviveMachineEuler = new(0f, 180f, 0f);

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

            // The brain is what a CinemachineCamera drives. Without one the scene camera sits where it
            // was placed and every player looks at the greybox from the same fixed angle.
            cameraGo.AddComponent<Unity.Cinemachine.CinemachineBrain>();

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            ArenaBuilder.Build();

            BuildNetworkManager();

            // After the manager, because this is the step that needs the PlayerSpawner to exist.
            ArenaBuilder.WireSpawnPoints();

            EditorSceneManager.SaveScene(scene, BootstrapPath);
            Debug.Log($"[SceneBootstrap] created {BootstrapPath}");

            RegisterInBuildSettings(BootstrapPath);

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Rebuilds the greybox arena in the existing Bootstrap scene. Kept as an alias rather than
        /// deleted: it is the name every earlier commit message and every note in docs/ uses.
        ///
        ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.SceneBootstrap.EnsureArena
        /// </summary>
        public static void EnsureArena() => ArenaBuilder.BuildArena();

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
        /// Adds or re-points the PlayerSpawner on the existing Bootstrap scene. Separate entry point
        /// because the player prefab is generated by PlayerPrefabBuilder, which runs in its own
        /// batchmode invocation: the reference can only be resolved after that has finished.
        /// </summary>
        public static void EnsurePlayerSpawner()
        {
            var scene = EditorSceneManager.OpenScene(BootstrapPath, OpenSceneMode.Single);

            var manager = Object.FindFirstObjectByType<NetworkManager>();
            if (manager == null)
            {
                Debug.LogError("[SceneBootstrap] No NetworkManager in the scene; run EnsureNetworkManager first.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            AttachPlayerSpawner(manager.gameObject);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, BootstrapPath);

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Adds or re-points the WorldSpawner on the existing Bootstrap scene, and fills its list with
        /// the props the greybox arena needs. Separate entry point for the same reason
        /// <see cref="EnsurePlayerSpawner"/> is one: the prefabs it references are generated by other
        /// batchmode invocations and can only be resolved after those have finished.
        /// </summary>
        public static void EnsureWorldSpawner()
        {
            var scene = EditorSceneManager.OpenScene(BootstrapPath, OpenSceneMode.Single);

            var manager = Object.FindFirstObjectByType<NetworkManager>();
            if (manager == null)
            {
                Debug.LogError("[SceneBootstrap] No NetworkManager in the scene; run EnsureNetworkManager first.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            AttachWorldSpawner(manager.gameObject);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, BootstrapPath);

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Adds the HUD to the existing Bootstrap scene if it is missing.
        ///
        ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.SceneBootstrap.EnsureHud
        ///
        /// The HUD is a bare GameObject with one component: everything it draws is built in code at
        /// runtime, so there is nothing here to lay out and nothing to keep in sync with a prefab.
        /// It lives in Bootstrap rather than in a gameplay scene because it has to survive the scene
        /// loads that will eventually swap the arena for the island.
        /// </summary>
        public static void EnsureHud()
        {
            var scene = EditorSceneManager.OpenScene(BootstrapPath, OpenSceneMode.Single);

            if (Object.FindFirstObjectByType<HudRoot>() != null)
            {
                Debug.Log("[SceneBootstrap] HUD already present; nothing to do.");
            }
            else
            {
                var go = new GameObject("HUD");
                go.AddComponent<HudRoot>();

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, BootstrapPath);
                Debug.Log($"[SceneBootstrap] HUD added to {BootstrapPath}");
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

            BuildTransports(go);

            // The tick rate FishNet ships with happens to already be 30, but leaving it implicit means
            // a package update could quietly change how fast the whole game simulates.
            var timeManager = go.AddComponent<TimeManager>();
            timeManager.SetTickRate(TickRate);

            go.AddComponent<NetworkBootstrap>();
            AttachLobby(go);
            AttachPlayerSpawner(go);
            AttachWorldSpawner(go);

            Debug.Log($"[SceneBootstrap] NetworkManager: Multipass on {DefaultPort}, tick {TickRate}Hz.");
        }

        /// <summary>
        /// Rebuilds the transport stack on the NetworkManager of the existing Bootstrap scene.
        ///
        ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.SceneBootstrap.EnsureTransports
        /// </summary>
        public static void EnsureTransports()
        {
            var scene = EditorSceneManager.OpenScene(BootstrapPath, OpenSceneMode.Single);

            var manager = Object.FindFirstObjectByType<NetworkManager>();
            if (manager == null)
            {
                Debug.LogError("[SceneBootstrap] no NetworkManager in the scene; run EnsureNetworkManager first.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            GameObject go = manager.gameObject;

            foreach (Transport existing in go.GetComponents<Transport>())
                Object.DestroyImmediate(existing);

            var oldSelector = go.GetComponent<TransportSelector>();
            if (oldSelector != null) Object.DestroyImmediate(oldSelector);

            var oldSteam = go.GetComponent<SteamRuntime>();
            if (oldSteam != null) Object.DestroyImmediate(oldSteam);

            BuildTransports(go);
            AttachLobby(go);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, BootstrapPath);
            Debug.Log($"[SceneBootstrap] transports rebuilt: Multipass with Tugboat on {DefaultPort} "
                      + "and FishyFacepunch on Steam app 480.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Puts both transports on the NetworkManager, under Multipass.
        ///
        /// A shipped session runs over Steam: no port forwarding, no dedicated server, no bill. But
        /// Steam needs a running Steam client, which a headless build launched from a terminal script
        /// does not have, so Tugboat has to stay reachable or the whole automated test setup for this
        /// project dies. Multipass is FishNet answer to exactly that: the server starts every listed
        /// transport, and a client picks one. See TransportSelector and #13.
        ///
        /// Order matters. Multipass defaults its client transport to index 0, so Tugboat first means
        /// a build with no arguments behaves exactly as it did before Steam existed.
        /// </summary>
        static void BuildTransports(GameObject go)
        {
            var tugboat = go.AddComponent<Tugboat>();
            tugboat.SetPort(DefaultPort);
            tugboat.SetMaximumClients(8);

            var steam = go.AddComponent<global::FishyFacepunch.FishyFacepunch>();
            steam.SetMaximumClients(8);

            var multipass = go.AddComponent<Multipass>();

            // Three transports now sit on this object and TransportManager would otherwise take
            // whichever GetComponent returns first, which is a component ordering accident. Adding the
            // manager here and naming the transport makes the choice explicit and diffable.
            var transportManager = go.GetComponent<TransportManager>();
            if (transportManager == null) transportManager = go.AddComponent<TransportManager>();
            transportManager.Transport = multipass;

            // The list is private and serialized, which is the only way it can be filled from a script
            // and still be visible in a scene diff.
            var so = new SerializedObject(multipass);
            SerializedProperty transports = so.FindProperty("_transports");
            transports.arraySize = 2;
            transports.GetArrayElementAtIndex(0).objectReferenceValue = tugboat;
            transports.GetArrayElementAtIndex(1).objectReferenceValue = steam;
            so.ApplyModifiedPropertiesWithoutUndo();

            go.AddComponent<SteamRuntime>();
            go.AddComponent<TransportSelector>();
        }

        /// <summary>
        /// Puts the Steam lobby on the NetworkManager object, next to the bootstrap it drives.
        ///
        /// It sits here rather than on a menu object because a lobby outlives any one scene: an
        /// invite accepted mid-game has to be handled by something that is still alive, and the
        /// NetworkManager object is the one thing guaranteed to be.
        /// </summary>
        static void AttachLobby(GameObject go)
        {
            if (go.GetComponent<SteamLobby>() == null) go.AddComponent<SteamLobby>();
        }

        /// <summary>
        /// Puts the spawner on the NetworkManager object and points it at the player prefab. It lives
        /// here rather than in a gameplay scene because the connection it reacts to is owned by the
        /// NetworkManager, and Bootstrap is the one scene guaranteed to be loaded when someone joins.
        /// </summary>
        static void AttachPlayerSpawner(GameObject go)
        {
            var spawner = go.GetComponent<PlayerSpawner>();
            if (spawner == null) spawner = go.AddComponent<PlayerSpawner>();

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (prefab == null)
            {
                Debug.LogWarning($"[SceneBootstrap] {PlayerPrefabPath} does not exist yet; "
                                 + "run PlayerPrefabBuilder.BuildPlayerPrefab, then EnsurePlayerSpawner.");
                return;
            }

            var networkObject = prefab.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                Debug.LogError($"[SceneBootstrap] {PlayerPrefabPath} has no NetworkObject.");
                return;
            }

            var so = new SerializedObject(spawner);
            so.FindProperty("_playerPrefab").objectReferenceValue = networkObject;
            so.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log($"[SceneBootstrap] PlayerSpawner points at {PlayerPrefabPath}.");
        }

        /// <summary>
        /// Puts the world spawner on the NetworkManager object and writes its list of props. One
        /// entry for now — the Revive Machine — because it is the first thing in the game that is
        /// part of the map. The shop, the casino and the native village join this list unchanged.
        /// </summary>
        static void AttachWorldSpawner(GameObject go)
        {
            var spawner = go.GetComponent<WorldSpawner>();
            if (spawner == null) spawner = go.AddComponent<WorldSpawner>();

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ReviveMachinePrefabPath);
            if (prefab == null)
            {
                Debug.LogWarning($"[SceneBootstrap] {ReviveMachinePrefabPath} does not exist yet; "
                                 + "run ReviveMachineBuilder.BuildReviveMachine, then EnsureWorldSpawner.");
                return;
            }

            var networkObject = prefab.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                Debug.LogError($"[SceneBootstrap] {ReviveMachinePrefabPath} has no NetworkObject.");
                return;
            }

            var so = new SerializedObject(spawner);
            SerializedProperty placements = so.FindProperty("_placements");

            // Rewritten from scratch rather than appended to: this list is generated output, and a
            // second run must not leave two machines standing in the same spot.
            placements.arraySize = 1;
            SerializedProperty entry = placements.GetArrayElementAtIndex(0);
            entry.FindPropertyRelative("Prefab").objectReferenceValue = networkObject;
            entry.FindPropertyRelative("Position").vector3Value = ReviveMachinePosition;
            entry.FindPropertyRelative("Euler").vector3Value = ReviveMachineEuler;

            so.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log($"[SceneBootstrap] WorldSpawner holds 1 placement: {ReviveMachinePrefabPath} "
                      + $"at {ReviveMachinePosition}.");
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
