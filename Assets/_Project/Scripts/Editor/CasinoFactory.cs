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
        internal const string TablePath = PrefabDir + "/RouletteTable.prefab";

        /// <summary>Money per press. A stack to bet with, not a night's savings.</summary>
        const int Chunk = 100;

        public static void Build()
        {
            int built = 0;

            if (Booth(CageDirection.Buy, BuyWindowPath, "Chips",
                      new Color(0.85f, 0.70f, 0.25f))) built++;

            if (Booth(CageDirection.CashOut, CashWindowPath, "Cash",
                      new Color(0.35f, 0.65f, 0.45f))) built++;

            if (Table()) built++;

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

        /// <summary>
        /// The roulette table (#64): a baize, a wheel that turns, and ten squares you can aim at.
        ///
        /// Every square is its own nested <see cref="NetworkObject"/>. That is not tidiness - the
        /// server resolves an interaction with <c>GetComponentInChildren</c> on the object the client
        /// named, so ten <see cref="BetSpot"/>s under one networked root would all answer as the
        /// first one and every bet in the game would land on red.
        /// </summary>
        static bool Table()
        {
            Directory.CreateDirectory(PrefabDir);

            if (AssetDatabase.LoadAssetAtPath<GameObject>(TablePath) != null) return false;

            var root = new GameObject("RouletteTable");

            Block(root.transform, "Baize", new Vector3(0f, 0.45f, 0f), new Vector3(3.2f, 0.9f, 1.8f),
                  new Color(0.10f, 0.32f, 0.16f), solid: true);
            Block(root.transform, "Rim", new Vector3(0f, 0.93f, 0f), new Vector3(3.4f, 0.08f, 2f),
                  new Color(0.28f, 0.16f, 0.10f), solid: false);

            var wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            wheel.name = "Wheel";
            wheel.transform.SetParent(root.transform, false);
            wheel.transform.localPosition = new Vector3(-1.0f, 1.03f, 0f);
            wheel.transform.localScale = new Vector3(1.1f, 0.05f, 1.1f);
            Object.DestroyImmediate(wheel.GetComponent<Collider>());
            Paint(wheel, new Color(0.18f, 0.14f, 0.12f));

            // The one painted pocket. With the wheel turning under a fixed table, a single marked
            // pocket is all it takes to read the result off the geometry, which is what the harness
            // does instead of trusting the number the wheel was handed.
            Block(wheel.transform, "Zero", new Vector3(0f, 0.6f, 0.42f), new Vector3(0.14f, 1.2f, 0.16f),
                  new Color(0.15f, 0.55f, 0.25f), solid: false);

            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = "Ball";
            ball.transform.SetParent(root.transform, false);
            ball.transform.localPosition = new Vector3(-1.0f, 1.08f, 0f);
            ball.transform.localScale = Vector3.one * 0.5f;
            Object.DestroyImmediate(ball.GetComponent<Collider>());

            var spinner = GameObject.CreatePrimitive(PrimitiveType.Cube);
            spinner.name = "Marker";
            spinner.transform.SetParent(ball.transform, false);
            spinner.transform.localPosition = new Vector3(0f, 0f, 0.9f);
            spinner.transform.localScale = new Vector3(0.16f, 0.16f, 0.16f);
            Object.DestroyImmediate(spinner.GetComponent<Collider>());
            Paint(spinner, Color.white);
            Paint(ball, new Color(0.9f, 0.9f, 0.85f));
            ball.GetComponent<Renderer>().enabled = false;

            root.AddComponent<NetworkObject>();
            root.AddComponent<RouletteWheel>()
                .Configure(wheel.transform, ball.transform, betWindow: 12f, spinSeconds: 5f);

            (BetKind kind, int number, string label, Color colour)[] spots =
            {
                (BetKind.Straight, 7, "Seven", new Color(0.85f, 0.70f, 0.25f)),
                (BetKind.Red, 0, "Red", new Color(0.62f, 0.13f, 0.13f)),
                (BetKind.Black, 0, "Black", new Color(0.09f, 0.09f, 0.11f)),
                (BetKind.Odd, 0, "Odd", new Color(0.35f, 0.35f, 0.40f)),
                (BetKind.Even, 0, "Even", new Color(0.35f, 0.35f, 0.40f)),
                (BetKind.Low, 0, "Low", new Color(0.28f, 0.34f, 0.46f)),
                (BetKind.High, 0, "High", new Color(0.28f, 0.34f, 0.46f)),
                (BetKind.DozenLow, 0, "Dozen1", new Color(0.24f, 0.42f, 0.38f)),
                (BetKind.DozenMid, 0, "Dozen2", new Color(0.24f, 0.42f, 0.38f)),
                (BetKind.DozenHigh, 0, "Dozen3", new Color(0.24f, 0.42f, 0.38f)),
            };

            for (int i = 0; i < spots.Length; i++)
            {
                (BetKind kind, int number, string label, Color colour) = spots[i];

                float x = 0.1f + i % 5 * 0.62f;
                float z = i < 5 ? 0.38f : -0.38f;

                GameObject square = Block(root.transform, label + "Spot",
                                          new Vector3(x, 0.94f, z), new Vector3(0.55f, 0.06f, 0.6f),
                                          colour, solid: true);

                square.AddComponent<NetworkObject>();
                square.AddComponent<BetSpot>().Configure(kind, number, Chunk);
            }

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, TablePath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[CasinoFactory] Failed to save {TablePath}.");
                return false;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>(), TablePath);
            Debug.Log($"[CasinoFactory] Built {TablePath} with {spots.Length} bet spots.");

            return true;
        }

        static void Paint(GameObject target, Color colour)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = colour };
            target.GetComponent<Renderer>().sharedMaterial = material;
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
