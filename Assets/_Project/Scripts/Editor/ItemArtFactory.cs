using System.Collections.Generic;
using System.IO;
using EscapeWithYourFriends.Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// What every item looks like on the ground, and its icon in the bag (#206, #208, #283).
    ///
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod
    ///     EscapeWithYourFriends.EditorTools.ItemArtFactory.Build
    ///
    /// Not -nographics: the icons are rendered by a real camera.
    ///
    /// Every item's model is ours, from tools/art/items.py: <c>Itm_&lt;id&gt;</c> in Items.fbx, on the
    /// slots' ramp sheet, lying the way it rests and already its size in metres. A world prefab
    /// somebody else chose (the guns) is never replaced. One this factory made is rebuilt on every run.
    ///
    /// Then every item with a world prefab, the guns included, is photographed into a 128px icon.
    /// A new item gets its icon by existing.
    /// </summary>
    public static class ItemArtFactory
    {
        const string ItemFolder = "Assets/_Project/Data/Items";
        const string PrefabFolder = "Assets/_Project/Prefabs/Items";
        const string IconFolder = "Assets/_Project/Art/Icons";
        const string ModelsPath = "Assets/_Project/Art/Casino/Models/Items.fbx";
        const int IconSize = 128;

        [MenuItem("EWYF/Build item art")]
        public static void Build()
        {
            Directory.CreateDirectory(PrefabFolder);
            Directory.CreateDirectory(IconFolder);

            int built = 0, icons = 0;
            Dictionary<string, Mesh> models = SlotFactory.Models(ModelsPath);
            var items = new List<ItemDef>();
            foreach (string guid in AssetDatabase.FindAssets("t:ItemDef", new[] { ItemFolder }))
                items.Add(AssetDatabase.LoadAssetAtPath<ItemDef>(AssetDatabase.GUIDToAssetPath(guid)));

            foreach (ItemDef item in items)
            {
                if (item == null) continue;

                string path = $"{PrefabFolder}/{item.Id}.prefab";
                bool ours = item.WorldPrefab == null || AssetDatabase.GetAssetPath(item.WorldPrefab) == path;
                if (!ours) continue;

                if (models == null || !models.TryGetValue("Itm_" + item.Id, out Mesh mesh))
                {
                    Debug.LogWarning($"[ItemArtFactory] {item.Id}: no Itm_{item.Id} in {ModelsPath}; it stays a box.");
                    continue;
                }

                var root = new GameObject(item.Id);
                var model = new GameObject("Model");
                model.transform.SetParent(root.transform, false);
                model.AddComponent<MeshFilter>().sharedMesh = mesh;
                model.AddComponent<MeshRenderer>().sharedMaterial = SlotFactory.Atlas();

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
