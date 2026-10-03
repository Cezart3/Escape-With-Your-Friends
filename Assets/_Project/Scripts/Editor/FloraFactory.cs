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
    }
}
