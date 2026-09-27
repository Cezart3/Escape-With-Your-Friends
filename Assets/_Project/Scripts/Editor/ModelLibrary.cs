using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Turns the modelled assets in <c>Art/Models</c> into prefabs the game can place (#76, #78).
    ///
    /// The meshes come from <c>tools/blender/flora.py</c>, which drives Blender over MCP and writes
    /// one FBX per asset. FBX rather than glTF for one reason: Unity imports it with no extra
    /// package, and the alternative is adding com.unity.cloud.gltfast to carry eighteen static
    /// meshes.
    ///
    /// The bit worth reading is the material remap. Blender paints every asset with the palette's
    /// own names - Wood, LeafDark, Stone - so the importer can point each imported slot straight at
    /// <see cref="Palette"/> instead of embedding a copy. Eighteen models therefore add **no new
    /// materials at all**, which is what keeps <c>-lookTest</c>'s count flat and the island batching.
    ///
    /// Nothing here is hand-placed and nothing here is hand-edited: delete the prefabs and the next
    /// terrain bake rebuilds them, delete the FBX and Blender regenerates it.
    /// </summary>
    public static class ModelLibrary
    {
        public const string ModelFolder = "Assets/_Project/Art/Models";

        /// <summary>
        /// The prefab for one model, built on first ask and reused afterwards. Null when the FBX is
        /// missing, which means somebody has not run the Blender script - the caller says so, because
        /// the caller knows what was being built.
        /// </summary>
        public static GameObject EnsurePrefab(string model, float colliderRadius)
        {
            string prefabPath = $"{ModelFolder}/{model}.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (existing != null) return existing;

            string sourcePath = $"{ModelFolder}/{model}.fbx";
            GameObject source = Import(sourcePath);
            if (source == null) return null;

            var root = new GameObject(model);
            var lods = new LOD[2];
            int triangles = 0;

            for (int detail = 0; detail < 2; detail++)
            {
                // Instantiated rather than rebuilt out of a MeshFilter and a mesh. Building it by
                // hand drops whatever transform the importer put on the model's root, and for an
                // FBX out of Blender that transform is the entire axis conversion - so every tree
                // came out lying on its back, with a mesh whose bounds insisted it was upright.
                var level = (GameObject)PrefabUtility.InstantiatePrefab(source);
                level.name = $"{model}_LOD{detail}";
                level.transform.SetParent(root.transform, false);

                Renderer[] renderers = level.GetComponentsInChildren<MeshRenderer>(true);
                if (renderers.Length == 0)
                {
                    Debug.LogError($"[ModelLibrary] {sourcePath} imported without a renderer.");
                    Object.DestroyImmediate(root);
                    return null;
                }

                foreach (Renderer worn in renderers)
                {
                    // Same mesh at both levels - these are sixty to six hundred triangles, and what
                    // actually costs money on a weak GPU is every one of fifteen thousand plants
                    // casting a shadow. So the far level is the near level minus its shadow.
                    worn.shadowCastingMode = detail == 0
                        ? UnityEngine.Rendering.ShadowCastingMode.On
                        : UnityEngine.Rendering.ShadowCastingMode.Off;
                    worn.receiveShadows = detail == 0;

                    var filter = worn.GetComponent<MeshFilter>();
                    if (detail == 0 && filter != null && filter.sharedMesh != null)
                        triangles += filter.sharedMesh.triangles.Length / 3;
                }

                lods[detail] = new LOD(detail == 0 ? 0.22f : 0.02f, renderers);
            }

            var group = root.AddComponent<LODGroup>();
            group.SetLODs(lods);
            group.RecalculateBounds();

            // Measured off the placed renderers, not off the mesh: the mesh is in whatever space the
            // exporter left it in, and this is the space the game will see it in.
            Bounds bounds = lods[0].renderers[0].bounds;
            foreach (Renderer worn in lods[0].renderers) bounds.Encapsulate(worn.bounds);

            if (colliderRadius != 0f) AddCollider(root, bounds, colliderRadius);

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);

            Debug.Log($"[ModelLibrary] {model}: {triangles} tris, "
                      + $"{bounds.size.x:F1}x{bounds.size.y:F1}x{bounds.size.z:F1}m.");

            // The cheapest possible axis check, and it is worth having: a tree is taller than it is
            // wide, and an import that gets the up axis wrong lays the whole island down without a
            // single error anywhere. A rock or a log is allowed to be flat.
            bool upright = model.StartsWith("Tree_") || model.StartsWith("Pine_") || model.StartsWith("Palm_");
            if (upright && bounds.size.y < Mathf.Max(bounds.size.x, bounds.size.z))
                Debug.LogError($"[ModelLibrary] {model} is {bounds.size.y:F1}m tall and "
                               + $"{bounds.size.z:F1}m deep. It is on its side.");

            return prefab;
        }

        /// <summary>
        /// A capsule around the trunk. A negative radius means "work it out from the mesh", which is
        /// right for a rock, where the bounds *are* the object, and wrong for a tree, where four
        /// fifths of the bounds are canopy you are meant to walk under.
        /// </summary>
        static void AddCollider(GameObject root, Bounds bounds, float radius)
        {
            if (radius < 0f)
                radius = Mathf.Max(0.15f, Mathf.Min(bounds.extents.x, bounds.extents.z) * 0.8f);

            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.radius = radius;
            capsule.height = Mathf.Max(radius * 2f, bounds.size.y);
            capsule.center = new Vector3(0f, bounds.center.y, 0f);
        }

        /// <summary>
        /// Imports the FBX the way this project wants it and points its materials at the palette.
        /// Runs once per model: the remap is stored in the .meta, so the second call is a load.
        /// </summary>
        static GameObject Import(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError($"[ModelLibrary] No FBX at {path}. Run tools/blender/flora.py in Blender.");
                return null;
            }

            bool dirty = false;

            // Off, and it took a bake to find out. Blender is Z-up, but the export in flora.py asks
            // for Y-up and -Z forward, which is already Unity's convention - so "bake axis
            // conversion" applies a *second* rotation and lays every tree on its back. The bounds
            // this file logs are how that was caught, which is why it logs them.
            if (importer.bakeAxisConversion) { importer.bakeAxisConversion = false; dirty = true; }
            if (importer.globalScale != 1f) { importer.globalScale = 1f; dirty = true; }
            if (importer.importAnimation) { importer.importAnimation = false; dirty = true; }
            if (importer.importCameras) { importer.importCameras = false; dirty = true; }
            if (importer.importLights) { importer.importLights = false; dirty = true; }

            // Flat-shaded low poly: the normals are already right in the file, and there is no normal
            // map anywhere in this game, so tangents are eight bytes a vertex of nothing.
            if (importer.importNormals != ModelImporterNormals.Import)
            {
                importer.importNormals = ModelImporterNormals.Import;
                dirty = true;
            }

            if (importer.importTangents != ModelImporterTangents.None)
            {
                importer.importTangents = ModelImporterTangents.None;
                dirty = true;
            }

            // In the prefab, not extracted. "Use External Materials" would write a .mat next to every
            // FBX for anything it could not find by name, and a folder of eighteen stray materials
            // is exactly what the palette exists to prevent.
            if (importer.materialImportMode != ModelImporterMaterialImportMode.ImportStandard)
            {
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                dirty = true;
            }

            if (importer.materialLocation != ModelImporterMaterialLocation.InPrefab)
            {
                importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
                dirty = true;
            }

            if (dirty) importer.SaveAndReimport();

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (model == null)
            {
                Debug.LogError($"[ModelLibrary] {path} failed to import.");
                return null;
            }

            return Remap(importer, model) ? AssetDatabase.LoadAssetAtPath<GameObject>(path) : model;
        }

        /// <summary>
        /// Points every material slot at its palette entry, by name. True when anything changed and
        /// the model needs loading again.
        ///
        /// A name the palette has never heard of is a bug in the Blender script rather than a new
        /// colour, but it is not worth failing a terrain bake over, so it snaps to the nearest entry
        /// and says so.
        /// </summary>
        static bool Remap(ModelImporter importer, GameObject model)
        {
            Dictionary<AssetImporter.SourceAssetIdentifier, Object> map = importer.GetExternalObjectMap();
            var seen = new HashSet<string>();
            bool changed = false;

            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material worn in renderer.sharedMaterials)
                {
                    if (worn == null || !seen.Add(worn.name)) continue;

                    var slot = new AssetImporter.SourceAssetIdentifier(typeof(Material), worn.name);
                    if (map.ContainsKey(slot)) continue;

                    Material entry = Palette.Has(worn.name) ? Palette.Named(worn.name) : Palette.For(worn.color);
                    if (!Palette.Has(worn.name))
                        Debug.LogWarning($"[ModelLibrary] {model.name} wears \"{worn.name}\", which is not "
                                         + $"a palette entry. Snapped to {entry.name}.");

                    importer.AddRemap(slot, entry);
                    changed = true;
                }
            }

            if (changed) importer.SaveAndReimport();
            return changed;
        }
    }
}
