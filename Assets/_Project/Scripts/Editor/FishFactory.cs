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
    /// What is in the sea, and the rod that gets it out.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.FishFactory.Build
    ///
    /// Same doctrine as every other factory here: creates what does not exist, never overwrites a
    /// number somebody has tuned, and re-applies the structural half on every run. The split for fish
    /// is the same as for animals - the seed table is balance and is written once; what a species
    /// turns into in the bag is a reference, and a reference that has rotted is a bug rather than a
    /// design decision.
    ///
    /// **The table is six rows and three jobs.** Three of them are fish, and they are the income: the
    /// same raw fish item in larger numbers, so one cooking recipe covers the lot and a bigger fish is
    /// a bigger number rather than a second food to balance. One is a boot, which is worth nothing and
    /// is the joke. One is a bottle, which is worth almost nothing and is the joke landing twice. And
    /// one is a pearl, which is worth more than anything else you can carry per kilogram and is the
    /// reason anybody casts a fourth time.
    ///
    /// Balance in one line: at these weights a cast is worth about <c>ExpectedValue</c> in items, and
    /// <c>-fishTest</c> prints that number next to what the same minute of hunting pays, because
    /// "profitable" is a comparison and not an adjective.
    /// </summary>
    public static class FishFactory
    {
        const string Folder = "Assets/_Project/Data/Fish";
        const string CatalogPath = "Assets/_Project/Data/Fish.asset";
        const string ItemFolder = "Assets/_Project/Data/Items";

        /// <summary>The item that opens the minigame. Held in the catalog so one lookup answers it.</summary>
        const string RodId = "fishing_rod";

        readonly struct Seed
        {
            public readonly string Id;
            public readonly string Name;
            public readonly string Description;
            public readonly int Rarity;
            public readonly Vector2 Bite;
            public readonly float Hook;
            public readonly float Distance;
            public readonly float Reel;
            public readonly float Slip;
            public readonly float CalmPull;
            public readonly float RunPull;
            public readonly float Struggle;
            public readonly float Run;

            public Seed(string id, string name, string description, int rarity, Vector2 bite, float hook,
                        float distance, float reel, float slip, float calmPull, float runPull,
                        float struggle, float run)
            {
                Id = id;
                Name = name;
                Description = description;
                Rarity = rarity;
                Bite = bite;
                Hook = hook;
                Distance = distance;
                Reel = reel;
                Slip = slip;
                CalmPull = calmPull;
                RunPull = runPull;
                Struggle = struggle;
                Run = run;
            }
        }

        /// <summary>
        /// The table. Rarities are relative and happen to add up to a hundred, which is a convenience
        /// for reading them as percentages and nothing the code relies on.
        ///
        /// The fight numbers are tuned against one claim: **a sardine lands in a single unbroken pull
        /// and a tuna does not.** A sardine's six metres at 2.4 m/s is 2.5 seconds of reeling, and 2.5
        /// seconds of its calm pull is 0.45 tension - under the snap, comfortably, first time, with no
        /// technique. A tuna is eighteen metres and it runs every three seconds, so holding the button
        /// down snaps the line about a third of the way in. Everything between those two is the
        /// difficulty curve.
        /// </summary>
        static readonly Seed[] Seeds =
        {
            new("sardine", "Sardine", "Small, silver, and the reason you own a rod.",
                rarity: 34, bite: new Vector2(2f, 5f), hook: 1.4f,
                distance: 6f, reel: 2.4f, slip: 0.25f,
                calmPull: 0.18f, runPull: 0.70f, struggle: 3.5f, run: 0.6f),

            new("snapper", "Snapper", "Bright red, deeply annoyed, worth two sardines.",
                rarity: 24, bite: new Vector2(3f, 7f), hook: 1.1f,
                distance: 11f, reel: 2.2f, slip: 0.30f,
                calmPull: 0.24f, runPull: 1.00f, struggle: 3.2f, run: 0.8f),

            new("tuna", "Tuna", "Longer than your arm and stronger than your argument.",
                rarity: 11, bite: new Vector2(5f, 11f), hook: 0.9f,
                distance: 18f, reel: 2.0f, slip: 0.35f,
                calmPull: 0.28f, runPull: 1.40f, struggle: 3.0f, run: 0.9f),

            new("boot", "Sodden Boot", "Somebody's. Was. It is yours now.",
                rarity: 17, bite: new Vector2(1f, 4f), hook: 2f,
                distance: 3f, reel: 3.0f, slip: 0.05f,
                calmPull: 0.02f, runPull: 0.02f, struggle: 9f, run: 0f),

            new("bottle", "Message in a Bottle", "The message is a receipt. The bottle is the point.",
                rarity: 9, bite: new Vector2(2f, 6f), hook: 1.8f,
                distance: 4f, reel: 2.8f, slip: 0.05f,
                calmPull: 0.04f, runPull: 0.04f, struggle: 9f, run: 0f),

            new("oyster", "Pearl Oyster", "Heavy, sullen, and occasionally the best hour of your week.",
                rarity: 5, bite: new Vector2(8f, 16f), hook: 0.8f,
                distance: 14f, reel: 1.7f, slip: 0.40f,
                calmPull: 0.30f, runPull: 1.20f, struggle: 2.6f, run: 0.9f),
        };

        /// <summary>
        /// What each row turns into. Structural, so it is re-applied on every run.
        ///
        /// Three species share <c>fish_raw</c> deliberately: the size of a fish is how many of it you
        /// get, which means the one cooking recipe from #43 covers the whole table and the trader
        /// needs one price rather than six.
        /// </summary>
        static readonly (string Fish, string Item, int Min, int Max)[] Catches =
        {
            ("sardine", "fish_raw", 1, 1),
            ("snapper", "fish_raw", 2, 2),
            ("tuna", "fish_raw", 3, 5),
            ("boot", "boot", 1, 1),
            ("bottle", "empty_bottle", 1, 1),
            ("oyster", "pearl", 1, 1),
        };

        public static void Build()
        {
            Directory.CreateDirectory(Folder);

            int created = 0;

            foreach (Seed seed in Seeds)
            {
                string path = $"{Folder}/{seed.Id}.asset";
                var def = AssetDatabase.LoadAssetAtPath<FishDef>(path);

                if (def != null) continue;

                def = ScriptableObject.CreateInstance<FishDef>();
                def.Configure(seed.Id, seed.Name, seed.Description, seed.Rarity, seed.Bite, seed.Hook,
                              seed.Distance, seed.Reel, seed.Slip, seed.CalmPull, seed.RunPull,
                              seed.Struggle, seed.Run);

                AssetDatabase.CreateAsset(def, path);
                created++;
            }

            // Everything in the folder, not just the seeded rows - a fish dropped in by hand is as
            // real as one from the table. Ordinal so the wire order does not depend on a locale.
            FishDef[] all = AssetDatabase.FindAssets("t:FishDef", new[] { Folder })
                                         .Select(AssetDatabase.GUIDToAssetPath)
                                         .Select(AssetDatabase.LoadAssetAtPath<FishDef>)
                                         .Where(d => d != null)
                                         .OrderBy(d => d.Id, StringComparer.Ordinal)
                                         .ToArray();

            int links = ApplyCatches(all);

            ItemDef rod = Item(RodId);
            if (rod == null)
                Debug.LogError($"[FishFactory] No '{RodId}' item. Run ItemFactory.Build first - "
                               + "without the rod nothing can open the minigame.");

            FishCatalog catalog = EnsureCatalog();
            catalog.Configure(all, rod);
            EditorUtility.SetDirty(catalog);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[FishFactory] {all.Length} in the table ({created} new), {links} catch link(s), "
                      + $"rod {(rod != null ? "ok" : "MISSING")}.");

            int total = catalog.TotalRarity;

            foreach (FishDef def in all)
            {
                float chance = total > 0 ? def.Rarity / (float)total : 0f;
                string item = def.Catch != null ? def.Catch.Id : "nothing";
                string count = def.Low == def.High ? $"{def.Low}x" : $"{def.Low}-{def.High}x";

                Debug.Log($"[FishFactory]   {def.Id,-8} {chance * 100f,5:0.0}%  {count,5} {item,-12} "
                          + $"worth {def.ExpectedValue,5:0.0}  fight {def.PerfectFightSeconds,4:0.0}s");
            }

            Debug.Log($"[FishFactory] a cast is worth {catalog.ExpectedValue:0.0} in items on average, "
                      + $"across {total} points of rarity.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Re-points every row at the item it produces. Runs every build, so an item that was renamed
        /// or regenerated is repaired rather than left as a null nobody notices until a player lands
        /// a tuna and gets nothing.
        /// </summary>
        static int ApplyCatches(IReadOnlyCollection<FishDef> all)
        {
            var byId = all.Where(d => d != null && !string.IsNullOrEmpty(d.Id))
                          .ToDictionary(d => d.Id, d => d);

            int links = 0;

            foreach ((string fishId, string itemId, int min, int max) in Catches)
            {
                if (!byId.TryGetValue(fishId, out FishDef fish)) continue;

                ItemDef item = Item(itemId);
                if (item == null)
                {
                    Debug.LogWarning($"[FishFactory] '{fishId}' wants '{itemId}', which does not exist. "
                                     + "Run ItemFactory.Build.");
                    continue;
                }

                fish.SetCatch(item, min, max);
                EditorUtility.SetDirty(fish);
                links++;
            }

            return links;
        }

        static ItemDef Item(string id) =>
            AssetDatabase.LoadAssetAtPath<ItemDef>($"{ItemFolder}/{id}.asset");

        static FishCatalog EnsureCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<FishCatalog>(CatalogPath);
            if (catalog != null) return catalog;

            catalog = ScriptableObject.CreateInstance<FishCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogPath);

            return catalog;
        }

        /// <summary>The catalog, for anything that has to wire it into a prefab.</summary>
        internal static FishCatalog Catalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<FishCatalog>(CatalogPath);
            if (catalog == null) Debug.LogError($"[FishFactory] No catalog at {CatalogPath}. Run Build.");

            return catalog;
        }
    }
}
