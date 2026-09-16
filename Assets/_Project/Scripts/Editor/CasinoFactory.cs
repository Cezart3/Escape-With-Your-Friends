using System.IO;
using EscapeWithYourFriends.Casino;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Economy;
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
        internal const string BarmanPath = PrefabDir + "/Barman.prefab";

        const string BarShopPath = "Assets/_Project/Data/Bar.asset";
        const string ItemFolder = "Assets/_Project/Data/Items";

        /// <summary>
        /// What a drink costs. Grog is worth 12 to anybody else, which is the joke: the barman is
        /// the only person on the island charging a markup on something that makes you worse at the
        /// game, and he is never short of customers.
        /// </summary>
        const int GrogPrice = 25;

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

            if (Barman(EnsureBar())) built++;

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

        /// <summary>
        /// The bar's stock: one line, unlimited, paid for in money. #66.
        ///
        /// A second <see cref="ShopDef"/> rather than a line on the trader's, because the trader is
        /// two hundred metres away and the thing being sold is the reason to walk into this room.
        /// It is a <see cref="ShopCounter"/> rather than a new kind of NPC for the lazier reason:
        /// buying, stock, restocking, the reach check and the trade UI all already exist, and a
        /// barman is a shopkeeper with one thing on the shelf.
        ///
        /// **Money, not chips.** Nothing outside the cage takes a payment in chips (#63), and that
        /// line is worth more than the convenience of paying for a drink with your winnings: it is
        /// what keeps "chips buy nothing real" true, which is the sentence #67's compliance
        /// checklist has to be able to say.
        /// </summary>
        static ShopDef EnsureBar()
        {
            var shop = AssetDatabase.LoadAssetAtPath<ShopDef>(BarShopPath);
            bool rebuild = CommandLine.HasFlag("-rebuildShop");

            if (shop != null && !rebuild) return shop;

            bool fresh = shop == null;
            if (fresh) shop = ScriptableObject.CreateInstance<ShopDef>();

            var grog = AssetDatabase.LoadAssetAtPath<ItemDef>($"{ItemFolder}/grog.asset");
            if (grog == null)
                Debug.LogError("[CasinoFactory] the bar has nothing to sell: no grog item. Run "
                               + "ItemFactory.Build first.");

            var so = new SerializedObject(shop);
            so.FindProperty("_id").stringValue = "casino_bar";
            so.FindProperty("_displayName").stringValue = "the Barman";

            // He buys a bottle back for a quarter of its worth, which is three. Nobody has ever
            // done this twice.
            so.FindProperty("_buyBackFraction").floatValue = 0.25f;
            so.FindProperty("_restockSeconds").floatValue = 60f;

            SerializedProperty offers = so.FindProperty("_offers");
            offers.arraySize = 1;

            SerializedProperty entry = offers.GetArrayElementAtIndex(0);
            entry.FindPropertyRelative("Item").objectReferenceValue = grog;
            entry.FindPropertyRelative("Price").intValue = GrogPrice;
            entry.FindPropertyRelative("Stock").intValue = -1;

            so.ApplyModifiedPropertiesWithoutUndo();

            if (fresh)
            {
                AssetDatabase.CreateAsset(shop, BarShopPath);
                Debug.Log($"[CasinoFactory] Created {BarShopPath}: grog at {GrogPrice}.");
            }
            else
            {
                EditorUtility.SetDirty(shop);
                Debug.Log($"[CasinoFactory] Rebuilt {BarShopPath} from code (-rebuildShop).");
            }

            return shop;
        }

        /// <summary>
        /// The man himself: seven boxes and a shop. He stands in the gap between the bar and the
        /// back wall that <c>GreyboxBuilder</c> leaves at <c>BarNpcStand</c>, facing the door.
        ///
        /// Only the torso has a collider, so the crosshair finding him is unambiguous and walking
        /// into the bar does not shove him through the wall.
        /// </summary>
        static bool Barman(ShopDef shop)
        {
            Directory.CreateDirectory(PrefabDir);

            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(BarmanPath);
            if (existing != null)
            {
                // Same rule as ShopFactory: the stock link is structure, not balance, so it is
                // re-applied even to a prefab somebody has already dressed.
                var counter = existing.GetComponent<ShopCounter>();
                if (counter != null)
                {
                    var so = new SerializedObject(counter);
                    so.FindProperty("_shop").objectReferenceValue = shop;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.SavePrefabAsset(existing);
                }

                return false;
            }

            var root = new GameObject("Barman");

            var skin = new Color(0.72f, 0.55f, 0.42f);
            var shirt = new Color(0.90f, 0.88f, 0.82f);
            var apron = new Color(0.30f, 0.28f, 0.34f);

            Block(root.transform, "Torso", new Vector3(0f, 1.25f, 0f), new Vector3(0.6f, 0.75f, 0.3f),
                  shirt, solid: true);
            Block(root.transform, "Legs", new Vector3(0f, 0.45f, 0f), new Vector3(0.5f, 0.9f, 0.28f),
                  apron, solid: false);
            Block(root.transform, "Apron", new Vector3(0f, 1.05f, 0.17f), new Vector3(0.52f, 0.8f, 0.05f),
                  apron, solid: false);
            Block(root.transform, "Arm.Left", new Vector3(-0.38f, 1.25f, 0f),
                  new Vector3(0.14f, 0.7f, 0.22f), shirt, solid: false);
            Block(root.transform, "Arm.Right", new Vector3(0.38f, 1.25f, 0f),
                  new Vector3(0.14f, 0.7f, 0.22f), shirt, solid: false);
            Block(root.transform, "Head", new Vector3(0f, 1.78f, 0f), new Vector3(0.28f, 0.32f, 0.28f),
                  skin, solid: false);

            // The hat is the whole characterisation budget.
            Block(root.transform, "Hat", new Vector3(0f, 1.97f, 0f), new Vector3(0.38f, 0.08f, 0.38f),
                  new Color(0.20f, 0.18f, 0.22f), solid: false);

            root.AddComponent<NetworkObject>();
            root.AddComponent<ShopCounter>().Configure(shop);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, BarmanPath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[CasinoFactory] Failed to save {BarmanPath}.");
                return false;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>(), BarmanPath);
            Debug.Log($"[CasinoFactory] Built {BarmanPath}: grog at {GrogPrice}, paid in money.");

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
