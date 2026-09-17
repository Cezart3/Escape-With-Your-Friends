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
        /// <summary>Distinct materials allowed in one scene. See the class note.</summary>
        const int Budget = 40;

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

            Check($"nothing is wearing a missing shader ({string.Join(", ", broken.Take(5))})",
                  broken.Count == 0);

            Check($"nothing is wearing nothing ({string.Join(", ", blank.Take(5))})", blank.Count == 0);

            Debug.Log($"[LookTest] {materials.Count} distinct materials over {renderers.Length} "
                      + $"renderers: {string.Join(", ", materials.Select(m => m.name).Distinct().OrderBy(n => n))}");

            Check($"the scene shares one palette ({materials.Count} materials, budget {Budget})",
                  materials.Count <= Budget);

            // Terrain, water and the sky are one object each, so instancing would buy them nothing.
            // The check is about the thousands of crates, trees and rocks.
            string[] uninstanced = worn_by.Where(pair => pair.Value > 1 && !pair.Key.enableInstancing)
                                          .Select(pair => $"{pair.Key.name} x{pair.Value}")
                                          .ToArray();

            Check($"and everything that repeats can batch ({string.Join(", ", uninstanced.Take(5))})",
                  uninstanced.Length == 0);

            Debug.Log($"[LookTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[LookTest] {_failed} check(s) failed.");
        }

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[LookTest] FAILED: {what}.");
        }
    }
}
