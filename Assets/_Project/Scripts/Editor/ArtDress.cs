using System.Collections.Generic;
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
        /// metres. A panel (a <see cref="ArtCategory.Structure"/>) is turned so its thinnest side faces
        /// the box's thinnest side, which is how one floor tile becomes both the floor and, stood on
        /// its edge, the walls. Anything else only turns about the vertical.
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

            // Thinnest to thinnest, for a panel: FromToRotation between two different axes is an exact
            // quarter turn, which is the only kind a stretched parent can carry without shearing, and
            // it is how one floor tile stands on its edge as a wall. Anything else keeps its feet on
            // the ground and only turns about the vertical: a stump in a tall thin totem box would
            // otherwise be laid on its side because the box is thinner across than the stump is tall.
            int thinModel = Smallest(native);
            int thinBox = Smallest(world);
            Quaternion turn;
            if (ArtCatalog.Find(id).Category == ArtCategory.Structure)
                turn = thinModel == thinBox ? Quaternion.identity : Quaternion.FromToRotation(Axis(thinModel), Axis(thinBox));
            else
                turn = (native.x <= native.z) == (world.x <= world.z) ? Quaternion.identity : Quaternion.Euler(0f, 90f, 0f);

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
        /// One model over several greybox blocks that are together one thing: a chest's body, lid,
        /// bands and latch are one chest. The blocks are direct children of <paramref name="root"/>
        /// and keep their names and colliders; their looks go once the model is in.
        ///
        /// The model is turned a quarter if that puts its long side along the blocks' long side.
        /// Without <paramref name="keepShape"/> it fills the blocks' box, which is what a block whose
        /// collider is the thing itself wants: a bench you bump into where there is no bench is worse
        /// than a bench a little wider than Kenney drew it.
        /// </summary>
        public static bool Replace(Transform root, string id, string name, bool keepShape, params string[] blocks)
        {
            var pieces = new List<GameObject>();
            if (!Blocks(root, id, blocks, pieces, out Bounds box)) return false;

            GameObject source = ArtLibrary.Source(id);
            if (source == null) return false;

            Vector3 native = ArtLibrary.NativeBounds(source).size;
            int turns = (native.x >= native.z) == (box.size.x >= box.size.z) ? 0 : 1;

            if (!FitBox(root, box, id, keepShape, name, turns)) return false;

            foreach (GameObject piece in pieces) Strip(piece);
            return true;
        }

        /// <summary><see cref="Replace"/>, with the module repeated across the blocks as <see cref="Tile"/> does.</summary>
        public static bool ReplaceTiled(Transform root, string id, string name, float cell, params string[] blocks)
        {
            var pieces = new List<GameObject>();
            if (!Blocks(root, id, blocks, pieces, out Bounds box)) return false;
            if (!TileBox(root, box, id, cell, name)) return false;

            foreach (GameObject piece in pieces) Strip(piece);
            return true;
        }

        /// <summary>The named direct children of <paramref name="root"/>, and one box around all of them in its space.</summary>
        static bool Blocks(Transform root, string id, string[] names, List<GameObject> pieces, out Bounds box)
        {
            box = default;

            foreach (string block in names)
            {
                Transform piece = root.Find(block);
                if (piece == null || !piece.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null) continue;

                Bounds mesh = filter.sharedMesh.bounds;
                Matrix4x4 local = Matrix4x4.TRS(piece.localPosition, piece.localRotation, piece.localScale);

                for (int corner = 0; corner < 8; corner++)
                {
                    var sign = new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f);
                    Vector3 point = local.MultiplyPoint3x4(mesh.center + Vector3.Scale(mesh.extents, sign));

                    if (pieces.Count == 0 && corner == 0) box = new Bounds(point, Vector3.zero);
                    else box.Encapsulate(point);
                }

                pieces.Add(piece.gameObject);
            }

            if (pieces.Count > 0) return true;

            Debug.LogWarning($"[ArtDress] {root.name} has none of {string.Join(", ", names)} to put '{id}' over.");
            return false;
        }

        /// <summary>
        /// Dresses a saved prefab in place: load its contents, <paramref name="dress"/> them, save over
        /// the same path. The GUID and every file id inside survive, so the scenes that place it and
        /// FishNet's spawnable list that names it never notice. Skipped when the root already has a
        /// child called <paramref name="marker"/>, which is how a re-run knows it has been here.
        /// </summary>
        public static bool DressPrefab(string path, string marker, System.Func<Transform, bool> dress)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) return false;

            GameObject root = PrefabUtility.LoadPrefabContents(path);

            try
            {
                if (root.transform.Find(marker) != null || !dress(root.transform)) return false;

                PrefabUtility.SaveAsPrefabAsset(root, path, out bool saved);
                if (!saved) Debug.LogError($"[ArtDress] Could not save the dressed {path}.");
                else Debug.Log($"[ArtDress] Dressed {path}, guid {AssetDatabase.AssetPathToGUID(path)} kept.");
                return saved;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
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

                // At the origin of the wrapper, where NativeBounds measured it: an FBX whose root node
                // carries a translation would otherwise land that far from its cell.
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
                instance.transform.SetParent(wrapper.transform, false);
                instance.transform.localPosition = Vector3.zero;

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
