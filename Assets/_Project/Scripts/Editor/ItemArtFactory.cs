using System.Collections.Generic;
using System.IO;
using EscapeWithYourFriends.Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// What every item looks like on the ground, and its icon in the bag (#206, #208).
    ///
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod
    ///     EscapeWithYourFriends.EditorTools.ItemArtFactory.Build
    ///
    /// Not -nographics: the icons are rendered by a real camera.
    ///
    /// A boar used to drop two yellow cubes. Items with a Kenney model wear it, fitted to a size in
    /// metres. Items no kit has are put together from a few primitives in palette colours: a coil of
    /// rope, a box of rounds, a pearl. They are cheap, but they read as the thing, and that is the
    /// bar. A world prefab somebody else chose (the guns) is never replaced. One this factory made
    /// is rebuilt on every run.
    ///
    /// Then every item with a world prefab, the guns included, is photographed into a 128px icon.
    /// A new item gets its icon by existing.
    /// </summary>
    public static class ItemArtFactory
    {
        const string ItemFolder = "Assets/_Project/Data/Items";
        const string PrefabFolder = "Assets/_Project/Prefabs/Items";
        const string IconFolder = "Assets/_Project/Art/Icons";
        const int IconSize = 128;

        /// <summary>Kenney model id and the longest side in metres.</summary>
        static readonly Dictionary<string, (string Model, float Size)> Kit = new()
        {
            ["meat_raw"] = ("MeatRaw", 0.28f),
            ["meat_cooked"] = ("MeatCooked", 0.28f),
            ["fish_raw"] = ("FishRaw", 0.38f),
            ["fish_cooked"] = ("FishCooked", 0.32f),
            ["coconut"] = ("Coconut", 0.2f),
            ["empty_bottle"] = ("GlassBottle", 0.28f),
            ["water_bottle"] = ("WaterBottle", 0.32f),
            ["grog"] = ("Rum", 0.3f),
            ["plank"] = ("Planks", 0.9f),
            ["boat_part"] = ("Planks", 1.2f),
            ["flint"] = ("Flint", 0.16f),
            ["scrap_metal"] = ("ScrapMetal", 0.4f),
            ["armour_kit"] = ("ScrapMetal", 0.6f),
            ["cloth"] = ("ClothRoll", 0.45f),
            ["engine_kit"] = ("Crate", 0.6f),
            ["tank_kit"] = ("PirateBarrel", 0.6f),
        };

        static readonly HashSet<string> Lying = new() { "FishRaw", "ScrapMetal" };

        [MenuItem("EWYF/Build item art")]
        public static void Build()
        {
            Directory.CreateDirectory(PrefabFolder);
            Directory.CreateDirectory(IconFolder);

            int built = 0, icons = 0;
            var items = new List<ItemDef>();
            foreach (string guid in AssetDatabase.FindAssets("t:ItemDef", new[] { ItemFolder }))
                items.Add(AssetDatabase.LoadAssetAtPath<ItemDef>(AssetDatabase.GUIDToAssetPath(guid)));

            foreach (ItemDef item in items)
            {
                if (item == null) continue;

                string path = $"{PrefabFolder}/{item.Id}.prefab";
                bool ours = item.WorldPrefab == null || AssetDatabase.GetAssetPath(item.WorldPrefab) == path;
                if (!ours) continue;

                GameObject root = Make(item.Id);
                if (root == null)
                {
                    Debug.LogWarning($"[ItemArtFactory] {item.Id}: no model and no recipe; it stays a box.");
                    continue;
                }

                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
                Object.DestroyImmediate(root);
                Assign(item, "_worldPrefab", prefab);
                built++;
            }

            AssetDatabase.SaveAssets();

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Debug.LogError("[ItemArtFactory] No graphics device (-nographics?); icons skipped.");
            else
                foreach (ItemDef item in items)
                    if (item != null && item.WorldPrefab != null && Photograph(item)) icons++;

            AssetDatabase.SaveAssets();
            Debug.Log($"[ItemArtFactory] {built} world model(s) built, {icons} icon(s) rendered, "
                      + $"{items.Count} item(s) in the catalog.");
        }

        // ------------------------------------------------------------------ models

        static GameObject Make(string id)
        {
            var root = new GameObject(id);

            if (Kit.TryGetValue(id, out var kit))
            {
                var box = new Bounds(Vector3.up * kit.Size * 0.5f, Vector3.one * kit.Size);
                // The kit stands these up; on the ground they lie down.
                Quaternion turn = Lying.Contains(kit.Model) ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
                if (ArtDress.FitBox(root.transform, box, kit.Model, true, "Art", turn)) return root;

                Object.DestroyImmediate(root);
                return null;
            }

            switch (id)
            {
                case "pistol_ammo": Ammo(root, 0.012f, 0.03f, "Gold", "Cloth"); break;
                case "rifle_ammo": Ammo(root, 0.014f, 0.06f, "Gold", "Felt"); break;
                case "shotgun_shell": Ammo(root, 0.022f, 0.065f, "Accent", "Dark"); break;
                case "pearl":
                    Part(root, PrimitiveType.Sphere, new Vector3(0f, 0.025f, 0f), Vector3.one * 0.05f, "Plastic");
                    break;
                case "rope":
                    // A coil: stacked flat rings read as rope from any angle a player sees it at.
                    for (int i = 0; i < 4; i++)
                        Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.02f + i * 0.035f, 0f),
                             new Vector3(0.3f - i * 0.02f, 0.017f, 0.3f - i * 0.02f), i % 2 == 0 ? "Canvas" : "Sand");
                    break;
                case "feather":
                    Part(root, PrimitiveType.Capsule, new Vector3(0f, 0.01f, 0f), new Vector3(0.05f, 0.1f, 0.01f),
                         "Plastic", Quaternion.Euler(90f, 20f, 0f));
                    Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.01f, -0.1f), new Vector3(0.006f, 0.03f, 0.006f),
                         "Dark", Quaternion.Euler(90f, 20f, 0f));
                    break;
                case "bandage":
                    Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.04f, 0f), new Vector3(0.08f, 0.035f, 0.08f),
                         "Plastic", Quaternion.Euler(0f, 0f, 90f));
                    break;
                case "torch":
                    Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.025f, 0f), new Vector3(0.04f, 0.28f, 0.04f),
                         "Wood", Quaternion.Euler(90f, 0f, 0f));
                    Part(root, PrimitiveType.Sphere, new Vector3(0f, 0.035f, 0.3f), new Vector3(0.08f, 0.08f, 0.12f), "Canvas");
                    break;
                case "boot":
                    Part(root, PrimitiveType.Cube, new Vector3(0f, 0.09f, -0.03f), new Vector3(0.1f, 0.18f, 0.12f), "WoodDark");
                    Part(root, PrimitiveType.Cube, new Vector3(0f, 0.04f, 0.06f), new Vector3(0.1f, 0.08f, 0.16f), "WoodDark");
                    Part(root, PrimitiveType.Cube, new Vector3(0f, 0.008f, 0.02f), new Vector3(0.11f, 0.016f, 0.3f), "Dark");
                    break;
                case "hide":
                    // A pelt: a squashed, uneven ellipsoid with a darker patch.
                    Part(root, PrimitiveType.Sphere, new Vector3(0f, 0.02f, 0f), new Vector3(0.6f, 0.04f, 0.45f), "Wood");
                    Part(root, PrimitiveType.Sphere, new Vector3(0.08f, 0.03f, 0.05f), new Vector3(0.3f, 0.03f, 0.25f), "WoodDark");
                    break;
                case "fishing_rod":
                    Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.02f, 0f), new Vector3(0.02f, 0.8f, 0.02f),
                         "Wood", Quaternion.Euler(90f, 0f, 0f));
                    Part(root, PrimitiveType.Cylinder, new Vector3(0.035f, 0.03f, -0.45f), new Vector3(0.06f, 0.02f, 0.06f),
                         "Metal", Quaternion.Euler(0f, 0f, 90f));
                    break;
                case "fuel":
                    // A jerrycan.
                    Part(root, PrimitiveType.Cube, new Vector3(0f, 0.18f, 0f), new Vector3(0.14f, 0.36f, 0.3f), "Accent");
                    Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.38f, 0.1f), new Vector3(0.04f, 0.03f, 0.04f), "Dark");
                    Part(root, PrimitiveType.Cube, new Vector3(0f, 0.38f, -0.04f), new Vector3(0.03f, 0.03f, 0.14f), "Dark");
                    break;
                case "tyre_kit":
                    Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.11f, 0f), new Vector3(0.6f, 0.11f, 0.6f), "Dark");
                    Part(root, PrimitiveType.Cylinder, new Vector3(0f, 0.115f, 0f), new Vector3(0.3f, 0.112f, 0.3f), "Metal");
                    break;
                default:
                    Object.DestroyImmediate(root);
                    return null;
            }

            return root;
        }

        /// <summary>A small card box with a row of rounds standing in it, tips out.</summary>
        static void Ammo(GameObject root, float radius, float length, string round, string box)
        {
            const int count = 5;
            float pitch = radius * 2.4f;
            float width = pitch * count;
            Part(root, PrimitiveType.Cube, new Vector3(0f, length * 0.35f, 0f),
                 new Vector3(width + 0.01f, length * 0.7f, pitch * 2f + 0.01f), box);

            for (int row = 0; row < 2; row++)
                for (int i = 0; i < count; i++)
                {
                    var at = new Vector3(-width * 0.5f + pitch * (i + 0.5f), length * 0.5f, (row - 0.5f) * pitch);
                    Part(root, PrimitiveType.Cylinder, at, new Vector3(radius * 2f, length * 0.5f, radius * 2f), round);
                    Part(root, PrimitiveType.Sphere, at + Vector3.up * length * 0.5f, Vector3.one * radius * 2f, "Metal");
                }
        }

        static void Part(GameObject root, PrimitiveType type, Vector3 at, Vector3 scale, string colour,
                         Quaternion turn = default)
        {
            GameObject part = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(root.transform, false);
            part.transform.SetLocalPositionAndRotation(at, turn == default ? Quaternion.identity : turn);
            part.transform.localScale = scale;
            part.GetComponent<MeshRenderer>().sharedMaterial = Palette.Named(colour);
        }

        // ------------------------------------------------------------------ icons

        /// <summary>
        /// Three-quarter view from above, fitted to the model's bounds, on a transparent background.
        /// Far below the world, so nothing in whatever scene is open ends up in the picture.
        /// </summary>
        static bool Photograph(ItemDef item)
        {
            var stage = new Vector3(0f, -5000f, 0f);
            GameObject model = Object.Instantiate(item.WorldPrefab, stage, Quaternion.Euler(0f, 30f, 0f));
            var lightGo = new GameObject("IconSun");
            var cameraGo = new GameObject("IconCamera");
            var target = new RenderTexture(IconSize, IconSize, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };

            try
            {
                Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0) return false;

                Bounds bounds = renderers[0].bounds;
                foreach (Renderer r in renderers) bounds.Encapsulate(r.bounds);

                Light sun = lightGo.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.intensity = 1.3f;
                sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

                Camera camera = cameraGo.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                camera.orthographic = true;
                camera.orthographicSize = bounds.extents.magnitude * 0.85f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = bounds.extents.magnitude * 8f + 1f;
                Vector3 view = Quaternion.Euler(35f, -35f, 0f) * Vector3.forward;
                camera.transform.SetPositionAndRotation(bounds.center - view * bounds.extents.magnitude * 3f,
                                                        Quaternion.LookRotation(view));
                camera.targetTexture = target;
                camera.Render();

                RenderTexture.active = target;
                var pixels = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
                pixels.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
                pixels.Apply();
                RenderTexture.active = null;

                string path = $"{IconFolder}/{item.Id}.png";
                File.WriteAllBytes(path, pixels.EncodeToPNG());
                Object.DestroyImmediate(pixels);

                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                if (AssetImporter.GetAtPath(path) is TextureImporter importer)
                {
                    importer.textureType = TextureImporterType.Sprite;
                    importer.alphaIsTransparency = true;
                    importer.mipmapEnabled = false;
                    importer.SaveAndReimport();
                }

                Assign(item, "_icon", AssetDatabase.LoadAssetAtPath<Sprite>(path));
                return true;
            }
            finally
            {
                Object.DestroyImmediate(model);
                Object.DestroyImmediate(lightGo);
                Object.DestroyImmediate(cameraGo);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        static void Assign(ItemDef item, string field, Object value)
        {
            var so = new SerializedObject(item);
            so.FindProperty(field).objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(item);
        }
    }
}
