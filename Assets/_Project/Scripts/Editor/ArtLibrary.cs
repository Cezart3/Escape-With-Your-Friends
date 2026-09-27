using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Turns the third-party models <see cref="ArtExtract"/> copied in into things the game can place
    /// (#79, docs/ART-PLAN.md T1). It replaces ModelLibrary, which did the same for the Blender models
    /// this pass threw away; the two lessons that file learned the hard way are kept, the rest is new.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . -logFile art.log
    ///     -executeMethod EscapeWithYourFriends.EditorTools.ArtLibrary.BuildAll
    ///
    /// Three decisions worth knowing before changing anything here:
    ///
    /// **Each kit keeps its own atlas.** Every current Kenney kit paints every model from one
    /// colormap.png, so every material slot in a kit is pointed at one shared material made from it.
    /// The alternative - snapping the artist's colours onto <see cref="Palette"/> - is how the island
    /// got its painted-in-Paint look in the first place. Kits still on flat colours get one material
    /// per distinct colour, shared across every kit, which is the palette idea without the snapping.
    ///
    /// **Size is a number in the catalogue, not whatever the kit shipped.** The survival kit is built on
    /// a half-unit grid and the pirate kit on two and a half; neither is a metre. Every model is
    /// measured once imported and scaled to its catalogue size, so a kit's units never reach the game.
    ///
    /// **Which way is up is checked, not assumed.** The previous importer shipped a forest lying on
    /// its back because "bake axis conversion" applied a second rotation to files already exported
    /// Y-up, and it was only caught because it logged bounds. Here a model that is meant to stand is
    /// measured after import; if it lies down it is re-imported with the other setting, and if it
    /// still lies down that is an error naming the file.
    /// </summary>
    public static class ArtLibrary
    {
        public const string PrefabFolder = ArtCatalog.Root + "/_Prefabs";
        public const string MaterialFolder = ArtCatalog.Root + "/_Materials";

        static readonly Dictionary<string, GameObject> Imported = new();

        /// <summary>
        /// The axis setting each kit turned out to need, learned from its models that must stand. A
        /// kit is exported one way, so a bottle takes the setting its palm proved - the bottle has no
        /// way to prove it itself.
        /// </summary>
        static readonly Dictionary<string, bool> PackAxis = new();

        /// <summary>Every model in the catalogue imported and reported, one line each. Batchmode entry.</summary>
        public static void BuildAll()
        {
            int failed = 0;

            // Standing models first: they settle each kit's axis for the ones that cannot tell.
            foreach (ArtCatalog.Model model in ArtCatalog.Models.OrderByDescending(m => m.Upright))
            {
                GameObject source = Source(model.Id);
                if (source == null) { failed++; continue; }

                Bounds native = NativeBounds(source);
                int triangles = ArtVisual.Triangles(source);
                int cap = ArtVisual.Cap(model.Category);

                string line = $"[ArtLibrary] {model.Id} ({model.Pack}/{model.File}): {triangles} tris "
                              + $"(cap {cap}), native {native.size.x:F2}x{native.size.y:F2}x{native.size.z:F2}"
                              + (model.Upright ? ", upright" : "");

                if (triangles > cap) { Debug.LogError(line + " - OVER ITS CAP."); failed++; }
                else if (model.Upright && !ArtVisual.Standing(native.size)) { Debug.LogError(line + " - ON ITS SIDE."); failed++; }
                else Debug.Log(line);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[ArtLibrary] {ArtCatalog.Models.Length - failed} of {ArtCatalog.Models.Length} models ready, "
                      + $"{failed} failed.");

            if (Application.isBatchMode) EditorApplication.Exit(failed == 0 ? 0 : 1);
        }

        /// <summary>
        /// The imported model for a catalogue id, with its materials remapped and its up axis settled.
        /// Null, with an error saying what to run, when the file is not there.
        /// </summary>
        public static GameObject Source(string id)
        {
            if (Imported.TryGetValue(id, out GameObject cached) && cached != null) return cached;

            ArtCatalog.Model model = ArtCatalog.Find(id);

            // A model that cannot tell which way is up takes its kit's word for it, so the kit has to
            // have spoken first - in every entry point, not only BuildAll's standing-first order. A
            // counter imported before the stool that flips its kit would be placed on the old setting
            // and turn under its wrapper on the next run.
            if (!model.Upright && !PackAxis.ContainsKey(model.Pack))
                foreach (ArtCatalog.Model teacher in ArtCatalog.Models)
                    if (teacher.Pack == model.Pack && teacher.Upright) { Source(teacher.Id); break; }

            GameObject source = Import(model);
            if (source != null) Imported[id] = source;

            return source;
        }

        /// <summary>
        /// A terrain tree prototype: the model at its catalogue size, in a two-level LOD group, with
        /// its collider. Rebuilt on every call and saved over the same path, which keeps the GUID the
        /// terrain holds - so a size changed in the catalogue reaches the island on the next bake
        /// without anybody deleting anything.
        /// </summary>
        public static GameObject EnsureFloraPrefab(string id, float colliderRadius)
        {
            ArtCatalog.Model model = ArtCatalog.Find(id);
            GameObject source = Source(id);
            if (source == null) return null;

            Bounds native = NativeBounds(source);
            float measured = model.Measure == ArtCatalog.Measure.Width
                ? Mathf.Max(native.size.x, native.size.z)
                : native.size.y;

            float scale = model.Measure == ArtCatalog.Measure.Fitted || measured <= 0.0001f
                ? 1f
                : model.Size / measured;

            // Standing on its own base rather than on wherever the kit put the pivot, then pushed a
            // little into the ground: the terrain plants a tree at the height under its middle, and
            // on a slope the uphill side of a flat base floats.
            float lift = -native.min.y * scale - Sink(model.Category, native.size.y * scale);

            var root = new GameObject(id);
            var lods = new LOD[2];

            for (int detail = 0; detail < 2; detail++)
            {
                // Instantiated rather than rebuilt from a mesh, so whatever rotation the importer left
                // on the model's own root survives. See the class note.
                var level = (GameObject)PrefabUtility.InstantiatePrefab(source);
                level.name = $"{id}_LOD{detail}";
                level.transform.SetParent(root.transform, false);
                level.transform.localScale *= scale;
                level.transform.localPosition = new Vector3(0f, lift, 0f);

                Renderer[] renderers = level.GetComponentsInChildren<MeshRenderer>(true);

                foreach (Renderer worn in renderers)
                {
                    // The far level is the near level minus its shadow. What costs money on a weak GPU
                    // is fifteen thousand plants each drawing into the shadow map, not their triangles.
                    worn.shadowCastingMode = detail == 0 ? ShadowCastingMode.On : ShadowCastingMode.Off;
                    worn.receiveShadows = detail == 0;
                }

                lods[detail] = new LOD(detail == 0 ? 0.22f : 0.02f, renderers);
            }

            var group = root.AddComponent<LODGroup>();
            group.SetLODs(lods);
            group.RecalculateBounds();

            Bounds placed = Encapsulate(lods[0].renderers);
            if (colliderRadius != 0f) AddCollider(root, placed, colliderRadius);

            var visual = root.AddComponent<ArtVisual>();
            visual.Id = id;
            visual.Category = model.Category;
            visual.Upright = model.Upright;

            System.IO.Directory.CreateDirectory(PrefabFolder);
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{PrefabFolder}/{id}.prefab");
            Object.DestroyImmediate(root);

            Debug.Log($"[ArtLibrary] {id}: {ArtVisual.Triangles(prefab)} tris, "
                      + $"{placed.size.x:F1}x{placed.size.y:F1}x{placed.size.z:F1}m at x{scale:F2}.");

            if (model.Upright && !ArtVisual.Standing(placed.size))
                Debug.LogError($"[ArtLibrary] {id} is {placed.size.y:F1}m tall and "
                               + $"{Mathf.Max(placed.size.x, placed.size.z):F1}m across. It is on its side.");

            return prefab;
        }

        /// <summary>How far a model is pushed into the ground: enough to hide a flat base on a slope.</summary>
        static float Sink(ArtCategory category, float height) => category switch
        {
            ArtCategory.Tree => 0.15f,
            ArtCategory.Rock => height * 0.15f,
            ArtCategory.Log => height * 0.1f,
            ArtCategory.Plant => 0.03f,
            _ => 0f,
        };

        /// <summary>
        /// A capsule around the trunk. Negative radius means "from the mesh", right for a rock whose
        /// bounds are the rock and wrong for a tree whose bounds are mostly canopy you walk under.
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
        /// A model's bounds as imported, at the origin and at scale one, in the space the game will see
        /// it in - which includes any rotation the importer put on its root, and is why this measures
        /// an instance rather than a mesh.
        /// </summary>
        public static Bounds NativeBounds(GameObject source)
        {
            // Position zeroed, rotation and scale left exactly as the importer set them: those are
            // the axis conversion and the unit scale, and measuring without them is measuring a
            // different model.
            var probe = (GameObject)PrefabUtility.InstantiatePrefab(source);
            probe.transform.position = Vector3.zero;

            Bounds bounds = Encapsulate(probe.GetComponentsInChildren<Renderer>(true));
            Object.DestroyImmediate(probe);
            return bounds;
        }

        static Bounds Encapsulate(Renderer[] renderers)
        {
            var bounds = new Bounds();
            bool first = true;

            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                if (first) { bounds = renderer.bounds; first = false; }
                else bounds.Encapsulate(renderer.bounds);
            }

            return bounds;
        }

        // ------------------------------------------------------------------------------ importing

        static GameObject Import(ArtCatalog.Model model)
        {
            string path = ArtCatalog.PathOf(model);
            ArtCatalog.Pack pack = ArtCatalog.PackOf(model);

            if (AssetImporter.GetAtPath(path) is not ModelImporter importer)
            {
                Debug.LogError($"[ArtLibrary] No {path}. Download {pack.Author} {pack.Name} ({pack.Page}) and run "
                               + "ArtExtract.Run -artZips <folder>.");
                return null;
            }

            bool dirty = false;

            void Set<T>(T current, T wanted, System.Action<T> apply)
            {
                if (EqualityComparer<T>.Default.Equals(current, wanted)) return;
                apply(wanted);
                dirty = true;
            }

            Set(importer.globalScale, 1f, v => importer.globalScale = v);
            Set(importer.importAnimation, false, v => importer.importAnimation = v);
            Set(importer.animationType, ModelImporterAnimationType.None, v => importer.animationType = v);
            Set(importer.importCameras, false, v => importer.importCameras = v);
            Set(importer.importLights, false, v => importer.importLights = v);
            Set(importer.importBlendShapes, false, v => importer.importBlendShapes = v);

            if (PackAxis.TryGetValue(pack.Name, out bool learned))
                Set(importer.bakeAxisConversion, learned, v => importer.bakeAxisConversion = v);

            // The normals are the artist's hard edges; recomputing them rounds every facet off. No
            // normal maps anywhere in this game, so tangents are eight bytes a vertex of nothing.
            Set(importer.importNormals, ModelImporterNormals.Import, v => importer.importNormals = v);
            Set(importer.importTangents, ModelImporterTangents.None, v => importer.importTangents = v);

            // In the model, not extracted: "use external materials" would write a stray .mat beside
            // every FBX for the remap below to then ignore.
            Set(importer.materialImportMode, ModelImporterMaterialImportMode.ImportStandard,
                v => importer.materialImportMode = v);
            Set(importer.materialLocation, ModelImporterMaterialLocation.InPrefab,
                v => importer.materialLocation = v);

            if (dirty) importer.SaveAndReimport();

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
            {
                Debug.LogError($"[ArtLibrary] {path} failed to import.");
                return null;
            }

            if (Remap(importer, asset, pack)) asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);

            if (model.Upright && !ArtVisual.Standing(NativeBounds(asset).size))
            {
                bool original = importer.bakeAxisConversion;
                importer.bakeAxisConversion = !original;
                importer.SaveAndReimport();
                asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                Vector3 size = NativeBounds(asset).size;
                if (ArtVisual.Standing(size))
                    Debug.Log($"[ArtLibrary] {path} imported lying down; standing with bakeAxisConversion "
                              + $"{importer.bakeAxisConversion}. {pack.Name} was exported in a different up axis.");
                else
                {
                    // Neither setting stands it, so the flip proved nothing: put it back, or the next
                    // run starts from the flipped one and every model of the kit follows it over.
                    // BuildAll counts it as failed.
                    importer.bakeAxisConversion = original;
                    importer.SaveAndReimport();
                    asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    Debug.LogError($"[ArtLibrary] {path} lies down with bakeAxisConversion on and off "
                                   + $"({size.x:F2}x{size.y:F2}x{size.z:F2}). The file itself is rotated, or "
                                   + "it is wider than it is tall and should not be marked upright.");
                }
            }

            if (model.Upright && ArtVisual.Standing(NativeBounds(asset).size))
                PackAxis[pack.Name] = importer.bakeAxisConversion;

            return asset;
        }

        /// <summary>
        /// Points every material slot the file brought with it at a shared material. True when the
        /// model had to be imported again.
        ///
        /// A slot is ours once its material lives in <see cref="MaterialFolder"/>; anything else is
        /// still the one embedded in the FBX, named as the artist named it, and that name is the key
        /// the importer remaps by.
        /// </summary>
        static bool Remap(ModelImporter importer, GameObject model, ArtCatalog.Pack pack)
        {
            var seen = new HashSet<string>();
            bool changed = false;

            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material worn in renderer.sharedMaterials)
                {
                    if (worn == null || !seen.Add(worn.name)) continue;
                    if (AssetDatabase.GetAssetPath(worn).StartsWith(MaterialFolder)) continue;

                    Material shared = pack.Atlas == ArtCatalog.Textured ? TexturedMaterial(worn, pack)
                                      : pack.Atlas != null ? AtlasMaterial(pack)
                                      : FlatMaterial(worn);
                    if (shared == null) continue;

                    importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), worn.name), shared);
                    changed = true;
                }
            }

            if (changed) importer.SaveAndReimport();
            return changed;
        }

        /// <summary>One material per kit, painted from the kit's swatch atlas.</summary>
        static Material AtlasMaterial(ArtCatalog.Pack pack)
        {
            string path = $"{MaterialFolder}/{pack.Author}_{pack.Name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            string texturePath = $"{pack.Folder}/{pack.Atlas}";
            if (AssetImporter.GetAtPath(texturePath) is TextureImporter textures)
            {
                // No mipmaps: a mip of a swatch atlas averages neighbouring swatches, and a palm sixty
                // metres away turns the colour of the rock beside it on the sheet. Uncompressed because
                // compression blocks straddle swatch edges the same way, and the file is 512 square.
                textures.mipmapEnabled = false;
                textures.maxTextureSize = 512;
                textures.textureCompression = TextureImporterCompression.Uncompressed;
                textures.filterMode = FilterMode.Bilinear;
                textures.wrapMode = TextureWrapMode.Clamp;
                textures.sRGBTexture = true;
                textures.SaveAndReimport();
            }

            var colormap = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (colormap == null)
            {
                Debug.LogError($"[ArtLibrary] {texturePath} is missing; {pack.Name} would import untextured. "
                               + "Run ArtExtract again.");
                return null;
            }

            Material material = NewLit($"{pack.Author}_{pack.Name}");
            material.SetTexture("_BaseMap", colormap);
            material.mainTexture = colormap;
            material.SetColor("_BaseColor", Color.white);

            return Save(material, path);
        }

        /// <summary>
        /// One material per painted texture, for a pack that has real textures rather than a swatch
        /// atlas (P6, the nature kit). Keyed by the texture, not the slot: every tree that wears
        /// Bark_NormalTree wears the same material, so the forest still batches.
        ///
        /// The texture is whichever one the FBX importer already found for the slot; failing that,
        /// the one in the pack's Textures folder whose name the slot's name contains, longest first
        /// ("Leaves_NormalTree" over "Leaves"). A texture with alpha is a leaf card: alpha clip, both
        /// faces, and mips that keep their coverage, or a pine thins to nothing at forty metres.
        /// </summary>
        static Material TexturedMaterial(Material worn, ArtCatalog.Pack pack)
        {
            var texture = worn.mainTexture as Texture2D;
            if (texture == null || !AssetDatabase.GetAssetPath(texture).StartsWith(pack.Folder))
                texture = MatchTexture(worn.name, pack);

            if (texture == null)
            {
                Debug.LogWarning($"[ArtLibrary] {pack.Name}: no texture for material '{worn.name}'; it keeps "
                                 + "its flat colour. Name the texture it should wear and add it to the match.");
                return FlatMaterial(worn);
            }

            string path = $"{MaterialFolder}/{pack.Author}_{pack.Name}_{texture.name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            string texturePath = AssetDatabase.GetAssetPath(texture);
            bool cutout = false;

            if (AssetImporter.GetAtPath(texturePath) is TextureImporter textures)
            {
                cutout = textures.DoesSourceTextureHaveAlpha();

                // Painted, not swatches: mips are right here. 1024 because the bark ships far larger
                // than anything a 760M should hold for a tree trunk.
                textures.mipmapEnabled = true;
                textures.mipMapsPreserveCoverage = cutout;
                textures.alphaTestReferenceValue = 0.5f;
                textures.alphaIsTransparency = cutout;
                textures.maxTextureSize = 1024;
                textures.textureCompression = TextureImporterCompression.Compressed;
                textures.wrapMode = TextureWrapMode.Repeat;
                textures.sRGBTexture = true;
                textures.SaveAndReimport();
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            }

            Material material = NewLit($"{pack.Author}_{pack.Name}_{texture.name}");
            material.SetTexture("_BaseMap", texture);
            material.mainTexture = texture;
            material.SetColor("_BaseColor", Color.white);

            if (cutout)
            {
                material.SetFloat("_AlphaClip", 1f);
                material.SetFloat("_Cutoff", 0.5f);
                material.EnableKeyword("_ALPHATEST_ON");
                material.SetFloat("_Cull", (float)CullMode.Off);
                material.doubleSidedGI = true;
                material.renderQueue = (int)RenderQueue.AlphaTest;
            }

            return Save(material, path);
        }

        static Texture2D MatchTexture(string materialName, ArtCatalog.Pack pack)
        {
            string folder = $"{pack.Folder}/Textures";
            if (!AssetDatabase.IsValidFolder(folder)) return null;

            string wanted = materialName.ToLowerInvariant();

            return AssetDatabase.FindAssets("t:Texture2D", new[] { folder })
                                .Select(guid => AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guid)))
                                .Where(t => t != null && wanted.Contains(t.name.ToLowerInvariant()))
                                .OrderByDescending(t => t.name.Length)
                                .FirstOrDefault();
        }

        /// <summary>
        /// One material per distinct colour, across every flat-coloured kit. Two kits that both call
        /// something "wood" and picked the same brown share it; two that picked different browns do not,
        /// because the artist's brown is the point.
        /// </summary>
        static Material FlatMaterial(Material worn)
        {
            Color colour = worn.HasProperty("_BaseColor") ? worn.GetColor("_BaseColor") : worn.color;
            string hex = ColorUtility.ToHtmlStringRGB(colour);
            string path = $"{MaterialFolder}/Flat_{hex}.mat";

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Material material = NewLit($"Flat_{hex}");
            material.SetColor("_BaseColor", colour);
            material.color = colour;

            // Metal is the one thing allowed to shine. Same rule as the palette.
            string name = worn.name.ToLowerInvariant();
            material.SetFloat("_Smoothness", name.Contains("metal") || name.Contains("grey") ? 0.45f : 0.1f);

            return Save(material, path);
        }

        internal static Material NewLit(string name)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var material = new Material(shader) { name = name };

            // Flat-shaded low poly under a hard sun: a specular sheen on every leaf reads as plastic.
            material.SetFloat("_Smoothness", 0.1f);
            material.enableInstancing = true;
            return material;
        }

        internal static Material Save(Material material, string path)
        {
            System.IO.Directory.CreateDirectory(MaterialFolder);
            AssetDatabase.CreateAsset(material, path);
            Debug.Log($"[ArtLibrary] Generated {path}.");
            return material;
        }
    }
}
