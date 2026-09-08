using System.Collections.Generic;
using System.IO;
using System.Linq;
using EscapeWithYourFriends.Data;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Every weapon upgrade in the game, and the catalog that indexes them.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.UpgradeFactory.Build
    ///
    /// Run after <see cref="WeaponFactory"/>, which is where the weapons at both ends of a line come
    /// from. An upgrade adds no weapon of its own: it is a row naming two that already exist, plus
    /// what the swap costs and who does the work.
    ///
    /// **The deltas are derived, never typed.** <see cref="UpgradeDef.Configure"/> reads the two
    /// weapons and works out what changed, so the number on the shelf and the number in your hand
    /// come from the same place and cannot drift apart. Typing "+30% damage" into a seed table would
    /// have been one edit away from a shop that lies.
    ///
    /// This is also the one thing that writes <c>WeaponDef._upgradesTo</c>. That field has existed
    /// since #49 as a link nothing followed; the link and the upgrade that uses it are now made in
    /// the same pass, which is what keeps them agreeing with each other.
    /// </summary>
    public static class UpgradeFactory
    {
        const string UpgradeDir = "Assets/_Project/Data/Upgrades";
        const string CatalogPath = "Assets/_Project/Data/Upgrades.asset";
        const string ItemFolder = "Assets/_Project/Data/Items";

        /// <summary>One step up a line. Two weapon ids, a venue, and a bill.</summary>
        readonly struct Seed
        {
            public readonly string Id;
            public readonly string Name;
            public readonly string From;
            public readonly string To;
            public readonly UpgradeVenue Venue;
            public readonly int Price;
            public readonly (string Item, int Count)[] Materials;
            public readonly string Description;

            public Seed(string id, string name, string from, string to, UpgradeVenue venue, int price,
                        (string, int)[] materials, string description)
            {
                Id = id; Name = name; From = from; To = to; Venue = venue; Price = price;
                Materials = materials; Description = description;
            }
        }

        /// <summary>
        /// Three lines, two steps each.
        ///
        /// **A bench costs materials, a trader costs money.** That split is the reason there are two
        /// venues at all: the bench is you bolting nails into a bat out of what the island gave you,
        /// and the trader is somebody with a workshop charging for it. It also means the two halves
        /// of the economy each have a use - scrap you would otherwise sell, and cash you would
        /// otherwise sit on - and that a group with no money can still climb the melee lines.
        /// </summary>
        static readonly Seed[] Seeds =
        {
            new("bat_nails", "Nail the Bat", "bat", "bat_nailed", UpgradeVenue.Bench, 0,
                new[] { ("scrap_metal", 4), ("plank", 1) },
                "Four nails through the fat end. Anybody can do this and everybody should."),

            new("bat_teeth", "Set the Teeth", "bat_nailed", "bat_shark", UpgradeVenue.Bench, 0,
                new[] { ("scrap_metal", 6), ("rope", 2), ("flint", 4) },
                "Flint and wire down both sides. Looks unhinged, works."),

            new("hatchet_fire", "Reforge the Hatchet", "hatchet", "hatchet_fire", UpgradeVenue.Bench, 0,
                new[] { ("scrap_metal", 5), ("plank", 2) },
                "A longer handle and a heavier head off the wreck."),

            new("hatchet_saw", "Motorise It", "hatchet_fire", "chainsaw", UpgradeVenue.Trader, 180,
                new[] { ("scrap_metal", 8) },
                "The trader has an engine and will not explain where it came from."),

            new("pistol_tune", "Tune the Pistol", "pistol", "pistol_mk2", UpgradeVenue.Trader, 150,
                new[] { ("scrap_metal", 3) },
                "Bored, ported, and a longer magazine. Same box of rounds."),

            new("pistol_full", "Go Automatic", "pistol_mk2", "pistol_auto", UpgradeVenue.Trader, 380,
                new[] { ("scrap_metal", 6), ("rope", 1) },
                "Ten rounds a second out of a pistol. The trader is very clear that this is illegal."),
        };

        public static void Build()
        {
            BuildAll();

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static UpgradeCatalog BuildAll()
        {
            Directory.CreateDirectory(UpgradeDir);

            WeaponCatalog weapons = WeaponFactory.Catalog();
            if (weapons == null)
            {
                Debug.LogError("[UpgradeFactory] No weapon catalog. Run WeaponFactory.Build first.");
                return null;
            }

            var built = new List<UpgradeDef>();
            foreach (Seed seed in Seeds)
            {
                UpgradeDef def = Ensure(seed, weapons);
                if (def != null) built.Add(def);
            }

            UpgradeCatalog catalog = Rebuild(built);
            LinkWeapons(catalog);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[UpgradeFactory] {catalog.Count} upgrade(s).");

            for (int i = 0; i < catalog.Count; i++)
            {
                UpgradeDef def = catalog.Upgrades[i];
                Debug.Log($"[UpgradeFactory]   [{i + 1}] {def.Describe()}");

                if (!def.Verify(out string mismatch))
                    Debug.LogError($"[UpgradeFactory] '{def.Id}' advertises what it does not "
                                   + $"deliver: {mismatch}.");

                if (!def.IsStrictlyBetter)
                    Debug.LogError($"[UpgradeFactory] '{def.Id}' is not an upgrade: it has to be a "
                                   + "tier higher and worse at nothing.");
            }

            // The curve, printed once, because "a visible power curve from island 1 gear to island 2
            // gear" is the acceptance and a number nobody prints is a number nobody checks.
            foreach (WeaponDef start in Starts(catalog))
            {
                List<WeaponDef> line = catalog.LineFrom(start);
                if (line.Count < 2) continue;

                Debug.Log($"[UpgradeFactory]   line: "
                          + string.Join(" -> ",
                                        line.Select(w => $"{w.Id} t{w.Tier} {w.Hit.Damage:F0}dmg "
                                                         + $"{1f / w.Cooldown:F1}/s")));
            }

            return catalog;
        }

        /// <summary>Weapons that start a line: something upgrades out of them, nothing into them.</summary>
        static IEnumerable<WeaponDef> Starts(UpgradeCatalog catalog)
        {
            var targets = new HashSet<WeaponDef>(catalog.Upgrades.Where(u => u != null)
                                                        .Select(u => u.To));

            return catalog.Upgrades.Where(u => u != null && u.From != null && !targets.Contains(u.From))
                          .Select(u => u.From)
                          .Distinct();
        }

        static UpgradeDef Ensure(Seed seed, WeaponCatalog weapons)
        {
            WeaponDef from = weapons.Find(seed.From);
            WeaponDef to = weapons.Find(seed.To);

            if (from == null || to == null)
            {
                Debug.LogError($"[UpgradeFactory] '{seed.Id}' wants "
                               + $"{(from == null ? seed.From : seed.To)}, which is not a weapon. "
                               + "Run WeaponFactory.Build first.");
                return null;
            }

            string path = $"{UpgradeDir}/{seed.Id}.asset";
            var def = AssetDatabase.LoadAssetAtPath<UpgradeDef>(path);
            bool fresh = def == null;

            if (fresh)
            {
                def = ScriptableObject.CreateInstance<UpgradeDef>();
                AssetDatabase.CreateAsset(def, path);
                Debug.Log($"[UpgradeFactory] Created {path}.");
            }

            var so = new SerializedObject(def);

            // Same split as every other factory: what somebody balances is written once, on creation.
            // What is structural - which two weapons this joins, and therefore what it advertises -
            // is re-applied every run, because a delta that no longer matches its weapons is a lie.
            if (fresh)
            {
                so.FindProperty("_id").stringValue = seed.Id;
                so.FindProperty("_displayName").stringValue = seed.Name;
                so.FindProperty("_description").stringValue = seed.Description;
                so.FindProperty("_venue").enumValueIndex = (int)seed.Venue;
                so.FindProperty("_price").intValue = seed.Price;

                SerializedProperty materials = so.FindProperty("_materials");
                materials.arraySize = seed.Materials?.Length ?? 0;

                for (int i = 0; i < materials.arraySize; i++)
                {
                    (string id, int count) = seed.Materials[i];
                    SerializedProperty entry = materials.GetArrayElementAtIndex(i);
                    entry.FindPropertyRelative("Item").objectReferenceValue = Item(id, seed.Id);
                    entry.FindPropertyRelative("Count").intValue = Mathf.Max(1, count);
                }
            }

            so.ApplyModifiedPropertiesWithoutUndo();

            def.Configure(from, to);
            EditorUtility.SetDirty(def);

            return def;
        }

        static ItemDef Item(string id, string owner)
        {
            if (string.IsNullOrEmpty(id)) return null;

            var item = AssetDatabase.LoadAssetAtPath<ItemDef>($"{ItemFolder}/{id}.asset");
            if (item == null)
                Debug.LogError($"[UpgradeFactory] '{owner}' wants the item '{id}', which does not "
                               + "exist. Run ItemFactory.Build first.");

            return item;
        }

        /// <summary>
        /// Writes <c>UpgradesTo</c> onto every weapon that has an upgrade, and clears it on every one
        /// that does not. Clearing matters: an upgrade somebody deletes has to leave the link behind
        /// it, or the weapon still claims to become something the catalog cannot sell.
        /// </summary>
        static void LinkWeapons(UpgradeCatalog catalog)
        {
            WeaponCatalog weapons = WeaponFactory.Catalog();
            if (weapons == null || catalog == null) return;

            int linked = 0;

            foreach (WeaponDef weapon in weapons.Weapons)
            {
                if (weapon == null) continue;

                UpgradeDef upgrade = catalog.For(weapon);
                WeaponDef next = upgrade != null ? upgrade.To : null;

                if (weapon.UpgradesTo == next) continue;

                var so = new SerializedObject(weapon);
                so.FindProperty("_upgradesTo").objectReferenceValue = next;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(weapon);

                if (next != null) linked++;
            }

            if (linked > 0) Debug.Log($"[UpgradeFactory] Linked {linked} weapon(s) to what they become.");
        }

        /// <summary>
        /// Rebuilds the catalog from everything in the folder, sorted by id. Reading the folder rather
        /// than the seed table is the point: an upgrade somebody adds by hand is in the catalog too,
        /// and a seed that gets deleted actually leaves.
        /// </summary>
        static UpgradeCatalog Rebuild(List<UpgradeDef> seeded)
        {
            UpgradeDef[] all = AssetDatabase.FindAssets("t:UpgradeDef", new[] { UpgradeDir })
                                            .Select(AssetDatabase.GUIDToAssetPath)
                                            .Select(AssetDatabase.LoadAssetAtPath<UpgradeDef>)
                                            .Where(u => u != null)
                                            .Concat(seeded)
                                            .Distinct()
                                            .OrderBy(u => u.Id, System.StringComparer.Ordinal)
                                            .ToArray();

            var catalog = AssetDatabase.LoadAssetAtPath<UpgradeCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<UpgradeCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
                Debug.Log($"[UpgradeFactory] Created {CatalogPath}.");
            }

            catalog.Configure(all);
            EditorUtility.SetDirty(catalog);

            // Two upgrades out of one weapon would make "what does this become" a question with two
            // answers, and the catalog answers it with whichever sorted first. Say so loudly.
            var seen = new HashSet<WeaponDef>();
            foreach (UpgradeDef def in all)
            {
                if (def.From != null && !seen.Add(def.From))
                    Debug.LogError($"[UpgradeFactory] More than one upgrade starts from "
                                   + $"'{def.From.Id}'. Lines, not trees - only the first is used.");
            }

            return catalog;
        }

        /// <summary>The catalog, for anything at bake time that needs it.</summary>
        internal static UpgradeCatalog Catalog()
            => AssetDatabase.LoadAssetAtPath<UpgradeCatalog>(CatalogPath) ?? BuildAll();
    }
}
