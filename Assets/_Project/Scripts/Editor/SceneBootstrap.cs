using System.IO;
using System.Linq;
using EscapeWithYourFriends.Core;
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
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.SceneBootstrap.EnsureAuthenticator
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
            camera.nearClipPlane = CameraTuning.NearPlane;
            camera.farClipPlane = CameraTuning.FarPlane;
            camera.transform.position = new Vector3(0f, 2f, -10f);
            cameraGo.AddComponent<AudioListener>();

            // The brain is what a CinemachineCamera drives. Without one the scene camera sits where it
            // was placed and every player looks at the greybox from the same fixed angle.
            cameraGo.AddComponent<Unity.Cinemachine.CinemachineBrain>();

            // No light and no geometry here any more. The arena used to be built into this scene,
            // which was fine while it was the only map and wrong the moment there were two: Bootstrap
            // stays loaded for the whole session, so a sixty-metre plate in it would sit in the
            // middle of the island's sea and its sun would fight the island's day/night cycle for
            // which directional light URP treats as the main one. Both maps bring their own.

            BuildNetworkManager();

            // The HUD belongs to the scene that stays loaded for the whole session, and it belongs
            // *here* rather than only in EnsureHud: this method rebuilds the scene from an empty one,
            // so anything added by a separate entry point is silently thrown away the next time it
            // runs. That is what happened to the HUD between #106 and #40, which is why the survival
            // bars had nothing to draw on and -hudTest printed nothing at all.
            new GameObject("HUD").AddComponent<HudRoot>();

            EditorSceneManager.SaveScene(scene, BootstrapPath);
            Debug.Log($"[SceneBootstrap] created {BootstrapPath}");

            RegisterInBuildSettings(BootstrapPath);
            RegisterInBuildSettings(SceneDir + "/Island.unity");
            RegisterInBuildSettings(SceneDir + "/Arena.unity");

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
        /// <summary>
        ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.SceneBootstrap.EnsureSceneLoader
        ///
        /// Adds the map loader to a Bootstrap scene that predates it, and registers both gameplay
        /// scenes in build settings. Separate from CreateBootstrapScene because rebuilding Bootstrap
        /// from scratch throws away the NetworkManager's serialized transport settings.
        /// </summary>
        public static void EnsureSceneLoader()
        {
            var scene = EditorSceneManager.OpenScene(BootstrapPath, OpenSceneMode.Single);

            var manager = Object.FindFirstObjectByType<NetworkManager>();
            if (manager == null)
            {
                Debug.LogError("[SceneBootstrap] No NetworkManager in Bootstrap; run CreateBootstrapScene.");
                return;
            }

            if (manager.GetComponent<GameSceneLoader>() == null)
            {
                manager.gameObject.AddComponent<GameSceneLoader>();
                Debug.Log("[SceneBootstrap] Added GameSceneLoader to the NetworkManager.");
            }

            // The arena is its own scene now, so anything left of it here is a plate floating in the
            // island's sea. Removed rather than disabled: it is generated output, not somebody's work.
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name != "Arena" && root.name != "Floor" && root.name != "Directional Light") continue;

                Debug.Log($"[SceneBootstrap] Removed '{root.name}' from Bootstrap; it belongs to a map.");
                Object.DestroyImmediate(root);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, BootstrapPath);

            RegisterInBuildSettings(BootstrapPath);
            RegisterInBuildSettings(SceneDir + "/Island.unity");
            RegisterInBuildSettings(SceneDir + "/Arena.unity");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

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
            AttachAuthenticator(go);
            AttachLobby(go);
            AttachPlayerSpawner(go);
            AttachWorldSpawner(go);

            // Decides which map the session is played on, and loads it as a global scene when the
            // server starts. Bootstrap holds no gameplay, so something has to say what the game is.
            if (go.GetComponent<GameSceneLoader>() == null) go.AddComponent<GameSceneLoader>();

            Debug.Log($"[SceneBootstrap] NetworkManager: Multipass on {DefaultPort}, tick {TickRate}Hz, "
                      + "GameSceneLoader attached.");
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
        /// Adds the player-key authenticator to the NetworkManager of the existing Bootstrap scene.
        ///
        ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.SceneBootstrap.EnsureAuthenticator
        /// </summary>
        public static void EnsureAuthenticator()
        {
            var scene = EditorSceneManager.OpenScene(BootstrapPath, OpenSceneMode.Single);

            var manager = Object.FindFirstObjectByType<NetworkManager>();
            if (manager == null)
            {
                Debug.LogError("[SceneBootstrap] No NetworkManager in the scene; run EnsureNetworkManager first.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            if (manager.GetComponent<PlayerKeyAuthenticator>() != null)
            {
                Debug.Log("[SceneBootstrap] Authenticator already present; nothing to do.");
            }
            else
            {
                AttachAuthenticator(manager.gameObject);

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, BootstrapPath);
                Debug.Log($"[SceneBootstrap] PlayerKeyAuthenticator added to {BootstrapPath}");
            }

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Puts the key authenticator on the NetworkManager object.
        ///
        /// It has to be on this exact GameObject and not a child: FishNet's ServerManager finds an
        /// authenticator with <c>GetComponent</c> on its own object when none is assigned, and its
        /// sub-managers are added to the NetworkManager's object at runtime. Wiring it that way
        /// rather than through a serialized reference means the link cannot break when either side is
        /// regenerated, and there is nothing in the scene file to review except the component itself.
        /// </summary>
        static void AttachAuthenticator(GameObject go)
        {
            if (go.GetComponent<PlayerKeyAuthenticator>() == null) go.AddComponent<PlayerKeyAuthenticator>();
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

            scenes.Add(new EditorBuildSettingsScene(path, true));

            // Bootstrap must be index 0 - it is the scene the player loads into, and everything else
            // is loaded around it. This used to insert at the front, which was right while Bootstrap
            // was the only entry and quietly wrong the moment a second scene was registered after it.
            int bootstrap = scenes.FindIndex(entry => entry.path == BootstrapPath);
            if (bootstrap > 0)
            {
                EditorBuildSettingsScene first = scenes[bootstrap];
                scenes.RemoveAt(bootstrap);
                scenes.Insert(0, first);
            }

            EditorBuildSettings.scenes = scenes.ToArray();

            Debug.Log($"[SceneBootstrap] build settings now: " +
                      string.Join(", ", scenes.Select(s => Path.GetFileNameWithoutExtension(s.path))));
        }
    }
}
