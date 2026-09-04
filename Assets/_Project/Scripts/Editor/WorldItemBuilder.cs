using System.IO;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using FishNet.Component.Transforming;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Builds the one networked prefab every dropped item becomes, and hands it to the catalog.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.WorldItemBuilder.Build
    ///
    /// **One prefab for every item in the game.** A networked prefab has to be registered in FishNet's
    /// spawnable list, identically, on every peer - so a prefab per item would make adding an item a
    /// registration step and a rebuild, which is precisely what #41 spent its effort removing. What
    /// an item looks like on the ground is <see cref="ItemDef.WorldPrefab"/>: an ordinary,
    /// non-networked visual that <see cref="WorldItem"/> parents underneath at run time, and that
    /// falls back to a category-coloured cube while the game is still greybox.
    ///
    /// The physical numbers here are the whole feel of loot on the floor. Light enough to skitter when
    /// kicked, heavy enough not to fly across the island when a car clips it, and with enough drag
    /// that a thrown bag lands roughly where it was aimed rather than sliding for another ten metres.
    /// </summary>
    public static class WorldItemBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs";
        const string PrefabPath = PrefabDir + "/WorldItem.prefab";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";
        const string CatalogPath = "Assets/_Project/Data/Items.asset";

        /// <summary>Half-extent of the pickup box, in metres. Bigger than the visual on purpose.</summary>
        const float PickupRadius = 0.28f;

        public static void Build()
        {
            Directory.CreateDirectory(PrefabDir);

            GameObject root = BuildHierarchy();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[WorldItemBuilder] Failed to save {PrefabPath}.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>());
            AssignToCatalog(saved);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[WorldItemBuilder] Built {PrefabPath}: one networked prefab for all "
                      + $"{Count()} item(s); appearance comes from ItemDef.WorldPrefab.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static GameObject BuildHierarchy()
        {
            var root = new GameObject("WorldItem");

            // A box rather than a sphere: a sphere rolls forever down the island's slopes, and loot
            // that ends up in the sea because somebody dropped it on a hill is not a funny bug.
            var collider = root.AddComponent<BoxCollider>();
            collider.size = Vector3.one * (PickupRadius * 2f);
            collider.center = Vector3.zero;

            var body = root.AddComponent<Rigidbody>();
            body.mass = 2f;
            body.linearDamping = 0.4f;
            body.angularDamping = 1.2f;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            // Continuous, because a thrown item at 9 m/s covers 30 cm in a tick and the ground is a
            // terrain collider - discrete sweeps let it tunnel through and fall out of the world.
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // Aggressive sleeping. Most of these objects spend their whole life motionless in a pile
            // at the base, and a hundred awake rigidbodies is the host's frame budget for nothing.
            body.sleepThreshold = 0.05f;

            var networkObject = root.AddComponent<NetworkObject>();
            SetFields(networkObject, so =>
            {
                // Nobody owns loot. It is spawned by the server, simulated by the server, and taken by
                // whoever gets there first - the entire point of the issue.
                SerializedProperty defaultDespawn = so.FindProperty("_defaultDespawnType");
                if (defaultDespawn != null) defaultDespawn.enumValueIndex = 0;
            });

            var networkTransform = root.AddComponent<NetworkTransform>();
            SetFields(networkTransform, so =>
            {
                // Server-authoritative: the physics runs there and clients interpolate what arrives.
                SerializedProperty clientAuthoritative = so.FindProperty("_clientAuthoritative");
                if (clientAuthoritative != null) clientAuthoritative.boolValue = false;

                SerializedProperty sendToOwner = so.FindProperty("_sendToOwner");
                if (sendToOwner != null) sendToOwner.boolValue = true;

                // Scale never changes on these, so replicating it is pure overhead on an object type
                // that can exist a couple of hundred times over.
                SerializedProperty synchronizeScale = so.FindProperty("_synchronizeScale");
                if (synchronizeScale != null) synchronizeScale.boolValue = false;
            });

            var visualRoot = new GameObject("Visual");
            visualRoot.transform.SetParent(root.transform, false);

            var item = root.AddComponent<WorldItem>();
            item.Configure(visualRoot.transform, body, collider);

            return root;
        }

        static void SetFields(Object target, System.Action<SerializedObject> configure)
        {
            var so = new SerializedObject(target);
            configure(so);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Same reasoning as PlayerPrefabBuilder.RegisterSpawnable; see the note there.</summary>
        static void RegisterSpawnable(NetworkObject networkObject)
        {
            if (networkObject == null)
            {
                Debug.LogError("[WorldItemBuilder] Saved prefab has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[WorldItemBuilder] missing {PrefabObjectsPath}; not registered.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);

            Debug.Log($"[WorldItemBuilder] spawnable prefabs now hold {prefabs.GetObjectCount()} object(s).");
        }

        /// <summary>
        /// The catalog holds the reference, because it is already published globally and already on
        /// every inventory - so anything that needs to drop something can find it without a singleton
        /// component to place in every scene.
        /// </summary>
        static void AssignToCatalog(GameObject prefab)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(CatalogPath);
            if (catalog == null)
            {
                Debug.LogError($"[WorldItemBuilder] missing {CatalogPath}; run ItemFactory.Build first.");
                return;
            }

            // Through a SerializedObject for the same reason ItemFactory writes _items that way:
            // internal does not cross Runtime -> Editor, and this has no business being settable at
            // run time.
            var so = new SerializedObject(catalog);
            so.FindProperty("_worldItemPrefab").objectReferenceValue = prefab;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(catalog);
        }

        static int Count()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(CatalogPath);
            return catalog != null ? catalog.Count : 0;
        }
    }
}
