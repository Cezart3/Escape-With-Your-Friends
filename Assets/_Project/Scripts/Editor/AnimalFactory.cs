using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EscapeWithYourFriends.AI;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.World;
using FishNet.Component.Transforming;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The island's wildlife: three species, one prefab, and the catalog that indexes them.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.AnimalFactory.Build
    ///
    /// Same doctrine as every other factory here - creates what does not exist, never overwrites a
    /// number somebody has tuned, and rebuilds the structural half on every run. For animals the
    /// split is: the seed table below is balance and is written once, the loot tables are structure
    /// and are re-applied always. A hide that has stopped being a hide is a broken reference, not a
    /// design decision, in exactly the way <see cref="BuffFactory"/>'s links are.
    ///
    /// There is one prefab for all three species, and there will be one for all eight. The body's
    /// size, colour, collider, speed, health and loot are read off the definition at spawn time, so a
    /// fourth animal is a row in <see cref="Seeds"/> - or a .asset file dropped in the folder by hand
    /// - and never a new prefab, a new script or a new registration.
    /// </summary>
    public static class AnimalFactory
    {
        const string Folder = "Assets/_Project/Data/Animals";
        const string CatalogPath = "Assets/_Project/Data/Animals.asset";
        const string ItemFolder = "Assets/_Project/Data/Items";
        const string PrefabDir = "Assets/_Project/Prefabs";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        internal const string PrefabPath = PrefabDir + "/Animal.prefab";

        readonly struct Seed
        {
            public readonly string Id;
            public readonly string Name;
            public readonly string Description;
            public readonly Temperament Temperament;
            public readonly float Health;
            public readonly float Walk;
            public readonly float Run;
            public readonly float Wander;
            public readonly Vector2 Idle;
            public readonly float Sense;
            public readonly float React;
            public readonly float Calm;
            public readonly float Damage;
            public readonly float Reach;
            public readonly float Interval;
            public readonly float Stun;
            public readonly float Knockback;
            public readonly Vector3 Body;
            public readonly Color Colour;
            public readonly float AgentRadius;

            public Seed(string id, string name, string description, Temperament temperament, float health,
                        float walk, float run, float wander, Vector2 idle, float sense, float react,
                        float calm, float damage, float reach, float interval, float stun, float knockback,
                        Vector3 body, Color colour, float agentRadius)
            {
                Id = id;
                Name = name;
                Description = description;
                Temperament = temperament;
                Health = health;
                Walk = walk;
                Run = run;
                Wander = wander;
                Idle = idle;
                Sense = sense;
                React = react;
                Calm = calm;
                Damage = damage;
                Reach = reach;
                Interval = interval;
                Stun = stun;
                Knockback = knockback;
                Body = body;
                Colour = colour;
                AgentRadius = agentRadius;
            }
        }

        /// <summary>
        /// Three species, and every number in here is tuned against something the player already has.
        ///
        /// **The speeds are tuned against a sprint (7.5 m/s).** Both prey animals run slightly slower
        /// than that, which is the single decision that makes hunting playable with a bat on day one:
        /// a deer that outran a sprinting player would be huntable only with the guns from #51, and
        /// #53's acceptance is about *early* money. The boar is slower than a sprint too, for the
        /// mirror-image reason - a charge you cannot escape is not a fight, it is a death sentence.
        ///
        /// **The health is tuned against tier-1 weapons.** A bat does 14 a swing and a hatchet 34, so
        /// a boar is four swings or two chops, a deer three or two, and a gull is one of anything.
        ///
        /// **The loot is tuned against what the trader pays**, which is half of an item's value. See
        /// the table <c>AnimalTest</c> prints; the short version is that a kilogram of hunted goods
        /// is worth about three times a kilogram of scrap, and weight is the real constraint on a
        /// forty-kilo carry limit.
        /// </summary>
        static readonly Seed[] Seeds =
        {
            new("boar", "Boar", "Bad eyesight, bad temper, worse manners. Will absolutely start it.",
                Temperament.Aggressive, health: 55f, walk: 2f, run: 7.2f, wander: 25f, idle: new Vector2(2f, 5f),
                sense: 30f, react: 16f, calm: 8f,
                damage: 18f, reach: 2f, interval: 1.6f, stun: 0.9f, knockback: 950f,
                body: new Vector3(0.85f, 0.85f, 1.7f), colour: new Color(0.30f, 0.23f, 0.18f),
                agentRadius: 0.55f),

            new("deer", "Deer", "Nervous, quick, and carrying most of the island's money on its back.",
                Temperament.Skittish, health: 40f, walk: 2.4f, run: 7f, wander: 45f, idle: new Vector2(2f, 6f),
                sense: 34f, react: 20f, calm: 7f,
                damage: 0f, reach: 1f, interval: 2f, stun: 0f, knockback: 0f,
                body: new Vector3(0.7f, 1.35f, 1.8f), colour: new Color(0.56f, 0.41f, 0.26f),
                agentRadius: 0.5f),

            new("gull", "Gull", "Loud, worthless, and extremely satisfying to hit with a plank.",
                Temperament.Skittish, health: 12f, walk: 1.6f, run: 6.5f, wander: 35f, idle: new Vector2(1f, 3f),
                sense: 22f, react: 12f, calm: 4f,
                damage: 0f, reach: 1f, interval: 2f, stun: 0f, knockback: 0f,
                body: new Vector3(0.35f, 0.35f, 0.55f), colour: new Color(0.86f, 0.86f, 0.82f),
                agentRadius: 0.25f),
        };

        /// <summary>
        /// Who drops what. Structure, so it is re-applied on every run: a boar that has stopped
        /// dropping meat is a broken reference and never a balance decision.
        ///
        /// The chance column is what separates "always some meat" from "usually a hide". A boar
        /// always gives both; a deer's hide is good five times in six, because the sixth one you
        /// ruined; a gull gives feathers and, if you are lucky, one mouthful.
        /// </summary>
        static readonly (string Animal, string Item, int Min, int Max, float Chance)[] Loot =
        {
            ("boar", "meat_raw", 2, 3, 1f),
            ("boar", "hide", 1, 2, 1f),

            ("deer", "meat_raw", 3, 4, 1f),
            ("deer", "hide", 1, 2, 0.85f),

            ("gull", "meat_raw", 1, 1, 0.8f),
            ("gull", "feather", 2, 4, 1f),
        };

        public static void Build()
        {
            Directory.CreateDirectory(Folder);

            var defs = new List<AnimalDef>();
            int created = 0;

            foreach (Seed seed in Seeds)
            {
                string path = $"{Folder}/{seed.Id}.asset";
                var def = AssetDatabase.LoadAssetAtPath<AnimalDef>(path);

                if (def == null)
                {
                    def = ScriptableObject.CreateInstance<AnimalDef>();
                    def.Configure(seed.Id, seed.Name, seed.Description, seed.Temperament, seed.Health,
                                  seed.Walk, seed.Run, seed.Wander, seed.Idle, seed.Sense, seed.React,
                                  seed.Calm, seed.Damage, seed.Reach, seed.Interval, seed.Stun,
                                  seed.Knockback, seed.Body, seed.Colour, seed.AgentRadius);

                    AssetDatabase.CreateAsset(def, path);
                    created++;
                }

                defs.Add(def);
            }

            // Everything in the folder, not just the seeded rows - a species dropped in by hand is as
            // real as one from the table. Ordinal so the wire order does not depend on a locale.
            AnimalDef[] all = AssetDatabase.FindAssets("t:AnimalDef", new[] { Folder })
                                           .Select(AssetDatabase.GUIDToAssetPath)
                                           .Select(AssetDatabase.LoadAssetAtPath<AnimalDef>)
                                           .Where(d => d != null)
                                           .OrderBy(d => d.Id, StringComparer.Ordinal)
                                           .ToArray();

            int links = ApplyLoot(all);

            AnimalCatalog catalog = EnsureCatalog();
            catalog.Configure(all);
            EditorUtility.SetDirty(catalog);

            GameObject prefab = EnsurePrefab(catalog);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[AnimalFactory] {all.Length} species ({created} new), {links} loot lines, "
                      + $"prefab {(prefab != null ? "ok" : "MISSING")}.");

            foreach (AnimalDef def in all)
                Debug.Log($"[AnimalFactory]   {def.Id,-6} {def.Temperament,-10} {def.MaxHealth,3:0} hp  "
                          + $"run {def.RunSpeed:0.0}  worth {def.ExpectedLootValue:0} in items "
                          + $"({Describe(def)})");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static string Describe(AnimalDef def)
        {
            if (def.Loot == null || def.Loot.Length == 0) return "nothing";

            return string.Join(", ", def.Loot.Where(l => l != null && l.Item != null)
                                             .Select(l => l.Low == l.High
                                                         ? $"{l.Low}x {l.Item.Id}"
                                                         : $"{l.Low}-{l.High}x {l.Item.Id}"));
        }

        static int ApplyLoot(AnimalDef[] all)
        {
            var byId = all.ToDictionary(d => d.Id, d => d);
            var drops = new Dictionary<string, List<LootDrop>>();
            int links = 0;

            foreach ((string animal, string item, int min, int max, float chance) in Loot)
            {
                if (!byId.ContainsKey(animal))
                {
                    Debug.LogWarning($"[AnimalFactory] loot line for unknown species '{animal}'; skipped.");
                    continue;
                }

                var def = AssetDatabase.LoadAssetAtPath<ItemDef>($"{ItemFolder}/{item}.asset");
                if (def == null)
                {
                    Debug.LogError($"[AnimalFactory] '{animal}' drops '{item}', which does not exist. "
                                   + "Run ItemFactory.Build first.");
                    continue;
                }

                if (!drops.TryGetValue(animal, out List<LootDrop> list))
                {
                    list = new List<LootDrop>();
                    drops[animal] = list;
                }

                list.Add(new LootDrop { Item = def, Min = min, Max = max, Chance = chance });
                links++;
            }

            foreach (AnimalDef def in all)
            {
                def.SetLoot(drops.TryGetValue(def.Id, out List<LootDrop> list)
                                ? list.ToArray()
                                : Array.Empty<LootDrop>());

                EditorUtility.SetDirty(def);
            }

            return links;
        }

        static AnimalCatalog EnsureCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<AnimalCatalog>(CatalogPath);
            if (catalog != null) return catalog;

            catalog = ScriptableObject.CreateInstance<AnimalCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogPath);
            return catalog;
        }

        /// <summary>The catalog, for anything that needs to wire a component to it at bake time.</summary>
        internal static AnimalCatalog Catalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<AnimalCatalog>(CatalogPath);
            if (catalog == null) Debug.LogError($"[AnimalFactory] missing {CatalogPath}; run Build first.");

            return catalog;
        }

        /// <summary>The one animal prefab, for the spawner that has to instantiate it.</summary>
        internal static NetworkObject Prefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[AnimalFactory] missing {PrefabPath}; run Build first.");
                return null;
            }

            return prefab.GetComponent<NetworkObject>();
        }

        // ---------------------------------------------------------------- prefab

        /// <summary>
        /// Rebuilt whole on every run, unlike the definitions. It carries no tuned numbers - every
        /// number on it is overwritten from the species at spawn time - so there is nothing here for
        /// a rebuild to destroy, and keeping it generated means the component list cannot drift away
        /// from what <see cref="Animal"/> expects to find.
        /// </summary>
        static GameObject EnsurePrefab(AnimalCatalog catalog)
        {
            Directory.CreateDirectory(PrefabDir);

            GameObject root = BuildHierarchy(catalog);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool success);
            UnityEngine.Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[AnimalFactory] Failed to save {PrefabPath}.");
                return null;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>());
            return saved;
        }

        static GameObject BuildHierarchy(AnimalCatalog catalog)
        {
            var root = new GameObject("Animal");

            // Everything visible is a child with no collider. The one collider is on the root, so
            // Weapon's GetComponentInParent<Health>() finds this object from the first thing it hits
            // - the same arrangement every other damageable thing in the project uses.
            GameObject body = Primitive(root.transform, "Body", new Vector3(0f, 0.4f, 0f), Vector3.one);
            GameObject head = Primitive(root.transform, "Head", new Vector3(0f, 0.65f, 0.5f),
                                        Vector3.one * 0.4f);

            var collider = root.AddComponent<CapsuleCollider>();
            collider.radius = 0.4f;
            collider.height = 1f;
            collider.center = new Vector3(0f, 0.5f, 0f);

            var agent = root.AddComponent<NavMeshAgent>();
            agent.radius = 0.5f;
            agent.height = 1f;
            agent.baseOffset = 0f;
            agent.autoBraking = true;
            agent.autoRepath = true;

            root.AddComponent<NetworkObject>();

            // Server-driven: nothing owns an animal, so the server's transform is the only one, and
            // every client interpolates towards it. The same component the player body uses.
            root.AddComponent<NetworkTransform>();

            var health = root.AddComponent<Health>();
            SetFields(health, so =>
            {
                // No downed state and no spawn grace. A boar has nobody to pick it up, and two
                // seconds of immunity the instant you find one reads as a broken hit rather than as
                // a rule.
                so.FindProperty("_canBeDowned").boolValue = false;
                so.FindProperty("_spawnInvulnerability").floatValue = 0f;
                so.FindProperty("_maxHealth").floatValue = 40f;
            });

            var animal = root.AddComponent<Animal>();
            SetFields(animal, so =>
            {
                so.FindProperty("_catalog").objectReferenceValue = catalog;
                so.FindProperty("_body").objectReferenceValue = body.transform;
                so.FindProperty("_head").objectReferenceValue = head.transform;
                so.FindProperty("_collider").objectReferenceValue = collider;
            });

            return root;
        }

        static GameObject Primitive(Transform parent, string name, Vector3 localPosition, Vector3 localScale)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;

            Collider existing = go.GetComponent<Collider>();
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);

            return go;
        }

        static void SetFields(UnityEngine.Object target, Action<SerializedObject> configure)
        {
            var so = new SerializedObject(target);
            configure(so);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Same reasoning as PlayerPrefabBuilder.RegisterSpawnable; see the note there.</summary>
        static void RegisterSpawnable(NetworkObject networkObject)
        {
            if (networkObject == null)
            {
                Debug.LogError("[AnimalFactory] Saved prefab has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[AnimalFactory] missing {PrefabObjectsPath}; not registered.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);
        }

        // ---------------------------------------------------------------- zones

        /// <summary>
        /// Where the populations live, in island coordinates, derived from the POIs that are already
        /// placed. Called by <see cref="TerrainGenerator"/> while it is assembling the island scene.
        ///
        /// Derived rather than typed for the reason every coordinate in this project is derived: the
        /// island moves when the seed changes, and a hard-coded herd would end up in the sea. Each
        /// zone hangs off a landmark, so the wildlife follows the map.
        ///
        /// The distribution is a balance statement, and it is the one the acceptance criterion rests
        /// on: **boars are the animal near camp.** They are the aggressive species, they carry the
        /// most valuable hide per kilogram, and putting them where a new player already is means
        /// hunting is what you do first rather than a thing you unlock by walking. Deer are inland
        /// and worth more per kill; gulls are on the tideline and are mostly a joke.
        /// </summary>
        internal static void BakeZones(IslandProfile profile, AnimalSpawner spawner)
        {
            if (spawner == null) return;

            AnimalCatalog catalog = Catalog();
            NetworkObject prefab = Prefab();

            if (catalog == null || prefab == null)
            {
                Debug.LogError("[AnimalFactory] No catalog or prefab; the island will have no wildlife.");
                return;
            }

            POICatalog pois = profile != null ? profile.Pois : null;
            var shape = new IslandShape(profile);

            Vector2 camp = At(pois, "camp.base", Vector2.zero);
            Vector2 cave = At(pois, "cave", camp);
            Vector2 village = At(pois, "village", camp);
            Vector2 wreck = At(pois, "wreck", camp);

            var zones = new List<AnimalSpawner.Zone>
            {
                Zone("boar.camp", catalog.Find("boar"), camp, 130f, 5, shape),
                Zone("boar.village", catalog.Find("boar"), village, 110f, 4, shape),
                Zone("deer.inland", catalog.Find("deer"), cave, 160f, 6, shape),
                Zone("deer.village", catalog.Find("deer"), village, 140f, 4, shape),
                Zone("gull.shore", catalog.Find("gull"), wreck, 120f, 5, shape),
            };

            zones.RemoveAll(z => z.Species == null);

            spawner.Configure(zones.ToArray(), prefab, catalog);
            EditorUtility.SetDirty(spawner);

            int total = zones.Sum(z => z.Population);
            Debug.Log($"[AnimalFactory] {zones.Count} zones holding up to {total} animals:");

            foreach (AnimalSpawner.Zone zone in zones)
                Debug.Log($"[AnimalFactory]   {zone.Id,-14} {zone.Population}x {zone.Species.Id} "
                          + $"within {zone.Radius:0}m of {zone.Centre}");
        }

        static AnimalSpawner.Zone Zone(string id, AnimalDef species, Vector2 centre, float radius,
                                       int population, IslandShape shape)
            => new()
            {
                Id = id,
                Species = species,
                Centre = new Vector3(centre.x, shape.HeightAt(centre.x, centre.y), centre.y),
                Radius = radius,
                Population = population,
            };

        static Vector2 At(POICatalog pois, string id, Vector2 fallback)
        {
            POIEntry entry = pois != null ? pois.Find(id) : null;
            return entry != null ? entry.Position : fallback;
        }
    }
}
