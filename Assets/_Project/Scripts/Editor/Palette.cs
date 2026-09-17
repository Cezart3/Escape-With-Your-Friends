using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Every colour in the game, in one list (#79).
    ///
    /// Before this, each factory called <c>new Material(Shader.Find(...))</c> with a colour picked
    /// where it stood. That is three problems in one line: the island ended up with forty
    /// near-identical browns, none of them instanced, and every baked prefab carried its own material
    /// asset - so a hundred crates were a hundred draw calls and a hundred slightly different crates.
    ///
    /// Now a factory asks for a colour and gets the nearest palette entry, as a shared asset in
    /// <see cref="Folder"/> with instancing on. Snapping is the point rather than a compromise: a
    /// unified look *is* a short list of materials that everything is painted with, and the fastest
    /// way to get one is to make the wrong answer unavailable.
    ///
    /// The runtime side of this is <c>-lookTest</c>, which walks the island and fails on anything
    /// wearing a material that is not from here.
    /// </summary>
    public static class Palette
    {
        public const string Folder = "Assets/_Project/Art/Greybox";

        /// <summary>
        /// The whole palette. Deliberately small and deliberately flat: nothing in this game is shiny
        /// except metal, because a low-poly island with specular highlights everywhere reads as a
        /// mistake rather than as a style.
        /// </summary>
        public static readonly (string Name, Color Colour, float Smoothness)[] Entries =
        {
            ("Wood", new Color(0.42f, 0.30f, 0.19f), 0.12f),
            ("Stone", new Color(0.46f, 0.46f, 0.44f), 0.08f),
            ("Canvas", new Color(0.74f, 0.68f, 0.52f), 0.06f),
            ("Metal", new Color(0.55f, 0.57f, 0.60f), 0.55f),
            ("Accent", new Color(0.72f, 0.28f, 0.22f), 0.20f),
            ("Sand", new Color(0.82f, 0.74f, 0.55f), 0.05f),
            ("Leaf", new Color(0.28f, 0.45f, 0.24f), 0.10f),
            ("Felt", new Color(0.16f, 0.36f, 0.24f), 0.04f),
            ("Skin", new Color(0.78f, 0.60f, 0.47f), 0.15f),
            ("Cloth", new Color(0.32f, 0.38f, 0.48f), 0.08f),
            ("Plastic", new Color(0.86f, 0.84f, 0.80f), 0.35f),
            ("Gold", new Color(0.83f, 0.68f, 0.24f), 0.65f),
            ("Dark", new Color(0.13f, 0.14f, 0.17f), 0.10f),
        };

        static readonly Dictionary<string, Material> Cache = new();

        /// <summary>The palette entry by name. Creates the asset the first time anybody asks.</summary>
        public static Material Named(string name)
        {
            if (Cache.TryGetValue(name, out Material cached) && cached != null) return cached;

            string path = $"{Folder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                material = new Material(shader) { name = name };

                AssetDatabase.CreateAsset(material, path);
                Debug.Log($"[Palette] Generated {path}.");
            }

            Apply(material, name);

            Cache[name] = material;
            return material;
        }

        /// <summary>
        /// The nearest entry to a colour somebody picked by eye. This is how the old per-object
        /// materials were converted without anybody re-choosing forty colours by hand.
        /// </summary>
        public static Material For(Color colour)
        {
            string best = Entries[0].Name;
            float closest = float.MaxValue;

            foreach ((string name, Color entry, float _) in Entries)
            {
                float distance = (entry.r - colour.r) * (entry.r - colour.r)
                                 + (entry.g - colour.g) * (entry.g - colour.g)
                                 + (entry.b - colour.b) * (entry.b - colour.b);

                if (distance >= closest) continue;

                closest = distance;
                best = name;
            }

            return Named(best);
        }

        static void Apply(Material material, string name)
        {
            foreach ((string entry, Color colour, float smoothness) in Entries)
            {
                if (entry != name) continue;

                material.color = colour;
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
                if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
                if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
            }

            // The difference between four thousand draw calls and forty.
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
        }

        /// <summary>
        /// Repaints every prefab that is wearing something that is not from here (#79).
        ///
        /// The factories are fixed, but most of them only build a prefab when it is missing, and
        /// deleting a prefab to force a rebuild changes its GUID - which silently unhooks it from
        /// every scene that places it. So this edits in place instead: it walks the prefabs, snaps
        /// any stray material to the nearest palette entry, and fills in the ones that are wearing
        /// nothing at all.
        ///
        /// "Wearing nothing" is not vandalism, it is the old bug: a material created with
        /// <c>new Material(...)</c> and never saved as an asset cannot be referenced by a prefab, so
        /// it deserialises as null the next time the editor loads. Those renderers have no colour
        /// left to snap to, so they are named instead - which is why <see cref="Guess"/> exists and
        /// why it is allowed to be crude.
        /// </summary>
        [MenuItem("EWYF/Art/Repaint prefabs")]
        public static void Repaint()
        {
            RebuildAll();

            string[] prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Project/Prefabs" });
            int touched = 0;
            int slots = 0;

            foreach (string guid in prefabs)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                bool changed = false;

                foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] worn = renderer.sharedMaterials;

                    for (int i = 0; i < worn.Length; i++)
                    {
                        if (worn[i] != null && AssetDatabase.GetAssetPath(worn[i]).StartsWith(Folder))
                            continue;

                        worn[i] = worn[i] == null
                                  ? Named(Guess(renderer.name))
                                  : For(worn[i].color);

                        changed = true;
                        slots++;
                    }

                    if (changed) renderer.sharedMaterials = worn;
                }

                if (!changed) continue;

                PrefabUtility.SavePrefabAsset(prefab);
                touched++;
            }

            // Instancing on everything, not only the palette. The sea, the sky and the terrain each
            // own a material that a factory generated once and never set this on, and the ones that
            // turn out to be worn twice are exactly the ones -lookTest fails on.
            int batched = 0;

            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/_Project" }))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (material == null || material.enableInstancing) continue;

                material.enableInstancing = true;
                EditorUtility.SetDirty(material);
                batched++;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[Palette] Repainted {slots} slot(s) across {touched} of {prefabs.Length} "
                      + $"prefabs, and switched instancing on for {batched} material(s).");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// What a renderer with no material left should be wearing, by name. Crude on purpose: it
        /// only ever runs on objects that already lost their colour, and the alternative is grey.
        /// </summary>
        static string Guess(string name)
        {
            if (name.Contains("Spot") || name.Contains("Felt") || name.Contains("Table")) return "Felt";
            if (name.Contains("Head") || name.Contains("Hand") || name.Contains("Arm")) return "Skin";
            if (name.Contains("Sign") || name.Contains("Paper") || name.Contains("Sail")) return "Canvas";
            if (name.Contains("Marker") || name.Contains("Light") || name.Contains("Lamp")) return "Accent";
            if (name.Contains("Barrel") || name.Contains("Crate") || name.Contains("Plank")) return "Wood";
            if (name.Contains("Pipe") || name.Contains("Bar") || name.Contains("Rail")) return "Metal";

            return "Stone";
        }

        /// <summary>Writes every entry to disk. The look pass, as one re-runnable command.</summary>
        [MenuItem("EWYF/Art/Rebuild palette")]
        public static void RebuildAll()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets/_Project/Art", "Greybox");

            foreach ((string name, Color _, float _) in Entries) Named(name);

            AssetDatabase.SaveAssets();
            Debug.Log($"[Palette] {Entries.Length} materials in {Folder}.");
        }
    }
}
