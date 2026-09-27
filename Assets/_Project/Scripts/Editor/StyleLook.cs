using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Puts every kit material and every palette colour on <c>EWYF/Stylized</c>, with the numbers
    /// below (docs/ART-PLAN.md P6, V6). The island mixes Kenney swatch atlases, Quaternius painted
    /// textures and flat colours; lit by URP/Lit they read as three kits. Lit by one banded shader,
    /// with the painted kit's brush strokes turned down to the swatches' level, they read as one.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . -logFile style.log
    ///     -executeMethod EscapeWithYourFriends.EditorTools.StyleLook.Apply
    ///
    /// Re-runnable, and run at the end of <see cref="ArtLibrary.BuildAll"/> and by every palette
    /// write, so a material generated before this existed is re-shaded in place - same asset, same
    /// GUID, same prefabs pointing at it. The shader shares URP/Lit's property names (_BaseMap,
    /// _BaseColor, _Cutoff, _AlphaClip, _Cull, _Smoothness), so switching keeps every texture and colour.
    /// </summary>
    public static class StyleLook
    {
        public const string ShaderName = "EWYF/Stylized";

        // The look, shared by everything. Tuning it is editing these four lines and running Apply.
        const float RampCentre = 0.05f;
        const float RampSoftness = 0.08f;
        const float RimStrength = 0.2f;
        static readonly Color ShadowTint = new(0.86f, 0.9f, 1.05f, 1f);

        /// <summary>
        /// Per kit, by material-name prefix, first match wins: how much painted detail survives and how
        /// saturated it is. The nature kit's bark and leaves are painted at a finer grain than anything
        /// else on the island, so they lose most of it; the swatch kits have none to lose.
        /// </summary>
        static readonly (string Prefix, float Detail, float Saturation, float Brightness)[] Kits =
        {
            ("Quaternius_NatureMegaKit_", 0.45f, 0.92f, 1.0f),
            ("Quaternius_", 1f, 0.95f, 1.0f),
            ("Kenney_", 1f, 1f, 1f),
            ("Flat_", 1f, 1f, 1f),
        };

        static Shader _stylized;

        /// <summary>The shared shader, or URP/Lit when it failed to import (Restyle reports that).</summary>
        public static Shader Stylized
        {
            get
            {
                if (_stylized == null) _stylized = Shader.Find(ShaderName);
                return _stylized != null ? _stylized : Shader.Find("Universal Render Pipeline/Lit");
            }
        }

        /// <summary>A new material already on the shared shader, instanced, matte.</summary>
        internal static Material New(string name)
        {
            var material = new Material(Stylized) { name = name };
            material.SetFloat("_Smoothness", 0.1f);
            Wear(material);
            return material;
        }

        /// <summary>Switches one material to the shared shader and writes the look into it.</summary>
        internal static void Wear(Material material)
        {
            Shader shader = Stylized;
            if (material.shader != shader) material.shader = shader;

            // Instanced and saved whatever the shader: on the URP/Lit fallback the palette still
            // needs both, and it has no other place that does them.
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            if (shader.name != ShaderName) return;

            float detail = 1f, saturation = 1f, brightness = 1f;
            foreach (var kit in Kits)
            {
                if (!material.name.StartsWith(kit.Prefix)) continue;
                (detail, saturation, brightness) = (kit.Detail, kit.Saturation, kit.Brightness);
                break;
            }

            material.SetFloat("_Detail", detail);

            // The second texture fetch is compiled in only where it changes something.
            if (detail < 1f) material.EnableKeyword("_DETAIL_SOFTEN");
            else material.DisableKeyword("_DETAIL_SOFTEN");
            material.SetFloat("_Saturation", saturation);
            material.SetFloat("_Brightness", brightness);
            material.SetFloat("_RampCentre", RampCentre);
            material.SetFloat("_RampSoftness", RampSoftness);
            material.SetFloat("_RimStrength", RimStrength);
            material.SetColor("_ShadowTint", ShadowTint);

            // The keyword is what the passes compile against; the float is what the inspector shows.
            // A material carried over from URP/Lit has the float right and the keyword possibly not.
            if (material.GetFloat("_AlphaClip") > 0.5f) material.EnableKeyword("_ALPHATEST_ON");
            else material.DisableKeyword("_ALPHATEST_ON");
        }

        /// <summary>Every kit material and every palette entry, re-shaded. Batchmode entry.</summary>
        [MenuItem("EWYF/Art/Apply stylized look")]
        public static void Apply()
        {
            int count = Restyle();
            AssetDatabase.SaveAssets();

            if (Application.isBatchMode) EditorApplication.Exit(count >= 0 ? 0 : 1);
        }

        /// <summary>The walk itself, without saving or exiting. -1 when the shader is missing.</summary>
        internal static int Restyle()
        {
            if (Stylized.name != ShaderName)
            {
                Debug.LogError($"[StyleLook] Shader '{ShaderName}' not found; is Art/Stylized/Stylized.shader "
                               + "imported without errors? Materials left on URP/Lit.");
                return -1;
            }

            string[] folders = { ArtLibrary.MaterialFolder, Palette.Folder, "Assets/_Project/Art/Casino" };
            int count = 0;

            foreach (string folder in folders)
            {
                if (!AssetDatabase.IsValidFolder(folder)) continue;

                foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { folder }))
                {
                    var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                    if (material == null) continue;
                    Wear(material);
                    count++;
                }
            }

            Debug.Log($"[StyleLook] {count} materials on {ShaderName} "
                      + $"(ramp {RampCentre}±{RampSoftness}, rim {RimStrength}).");
            return count;
        }
    }
}
