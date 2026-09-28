using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>What kind of thing a third-party model is, for the purpose of what it may cost.</summary>
    public enum ArtCategory
    {
        Tree,
        Plant,
        Rock,
        Log,
        SmallProp,
        Prop,
        Structure,
        Wreck,
        Vehicle,
        Character,
        Animal,
        Weapon,
    }

    /// <summary>
    /// A marker on every third-party model the editor placed (#79), so the look harness can find
    /// them in a built player, where asset paths no longer exist.
    ///
    /// It carries the one thing a batch job knows and a harness cannot work out: what the model is
    /// meant to be. A 2 000-triangle wreck is fine and a 2 000-triangle bottle is a mistake, and the
    /// only way to tell those apart at run time is to have been told.
    ///
    /// The caps are here rather than in the editor so the editor's import report and
    /// <see cref="LookTest"/> read the same numbers. They are the table in docs/ART-PLAN.md §8.
    /// </summary>
    public class ArtVisual : MonoBehaviour
    {
        public string Id;
        public ArtCategory Category;

        /// <summary>Taller than it is wide when it is the right way up: a tree, a palm, a person.</summary>
        public bool Upright;

        /// <summary>LOD0 triangles, per model, at the size the art pass sold it at.</summary>
        public static int Cap(ArtCategory category) => category switch
        {
            // Measured on Quaternius's Nature MegaKit (P6 V1): its trees are 1 600 to 6 300, its
            // bushes up to 900. IslandProfile.TreeMaximumFullLOD (60) bounds what is drawn at once.
            ArtCategory.Tree => 6500,
            ArtCategory.Plant => 1000,
            ArtCategory.Rock => 600,
            ArtCategory.Log => 150,
            // P6 V2, measured on Quaternius's pirate kit: bottle 204, bucket 532, barrel 640, chest 1 636, the large ship
            // 20 636, the cliff 8 596 (three at the cave), the small ship 5 578.
            ArtCategory.SmallProp => 600,
            ArtCategory.Prop => 2000,
            ArtCategory.Structure => 1500,
            ArtCategory.Wreck => 30000,
            ArtCategory.Vehicle => 4000,
            // 16000, not the plan's 15000: Quaternius's female base body measures 15060 (ART-PLAN §1).
            ArtCategory.Character => 16000,
            // Measured on Quaternius's Deer (2 176) and Pig (2 821), both skinned and animated.
            ArtCategory.Animal => 3000,
            ArtCategory.Weapon => 2000,
            _ => 800,
        };

        /// <summary>
        /// Whether bounds of this shape are standing up. Four fifths rather than one, because a bent
        /// palm is nearly as wide as it is tall and is still standing; a model on its back swaps its
        /// height for its length and fails by a mile.
        /// </summary>
        public static bool Standing(Vector3 size) => size.y >= 0.8f * Mathf.Max(size.x, size.z);

        /// <summary>Triangles under a transform, first LOD only when there is a LOD group.</summary>
        public static int Triangles(GameObject root)
        {
            var group = root.GetComponent<LODGroup>();
            if (group != null)
            {
                LOD[] lods = group.GetLODs();
                if (lods.Length > 0)
                {
                    int sum = 0;
                    foreach (Renderer renderer in lods[0].renderers) sum += Triangles(renderer);
                    return sum;
                }
            }

            int total = 0;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true)) total += Triangles(renderer);
            return total;
        }

        static int Triangles(Renderer renderer)
        {
            Mesh mesh = renderer switch
            {
                SkinnedMeshRenderer skinned => skinned.sharedMesh,
                _ => renderer != null && renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null,
            };

            if (mesh == null) return 0;

            // GetIndexCount rather than triangles.Length: the triangles array is a copy of the whole
            // index buffer, and a harness walking a few hundred models should not allocate them all.
            long indices = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) indices += mesh.GetIndexCount(i);
            return (int)(indices / 3);
        }
    }
}
