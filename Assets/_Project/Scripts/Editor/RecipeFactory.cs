using System.Collections.Generic;
using System.IO;
using System.Linq;
using EscapeWithYourFriends.Data;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The tier-1 progression, as data.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.RecipeFactory.Build
    ///
    /// #43's acceptance is that "the tier-1 progression (tools, campfire, water filter) is craftable",
    /// and the shape of that progression is the interesting part rather than the numbers:
    ///
    ///   hand      -> bandage, torch, knife, and the campfire itself
    ///   campfire  -> cooked fish, and a reason to survive the night
    ///   bench     -> hatchet, fishing rod, water filter
    ///   filter    -> a full bottle from an empty one
    ///
    /// Each station is unlocked by something you made at the last one. The campfire is craftable by
    /// hand on purpose: it is the first thing a player builds, and gating it behind a bench they have
    /// to walk back to camp for would put the whole progression behind a walk.
    ///
    /// Same rules as the other factories: creates, never overwrites, rebuilds the catalog whole.
    /// </summary>
    public static class RecipeFactory
    {
        const string Folder = "Assets/_Project/Data/Recipes";
        const string CatalogPath = "Assets/_Project/Data/Recipes.asset";
        const string ItemFolder = "Assets/_Project/Data/Items";

        readonly struct Seed
        {
            public readonly string Id;
            public readonly string Name;
            public readonly string Description;
            public readonly CraftStation Station;
            public readonly float Seconds;
            public readonly int Tier;
            public readonly (string Item, int Count)[] Inputs;
            public readonly string OutputItem;
            public readonly int OutputCount;
            public readonly string OutputStructure;

            public Seed(string id, string name, string description, CraftStation station, float seconds,
                        int tier, (string, int)[] inputs, string outputItem = null, int outputCount = 1,
                        string outputStructure = null)
            {
                Id = id;
                Name = name;
                Description = description;
                Station = station;
                Seconds = seconds;
                Tier = tier;
                Inputs = inputs;
                OutputItem = outputItem;
                OutputCount = outputCount;
                OutputStructure = outputStructure;
            }
        }

        static readonly Seed[] Seeds =
        {
            // ---- by hand, in the field ------------------------------------------------------
            new("bandage", "Bandage", "Two strips of sailcloth. Better than bleeding.",
                CraftStation.Hand, 2.5f, 1, new[] { ("cloth", 2) }, outputItem: "bandage"),

            new("torch", "Torch", "A plank, a rag and a spark. Twenty minutes of not tripping over.",
                CraftStation.Hand, 3f, 1, new[] { ("plank", 1), ("cloth", 1), ("flint", 1) },
                outputItem: "torch"),

            new("knife", "Knife", "Scrap ground to an edge and bound with rope.",
                CraftStation.Hand, 4f, 1, new[] { ("scrap_metal", 1), ("rope", 1) }, outputItem: "knife"),

            new("rope", "Rope", "Palm fibre, twisted. Sailcloth works too, in a pinch.",
                CraftStation.Hand, 3f, 1, new[] { ("cloth", 3) }, outputItem: "rope", outputCount: 2),

            // The first structure, and by hand on purpose: gating it behind a bench would put the
            // whole progression behind a walk back to camp.
            new("campfire", "Campfire", "Four planks and a spark. The reason night is survivable.",
                CraftStation.Hand, 5f, 1, new[] { ("plank", 4), ("flint", 1) },
                outputStructure: "Campfire"),

            // ---- at a fire ------------------------------------------------------------------
            new("cook_fish", "Cook Fish", "Raw fish is edible. This is better in every way.",
                CraftStation.Fire, 4f, 2, new[] { ("fish_raw", 1) }, outputItem: "fish_cooked"),

            // ---- at the bench ---------------------------------------------------------------
            new("hatchet", "Hatchet", "Heavy scrap on a plank haft. Chops wood, settles arguments.",
                CraftStation.Bench, 6f, 2, new[] { ("scrap_metal", 2), ("plank", 1), ("rope", 1) },
                outputItem: "hatchet"),

            new("fishing_rod", "Fishing Rod", "Two planks and a lot of rope.",
                CraftStation.Bench, 6f, 2, new[] { ("plank", 2), ("rope", 2) }, outputItem: "fishing_rod"),

            new("bottle", "Bottle", "Scrap and sailcloth, shaped into something that holds water.",
                CraftStation.Bench, 4f, 2, new[] { ("scrap_metal", 1), ("cloth", 1) },
                outputItem: "empty_bottle"),

            new("water_filter", "Water Filter", "Sand, cloth and a barrel. The end of the thirst problem.",
                CraftStation.Bench, 8f, 3, new[] { ("plank", 2), ("cloth", 2), ("scrap_metal", 1) },
                outputStructure: "WaterFilter"),

            new("crafting_bench", "Crafting Bench", "A second bench, for a second camp.",
                CraftStation.Bench, 8f, 3, new[] { ("plank", 6), ("rope", 2) },
                outputStructure: "CraftingBench"),

            // ---- at the filter --------------------------------------------------------------
            new("fill_bottle", "Fill Bottle", "An empty bottle and a working filter.",
                CraftStation.Filter, 2f, 3, new[] { ("empty_bottle", 1) }, outputItem: "water_bottle"),
        };

        public static void Build()
        {
            Directory.CreateDirectory(Folder);

            int created = Seeds.Count(Ensure);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            int total = Rebuild();

            Debug.Log($"[RecipeFactory] {total} recipe(s) in the catalog"
                      + (created > 0 ? $", {created} created just now" : ", none created - they all existed")
                      + ". Another one is a row in RecipeFactory or an .asset in the folder.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static bool Ensure(Seed seed)
        {
            string path = $"{Folder}/{seed.Id}.asset";
            if (AssetDatabase.LoadAssetAtPath<RecipeDef>(path) != null) return false;

            var def = ScriptableObject.CreateInstance<RecipeDef>();
            var so = new SerializedObject(def);

            so.FindProperty("_id").stringValue = seed.Id;
            so.FindProperty("_displayName").stringValue = seed.Name;
            so.FindProperty("_description").stringValue = seed.Description;
            so.FindProperty("_station").enumValueIndex = (int)seed.Station;
            so.FindProperty("_seconds").floatValue = seed.Seconds;
            so.FindProperty("_tier").intValue = seed.Tier;

            SerializedProperty inputs = so.FindProperty("_inputs");
            inputs.arraySize = seed.Inputs.Length;

            for (int i = 0; i < seed.Inputs.Length; i++)
            {
                (string item, int count) = seed.Inputs[i];
                SerializedProperty entry = inputs.GetArrayElementAtIndex(i);

                ItemDef def2 = Item(item);
                if (def2 == null)
                    Debug.LogError($"[RecipeFactory] '{seed.Id}' needs item '{item}', which does not "
                                   + "exist. Run ItemFactory.Build first.");

                entry.FindPropertyRelative("Item").objectReferenceValue = def2;
                entry.FindPropertyRelative("Count").intValue = count;
            }

            if (seed.OutputItem != null)
            {
                ItemDef output = Item(seed.OutputItem);
                if (output == null)
                    Debug.LogError($"[RecipeFactory] '{seed.Id}' makes item '{seed.OutputItem}', which "
                                   + "does not exist.");

                so.FindProperty("_outputItem").objectReferenceValue = output;
                so.FindProperty("_outputCount").intValue = seed.OutputCount;
            }

            if (seed.OutputStructure != null)
            {
                GameObject structure = Structure(seed.OutputStructure);
                if (structure == null)
                    Debug.LogError($"[RecipeFactory] '{seed.Id}' builds '{seed.OutputStructure}', which "
                                   + "does not exist. Run StationBuilder.Build first.");

                so.FindProperty("_outputStructure").objectReferenceValue = structure;
            }

            so.ApplyModifiedPropertiesWithoutUndo();

            AssetDatabase.CreateAsset(def, path);
            return true;
        }

        static ItemDef Item(string id) => AssetDatabase.LoadAssetAtPath<ItemDef>($"{ItemFolder}/{id}.asset");

        static GameObject Structure(string name)
            => AssetDatabase.LoadAssetAtPath<GameObject>(
                $"Assets/_Project/Prefabs/Stations/{name}.prefab");

        static int Rebuild()
        {
            List<RecipeDef> found = AssetDatabase.FindAssets("t:RecipeDef", new[] { Folder })
                                                 .Select(AssetDatabase.GUIDToAssetPath)
                                                 .Select(AssetDatabase.LoadAssetAtPath<RecipeDef>)
                                                 .Where(def => def != null)
                                                 .ToList();

            var seen = new HashSet<string>();
            foreach (RecipeDef def in found)
            {
                if (string.IsNullOrWhiteSpace(def.Id))
                    Debug.LogError($"[RecipeFactory] {def.name} has no id and will not survive a rebuild.");
                else if (!seen.Add(def.Id))
                    Debug.LogError($"[RecipeFactory] Two recipes share the id '{def.Id}'.");

                if (!def.IsValid)
                    Debug.LogError($"[RecipeFactory] '{def.Id}' has no inputs or no output; it would be "
                                   + "either free or pointless.");
            }

            found.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            var catalog = AssetDatabase.LoadAssetAtPath<RecipeCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<RecipeCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
                Debug.Log($"[RecipeFactory] Created {CatalogPath}.");
            }

            var so = new SerializedObject(catalog);
            SerializedProperty recipes = so.FindProperty("_recipes");
            recipes.arraySize = found.Count;
            for (int i = 0; i < found.Count; i++)
                recipes.GetArrayElementAtIndex(i).objectReferenceValue = found[i];

            so.ApplyModifiedPropertiesWithoutUndo();
            catalog.Invalidate();

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            foreach (CraftStation station in new[]
                     { CraftStation.Hand, CraftStation.Fire, CraftStation.Bench, CraftStation.Filter })
            {
                foreach (RecipeDef def in found.Where(r => r.Station == station))
                    Debug.Log($"[RecipeFactory]   {station,-6} t{def.Tier}  {def.Id,-15} "
                              + $"{def.Seconds,4:F1}s  {def.Describe()}");
            }

            return found.Count;
        }

        /// <summary>The catalog, for anything at bake time that needs to wire it into a prefab.</summary>
        internal static RecipeCatalog Catalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<RecipeCatalog>(CatalogPath);
            if (catalog == null)
            {
                Directory.CreateDirectory(Folder);
                foreach (Seed seed in Seeds) Ensure(seed);
                AssetDatabase.SaveAssets();
                Rebuild();
                catalog = AssetDatabase.LoadAssetAtPath<RecipeCatalog>(CatalogPath);
            }

            return catalog;
        }
    }
}
