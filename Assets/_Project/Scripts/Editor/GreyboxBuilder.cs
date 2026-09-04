using System.Collections.Generic;
using System.IO;
using EscapeWithYourFriends.World;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The six landmarks, as boxes.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.GreyboxBuilder.BuildAll
    ///
    /// Blockouts exist so the island has structure and the economy loop can be walked through before
    /// any art exists. They are deliberately crude - primitives, five colours, no detail - because
    /// the moment a greybox starts looking finished it stops getting replaced, and everything here is
    /// meant to be thrown away in M8.
    ///
    /// What is not crude is the layout: each one is built around the thing a player does there. The
    /// shop has a counter you stand at, the casino has a table you gather round, the village has an
    /// open middle where a fight happens and huts to break line of sight. Those shapes are the part
    /// worth testing now, and they survive the art pass.
    /// </summary>
    public static class GreyboxBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs/World";
        const string MaterialDir = "Assets/_Project/Art/Greybox";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        // The whole palette. Five colours is enough to read a blockout and few enough that nobody
        // mistakes it for a look.
        static readonly (string name, Color colour, float smoothness)[] Palette =
        {
            ("Wood", new Color(0.42f, 0.30f, 0.19f), 0.12f),
            ("Stone", new Color(0.46f, 0.46f, 0.44f), 0.08f),
            ("Canvas", new Color(0.74f, 0.68f, 0.52f), 0.06f),
            ("Metal", new Color(0.55f, 0.57f, 0.60f), 0.55f),
            ("Accent", new Color(0.72f, 0.28f, 0.22f), 0.20f)
        };

        static readonly Dictionary<string, Material> Materials = new();

        [MenuItem("EWYF/Build greybox landmarks")]
        public static void BuildAll()
        {
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(MaterialDir);
            Materials.Clear();

            var built = new List<string>();

            built.Add(Save(BuildBaseCamp(), "BaseCamp"));
            built.Add(Save(BuildShop(), "Shop"));
            built.Add(Save(BuildCasino(), "Casino"));
            built.Add(Save(BuildVillage(), "NativeVillage"));
            built.Add(Save(BuildWreck(), "Wreck"));
            built.Add(Save(BuildCave(), "Cave"));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[GreyboxBuilder] Built {built.Count} landmarks: {string.Join(", ", built)}.");
        }

        // ---------------------------------------------------------------- the six

        /// <summary>
        /// Home. A shelter you spawn under, a crate to leave things in, a bench to make things at and
        /// a fire in the middle, arranged in a loose ring so four players landing at once do not all
        /// stand in the same box. The revive machine is placed separately, as its own POI, because it
        /// is a networked machine with behaviour rather than scenery.
        /// </summary>
        static GameObject BuildBaseCamp()
        {
            GameObject root = Root("BaseCamp", "Base Camp",
                                   "Where you wake up. Storage, a crafting bench, and the machine that "
                                   + "puts your friends back together.", radius: 18f, hostile: false);

            // Shelter: a canvas roof on four posts, open on every side so it never traps anyone.
            Box(root, "Shelter.Roof", "Canvas", new Vector3(-4f, 2.6f, 0f), new Vector3(5f, 0.15f, 5f));
            for (int i = 0; i < 4; i++)
            {
                float px = -4f + (i % 2 == 0 ? -2.2f : 2.2f);
                float pz = i < 2 ? -2.2f : 2.2f;
                Box(root, $"Shelter.Post{i}", "Wood", new Vector3(px, 1.3f, pz), new Vector3(0.2f, 2.6f, 0.2f));
            }

            // Two sleeping mats under it, purely so the shelter reads as somewhere you live.
            Box(root, "Shelter.Mat0", "Canvas", new Vector3(-5.2f, 0.08f, 0f), new Vector3(0.9f, 0.15f, 2f), solid: false);
            Box(root, "Shelter.Mat1", "Canvas", new Vector3(-2.8f, 0.08f, 0f), new Vector3(0.9f, 0.15f, 2f), solid: false);

            // Storage, at a height you can see into. Interaction comes with the inventory in M3; the
            // box is here now so the camp has a shape to test.
            Box(root, "Storage", "Wood", new Vector3(3.5f, 0.55f, -3f), new Vector3(2.2f, 1.1f, 1.2f));
            Box(root, "Storage.Lid", "Wood", new Vector3(3.5f, 1.18f, -3f), new Vector3(2.3f, 0.16f, 1.3f), solid: false);

            // Crafting bench: a table with a vice-sized lump on it, so it is not just another crate.
            Box(root, "Bench", "Wood", new Vector3(3.5f, 0.9f, 2.5f), new Vector3(2.6f, 0.15f, 1.1f));
            for (int i = 0; i < 4; i++)
            {
                float px = 3.5f + (i % 2 == 0 ? -1.1f : 1.1f);
                float pz = 2.5f + (i < 2 ? -0.4f : 0.4f);
                Box(root, $"Bench.Leg{i}", "Wood", new Vector3(px, 0.45f, pz), new Vector3(0.12f, 0.9f, 0.12f), solid: false);
            }
            Box(root, "Bench.Vice", "Metal", new Vector3(4.2f, 1.1f, 2.5f), new Vector3(0.4f, 0.25f, 0.4f), solid: false);

            // The fire, in the middle, where everyone ends up standing.
            Cylinder(root, "Fire.Ring", "Stone", new Vector3(0f, 0.12f, 0f), new Vector3(2f, 0.12f, 2f), solid: false);
            Box(root, "Fire.Logs", "Wood", new Vector3(0f, 0.3f, 0f), new Vector3(0.9f, 0.3f, 0.9f), solid: false);

            return root;
        }

        /// <summary>
        /// The shop. A counter you stand at from outside, so the trade is a gesture rather than a menu
        /// that opens when you walk through a door. The NPC stands behind it in M4.
        /// </summary>
        static GameObject BuildShop()
        {
            GameObject root = Root("Shop", "Trading Post",
                                   "Sell what you find, buy weapons and vehicle upgrades.",
                                   radius: 10f, hostile: false);

            Box(root, "Hut", "Wood", new Vector3(0f, 1.4f, -1.6f), new Vector3(6f, 2.8f, 3.2f));
            Box(root, "Roof", "Canvas", new Vector3(0f, 3f, -1.4f), new Vector3(7f, 0.2f, 4.4f));

            // The counter, and the gap behind it that the shopkeeper occupies.
            Box(root, "Counter", "Wood", new Vector3(0f, 1f, 0.6f), new Vector3(5f, 0.2f, 0.9f));
            Box(root, "Counter.Front", "Wood", new Vector3(0f, 0.5f, 1f), new Vector3(5f, 1f, 0.15f));

            Box(root, "Sign", "Accent", new Vector3(0f, 3.5f, 0.6f), new Vector3(2.4f, 0.9f, 0.12f), solid: false);

            // A rack of things for sale, so it reads as a shop from a distance and not as a hut.
            for (int i = 0; i < 3; i++)
                Box(root, $"Stock{i}", "Metal", new Vector3(-1.6f + i * 1.6f, 1.35f, -0.2f),
                    new Vector3(0.5f, 0.5f, 0.5f), solid: false);

            Empty(root, "NpcStand", new Vector3(0f, 0f, -0.4f));
            return root;
        }

        /// <summary>
        /// The casino. A round table in the middle with room for four around it, and a bar along one
        /// wall where the drink NPC stands. The roulette and the alcohol buff are M6; the shape is
        /// what has to be right now, because "four players crowding one table" is the whole scene.
        /// </summary>
        static GameObject BuildCasino()
        {
            GameObject root = Root("Casino", "The Shack",
                                   "Roulette, terrible decisions, and a man who will sell you a drink.",
                                   radius: 12f, hostile: false);

            // Three walls and an open front: an interior you can see into, with no door to get stuck in.
            Box(root, "Wall.Back", "Wood", new Vector3(0f, 1.6f, -4f), new Vector3(9f, 3.2f, 0.3f));
            Box(root, "Wall.Left", "Wood", new Vector3(-4.4f, 1.6f, -1f), new Vector3(0.3f, 3.2f, 6.2f));
            Box(root, "Wall.Right", "Wood", new Vector3(4.4f, 1.6f, -1f), new Vector3(0.3f, 3.2f, 6.2f));
            Box(root, "Roof", "Canvas", new Vector3(0f, 3.3f, -1f), new Vector3(9.6f, 0.2f, 7f));

            Cylinder(root, "Table", "Accent", new Vector3(0f, 0.9f, -0.6f), new Vector3(3.2f, 0.1f, 3.2f));
            Cylinder(root, "Table.Base", "Wood", new Vector3(0f, 0.45f, -0.6f), new Vector3(0.8f, 0.9f, 0.8f), solid: false);
            Cylinder(root, "Wheel", "Metal", new Vector3(0f, 1f, -0.6f), new Vector3(1.4f, 0.12f, 1.4f), solid: false);

            Box(root, "Bar", "Wood", new Vector3(-2.6f, 1f, -3.2f), new Vector3(3f, 0.2f, 0.8f));
            Box(root, "Bar.Front", "Wood", new Vector3(-2.6f, 0.5f, -2.9f), new Vector3(3f, 1f, 0.15f));

            Empty(root, "TableSeat", new Vector3(0f, 0f, 1.6f));
            Empty(root, "BarNpcStand", new Vector3(-2.6f, 0f, -3.7f));
            return root;
        }

        /// <summary>
        /// The native village. Huts around an open middle, which is the fight: cover to break line of
        /// sight, and nowhere to stand that is safe from all of it. The totem is the thing you can see
        /// over the trees, so the village is findable without a map.
        /// </summary>
        static GameObject BuildVillage()
        {
            GameObject root = Root("NativeVillage", "Native Village",
                                   "They live here and they do not want you to. Food, ammo, and the "
                                   + "place your friends end up if the natives carry them off.",
                                   radius: 22f, hostile: true);

            for (int i = 0; i < 5; i++)
            {
                float angle = i * Mathf.PI * 2f / 5f;
                var centre = new Vector3(Mathf.Cos(angle) * 11f, 0f, Mathf.Sin(angle) * 11f);

                Box(root, $"Hut{i}", "Wood", centre + new Vector3(0f, 1.3f, 0f), new Vector3(4f, 2.6f, 4f));
                Box(root, $"Hut{i}.Roof", "Canvas", centre + new Vector3(0f, 2.9f, 0f), new Vector3(5f, 0.4f, 5f), solid: false);
            }

            // The totem, tall enough to clear the canopy at 320m draw distance.
            Box(root, "Totem", "Wood", new Vector3(0f, 4f, 0f), new Vector3(0.8f, 8f, 0.8f));
            Box(root, "Totem.Arms", "Accent", new Vector3(0f, 6.6f, 0f), new Vector3(3.2f, 0.5f, 0.5f), solid: false);

            Cylinder(root, "Fire", "Stone", new Vector3(0f, 0.12f, 3f), new Vector3(2.4f, 0.12f, 2.4f), solid: false);

            // Where the prison goes in #108. Marked now so the layout does not have to change later.
            Empty(root, "PrisonSite", new Vector3(0f, 0f, -6f));
            return root;
        }

        /// <summary>
        /// The wreck. Half a hull, tipped over, with the mast down. It is the reason there is a
        /// shipwright's worth of scrap on this island, and it is where the boat parts come from in M5.
        /// Deliberately on the tideline, so it is the first landmark seen from the water.
        /// </summary>
        static GameObject BuildWreck()
        {
            GameObject root = Root("Wreck", "The Wreck",
                                   "What you arrived on. Scrap, rope, and the first parts of a boat.",
                                   radius: 14f, hostile: false);

            GameObject hull = Box(root, "Hull", "Wood", new Vector3(0f, 1.6f, 0f), new Vector3(4.5f, 3f, 14f));
            hull.transform.localRotation = Quaternion.Euler(0f, 0f, 28f);

            GameObject deck = Box(root, "Deck", "Wood", new Vector3(-0.6f, 3f, 1f), new Vector3(4f, 0.2f, 8f), solid: false);
            deck.transform.localRotation = Quaternion.Euler(0f, 0f, 28f);

            GameObject mast = Box(root, "Mast", "Wood", new Vector3(4f, 0.8f, -2f), new Vector3(0.5f, 0.5f, 11f));
            mast.transform.localRotation = Quaternion.Euler(0f, 34f, 0f);

            for (int i = 0; i < 4; i++)
                Box(root, $"Debris{i}", "Metal", new Vector3(-5f + i * 2.4f, 0.3f, 6f + (i % 2) * 2f),
                    new Vector3(1.2f, 0.6f, 1.2f), solid: false);

            return root;
        }

        /// <summary>
        /// The cave. A mound with a mouth in it and a room behind, which is enough to be shelter at
        /// night and a place to put ore in M3. Built as boxes rather than as a hollowed mesh because a
        /// greybox cave that needs a mesh is a cave that will not get rebuilt when the layout changes.
        /// </summary>
        static GameObject BuildCave()
        {
            GameObject root = Root("Cave", "The Cave",
                                   "Out of the rain, out of the dark, and something worth mining in the back.",
                                   radius: 12f, hostile: false);

            // The mound, as two slabs either side of a gap and a lintel over it: a doorway, not a wall.
            Box(root, "Rock.Left", "Stone", new Vector3(-4f, 2.5f, 0f), new Vector3(5f, 5f, 8f));
            Box(root, "Rock.Right", "Stone", new Vector3(4f, 2.5f, 0f), new Vector3(5f, 5f, 8f));
            Box(root, "Rock.Lintel", "Stone", new Vector3(0f, 4.2f, 0f), new Vector3(3.2f, 1.6f, 8f));
            Box(root, "Rock.Back", "Stone", new Vector3(0f, 2.5f, -5.5f), new Vector3(13f, 5f, 3f));

            // Floor and ceiling of the room itself, so it is a space rather than a slot.
            Box(root, "Room.Floor", "Stone", new Vector3(0f, -0.1f, -2f), new Vector3(6f, 0.2f, 6f));
            Box(root, "Room.Ceiling", "Stone", new Vector3(0f, 3.6f, -2f), new Vector3(6f, 0.4f, 6f));

            Box(root, "Ore", "Accent", new Vector3(1.6f, 0.6f, -4f), new Vector3(1.2f, 1.2f, 1.2f), solid: false);

            Empty(root, "Shelter", new Vector3(0f, 0f, -2f));
            return root;
        }

        // ---------------------------------------------------------------- plumbing

        static GameObject Root(string id, string displayName, string purpose, float radius, bool hostile)
        {
            var root = new GameObject(id);
            root.AddComponent<NetworkObject>();

            var landmark = root.AddComponent<Landmark>();
            landmark.Id = id;
            landmark.DisplayName = displayName;
            landmark.Purpose = purpose;
            landmark.Radius = radius;
            landmark.Hostile = hostile;

            return root;
        }

        static GameObject Box(GameObject root, string name, string material, Vector3 position,
                              Vector3 scale, bool solid = true)
            => Piece(root, name, PrimitiveType.Cube, material, position, scale, solid);

        static GameObject Cylinder(GameObject root, string name, string material, Vector3 position,
                                   Vector3 scale, bool solid = true)
            => Piece(root, name, PrimitiveType.Cylinder, material, position, scale, solid);

        static GameObject Piece(GameObject root, string name, PrimitiveType shape, string material,
                                Vector3 position, Vector3 scale, bool solid)
        {
            GameObject go = GameObject.CreatePrimitive(shape);
            go.name = name;
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = position;

            // Unity's cylinder is two units tall, so a scale of 1 is a two-metre column. Halving the
            // vertical scale makes every number in this file a metre, which is worth the one line.
            go.transform.localScale = shape == PrimitiveType.Cylinder
                ? new Vector3(scale.x, scale.y * 0.5f, scale.z)
                : scale;

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sharedMaterial = EnsureMaterial(material);

            // Decoration keeps no collider. Every collider on a landmark is something a ragdoll can
            // get wedged behind, and a blockout has no business generating those by accident.
            //
            // It casts no shadow either. A sign, a crate and four bench legs are five extra draws in
            // the shadow pass for silhouettes nobody can pick out from two metres away, and the
            // shadow pass is where an integrated GPU spends its afternoon. The pieces that make the
            // building's shape - the ones with colliders - still cast.
            if (!solid)
            {
                Collider existing = go.GetComponent<Collider>();
                if (existing != null) Object.DestroyImmediate(existing);

                if (renderer != null) renderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            return go;
        }

        static void Empty(GameObject root, string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = position;
        }

        static Material EnsureMaterial(string name)
        {
            if (Materials.TryGetValue(name, out Material cached)) return cached;

            string path = $"{MaterialDir}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                material = new Material(shader) { name = name };

                foreach ((string entry, Color colour, float smoothness) in Palette)
                {
                    if (entry != name) continue;

                    material.color = colour;
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
                    if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
                    if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
                }

                material.enableInstancing = true;
                AssetDatabase.CreateAsset(material, path);
                Debug.Log($"[GreyboxBuilder] Generated {path}.");
            }

            Materials[name] = material;
            return material;
        }

        static string Save(GameObject root, string name)
        {
            string path = $"{PrefabDir}/{name}.prefab";

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[GreyboxBuilder] Failed to save {path}.");
                return name + " (FAILED)";
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>(), path);

            int colliders = saved.GetComponentsInChildren<Collider>(true).Length;
            int renderers = saved.GetComponentsInChildren<MeshRenderer>(true).Length;
            return $"{name} ({renderers} parts, {colliders} solid)";
        }

        /// <summary>Same reasoning as PlayerPrefabBuilder.RegisterSpawnable; see the note there.</summary>
        static void RegisterSpawnable(NetworkObject networkObject, string path)
        {
            if (networkObject == null)
            {
                Debug.LogError($"[GreyboxBuilder] {path} has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[GreyboxBuilder] missing {PrefabObjectsPath}; {path} cannot be spawned.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);
        }
    }
}
