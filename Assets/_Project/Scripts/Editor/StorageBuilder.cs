using System.IO;
using EscapeWithYourFriends.Items;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The storage chest prefab.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.StorageBuilder.Build
    ///
    /// One prefab, unlike the three stations, because a chest is a chest: the only thing that would
    /// vary is the slot count, and that is a field rather than a second prefab.
    ///
    /// It is solid. A chest you can walk through is a chest you will lose in the grass, and the
    /// interactor needs something to raycast at anyway.
    /// </summary>
    public static class StorageBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs/Stations";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        internal const string ChestPath = PrefabDir + "/StorageChest.prefab";

        public static void Build()
        {
            Directory.CreateDirectory(PrefabDir);

            var root = new GameObject("StorageChest");

            Block(root.transform, "Body", new Vector3(0f, 0.35f, 0f), new Vector3(1.3f, 0.7f, 0.8f),
                  new Color(0.46f, 0.33f, 0.21f), solid: true);
            Block(root.transform, "Lid", new Vector3(0f, 0.76f, 0f), new Vector3(1.35f, 0.12f, 0.85f),
                  new Color(0.38f, 0.27f, 0.17f), solid: false);
            Block(root.transform, "BandLeft", new Vector3(-0.45f, 0.4f, 0f),
                  new Vector3(0.08f, 0.85f, 0.86f), new Color(0.42f, 0.44f, 0.47f), solid: false);
            Block(root.transform, "BandRight", new Vector3(0.45f, 0.4f, 0f),
                  new Vector3(0.08f, 0.85f, 0.86f), new Color(0.42f, 0.44f, 0.47f), solid: false);
            Block(root.transform, "Latch", new Vector3(0f, 0.62f, 0.42f),
                  new Vector3(0.16f, 0.18f, 0.06f), new Color(0.72f, 0.62f, 0.30f), solid: false);

            root.AddComponent<NetworkObject>();

            // No NetworkTransform, same reasoning as the stations: it never moves, and the spawn
            // message carries where it stands.
            var storage = root.AddComponent<Storage>();
            storage.Configure(slots: 30, label: "chest");

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, ChestPath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[StorageBuilder] Failed to save {ChestPath}.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[StorageBuilder] Built {ChestPath} with 30 slots.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static GameObject Block(Transform parent, string name, Vector3 position, Vector3 size,
                                Color color, bool solid)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = position;
            cube.transform.localScale = size;

            if (!solid)
            {
                Object.DestroyImmediate(cube.GetComponent<Collider>());

                var renderer = cube.GetComponent<Renderer>();
                if (renderer != null) renderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = color };
            cube.GetComponent<Renderer>().sharedMaterial = material;

            return cube;
        }

        /// <summary>Same reasoning as PlayerPrefabBuilder.RegisterSpawnable; see the note there.</summary>
        static void RegisterSpawnable(NetworkObject networkObject)
        {
            if (networkObject == null)
            {
                Debug.LogError($"[StorageBuilder] {ChestPath} has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[StorageBuilder] missing {PrefabObjectsPath}; the chest cannot spawn.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);
        }
    }
}
