using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Core;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The half of #79 a terminal can settle, behind <c>-lookTest</c>. Run it on either island, solo.
    ///
    /// "One coherent look" is a human verdict, but the thing that makes a low-poly island look
    /// incoherent is not a matter of taste and it is countable: dozens of one-off materials, each
    /// baked into one prefab by whichever factory made it, none of them instanced. So this counts
    /// what the scene is actually wearing - how many distinct materials, on what shaders, how many
    /// can batch - and fails on the states that are wrong no matter what the art ends up being: a
    /// missing shader, a material nobody shares, a renderer with nothing on it at all.
    ///
    /// The budget is deliberately a round number rather than a measurement. It is a ratchet: it
    /// passes today, and the day somebody adds their own brown it does not.
    /// </summary>
    public class LookTest : MonoBehaviour
    {
        /// <summary>
        /// Distinct materials allowed in one scene. See the class note. 40 was the greybox's number;
        /// the art pass adds a material per Kenney kit and per flat colour, and this now counts the
        /// terrain's trees, which it never used to see (docs/ART-PLAN.md §5). Ratchet it down to the
        /// measured count plus four after the first real run.
        /// </summary>
        const int Budget = 48;

        /// <summary>The island's one surface shader. See StyleLook in the editor scripts.</summary>
        const string Stylized = "EWYF/Stylized";

        /// <summary>
        /// Palette.Entries' names plus the casino's own (roulette wheel, slot atlas) and the flame: the rest of what StyleLook re-shades. A copy,
        /// because Palette is an editor script; a palette entry missing here only goes unchecked.
        /// </summary>
        static readonly HashSet<string> Painted = new()
        {
            "Wood", "Stone", "Canvas", "Metal", "Accent", "Sand", "Leaf", "WoodDark", "LeafDark",
            "Felt", "Skin", "Cloth", "Plastic", "Gold", "Dark", "RouletteWheel", "Flame", "SlotAtlas",
        };

        /// <summary>Renderers wearing one material before the draw calls are worth instancing away.</summary>
        const int Repeated = 8;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-lookTest")) return;
            _started = true;

            var go = new GameObject("LookTest");
            DontDestroyOnLoad(go);
            go.AddComponent<LookTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            // The island arrives as a loaded scene, not with this object.
            yield return new WaitForSeconds(10f);

            Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            Check($"there is something to look at ({renderers.Length} renderers)", renderers.Length > 50);

            var materials = new HashSet<Material>();
            var blank = new List<string>();
            var broken = new List<string>();
            var worn_by = new Dictionary<Material, int>();

            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;

                Material[] worn = renderer.sharedMaterials;

                if (worn == null || worn.Length == 0 || worn.All(m => m == null))
                {
                    blank.Add(renderer.name);
                    continue;
                }

                foreach (Material material in worn)
                {
                    if (material == null) { blank.Add(renderer.name); continue; }

                    materials.Add(material);

                    Shader shader = material.shader;
                    if (shader == null || shader.name.Contains("InternalErrorShader"))
                    {
                        broken.Add($"{renderer.name}:{material.name}");
                        continue;
                    }

                    if (renderer is MeshRenderer)
                        worn_by[material] = worn_by.TryGetValue(material, out int seen) ? seen + 1 : 1;
                }
            }

            // The terrain draws its trees without a Renderer anywhere, so a forest of stray materials
            // used to be invisible to the loop above. Its prototypes are prefabs; read them directly.
            Trees(materials, broken);
            Visuals();

            Check($"nothing is wearing a missing shader ({string.Join(", ", broken.Take(5))})",
                  broken.Count == 0);

            Check($"nothing is wearing nothing ({string.Join(", ", blank.Take(5))})", blank.Count == 0);

            Debug.Log($"[LookTest] {materials.Count} distinct materials over {renderers.Length} "
                      + $"renderers: {string.Join(", ", materials.Select(m => m.name).Distinct().OrderBy(n => n))}");

            int thirdParty = materials.Count(m => ThirdParty(m.name));
            Debug.Log($"[LookTest] of those, {thirdParty} are the art pass's (Kenney_*, Flat_*, Quaternius_*) "
                      + $"and {materials.Count - thirdParty} are everything else.");

            // One shader across every kit is what makes two artists' models read as one hand
            // (docs/ART-PLAN.md P6, V6). StyleLook.Apply puts them there; this fails if a generator
            // ever makes one on URP/Lit again, or StyleLook fell back because the shader did not
            // import. A shader that imports but fails to compile for the player keeps its name, so
            // that case is the build log's "Shader error in 'EWYF/Stylized'", not this check.
            Debug.Log("[LookTest] shaders: " + string.Join(", ", materials
                .GroupBy(m => m.shader != null ? m.shader.name : "(none)")
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} x{g.Count()}")));

            string[] offStyle = materials.Where(m => (ThirdParty(m.name) || Painted.Contains(m.name))
                                                     && m.shader != null && m.shader.name != Stylized)
                                         .Select(m => $"{m.name} on {m.shader.name}")
                                         .ToArray();

            Check($"every kit and palette material wears {Stylized} ({string.Join(", ", offStyle.Take(5))})",
                  offStyle.Length == 0);

            Check($"the scene shares one palette ({materials.Count} materials, budget {Budget})",
                  materials.Count <= Budget);

            // The check is about the thousands of crates, trees and rocks, not about the sea.
            //
            // "More than one renderer" was too strict and failed on water, which is two enormous
            // meshes: GPU instancing and the SRP Batcher are mutually exclusive in URP, and for two
            // draw calls the batcher is the better of the two. Instancing only starts paying at the
            // point where the draw calls themselves are the cost, so that is where the check starts.
            string[] uninstanced = worn_by.Where(pair => pair.Value > Repeated && !pair.Key.enableInstancing)
                                          .Select(pair => $"{pair.Key.name} x{pair.Value}")
                                          .ToArray();

            Check($"and everything that repeats can batch ({string.Join(", ", uninstanced.Take(5))})",
                  uninstanced.Length == 0);

            Debug.Log($"[LookTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[LookTest] {_failed} check(s) failed.");
        }

        static bool ThirdParty(string name)
            => name.StartsWith("Kenney_") || name.StartsWith("Flat_") || name.StartsWith("Quaternius_");

        /// <summary>
        /// The island's tree prototypes: what they wear, what they cost, and whether they stand up.
        /// A prototype that did not come through the art pass has no <see cref="ArtVisual"/>, and that
        /// is a failure in its own right - it means a bake ran against a stale prefab.
        /// </summary>
        void Trees(HashSet<Material> materials, List<string> broken)
        {
            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains.Length == 0)
            {
                Debug.Log("[LookTest] No terrain in this scene; the tree checks do not apply.");
                return;
            }

            var unmarked = new List<string>();
            var heavy = new List<string>();
            var lying = new List<string>();
            int prototypes = 0;

            foreach (Terrain terrain in terrains)
            {
                foreach (TreePrototype prototype in terrain.terrainData.treePrototypes)
                {
                    GameObject prefab = prototype.prefab;
                    if (prefab == null) { unmarked.Add("(missing prefab)"); continue; }

                    prototypes++;

                    foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material == null) continue;
                        materials.Add(material);
                        if (material.shader == null || material.shader.name.Contains("InternalErrorShader"))
                            broken.Add($"{prefab.name}:{material.name}");
                    }

                    var visual = prefab.GetComponent<ArtVisual>();
                    if (visual == null) { unmarked.Add(prefab.name); continue; }

                    int triangles = ArtVisual.Triangles(prefab);
                    int cap = ArtVisual.Cap(visual.Category);
                    if (triangles > cap) heavy.Add($"{prefab.name} {triangles}/{cap}");

                    Vector3 size = FirstLodBounds(prefab).size;
                    if (visual.Upright && !ArtVisual.Standing(size))
                        lying.Add($"{prefab.name} {size.x:F1}x{size.y:F1}x{size.z:F1}");
                }
            }

            Debug.Log($"[LookTest] {prototypes} tree prototypes on {terrains.Length} terrain(s).");

            Check($"every tree came through the art pass ({string.Join(", ", unmarked.Take(5))})",
                  unmarked.Count == 0);
            Check($"no tree is over its triangle cap ({string.Join(", ", heavy.Take(5))})", heavy.Count == 0);
            Check($"and every tree is standing up ({string.Join(", ", lying.Take(5))})", lying.Count == 0);
        }

        /// <summary>
        /// Every placed third-party model: under its cap, and standing if it is meant to. The markers
        /// are put there by the editor, which is the only place that knew what each model was for.
        /// </summary>
        void Visuals()
        {
            ArtVisual[] visuals = FindObjectsByType<ArtVisual>(FindObjectsSortMode.None);
            var heavy = new List<string>();
            var lying = new List<string>();

            foreach (ArtVisual visual in visuals)
            {
                int triangles = ArtVisual.Triangles(visual.gameObject);
                int cap = ArtVisual.Cap(visual.Category);
                if (triangles > cap) heavy.Add($"{visual.Id} {triangles}/{cap}");

                if (!visual.Upright) continue;

                Renderer[] renderers = visual.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0) continue;

                Bounds bounds = renderers[0].bounds;
                foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);

                if (!ArtVisual.Standing(bounds.size))
                    lying.Add($"{visual.Id} at {visual.transform.position:F0}");
            }

            Debug.Log($"[LookTest] {visuals.Length} placed art models.");

            Check($"no placed model is over its triangle cap ({string.Join(", ", heavy.Take(5))})",
                  heavy.Count == 0);
            Check($"and every one meant to stand is standing ({string.Join(", ", lying.Take(5))})",
                  lying.Count == 0);
        }

        /// <summary>
        /// Bounds of a prefab asset's first LOD, from its meshes. A prefab that is not in a scene has
        /// no renderer bounds - they are computed by the renderer when it draws - so this does the
        /// transform by hand, which is also exactly what the terrain does with it.
        /// </summary>
        static Bounds FirstLodBounds(GameObject prefab)
        {
            var group = prefab.GetComponent<LODGroup>();
            LOD[] lods = group != null ? group.GetLODs() : null;
            Renderer[] renderers = lods != null && lods.Length > 0
                ? lods[0].renderers
                : prefab.GetComponentsInChildren<Renderer>(true);

            Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
            var bounds = new Bounds();
            bool first = true;

            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !renderer.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null)
                    continue;

                Matrix4x4 matrix = toRoot * renderer.transform.localToWorldMatrix;
                Bounds mesh = filter.sharedMesh.bounds;

                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = mesh.center + Vector3.Scale(mesh.extents, new Vector3(
                        (corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
                    Vector3 point = matrix.MultiplyPoint3x4(local);

                    if (first) { bounds = new Bounds(point, Vector3.zero); first = false; }
                    else bounds.Encapsulate(point);
                }
            }

            return bounds;
        }

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[LookTest] FAILED: {what}.");
        }
    }
}
