using EscapeWithYourFriends.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Hangs third-party models on the greybox (#78, #79, docs/ART-PLAN.md §4).
    ///
    /// The greybox layout stays the authority. Every named box keeps its name, its collider and its
    /// place, so the harnesses that measure a doorway, count stools or find a bar, the POI
    /// validation and the NavMesh bake (which reads colliders) all see exactly what they saw before.
    /// Only the box's renderer goes, and a model is fitted to where the box was.
    ///
    /// Two ways to fit, because a building is made of two kinds of thing:
    ///
    ///  - <see cref="Fit"/> puts one model in a box. Right for a roof, a stool, a hull, a bottle.
    ///  - <see cref="Tile"/> repeats a module across a box in a grid. Right for a floor or a wall,
    ///    which smears if one plank panel is stretched nine metres.
    ///
    /// The model always hangs under an unscaled wrapper whose own scale does the fitting, so the
    /// model's root keeps whatever rotation and unit scale its importer gave it - the same reason
    /// <see cref="ArtLibrary"/> instantiates rather than rebuilds. Turns are quarter turns only: a
    /// quarter turn under a stretched parent permutes the stretch, anything else shears the model.
    /// </summary>
    internal static class ArtDress
    {
        /// <summary>
        /// One model in a greybox piece's box. With <paramref name="keepShape"/> it keeps its
        /// proportions, fits inside the box and stands on its floor; without, it fills the box.
        /// </summary>
        public static bool Fit(GameObject piece, string id, bool keepShape = false, int quarterTurns = 0)
        {
            if (!Box(piece, out Bounds box)) return false;
            if (!Place(piece.transform, box, id, Quaternion.Euler(0f, 90f * quarterTurns, 0f), 1, 1, keepShape, "Art"))
                return false;

            Strip(piece);
            return true;
        }

        /// <summary>
        /// A module repeated across a greybox piece in a grid of roughly <paramref name="cell"/>
        /// metres. The module is turned so its thinnest side faces the box's thinnest side, which is
        /// how one floor tile becomes both the floor and, stood on its edge, the walls.
        /// </summary>
        public static bool Tile(GameObject piece, string id, float cell)
        {
            if (!Box(piece, out Bounds box)) return false;
            if (!TileBox(piece.transform, box, id, cell, "Art")) return false;

            Strip(piece);
            return true;
        }

        /// <summary><see cref="Fit"/> into an explicit box in <paramref name="parent"/>'s space.</summary>
        public static bool FitBox(Transform parent, Bounds box, string id, bool keepShape, string name,
                                  int quarterTurns = 0)
            => Place(parent, box, id, Quaternion.Euler(0f, 90f * quarterTurns, 0f), 1, 1, keepShape, name);

        /// <summary><see cref="Tile"/> into an explicit box in <paramref name="parent"/>'s space.</summary>
        public static bool TileBox(Transform parent, Bounds box, string id, float cell, string name)
        {
            GameObject source = ArtLibrary.Source(id);
            if (source == null) return false;

            Vector3 world = Vector3.Scale(box.size, parent.lossyScale);
            Vector3 native = ArtLibrary.NativeBounds(source).size;

            // Thinnest to thinnest. FromToRotation between two different axes is an exact quarter
            // turn, which is the only kind a stretched parent can carry without shearing.
            int thinModel = Smallest(native);
            int thinBox = Smallest(world);
            Quaternion turn = thinModel == thinBox
                ? Quaternion.identity
                : Quaternion.FromToRotation(Axis(thinModel), Axis(thinBox));

            // The grid runs across the two long sides of the box.
            int a = (thinBox + 1) % 3;
            int b = (thinBox + 2) % 3;
            int countA = Mathf.Max(1, Mathf.RoundToInt(world[a] / cell));
            int countB = Mathf.Max(1, Mathf.RoundToInt(world[b] / cell));

            var counts = new Vector3Int(1, 1, 1);
            counts[a] = countA;
            counts[b] = countB;

            return Place(parent, box, id, turn, counts, false, name);
        }

        /// <summary>
        /// Removes a greybox piece's look and keeps everything else. The mesh filter goes too: a
        /// renderer-less filter is harmless, but a disabled renderer still wears a palette material,
        /// and the look harness would count it. Callers strip only once the art is in, so a machine
        /// without the kits keeps its greybox rather than an invisible building.
        /// </summary>
        public static void Strip(GameObject piece)
        {
            if (piece == null) return;

            if (piece.TryGetComponent(out MeshRenderer renderer)) Object.DestroyImmediate(renderer);
            if (piece.TryGetComponent(out MeshFilter filter)) Object.DestroyImmediate(filter);
        }

        // ------------------------------------------------------------------------------------ core

        static bool Place(Transform parent, Bounds box, string id, Quaternion turn, int countA, int countB,
                          bool keepShape, string name)
            => Place(parent, box, id, turn, new Vector3Int(countA, 1, countB), keepShape, name);

        static bool Place(Transform parent, Bounds box, string id, Quaternion turn, Vector3Int counts,
                          bool keepShape, string name)
        {
            GameObject source = ArtLibrary.Source(id);
            if (source == null) return false;

            ArtCatalog.Model model = ArtCatalog.Find(id);
            Bounds native = ArtLibrary.NativeBounds(source);

            // Everything below is in the parent's axes. S is how many metres one of the parent's
            // units is along each of them; the box is in the parent's units.
            Vector3 s = parent.lossyScale;
            Vector3 turnedSize = Abs(turn * native.size);
            Vector3 cellLocal = new(box.size.x / counts.x, box.size.y / counts.y, box.size.z / counts.z);
            Vector3 cellWorld = Vector3.Scale(cellLocal, s);

            Vector3 extents = cellWorld;
            if (keepShape)
            {
                float k = Mathf.Min(cellWorld.x / turnedSize.x, cellWorld.y / turnedSize.y, cellWorld.z / turnedSize.z);
                extents = turnedSize * k;
            }

            // Stretch per axis in the parent's frame, then carried back into the model's own frame
            // through the turn, then divided by the parent's scale that the wrapper will inherit.
            Vector3 stretch = Divide(extents, turnedSize);
            Vector3 local = Abs(Quaternion.Inverse(turn) * Divide(stretch, s));

            int index = 0;
            for (int i = 0; i < counts.x; i++)
            for (int j = 0; j < counts.y; j++)
            for (int k = 0; k < counts.z; k++)
            {
                Vector3 centre = box.min + Vector3.Scale(cellLocal, new Vector3(i + 0.5f, j + 0.5f, k + 0.5f));

                // Standing on the floor of its cell rather than floating in the middle of it.
                if (keepShape) centre.y = box.min.y + j * cellLocal.y + extents.y / s.y * 0.5f;

                var wrapper = new GameObject(index == 0 ? name : $"{name}.{index}");
                wrapper.transform.SetParent(parent, false);
                wrapper.transform.localRotation = turn;
                wrapper.transform.localScale = local;

                // The model's middle is not its pivot; shift the wrapper so the middle lands on the cell.
                wrapper.transform.localPosition = centre - turn * Vector3.Scale(local, native.center);

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
                instance.transform.SetParent(wrapper.transform, false);

                Finish(wrapper, model, extents);
                index++;
            }

            return true;
        }

        /// <summary>
        /// The marker the look harness reads, a cull distance, and the shadow rule the greybox
        /// already had: things smaller than a person's forearm do not cast.
        /// </summary>
        static void Finish(GameObject wrapper, ArtCatalog.Model model, Vector3 extents)
        {
            var visual = wrapper.AddComponent<ArtVisual>();
            visual.Id = model.Id;
            visual.Category = model.Category;
            visual.Upright = model.Upright;

            Renderer[] renderers = wrapper.GetComponentsInChildren<Renderer>(true);
            bool small = Mathf.Max(extents.x, extents.y, extents.z) < 0.6f;

            foreach (Renderer renderer in renderers)
                if (small) renderer.shadowCastingMode = ShadowCastingMode.Off;

            // One level and a cull: a bottle sixty metres away is gone rather than drawn as a pixel.
            // Structure goes much later than a prop, because a missing wall is noticed from anywhere.
            float cull = model.Category == ArtCategory.Structure || model.Category == ArtCategory.Wreck
                ? 0.005f
                : 0.015f;

            var group = wrapper.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(cull, renderers) });
            group.RecalculateBounds();
        }

        /// <summary>A piece's box in its own units, read off its mesh before the mesh is taken away.</summary>
        static bool Box(GameObject piece, out Bounds box)
        {
            box = default;
            if (piece == null || !piece.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null)
            {
                Debug.LogWarning($"[ArtDress] {(piece != null ? piece.name : "(null)")} has no mesh to fit to.");
                return false;
            }

            box = filter.sharedMesh.bounds;
            return true;
        }

        static int Smallest(Vector3 v) => v.x <= v.y && v.x <= v.z ? 0 : v.y <= v.z ? 1 : 2;

        static Vector3 Axis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;

        static Vector3 Abs(Vector3 v) => new(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        static Vector3 Divide(Vector3 a, Vector3 b)
            => new(a.x / Safe(b.x), a.y / Safe(b.y), a.z / Safe(b.z));

        static float Safe(float v) => Mathf.Abs(v) < 0.0001f ? 0.0001f : v;
    }
}
