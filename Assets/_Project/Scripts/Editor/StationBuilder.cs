using System.IO;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.World;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The three greybox crafting stations, as networked prefabs.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.StationBuilder.Build
    ///
    /// A bench, a campfire and a water filter. Unlike a dropped item there is one prefab *per* station
    /// - three of them rather than one driven by data - because they are not interchangeable: they
    /// have different shapes, different radii, and the campfire has a heat source the others do not.
    /// Three prefabs registered once is cheaper than a data-driven station that has to describe all
    /// three, and there will never be twenty of these.
    ///
    /// None of them has a collider that blocks movement except the bench. Walking through a campfire
    /// should be possible, and being wedged against your own water filter is not a game mechanic.
    /// </summary>
    public static class StationBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs/Stations";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        internal const string BenchPath = PrefabDir + "/CraftingBench.prefab";
        internal const string CampfirePath = PrefabDir + "/Campfire.prefab";
        internal const string FilterPath = PrefabDir + "/WaterFilter.prefab";

        public static void Build()
        {
            Directory.CreateDirectory(PrefabDir);

            int built = 0;

            if (Save(Bench(), BenchPath)) built++;
            if (Save(Campfire(), CampfirePath)) built++;
            if (Save(Filter(), FilterPath)) built++;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[StationBuilder] Built {built} station prefab(s) in {PrefabDir}.");

            if (Application.isBatchMode) EditorApplication.Exit(built == 3 ? 0 : 1);
        }

        // ---------------------------------------------------------------- the three

        /// <summary>
        /// A workbench: a solid top on two legs. The only one of the three that blocks you, because a
        /// bench you can walk through does not read as a bench.
        /// </summary>
        static GameObject Bench()
        {
            var root = new GameObject("CraftingBench");

            Block(root.transform, "Top", new Vector3(0f, 0.9f, 0f), new Vector3(2.2f, 0.15f, 1.0f),
                  new Color(0.55f, 0.40f, 0.26f), solid: true);
            Block(root.transform, "LegLeft", new Vector3(-0.95f, 0.45f, 0f),
                  new Vector3(0.15f, 0.9f, 0.9f), new Color(0.42f, 0.30f, 0.20f), solid: false);
            Block(root.transform, "LegRight", new Vector3(0.95f, 0.45f, 0f),
                  new Vector3(0.15f, 0.9f, 0.9f), new Color(0.42f, 0.30f, 0.20f), solid: false);
            Block(root.transform, "Vice", new Vector3(0.7f, 1.05f, 0.2f),
                  new Vector3(0.3f, 0.2f, 0.25f), new Color(0.45f, 0.47f, 0.50f), solid: false);

            Networked(root, CraftStation.Bench, radius: 4.5f);
            return root;
        }

        /// <summary>
        /// A ring of stones with logs in it. Warm, walk-through, and the reason night is survivable.
        /// </summary>
        static GameObject Campfire()
        {
            var root = new GameObject("Campfire");

            Block(root.transform, "Pit", new Vector3(0f, 0.08f, 0f), new Vector3(1.5f, 0.16f, 1.5f),
                  new Color(0.30f, 0.28f, 0.26f), solid: false);

            // Two crossed logs, so it reads as a fire rather than a paving slab from any angle.
            Log(root.transform, "LogA", 35f);
            Log(root.transform, "LogB", -40f);

            GameObject flame = Block(root.transform, "Flame", new Vector3(0f, 0.55f, 0f),
                                     new Vector3(0.5f, 0.7f, 0.5f),
                                     new Color(1.0f, 0.55f, 0.15f), solid: false);

            // Emissive so it reads at night, which is the only time anybody will look at it. A real
            // light comes with the art pass; a point light per fire is a shadow-caster budget question
            // and this is a greybox.
            var renderer = flame.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial.EnableKeyword("_EMISSION");
                renderer.sharedMaterial.SetColor("_EmissionColor", new Color(1.6f, 0.7f, 0.2f));
            }

            Networked(root, CraftStation.Fire, radius: 4f);

            var heat = root.AddComponent<HeatSource>();
            heat.Configure(radius: 5f, warmthPerSecond: 12f);

            return root;
        }

        /// <summary>A barrel with a spout. Turns an empty bottle back into a full one.</summary>
        static GameObject Filter()
        {
            var root = new GameObject("WaterFilter");

            Block(root.transform, "Barrel", new Vector3(0f, 0.6f, 0f), new Vector3(0.9f, 1.2f, 0.9f),
                  new Color(0.35f, 0.45f, 0.42f), solid: false);
            Block(root.transform, "Lid", new Vector3(0f, 1.25f, 0f), new Vector3(1.0f, 0.1f, 1.0f),
                  new Color(0.50f, 0.52f, 0.50f), solid: false);
            Block(root.transform, "Spout", new Vector3(0f, 0.35f, 0.55f), new Vector3(0.12f, 0.12f, 0.3f),
                  new Color(0.55f, 0.57f, 0.60f), solid: false);

            Networked(root, CraftStation.Filter, radius: 3.5f);
            return root;
        }

        // ---------------------------------------------------------------- shared

        static void Networked(GameObject root, CraftStation kind, float radius)
        {
            root.AddComponent<NetworkObject>();

            // No NetworkTransform: none of these ever moves. The spawn message carries the position
            // and that is the last word on it - replicating a transform that will never change is
            // rent on nothing, and the Revive Machine already makes the same call.
            var station = root.AddComponent<CraftingStation>();
            station.Configure(kind, radius);
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

                // Decoration does not cast shadows, the same rule GreyboxBuilder applies: four fires
                // burning in a camp is four shadow casters for something nobody looks at.
                var renderer = cube.GetComponent<Renderer>();
                if (renderer != null) renderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = color };
            cube.GetComponent<Renderer>().sharedMaterial = material;

            return cube;
        }

        static void Log(Transform parent, string name, float yaw)
        {
            GameObject log = Block(parent, name, new Vector3(0f, 0.22f, 0f),
                                   new Vector3(0.18f, 0.18f, 1.3f),
                                   new Color(0.38f, 0.26f, 0.16f), solid: false);

            log.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        }

        static bool Save(GameObject root, string path)
        {
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[StationBuilder] Failed to save {path}.");
                return false;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>(), path);
            return true;
        }

        /// <summary>Same reasoning as PlayerPrefabBuilder.RegisterSpawnable; see the note there.</summary>
        static void RegisterSpawnable(NetworkObject networkObject, string path)
        {
            if (networkObject == null)
            {
                Debug.LogError($"[StationBuilder] {path} has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[StationBuilder] missing {PrefabObjectsPath}; {path} cannot be spawned.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);
        }
    }
}
