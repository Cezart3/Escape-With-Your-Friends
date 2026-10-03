using System.Collections.Generic;
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
        internal const string VipDoorPath = PrefabDir + "/VipDoor.prefab";

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

            Remodel();

            // Both cage windows wear their Blender booth: gold and red for chips, green for cash, the
            // word on the sign as well as the colour.
            Dictionary<string, Mesh> booths = SlotFactory.Models(GreyboxBuilder.ModelsPath);
            foreach ((string window, string id) in new[] { (BuyWindowPath, "Cage_Chips"), (CashWindowPath, "Cage_Cash") })
            {
                GameObject cage = PrefabUtility.LoadPrefabContents(window);
                try
                {
                    GreyboxBuilder.Model(cage, id, booths);
                    PrefabUtility.SaveAsPrefabAsset(cage, window);
                }
                finally { PrefabUtility.UnloadPrefabContents(cage); }
            }

            if (Barman(EnsureBar())) built++;
            if (Door()) built++;

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
        /// The VIP room's door (#254): a gold double door you press, never one that opens. See
        /// <see cref="VipDoor"/>. Rebuilt every run, like the slot cabinets: nothing on it is dressed
        /// by hand, and the POI places it by path.
        /// </summary>
        static bool Door()
        {
            Directory.CreateDirectory(PrefabDir);

            var root = new GameObject("VipDoor");
            Block(root.transform, "Slab", new Vector3(0f, 1.2f, 0f), new Vector3(1.6f, 2.4f, 0.15f),
                  new Color(0.85f, 0.70f, 0.25f), solid: true);
            Block(root.transform, "Sign", new Vector3(0f, 2.75f, 0.1f), new Vector3(1.2f, 0.35f, 0.06f),
                  new Color(0.55f, 0.12f, 0.30f), solid: false);

            GreyboxBuilder.Model(root, "VipDoor", SlotFactory.Models(GreyboxBuilder.ModelsPath));

            root.AddComponent<NetworkObject>();
            root.AddComponent<VipDoor>();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, VipDoorPath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[CasinoFactory] Failed to save {VipDoorPath}.");
                return false;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>(), VipDoorPath);
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

        /// <summary>Where the rotor turns on the table: on the felt, at the table's left end.</summary>
        internal static readonly Vector3 WheelAt = new(-1f, 0.94f, 0f);

        /// <summary>A bet square, its collider, and where the first of them sits. Five to a row, two rows.</summary>
        static readonly Vector3 SpotSize = new(0.36f, 0.03f, 0.56f);
        const float SpotLeft = -0.2f, SpotStep = 0.4f, SpotRow = 0.33f, SpotHeight = 0.944f;

        /// <summary>
        /// The table modelled in Blender (#252, tools/art/tables.py), done in place on the saved prefab.
        ///
        /// In place because <see cref="Table"/> never overwrites: the table is a networked prefab with
        /// ten networked bet spots nested in it, and a rebuilt prefab is a new GUID that silently
        /// unhooks it from every scene that places it. Loading its contents, remodelling them and
        /// saving over the same path keeps every id.
        ///
        /// The baize keeps its collider and draws nothing; the model is a child of the root. The
        /// wheel and ball keep their transforms, which <see cref="RouletteWheel"/> turns, and wear
        /// the rotor and the ball. The bet squares move onto the felt - they used to run a metre and
        /// a quarter off the table's end - and wear their own lettered squares.
        /// </summary>
        static void Remodel()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(TablePath) == null) return;

            Dictionary<string, Mesh> meshes = SlotFactory.Models(SlotFactory.TablesPath);
            Material atlas = SlotFactory.Atlas();
            if (meshes == null || atlas == null)
            {
                Debug.LogError($"[CasinoFactory] No {SlotFactory.TablesPath} or slot atlas. Run tools/art/tables.py "
                               + "and SlotFactory.Build first.");
                return;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(TablePath);

            try
            {
                Transform t = root.transform;

                Transform baize = t.Find("Baize");
                Transform kit = baize.Find("Art");
                if (kit != null) Object.DestroyImmediate(kit.gameObject);
                if (baize.TryGetComponent(out Renderer old)) old.enabled = false;

                Transform rim = t.Find("Rim");
                if (rim != null) Object.DestroyImmediate(rim.gameObject);

                Transform model = t.Find("Model");
                if (model == null)
                {
                    model = new GameObject("Model").transform;
                    model.SetParent(t, false);
                }
                Wear(model.gameObject, meshes["Rou_Table"], atlas);

                Transform wheel = t.Find("Wheel");
                Transform zero = wheel.Find("Zero");
                if (zero != null) Object.DestroyImmediate(zero.gameObject);
                wheel.localPosition = WheelAt;
                wheel.localScale = Vector3.one;
                Wear(wheel.gameObject, meshes["Rou_Rotor"], atlas);

                Transform ball = t.Find("Ball");
                ball.localPosition = WheelAt;
                ball.localScale = Vector3.one;
                Transform marker = ball.Find("Marker");
                marker.localPosition = RouletteWheel.BallRest;
                marker.localScale = Vector3.one;
                Wear(marker.gameObject, meshes["Rou_Ball"], atlas);

                BetSpot[] spots = root.GetComponentsInChildren<BetSpot>();
                for (int i = 0; i < spots.Length; i++)
                {
                    Transform spot = spots[i].transform;
                    spot.localPosition = new Vector3(SpotLeft + i % 5 * SpotStep, SpotHeight, i < 5 ? SpotRow : -SpotRow);
                    spot.localScale = Vector3.one;
                    Wear(spot.gameObject, meshes["Rou_Spot_" + spot.name.Replace("Spot", "")], atlas);

                    var box = spot.GetComponent<BoxCollider>();
                    box.center = new Vector3(0f, 0.01f, 0f);
                    box.size = SpotSize;
                }

                PrefabUtility.SaveAsPrefabAsset(root, TablePath);
                Debug.Log($"[CasinoFactory] Remodelled {TablePath}: Blender table, rotor, ball and {spots.Length} squares.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>Puts a mesh on an object on the slots' one material, adding the components it lacks.</summary>
        static void Wear(GameObject target, Mesh mesh, Material material)
        {
            var filter = target.GetComponent<MeshFilter>();
            if (filter == null) filter = target.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            var renderer = target.GetComponent<MeshRenderer>();
            if (renderer == null) renderer = target.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.enabled = true;
            renderer.shadowCastingMode = ShadowCastingMode.On;
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

                // Built once and kept, so the body goes on in place (T10), GUID and all.
                ArtDress.DressPrefab(BarmanPath, "Skin",
                                     barman => CharacterArt.Dress(barman.gameObject, Palette.Named("Dark"), only: 1));
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

            // The people (T10): the players' other body from the castaway's, in a dark band where the
            // hat was.
            CharacterArt.Dress(root, Palette.Named("Dark"), only: 1);

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
            target.GetComponent<Renderer>().sharedMaterial = Palette.For(colour);
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

            cube.GetComponent<Renderer>().sharedMaterial = Palette.For(color);

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
