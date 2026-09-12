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
    /// The island's other people: three roles, one prefab, the catalog that indexes them, and the
    /// camps they are spawned into.
    ///
    /// Same doctrine as <see cref="AnimalFactory"/> - definitions are created once and never
    /// overwritten, structure (loot links, the prefab, the camp list) is re-applied on every run - so
    /// a number tuned in the inspector survives a rebuild and a broken reference cannot.
    /// </summary>
    public static class NativeFactory
    {
        const string Folder = "Assets/_Project/Data/Natives";
        const string CatalogPath = "Assets/_Project/Data/Natives.asset";
        const string ItemFolder = "Assets/_Project/Data/Items";
        const string PrefabDir = "Assets/_Project/Prefabs";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        internal const string PrefabPath = PrefabDir + "/Native.prefab";

        readonly struct Seed
        {
            public readonly string Id;
            public readonly string Name;
            public readonly string Description;
            public readonly NativeRole Role;
            public readonly float Health;
            public readonly float Walk;
            public readonly float Run;
            public readonly float Patrol;
            public readonly Vector2 Idle;
            public readonly float DayNotice;
            public readonly float NightNotice;
            public readonly float Vision;
            public readonly float Earshot;
            public readonly float Memory;
            public readonly float Investigate;
            public readonly float Alarm;
            public readonly float DayLeash;
            public readonly float NightLeash;
            public readonly float FleeAt;
            public readonly float Damage;
            public readonly float Reach;
            public readonly float Interval;
            public readonly float Stun;
            public readonly float Knockback;
            public readonly float Windup;
            public readonly bool Ranged;
            public readonly float Standoff;
            public readonly float Spread;
            public readonly Vector3 Body;
            public readonly Color Colour;
            public readonly Color Mark;
            public readonly float AgentRadius;

            public Seed(string id, string name, string description, NativeRole role, float health,
                        float walk, float run, float patrol, Vector2 idle, float dayNotice, float nightNotice,
                        float vision, float earshot, float memory, float investigate, float alarm,
                        float dayLeash, float nightLeash, float fleeAt, float damage, float reach,
                        float interval, float stun, float knockback, float windup, bool ranged,
                        float standoff, float spread, Vector3 body, Color colour, Color mark,
                        float agentRadius)
            {
                Id = id;
                Name = name;
                Description = description;
                Role = role;
                Health = health;
                Walk = walk;
                Run = run;
                Patrol = patrol;
                Idle = idle;
                DayNotice = dayNotice;
                NightNotice = nightNotice;
                Vision = vision;
                Earshot = earshot;
                Memory = memory;
                Investigate = investigate;
                Alarm = alarm;
                DayLeash = dayLeash;
                NightLeash = nightLeash;
                FleeAt = fleeAt;
                Damage = damage;
                Reach = reach;
                Interval = interval;
                Stun = stun;
                Knockback = knockback;
                Windup = windup;
                Ranged = ranged;
                Standoff = standoff;
                Spread = spread;
                Body = body;
                Colour = colour;
                Mark = mark;
                AgentRadius = agentRadius;
            }
        }

        /// <summary>
        /// Three roles, and every number is tuned against something the player already has.
        ///
        /// **Speed is the fairness lever.** A sprint is 7.5 m/s and nothing here reaches it, so
        /// running is always an answer - it just costs the stamina and whatever you were doing. That
        /// single decision is what lets the night numbers be as aggressive as they are.
        ///
        /// **Damage is tuned against 100 health and a revive that costs money.** A spearman needs
        /// five clean hits over nine seconds to put somebody down, which is long enough for three
        /// friends to do something about it and short enough that ignoring him is not a plan. The
        /// blowgun barely hurts - nine a dart - and the threat is the 1.6 s of stun, because being
        /// held still next to two spearmen is how a night raid actually kills you.
        ///
        /// **Health is tuned against tier-1 weapons.** A bat does 14 and a hatchet 34, so a scout is
        /// four swings or two chops, a blowgunner four or two, a spearman six or three. A pistol
        /// (#51) does it in two either way, which is most of what the trader's first gun is for.
        ///
        /// **The notice radii are the acceptance criterion in numbers.** By day: 18-26 m, inside a
        /// cone, with line of sight - so a hill is cover and you can watch a camp from a ridge. At
        /// night: 30-36 m, no cone and no ray, because they hear you. Same island, different game.
        /// </summary>
        static readonly Seed[] Seeds =
        {
            new("scout", "Lookout", "Sees you first, hits you least, and tells everybody.",
                NativeRole.Scout, health: 45f, walk: 2.3f, run: 7f, patrol: 30f, idle: new Vector2(2f, 5f),
                dayNotice: 24f, nightNotice: 36f, vision: 70f, earshot: 7f,
                memory: 10f, investigate: 7f, alarm: 45f, dayLeash: 45f, nightLeash: 130f, fleeAt: 0.35f,
                damage: 12f, reach: 2.2f, interval: 1.5f, stun: 0.5f, knockback: 600f, windup: 0.3f,
                ranged: false, standoff: 2.2f, spread: 0f,
                body: new Vector3(0.5f, 1.7f, 0.35f), colour: new Color(0.42f, 0.31f, 0.24f),
                mark: new Color(0.95f, 0.80f, 0.25f), agentRadius: 0.38f),

            new("spearman", "Spearman", "The reason you do not simply walk into the village.",
                NativeRole.Spearman, health: 85f, walk: 2f, run: 6.6f, patrol: 22f, idle: new Vector2(2f, 6f),
                dayNotice: 18f, nightNotice: 30f, vision: 60f, earshot: 8f,
                memory: 9f, investigate: 6f, alarm: 30f, dayLeash: 40f, nightLeash: 130f, fleeAt: 0.15f,
                damage: 22f, reach: 2.6f, interval: 1.8f, stun: 1f, knockback: 1000f, windup: 0.4f,
                ranged: false, standoff: 2.6f, spread: 0f,
                body: new Vector3(0.6f, 1.8f, 0.42f), colour: new Color(0.38f, 0.27f, 0.21f),
                mark: new Color(0.85f, 0.22f, 0.18f), agentRadius: 0.42f),

            new("blowgunner", "Blowgunner", "Nine damage and a nap. The nap is the problem.",
                NativeRole.Blowgunner, health: 55f, walk: 2f, run: 6f, patrol: 24f, idle: new Vector2(3f, 7f),
                dayNotice: 26f, nightNotice: 34f, vision: 65f, earshot: 6f,
                memory: 12f, investigate: 6f, alarm: 30f, dayLeash: 38f, nightLeash: 120f, fleeAt: 0.4f,
                damage: 9f, reach: 24f, interval: 2.4f, stun: 1.6f, knockback: 120f, windup: 0.6f,
                ranged: true, standoff: 12f, spread: 4f,
                body: new Vector3(0.5f, 1.7f, 0.35f), colour: new Color(0.45f, 0.34f, 0.26f),
                mark: new Color(0.90f, 0.88f, 0.80f), agentRadius: 0.38f),
        };

        /// <summary>
        /// What a body is carrying. Structure, so it is re-applied every run.
        ///
        /// Modest, but not a consolation prize. #56 priced a camp sweep against a bag of fish and a
        /// bag of venison and found raiding paying a quarter of what fishing paid, which made the
        /// most dangerous thing on the island the least worthwhile - so the quantities went up until
        /// a body was worth about two thirds of an animal. The best line is still a spearman's hide:
        /// one deer's worth of leather for a fight that can cost you a revive.
        /// </summary>
        static readonly (string Native, string Item, int Min, int Max, float Chance)[] Loot =
        {
            ("scout", "rope", 2, 3, 1f),
            ("scout", "feather", 2, 4, 1f),

            ("spearman", "hide", 1, 2, 1f),
            ("spearman", "rope", 2, 3, 1f),
            ("spearman", "flint", 1, 2, 1f),

            ("blowgunner", "feather", 3, 5, 1f),
            ("blowgunner", "flint", 2, 3, 1f),
        };

        public static void Build()
        {
            Directory.CreateDirectory(Folder);

            var all = new List<NativeDef>();
            int made = 0;

            foreach (Seed seed in Seeds)
            {
                string path = $"{Folder}/{seed.Id}.asset";
                var def = AssetDatabase.LoadAssetAtPath<NativeDef>(path);

                if (def == null)
                {
                    def = ScriptableObject.CreateInstance<NativeDef>();
                    def.Configure(seed.Id, seed.Name, seed.Description, seed.Role, seed.Health, seed.Walk,
                                  seed.Run, seed.Patrol, seed.Idle, seed.DayNotice, seed.NightNotice,
                                  seed.Vision, seed.Earshot, seed.Memory, seed.Investigate, seed.Alarm,
                                  seed.DayLeash, seed.NightLeash, seed.FleeAt, seed.Damage, seed.Reach,
                                  seed.Interval, seed.Stun, seed.Knockback, seed.Windup, seed.Ranged,
                                  seed.Standoff, seed.Spread, seed.Body, seed.Colour, seed.Mark,
                                  seed.AgentRadius);

                    AssetDatabase.CreateAsset(def, path);
                    made++;
                }

                all.Add(def);
            }

            all.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            int links = ApplyLoot(all.ToArray());

            NativeCatalog catalog = EnsureCatalog();
            catalog.Configure(all.ToArray());
            EditorUtility.SetDirty(catalog);

            foreach (NativeDef def in all) EditorUtility.SetDirty(def);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            GameObject prefab = EnsurePrefab(catalog);

            Debug.Log($"[NativeFactory] {all.Count} role(s) ({made} new), {links} loot link(s), "
                      + $"prefab {(prefab != null ? "ok" : "MISSING")}.");

            foreach (NativeDef def in all) Debug.Log($"[NativeFactory]   {Describe(def)}");
        }

        static string Describe(NativeDef def)
            => $"{def.Id,-11} {def.MaxHealth,4:0} hp  {def.AttackDamage,3:0} dmg/{def.AttackInterval:0.0}s "
               + $"({def.DamagePerSecond,4:0.0} dps)  notices {def.DayNotice:0}m by day / "
               + $"{def.NightNotice:0}m at night  leash {def.DayLeash:0}/{def.NightLeash:0}m  "
               + $"drops {def.ExpectedLootValue:0.0}c";

        static int ApplyLoot(NativeDef[] all)
        {
            var byId = all.Where(d => d != null).ToDictionary(d => d.Id, d => d);
            var lines = new Dictionary<string, List<LootDrop>>();
            int links = 0;

            foreach ((string native, string item, int min, int max, float chance) in Loot)
            {
                var def = AssetDatabase.LoadAssetAtPath<ItemDef>($"{ItemFolder}/{item}.asset");
                if (def == null)
                {
                    Debug.LogWarning($"[NativeFactory] {native} drops {item}, which does not exist; skipped.");
                    continue;
                }

                if (!lines.TryGetValue(native, out List<LootDrop> list))
                {
                    list = new List<LootDrop>();
                    lines[native] = list;
                }

                list.Add(new LootDrop { Item = def, Min = min, Max = max, Chance = chance });
                links++;
            }

            foreach (KeyValuePair<string, List<LootDrop>> pair in lines)
            {
                if (!byId.TryGetValue(pair.Key, out NativeDef def))
                {
                    Debug.LogWarning($"[NativeFactory] loot for unknown role {pair.Key}; skipped.");
                    continue;
                }

                def.SetLoot(pair.Value.ToArray());
                EditorUtility.SetDirty(def);
            }

            return links;
        }

        static NativeCatalog EnsureCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<NativeCatalog>(CatalogPath);
            if (catalog != null) return catalog;

            catalog = ScriptableObject.CreateInstance<NativeCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogPath);
            return catalog;
        }

        internal static NativeCatalog Catalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<NativeCatalog>(CatalogPath);
            if (catalog == null) Debug.LogError($"[NativeFactory] missing {CatalogPath}; run Build first.");

            return catalog;
        }

        /// <summary>The one native prefab, for the spawner that has to instantiate it.</summary>
        internal static NetworkObject Prefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[NativeFactory] missing {PrefabPath}; run Build first.");
                return null;
            }

            return prefab.GetComponent<NetworkObject>();
        }

        // ---------------------------------------------------------------- prefab

        /// <summary>
        /// Rebuilt whole on every run. It carries no tuned numbers - every one of them is overwritten
        /// from the role at spawn - so there is nothing here a rebuild can destroy, and generating it
        /// means the component list cannot drift away from what <see cref="Native"/> expects.
        /// </summary>
        static GameObject EnsurePrefab(NativeCatalog catalog)
        {
            Directory.CreateDirectory(PrefabDir);

            GameObject root = BuildHierarchy(catalog);

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool success);
            UnityEngine.Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[NativeFactory] Failed to save {PrefabPath}.");
                return null;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>());
            return saved;
        }

        static GameObject BuildHierarchy(NativeCatalog catalog)
        {
            var root = new GameObject("Native");

            // The visible parts have no colliders. The one collider is on the root, so a weapon's
            // GetComponentInParent<Health>() finds this object from the first thing its ray hits -
            // and so does the natives' own line of sight check.
            GameObject body = Primitive(root.transform, "Body", new Vector3(0f, 0.65f, 0f),
                                        new Vector3(0.6f, 1.3f, 0.42f));
            GameObject head = Primitive(root.transform, "Head", new Vector3(0f, 1.5f, 0f),
                                        Vector3.one * 0.45f);

            var collider = root.AddComponent<CapsuleCollider>();
            collider.radius = 0.3f;
            collider.height = 1.8f;
            collider.center = new Vector3(0f, 0.9f, 0f);

            var agent = root.AddComponent<NavMeshAgent>();
            agent.radius = 0.4f;
            agent.height = 1.8f;
            agent.baseOffset = 0f;
            agent.autoBraking = true;
            agent.autoRepath = true;

            root.AddComponent<NetworkObject>();
            root.AddComponent<NetworkTransform>();

            var health = root.AddComponent<Health>();
            SetFields(health, so =>
            {
                // No downed state: nobody is coming to pick a native up, and a body that has to be
                // finished off twice is a fight that outstays its welcome.
                so.FindProperty("_canBeDowned").boolValue = false;
                so.FindProperty("_spawnInvulnerability").floatValue = 0f;
                so.FindProperty("_maxHealth").floatValue = 70f;
            });

            var native = root.AddComponent<Native>();
            SetFields(native, so =>
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

        static void RegisterSpawnable(NetworkObject networkObject)
        {
            if (networkObject == null)
            {
                Debug.LogError("[NativeFactory] Saved prefab has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[NativeFactory] missing {PrefabObjectsPath}; the native cannot spawn.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);
            AssetDatabase.SaveAssets();
        }

        // ---------------------------------------------------------------- camps

        /// <summary>
        /// Where the camps are, derived from the POIs that already exist, so they follow the map when
        /// the seed changes rather than ending up in the sea.
        ///
        /// The village is the real one - a full roster, and the place the game means when it says
        /// "do not go there yet". The cave is an outpost: two bodies, enough that the walk to it is a
        /// decision. **Nothing is placed at <c>camp.base</c>**, and the bake says so out loud if a
        /// camp lands within earshot of the players' own fire, because a village that patrols your
        /// spawn is not difficulty, it is a broken map.
        /// </summary>
        internal static void BakeCamps(IslandProfile profile, NativeSpawner spawner)
        {
            if (spawner == null) return;

            NativeCatalog catalog = Catalog();
            NetworkObject prefab = Prefab();

            if (catalog == null || prefab == null)
            {
                Debug.LogError("[NativeFactory] No catalog or prefab; the island will have no natives.");
                return;
            }

            POICatalog pois = profile != null ? profile.Pois : null;
            var shape = new IslandShape(profile);

            Vector2 home = At(pois, "camp.base", Vector2.zero);
            Vector2 village = At(pois, "village", home);
            Vector2 cave = At(pois, "cave", home);

            var camps = new List<NativeSpawner.Camp>
            {
                Camp("village.spearman", catalog.Find("spearman"), village, 24f, 2, 1, shape),
                Camp("village.blowgun", catalog.Find("blowgunner"), village, 26f, 1, 1, shape),
                Camp("village.scout", catalog.Find("scout"), village, 34f, 1, 1, shape),

                Camp("cave.spearman", catalog.Find("spearman"), cave, 20f, 1, 1, shape),
                Camp("cave.scout", catalog.Find("scout"), cave, 26f, 1, 0, shape),
            };

            camps.RemoveAll(c => c.Role == null);

            spawner.Configure(camps.ToArray(), prefab, catalog);
            EditorUtility.SetDirty(spawner);

            int day = camps.Sum(c => c.Population);
            int night = camps.Sum(c => c.Wanted(1f));

            Debug.Log($"[NativeFactory] {camps.Count} camp line(s): {day} natives by day, {night} at night.");

            foreach (NativeSpawner.Camp camp in camps)
            {
                float toHome = Vector2.Distance(new Vector2(camp.Centre.x, camp.Centre.z), home);

                Debug.Log($"[NativeFactory]   {camp.Id,-18} {camp.Population}(+{camp.NightExtra})x "
                          + $"{camp.Role.Id,-11} within {camp.Radius:0}m of {camp.Centre}, "
                          + $"{toHome:0}m from base.");

                if (toHome < 150f)
                    Debug.LogWarning($"[NativeFactory] {camp.Id} is {toHome:0}m from camp.base - close "
                                     + "enough to patrol the players' own fire at night.");
            }
        }

        static NativeSpawner.Camp Camp(string id, NativeDef role, Vector2 centre, float radius,
                                       int population, int nightExtra, IslandShape shape)
            => new()
            {
                Id = id,
                Role = role,
                Centre = new Vector3(centre.x, shape.HeightAt(centre.x, centre.y), centre.y),
                Radius = radius,
                Population = population,
                NightExtra = nightExtra,
            };

        static Vector2 At(POICatalog pois, string id, Vector2 fallback)
        {
            POIEntry entry = pois != null ? pois.Find(id) : null;
            return entry != null ? entry.Position : fallback;
        }
    }
}
