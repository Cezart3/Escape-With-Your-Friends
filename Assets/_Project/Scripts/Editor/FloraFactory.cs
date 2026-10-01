using System.IO;
using EscapeWithYourFriends.World;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The plants the island is made of, as terrain tree prototypes.
    ///
    /// The meshes are Kenney's, through <see cref="ArtLibrary"/> (docs/ART-PLAN.md §4). This file is
    /// only the list of what the island wants and the grass texture, which is a texture and not a
    /// mesh.
    ///
    /// Neither the generated trees nor the Blender ones are kept as a fallback. Two flora pipelines
    /// is two things to keep in step, and one of them is always the one you are not looking at.
    /// </summary>
    public static class FloraFactory
    {
        public const string FloraFolder = "Assets/_Project/Art/Flora";

        // Fixed salt: the grass must not change shape when the island seed does.
        const int FloraSalt = 484848;

        /// <summary>
        /// Every prototype, flat, in <see cref="IslandFlora.Variants"/> order. The terrain stores an
        /// index into this array per tree, so the order is part of the saved terrain and appending
        /// is safe where reordering is not.
        /// </summary>
        /// <summary>Batchmode entry: rebuilds the tree prototypes in place, islands untouched (#199).</summary>
        public static void Bake()
        {
            EnsurePrototypes();
            AssetDatabase.SaveAssets();
        }

        public static GameObject[] EnsurePrototypes()
        {
            var prototypes = new GameObject[IslandFlora.PrototypeCount];

            for (int species = 0; species < IslandFlora.SpeciesCount; species++)
            {
                FloraModel[] variants = IslandFlora.Variants[species];

                for (int variant = 0; variant < variants.Length; variant++)
                {
                    GameObject prefab = ArtLibrary.EnsureFloraPrefab(variants[variant].Model,
                                                                     variants[variant].Radius);

                    if (prefab == null)
                        throw new FileNotFoundException(
                            $"[FloraFactory] No model for {variants[variant].Model}. "
                            + "Run ArtExtract.Run -artZips <folder> (docs/ART-PLAN.md T0-T1), then bake again.");

                    prototypes[IslandFlora.VariantBase[species] + variant] = prefab;
                }
            }

            return prototypes;
        }

        /// <summary>
        /// The grass billboard. A few blades cut out of a 64x64 texture with alpha, which is all a
        /// detail billboard ever is - Unity draws it as a camera-facing quad and no mesh is involved.
        /// </summary>
        public static Texture2D EnsureGrassTexture()
        {
            Directory.CreateDirectory(FloraFolder);
            string path = $"{FloraFolder}/GrassBlades.png";

            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            var clear = new Color32(60, 110, 45, 0);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;

            // Seven blades, each a parabola from the bottom edge, thinning as it rises. Drawn by
            // walking up the blade rather than by testing every pixel: cheaper and gives clean tips.
            for (int blade = 0; blade < 7; blade++)
            {
                float root = 6f + blade * 8f + IslandShape.Noise(blade * 1.1f, 0.5f, FloraSalt) * 5f;
                float lean = (IslandShape.Noise(blade * 2.3f, 7.7f, FloraSalt) - 0.5f) * 26f;
                float top = 34f + IslandShape.Noise(blade * 0.9f, 4.2f, FloraSalt) * 26f;

                for (float t = 0f; t <= 1f; t += 0.01f)
                {
                    float y = t * top;
                    float x = root + lean * t * t;
                    float width = Mathf.Lerp(1.9f, 0.35f, t);

                    // Green darkens towards the root, which is what makes a flat billboard read as
                    // a tuft with depth instead of as a sticker.
                    var colour = new Color32(
                        (byte)Mathf.Lerp(48f, 96f, t),
                        (byte)Mathf.Lerp(92f, 156f, t),
                        (byte)Mathf.Lerp(34f, 62f, t), 255);

                    for (float dx = -width; dx <= width; dx += 0.5f)
                    {
                        int px = Mathf.RoundToInt(x + dx);
                        int py = Mathf.RoundToInt(y);
                        if (px < 0 || px >= size || py < 0 || py >= size) continue;
                        pixels[py * size + px] = colour;
                    }
                }
            }

            texture.SetPixels32(pixels);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.maxTextureSize = size;
                importer.SaveAndReimport();
            }

            Debug.Log($"[FloraFactory] Generated {path}.");
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
