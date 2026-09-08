using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EscapeWithYourFriends.Data;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The starter item set, and the catalog that indexes it.
    ///
    /// #41's acceptance criterion is that adding an item is a data change rather than a code change,
    /// so this file has to be careful about what it owns. It **creates** an item asset that does not
    /// exist yet and it **rebuilds the catalog**, but it never overwrites an item that is already
    /// there: the table below is a seed, not a source of truth. Once an asset exists, its numbers
    /// belong to whoever is balancing the game, and a rerun of this must not undo their afternoon.
    ///
    /// Adding an item is therefore either a new row here, or - just as valid - a new .asset file
    /// dropped in the folder by hand or by sed. Both end up in the catalog.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.ItemFactory.Build
    /// </summary>
    internal static class ItemFactory
    {
        const string Folder = "Assets/_Project/Data/Items";
        const string CatalogPath = "Assets/_Project/Data/Items.asset";

        readonly struct Seed
        {
            public readonly string Id;
            public readonly string Name;
            public readonly ItemCategory Category;
            public readonly int Stack;
            public readonly float Weight;
            public readonly int Value;
            public readonly string Description;

            public Seed(string id, string name, ItemCategory category, int stack, float weight, int value,
                        string description)
            {
                Id = id;
                Name = name;
                Category = category;
                Stack = stack;
                Weight = weight;
                Value = value;
                Description = description;
            }
        }

        /// <summary>
        /// Enough to build the tier-1 progression in #43 and to have something to steal in #42.
        /// Weights are in kilograms and are chosen against a 40 kg carry limit: a full load is about
        /// two boat parts and nothing else, or eighty planks' worth of nothing useful.
        /// </summary>
        static readonly Seed[] Seeds =
        {
            new("rope", "Rope", ItemCategory.Material, 10, 0.5f, 4, "Fibre twisted out of palm. Holds things together."),
            new("plank", "Plank", ItemCategory.Material, 20, 2f, 3, "Cut from a trunk. The bulk of anything you build."),
            new("scrap_metal", "Scrap Metal", ItemCategory.Material, 20, 1.5f, 8, "Torn off the wreck. Sharp, useful, worth money."),
            new("cloth", "Cloth", ItemCategory.Material, 20, 0.3f, 3, "Sailcloth. Bandages, sails, and covering the shame."),
            new("flint", "Flint", ItemCategory.Material, 10, 0.4f, 2, "Strikes a spark. The whole reason you have fire."),

            // Hunting's half of the material list (#53). A hide is the single most valuable thing an
            // early island gives away, which is the point: it is what makes walking into the trees
            // with a hatchet a better idea than picking up another plank.
            new("hide", "Hide", ItemCategory.Material, 10, 1.2f, 26, "Scraped and dried. The trader always wants one."),
            new("feather", "Feather", ItemCategory.Material, 20, 0.05f, 7, "Light, plentiful, and worth almost nothing individually."),

            // Fishing's half (#54). A boot is worth *zero*, not one: the trader floors every price they
            // are willing to pay at a coin, so one coin would still be a sale and the joke would be a
            // consolation prize. At zero the counter refuses it out loud, and a boot is eight hundred
            // grams of your carry limit that exists only to be sworn at. A pearl is the other end -
            // the best coins-per-kilogram in the game, which is what makes a fourth cast a decision
            // rather than a habit.
            new("boot", "Sodden Boot", ItemCategory.Misc, 5, 0.8f, 0, "Full of water and somebody else's history."),
            new("pearl", "Pearl", ItemCategory.Material, 10, 0.05f, 140, "Small, round, and worth more than the boat you would spend it on."),

            new("coconut", "Coconut", ItemCategory.Food, 8, 0.8f, 5, "Food and water in one inconvenient shell."),

            // Raw fish was seeded at 6 when nothing could catch one. #54 gave it a minigame that takes
            // about fifteen seconds a cast, and at 6 a coin the whole loop paid less than picking up
            // scrap. Twelve puts a kilo of fish just above a kilo of scrap raw, and cooking it - which
            // is the walk back to the fire the food chain wants you to make - roughly doubles that.
            new("fish_raw", "Raw Fish", ItemCategory.Food, 5, 1f, 12, "Edible. Not advisable."),
            new("fish_cooked", "Cooked Fish", ItemCategory.Food, 5, 0.9f, 26, "Advisable."),
            new("meat_raw", "Raw Meat", ItemCategory.Food, 5, 0.9f, 14, "Off the bone. Somebody has to cook it."),
            new("meat_cooked", "Roast Meat", ItemCategory.Food, 5, 0.8f, 30, "The reason the fire was worth building."),
            new("water_bottle", "Water Bottle", ItemCategory.Drink, 1, 1.2f, 10, "Refillable at the filter. Empty it weighs nothing."),
            new("bandage", "Bandage", ItemCategory.Medical, 5, 0.2f, 15, "Stops the bleeding. Does not stop the shouting."),

            new("hatchet", "Hatchet", ItemCategory.Tool, 1, 2.5f, 40, "Chops wood. Also settles arguments."),
            new("knife", "Knife", ItemCategory.Tool, 1, 0.8f, 25, "Cuts rope, guts fish, and fits in a pocket."),
            new("torch", "Torch", ItemCategory.Tool, 1, 1f, 12, "Light, for the twenty minutes a night lasts."),
            new("fishing_rod", "Fishing Rod", ItemCategory.Tool, 1, 1.6f, 35, "For the minigame nobody asked for and everybody plays."),

            // Weapons are items like any other: what makes them weapons is a WeaponDef pointing back
            // at them, not a category. The category is what the shop and the tooltip read.
            new("machete", "Machete", ItemCategory.Weapon, 1, 1.8f, 55, "Long, heavy, and it sends people over ledges."),
            new("bat", "Baseball Bat", ItemCategory.Weapon, 1, 1.2f, 30, "Barely hurts. Sends people into the sea."),
            new("shovel", "Shovel", ItemCategory.Weapon, 1, 2.4f, 45, "Digs, buries, and settles the argument in between."),
            new("pistol", "Pistol", ItemCategory.Weapon, 1, 1.1f, 120, "Settles an argument from across the clearing."),
            new("pistol_ammo", "Pistol Rounds", ItemCategory.Material, 60, 0.02f, 2, "Small, heavy in bulk, gone in seconds."),

            // The rest of #51's arsenal. The SMG deliberately eats pistol rounds: one fewer ammunition
            // type is one fewer thing to carry, one fewer shelf in the shop, and a real reason to keep
            // buying the cheap box.
            new("shotgun", "Shotgun", ItemCategory.Weapon, 1, 3.2f, 200, "Eight pellets and an apology. Devastating up close, useless across a field."),
            new("rifle", "Hunting Rifle", ItemCategory.Weapon, 1, 4.1f, 320, "One round, one animal, a long walk to collect it."),
            new("smg", "Submachine Gun", ItemCategory.Weapon, 1, 2.6f, 260, "Empties a magazine faster than you can decide whether you meant to."),
            new("shotgun_shell", "Shotgun Shells", ItemCategory.Material, 24, 0.05f, 5, "Fat, heavy, and worth it once."),
            new("rifle_ammo", "Rifle Rounds", ItemCategory.Material, 30, 0.03f, 9, "Expensive per shot, which is the point of aiming."),

            // #52's upgrade targets. Each one is the far end of a swap rather than something the
            // shop stocks: you arrive holding the tier below it. Prices are here anyway, because an
            // item with no value is an item the trader will not buy back, and selling the chainsaw
            // you no longer want is a decision worth having.
            new("bat_nailed", "Nailed Bat", ItemCategory.Weapon, 1, 1.5f, 70, "A bat, plus nails. The nails are the upgrade."),
            new("bat_shark", "Shark-Tooth Bat", ItemCategory.Weapon, 1, 1.7f, 150, "Teeth set in resin along the barrel. Deeply unhygienic."),
            new("hatchet_fire", "Fire Axe", ItemCategory.Weapon, 1, 3.2f, 110, "Long handle, heavy head, red paint. Opens doors and arguments."),
            new("chainsaw", "Chainsaw", ItemCategory.Weapon, 1, 6.5f, 260, "Heavy, loud, and it does not stop when you want it to."),
            new("pistol_mk2", "Tuned Pistol", ItemCategory.Weapon, 1, 1.2f, 220, "The same pistol after somebody who knew what they were doing had it."),
            new("pistol_auto", "Machine Pistol", ItemCategory.Weapon, 1, 1.6f, 420, "A pistol that forgot to stop. Empties the box in three seconds."),

            new("boat_part", "Boat Part", ItemCategory.Quest, 1, 12f, 0, "One of the pieces that gets you off this island."),
        };

        public static void Build()
        {
            Directory.CreateDirectory(Folder);

            int created = 0;
            foreach (Seed seed in Seeds)
                if (Ensure(seed)) created++;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            int total = Rebuild();

            Debug.Log($"[ItemFactory] {total} items in the catalog"
                      + (created > 0 ? $", {created} created just now" : ", none created - they all existed")
                      + $". Adding another is a row in ItemFactory or an .asset in {Folder}; both end up here.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>Creates the asset if it is missing. Never touches one that already exists.</summary>
        static bool Ensure(Seed seed)
        {
            string path = $"{Folder}/{seed.Id}.asset";
            if (AssetDatabase.LoadAssetAtPath<ItemDef>(path) != null) return false;

            var def = ScriptableObject.CreateInstance<ItemDef>();
            var so = new SerializedObject(def);

            so.FindProperty("_id").stringValue = seed.Id;
            so.FindProperty("_displayName").stringValue = seed.Name;
            so.FindProperty("_description").stringValue = seed.Description;
            so.FindProperty("_category").enumValueIndex = (int)seed.Category;
            so.FindProperty("_maxStack").intValue = seed.Stack;
            so.FindProperty("_weight").floatValue = seed.Weight;
            so.FindProperty("_value").intValue = seed.Value;
            so.ApplyModifiedPropertiesWithoutUndo();

            AssetDatabase.CreateAsset(def, path);
            return true;
        }

        /// <summary>
        /// Rebuilds the catalog from whatever is in the folder, sorted by id.
        ///
        /// Sorted, and by id rather than by file name, because the sort order *is* the wire format:
        /// every peer has to derive the same index for the same item, and the only thing guaranteed to
        /// be identical across machines is the ordered set of ids.
        /// </summary>
        internal static int Rebuild()
        {
            List<ItemDef> found = AssetDatabase.FindAssets("t:ItemDef", new[] { Folder })
                                               .Select(AssetDatabase.GUIDToAssetPath)
                                               .Select(AssetDatabase.LoadAssetAtPath<ItemDef>)
                                               .Where(def => def != null)
                                               .ToList();

            var seen = new HashSet<string>();
            foreach (ItemDef def in found)
            {
                if (string.IsNullOrWhiteSpace(def.Id))
                    Debug.LogError($"[ItemFactory] {def.name} has no id. It cannot be referred to by a "
                                   + "recipe, a save or a shop, and it will not survive a catalog rebuild.");
                else if (!seen.Add(def.Id))
                    Debug.LogError($"[ItemFactory] Two items share the id '{def.Id}'. Lookups by id are "
                                   + "now a coin flip, and one of them will be unreachable.");
            }

            found.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<ItemCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
                Debug.Log($"[ItemFactory] Created {CatalogPath}.");
            }

            // Through a SerializedObject rather than a public setter: the array is the wire format,
            // and nothing at run time has any business being able to rewrite it.
            var so = new SerializedObject(catalog);
            SerializedProperty items = so.FindProperty("_items");
            items.arraySize = found.Count;
            for (int i = 0; i < found.Count; i++)
                items.GetArrayElementAtIndex(i).objectReferenceValue = found[i];

            so.ApplyModifiedPropertiesWithoutUndo();
            catalog.Invalidate();

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            for (int i = 0; i < found.Count; i++)
                Debug.Log($"[ItemFactory]   {i + 1,3}  {found[i].Id,-14} {found[i].Category,-8} "
                          + $"stack {found[i].MaxStack,3}  {found[i].Weight,5:F1}kg  {found[i].Value,4}c");

            return found.Count;
        }

        /// <summary>The catalog, for anything at bake time that needs to wire it into a prefab.</summary>
        internal static ItemCatalog Catalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(CatalogPath);
            if (catalog == null)
            {
                Directory.CreateDirectory(Folder);
                foreach (Seed seed in Seeds) Ensure(seed);
                AssetDatabase.SaveAssets();
                Rebuild();
                catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(CatalogPath);
            }

            return catalog;
        }
    }
}
