using System.IO;
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
    /// The trader's stock and the counter they stand behind.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.ShopFactory.Build
    ///
    /// Prices are set here rather than derived from <see cref="ItemDef.Value"/> because a shop price
    /// and an item's worth are two different decisions: the value is what the thing is worth, and the
    /// price is what the trader thinks they can get for it. Every price is comfortably above what the
    /// shop pays back, which is the spread that makes selling loot a living rather than a loop.
    ///
    /// The limited lines are the interesting ones. There is one hatchet, one fishing rod and four boat
    /// parts on the shelf, restocking one every ninety seconds, so "who gets it" is a conversation
    /// four players have to have - which is also exactly the concurrency the acceptance is about.
    ///
    /// Same rule as the other factories: creates what does not exist, never overwrites what does.
    /// </summary>
    public static class ShopFactory
    {
        const string ShopPath = "Assets/_Project/Data/Shop.asset";
        const string PrefabDir = "Assets/_Project/Prefabs/Stations";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";
        const string ItemFolder = "Assets/_Project/Data/Items";

        internal const string CounterPath = PrefabDir + "/ShopCounter.prefab";

        static readonly (string Item, int Price, int Stock)[] Stock =
        {
            // Materials, always available. Cheap enough that buying them is a shortcut rather than a
            // strategy - the island gives all of these away to anybody willing to walk.
            ("rope", 10, -1),
            ("plank", 8, -1),
            ("cloth", 8, -1),
            ("flint", 6, -1),
            ("scrap_metal", 18, -1),
            ("empty_bottle", 10, -1),

            // Made things. Craftable too, so the price is the tax on not having walked to the bench.
            ("torch", 25, 5),
            ("bandage", 35, 6),
            ("knife", 60, 2),
            ("fishing_rod", 80, 1),
            ("hatchet", 90, 1),

            // The reason anybody is saving. Worth nothing to the trader, so it cannot be flipped back.
            ("boat_part", 400, 4),
        };

        public static void Build()
        {
            ShopDef shop = EnsureShop();
            bool built = EnsureCounter(shop);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ShopFactory] {shop.Count} line(s) on the shelf, paying "
                      + $"{shop.BuyBackFraction:P0} of value, restocking every "
                      + $"{shop.RestockSeconds:F0}s.");

            foreach (ShopDef.Offer offer in shop.Offers)
                Debug.Log($"[ShopFactory]   {offer.Item.Id,-14} buy {offer.Price,4}   sell back "
                          + $"{shop.PriceFor(offer.Item),3}"
                          + (offer.Unlimited ? "   unlimited" : $"   {offer.Stock} on the shelf"));

            if (Application.isBatchMode) EditorApplication.Exit(built ? 0 : 0);
        }

        static ShopDef EnsureShop()
        {
            var shop = AssetDatabase.LoadAssetAtPath<ShopDef>(ShopPath);
            if (shop != null) return shop;

            shop = ScriptableObject.CreateInstance<ShopDef>();

            var so = new SerializedObject(shop);
            so.FindProperty("_id").stringValue = "island_trader";
            so.FindProperty("_displayName").stringValue = "the Trader";
            so.FindProperty("_buyBackFraction").floatValue = 0.5f;
            so.FindProperty("_restockSeconds").floatValue = 90f;

            SerializedProperty offers = so.FindProperty("_offers");
            offers.arraySize = Stock.Length;

            for (int i = 0; i < Stock.Length; i++)
            {
                (string id, int price, int stock) = Stock[i];

                var item = AssetDatabase.LoadAssetAtPath<ItemDef>($"{ItemFolder}/{id}.asset");
                if (item == null)
                    Debug.LogError($"[ShopFactory] The shelf wants '{id}', which does not exist. Run "
                                   + "ItemFactory.Build first.");

                SerializedProperty entry = offers.GetArrayElementAtIndex(i);
                entry.FindPropertyRelative("Item").objectReferenceValue = item;
                entry.FindPropertyRelative("Price").intValue = price;
                entry.FindPropertyRelative("Stock").intValue = stock;
            }

            so.ApplyModifiedPropertiesWithoutUndo();

            Directory.CreateDirectory(Path.GetDirectoryName(ShopPath));
            AssetDatabase.CreateAsset(shop, ShopPath);
            Debug.Log($"[ShopFactory] Created {ShopPath}.");

            return shop;
        }

        static bool EnsureCounter(ShopDef shop)
        {
            Directory.CreateDirectory(PrefabDir);

            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(CounterPath);
            if (existing != null)
            {
                // The stock link is re-applied even on an existing prefab, the same rule the buff
                // factory uses: balance belongs to whoever tuned it, structural links do not.
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

            var root = new GameObject("ShopCounter");

            Block(root.transform, "Counter", new Vector3(0f, 0.55f, 0f), new Vector3(2.6f, 1.1f, 0.9f),
                  new Color(0.50f, 0.36f, 0.24f), solid: true);
            Block(root.transform, "Top", new Vector3(0f, 1.14f, 0f), new Vector3(2.8f, 0.1f, 1.1f),
                  new Color(0.62f, 0.47f, 0.30f), solid: false);
            Block(root.transform, "Shelf", new Vector3(0f, 1.9f, -0.6f), new Vector3(2.6f, 0.08f, 0.4f),
                  new Color(0.45f, 0.33f, 0.22f), solid: false);
            Block(root.transform, "Post", new Vector3(-1.25f, 1.5f, -0.6f),
                  new Vector3(0.12f, 1.6f, 0.12f), new Color(0.40f, 0.29f, 0.19f), solid: false);
            Block(root.transform, "PostRight", new Vector3(1.25f, 1.5f, -0.6f),
                  new Vector3(0.12f, 1.6f, 0.12f), new Color(0.40f, 0.29f, 0.19f), solid: false);
            Block(root.transform, "Sign", new Vector3(0f, 2.3f, -0.6f), new Vector3(1.6f, 0.5f, 0.06f),
                  new Color(0.85f, 0.78f, 0.45f), solid: false);

            root.AddComponent<NetworkObject>();

            var component = root.AddComponent<ShopCounter>();
            component.Configure(shop);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, CounterPath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[ShopFactory] Failed to save {CounterPath}.");
                return false;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>());
            Debug.Log($"[ShopFactory] Built {CounterPath}.");

            return true;
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
                Debug.LogError($"[ShopFactory] {CounterPath} has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[ShopFactory] missing {PrefabObjectsPath}; the counter cannot spawn.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);
        }

        /// <summary>The shop asset, for anything at bake time that needs it.</summary>
        internal static ShopDef Shop() => EnsureShop();
    }
}
