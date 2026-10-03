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

            // Materials made before the shared shader existed are re-shaded in place. Its own failure,
            // not a model's: StyleLook logs why.
            bool styled = StyleLook.Restyle() >= 0;

            AssetDatabase.SaveAssets();
            Debug.Log($"[ArtLibrary] {ArtCatalog.Models.Length - failed} of {ArtCatalog.Models.Length} models ready, "
                      + $"{failed} failed.");

            if (Application.isBatchMode) EditorApplication.Exit(failed == 0 && styled ? 0 : 1);
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
        /// Puts a tree or a plant on the swaying copies of its materials (#244). Leaves flutter on top
        /// of the sway; bark and the palms' atlas only bend. A plant is short, so it bends harder per
        /// metre to move at all - the shader's bend grows with the square of the height.
        /// </summary>
        static void Sway(Renderer renderer, ArtCategory category)
        {
            if (category != ArtCategory.Tree && category != ArtCategory.Plant) return;

            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] == null) continue;
                string name = materials[i].name;
                bool leaves = name.Contains("Leaves") || name.Contains("Flowers") || name.Contains("Foliage");
                float sway = category == ArtCategory.Plant ? 8f : 1f;
                materials[i] = StyleLook.WindTwin(materials[i], sway, leaves ? 1f : 0.4f);
            }
            renderer.sharedMaterials = materials;
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

                // Every renderer, the one this level switches off too: it is still in the prefab, and
                // a still twin of the palm's materials would be two more materials in the scene.
                foreach (Renderer any in renderers) Sway(any, model.Category);

                // A model that brings its own far mesh, "<name>_Far" (our palms, tools/art/palms.py):
                // each level keeps the one mesh that is its own and switches the other off.
                if (renderers.Any(r => r.name.EndsWith(ArtVisual.FarSuffix)))
                {
                    foreach (Renderer r in renderers)
                        r.gameObject.SetActive(r.name.EndsWith(ArtVisual.FarSuffix) == (detail == 1));
                    renderers = renderers.Where(r => r.gameObject.activeSelf).ToArray();
                }

                foreach (Renderer worn in renderers)
                {
                    // The far level is the near level minus its shadow. What costs money on a weak GPU
                    // is fifteen thousand plants each drawing into the shadow map, not their triangles.
                    worn.shadowCastingMode = detail == 0 ? ShadowCastingMode.On : ShadowCastingMode.Off;
                    worn.receiveShadows = detail == 0;
                }

                lods[detail] = new LOD(detail == 0 ? 0.22f : 0.02f, renderers);
            }

            // The trunk goes on the tree's spot, not the kit's pivot (#199). The terrain puts the
            // capsule on the spot, and a palm's trunk stood a third of a metre off it, a leaning one
            // more, so a body stopped by the capsule stood inside the bark. Moving the model rather
            // than the capsule also keeps them together however the terrain turns the tree.
            if (colliderRadius != 0f)
            {
                Rect trunk = Trunk(root.transform, lods[0].renderers);
                var shift = new Vector3(-trunk.center.x, 0f, -trunk.center.y);
                foreach (Transform level in root.transform) level.localPosition += shift;

                // A tree's catalogue radius was a guess; the bark is measured.
                if (colliderRadius > 0f && model.Category == ArtCategory.Tree)
                    colliderRadius = Mathf.Max(0.2f, Mathf.Max(trunk.width, trunk.height) * 0.425f);

                Debug.Log($"[ArtLibrary] {id}: trunk moved {shift.magnitude:F2} m onto the spot, "
                          + $"{trunk.width:F2}x{trunk.height:F2} m across.");
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
            if (colliderRadius != 0f) visual.Trunk = Trunk(root.transform, lods[0].renderers).center;

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
        /// Where a body meets the model, as an xz box in <paramref name="root"/>'s space: its vertices
        /// in 25 cm slices from the ground to head height, stopping at the first slice more than
        /// twice as wide as the lowest (and over 1.2 m), because that is branches, which a body walks
        /// under or through. A palm's trunk is only rings, so some slices are empty.
        /// </summary>
        static Rect Trunk(Transform root, Renderer[] renderers)
        {
            const float Slice = 0.25f, Head = 1.75f;
            var slices = new Rect?[Mathf.CeilToInt(Head / Slice)];

            foreach (Renderer renderer in renderers)
            {
                if (renderer.name.EndsWith(ArtVisual.DecoSuffix)) continue;
                if (!renderer.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null) continue;

                foreach (Vector3 vertex in filter.sharedMesh.vertices)
                {
                    Vector3 p = root.InverseTransformPoint(renderer.transform.TransformPoint(vertex));
                    int i = Mathf.FloorToInt(p.y / Slice);
                    if (i < 0 || i >= slices.Length) continue;

                    slices[i] = slices[i] is Rect box
                        ? Rect.MinMaxRect(Mathf.Min(box.xMin, p.x), Mathf.Min(box.yMin, p.z),
                                          Mathf.Max(box.xMax, p.x), Mathf.Max(box.yMax, p.z))
                        : new Rect(p.x, p.z, 0f, 0f);
                }
            }

            Rect? trunk = null;
            float lowest = 0f;
            foreach (Rect? slice in slices)
            {
                if (slice is not Rect box) continue;

                float wide = Mathf.Max(box.width, box.height);
                if (trunk == null) lowest = wide;
                else if (wide > Mathf.Max(lowest * 2f, 1.2f)) break;

                trunk = trunk is Rect t
                    ? Rect.MinMaxRect(Mathf.Min(t.xMin, box.xMin), Mathf.Min(t.yMin, box.yMin),
                                      Mathf.Max(t.xMax, box.xMax), Mathf.Max(t.yMax, box.yMax))
                    : box;
            }

            return trunk ?? new Rect();
        }

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

        /// <summary>
        /// A skinned mesh as it stands in its bind pose. Its renderer's bounds are the importer's,
        /// grown to hold every clip in the file: an animal that dies on its side measured eight
        /// centimetres taller than it is, and stood that far off the ground.
        /// </summary>
        static Bounds Rest(SkinnedMeshRenderer skinned)
        {
            Bounds local = skinned.sharedMesh.bounds;
            Matrix4x4 m = skinned.transform.localToWorldMatrix;
            var world = new Bounds(m.MultiplyPoint3x4(local.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                world.Encapsulate(m.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return world;
        }

        static Bounds Encapsulate(Renderer[] renderers)
        {
            var bounds = new Bounds();
            bool first = true;

            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                Bounds b = renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null
                    ? Rest(skinned) : renderer.bounds;
                if (first) { bounds = b; first = false; }
                else bounds.Encapsulate(b);
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
            // Animals are the one category that moves (T13): a Generic rig and the kit's own clips.
            // Everything else is a still life, and a rig on a barrel is bones nobody drives.
            bool animated = model.Category == ArtCategory.Animal;
            Set(importer.importAnimation, animated, v => importer.importAnimation = v);
            Set(importer.animationType, animated ? ModelImporterAnimationType.Generic : ModelImporterAnimationType.None,
                v => importer.animationType = v);
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

            // After the rig is on, or the default clips are the ones of a model with no animation.
            if (animated && LoopClips(importer)) importer.SaveAndReimport();

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
        /// Every clip loops except a death, which holds its last frame. The FBX says nothing about
        /// looping, so without this a walk plays once and the animal glides the rest of the way.
        /// </summary>
        static bool LoopClips(ModelImporter importer)
        {
            ModelImporterClipAnimation[] clips = importer.clipAnimations;
            bool changed = clips == null || clips.Length == 0;
            if (changed) clips = importer.defaultClipAnimations;
            if (clips.Length == 0) return false;

            foreach (ModelImporterClipAnimation clip in clips)
            {
                bool loop = !IsDeath(clip.name);
                if (clip.loopTime == loop) continue;
                clip.loopTime = loop;
                changed = true;
            }

            if (changed) importer.clipAnimations = clips;
            return changed;
        }

        internal static bool IsDeath(string clip)
        {
            string name = clip.ToLowerInvariant();
            return name.Contains("death") || name.Contains("die");
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

                    Material shared = pack.Textured ? TexturedMaterial(worn, pack)
                                    : pack.Atlas ? AtlasMaterial(pack)
                                    : FlatMaterial(worn);
                    if (shared == null) continue;

                    importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), worn.name), shared);
                    changed = true;
                }
            }

            if (changed) importer.SaveAndReimport();
            return changed;
        }

        /// <summary>One material per kit, painted from the kit's colormap.</summary>
        static Material AtlasMaterial(ArtCatalog.Pack pack)
        {
            string path = $"{MaterialFolder}/{pack.Author}_{pack.Name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            string texturePath = $"{pack.Folder}/{pack.AtlasFile}";
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

            Material material = StyleLook.New($"{pack.Author}_{pack.Name}");
            material.SetTexture("_BaseMap", colormap);
            material.mainTexture = colormap;
            material.SetColor("_BaseColor", Color.white);

            return Save(material, path);
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

            Material material = StyleLook.New($"Flat_{hex}");
            material.SetColor("_BaseColor", colour);
            material.color = colour;

            // Metal is the one thing allowed to shine. Same rule as the palette.
            string name = worn.name.ToLowerInvariant();
            material.SetFloat("_Smoothness", name.Contains("metal") || name.Contains("grey") ? 0.45f : 0.1f);

            return Save(material, path);
        }

        /// <summary>
        /// One material per slot the artist named, for a kit painted from its own textures (the
        /// Quaternius kits, ART-PLAN P6). The texture is the one the FBX points at, else the file in the
        /// kit's Textures folder named after the slot, as <see cref="CharacterArt"/> finds a body's.
        /// A texture with an alpha channel is a leaf card: clipped, and drawn from both sides so a
        /// palm frond does not vanish edge-on.
        /// </summary>
        static Material TexturedMaterial(Material worn, ArtCatalog.Pack pack)
        {
            string slot = new(worn.name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            string name = $"{pack.Author}_{pack.Name}_{slot}";
            string path = $"{MaterialFolder}/{name}.mat";

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Texture texture = worn.HasProperty("_BaseMap") ? worn.GetTexture("_BaseMap") : null;
            if (texture == null) texture = worn.mainTexture;
            string file = texture != null ? AssetDatabase.GetAssetPath(texture) : CharacterArt.BaseColour(pack, slot);

            bool clip = false;
            if (file != null && AssetImporter.GetAtPath(file) is TextureImporter importer)
            {
                // 1024 is the budget on the 760M (ART-PLAN §8); the kits ship 2048 and up.
                clip = importer.DoesSourceTextureHaveAlpha();
                if (importer.maxTextureSize != 1024 || importer.alphaIsTransparency != clip)
                {
                    importer.maxTextureSize = 1024;
                    importer.alphaIsTransparency = clip;
                    importer.SaveAndReimport();
                }
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(file);
            }

            Material material = StyleLook.New(name);
            if (texture == null)
            {
                // A slot can be a plain colour even in a textured kit. Kept, and said.
                Color colour = worn.HasProperty("_BaseColor") ? worn.GetColor("_BaseColor") : worn.color;
                material.SetColor("_BaseColor", colour);
                material.color = colour;
                Debug.LogWarning($"[ArtLibrary] {worn.name} ({pack.Name}): no texture in the FBX or in "
                                 + $"{pack.Folder}/Textures; flat #{ColorUtility.ToHtmlStringRGB(colour)}.");
                return Save(material, path);
            }

            material.SetTexture("_BaseMap", texture);
            material.mainTexture = texture;
            material.SetColor("_BaseColor", Color.white);

            if (clip)
            {
                material.SetFloat("_AlphaClip", 1f);
                material.SetFloat("_Cutoff", 0.5f);
                material.EnableKeyword("_ALPHATEST_ON");
                material.renderQueue = (int)RenderQueue.AlphaTest;
            }

            // Leaves are seen from both sides, cut out of a card or not: our palms' leaflets are single
            // faces, and from under the crown every one of them is a back face.
            if (clip || slot.Contains("Leaves")) material.SetFloat("_Cull", 0f);

            Debug.Log($"[ArtLibrary] {worn.name} ({pack.Name}) is painted from {file}"
                      + (clip ? ", alpha-clipped, both sides." : "."));
            return Save(material, path);
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
