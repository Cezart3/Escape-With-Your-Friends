using System.IO;
using EscapeWithYourFriends.Casino;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The casino cage: two windows, one that sells chips and one that buys them back (#63).
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.CasinoFactory.Build
    ///
    /// Two prefabs rather than one, because a POI entry places a prefab and cannot set a field on
    /// it - the direction has to be baked in. They are the same six boxes with a different sign
    /// colour, which is all a greybox needs to tell them apart.
    ///
    /// Same rule as the other factories: creates what does not exist, never overwrites what does.
    /// </summary>
    public static class CasinoFactory
    {
        const string PrefabDir = "Assets/_Project/Prefabs/Stations";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        internal const string BuyWindowPath = PrefabDir + "/ChipWindow.prefab";
        internal const string CashWindowPath = PrefabDir + "/CashWindow.prefab";

        /// <summary>Money per press. A stack to bet with, not a night's savings.</summary>
        const int Chunk = 100;

        public static void Build()
        {
            int built = 0;

            if (Booth(CageDirection.Buy, BuyWindowPath, "Chips",
                      new Color(0.85f, 0.70f, 0.25f))) built++;

            if (Booth(CageDirection.CashOut, CashWindowPath, "Cash",
                      new Color(0.35f, 0.65f, 0.45f))) built++;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[CasinoFactory] cage windows ready, {built} built just now "
                      + $"({Chunk} a press, one for one).");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static bool Booth(CageDirection direction, string path, string label, Color sign)
        {
            Directory.CreateDirectory(PrefabDir);

            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return false;

            var root = new GameObject(label + "Window");

            Block(root.transform, "Counter", new Vector3(0f, 0.55f, 0f), new Vector3(1.8f, 1.1f, 0.8f),
                  new Color(0.22f, 0.20f, 0.26f), solid: true);
            Block(root.transform, "Top", new Vector3(0f, 1.14f, 0f), new Vector3(2f, 0.1f, 1f),
                  new Color(0.32f, 0.29f, 0.36f), solid: false);
            Block(root.transform, "Bars", new Vector3(0f, 1.85f, -0.35f),
                  new Vector3(1.8f, 1.3f, 0.06f), new Color(0.55f, 0.55f, 0.58f), solid: false);
            Block(root.transform, "Sign", new Vector3(0f, 2.6f, -0.35f),
                  new Vector3(1.2f, 0.4f, 0.06f), sign, solid: false);

            root.AddComponent<NetworkObject>();
            root.AddComponent<Cashier>().Configure(direction, Chunk);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[CasinoFactory] Failed to save {path}.");
                return false;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>(), path);
            Debug.Log($"[CasinoFactory] Built {path} ({direction}).");

            return true;
        }

        /// <summary>Same as ShopFactory.Block; copied rather than shared, because one greybox helper
        /// per factory is smaller than a greybox library nobody asked for.</summary>
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
        static void RegisterSpawnable(NetworkObject networkObject, string path)
        {
            if (networkObject == null)
            {
                Debug.LogError($"[CasinoFactory] {path} has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[CasinoFactory] missing {PrefabObjectsPath}; the cage cannot spawn.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);
        }
    }
}
