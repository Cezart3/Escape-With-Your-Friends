using System.Collections.Generic;
using System.IO;
using System.Linq;
using EscapeWithYourFriends.Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Every weapon in the game, and the catalog that indexes them.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.WeaponFactory.Build
    ///
    /// This is the thing #49's acceptance is actually about. **Adding a weapon is adding a row to
    /// <see cref="Seeds"/> and running this** - the asset is created, a greybox model is built, the
    /// item link is made, and the catalog is rebuilt from whatever is in the folder. No component is
    /// edited, no list is maintained by hand, and nothing anywhere switches on a weapon's name.
    ///
    /// Same rule as the other factories: creates what does not exist, never overwrites what does. A
    /// number somebody tuned in the inspector survives the next run; the catalog and the prefab links
    /// are structural and are re-applied every time.
    ///
    /// #49 seeds five: fists, two melee weapons off items that already exist, one new melee weapon,
    /// and one gun - enough that both branches of <c>Weapon.ServerResolve</c> are real and tested.
    /// #50 and #51 add the rest of the arsenal, and by then adding one is a row in this table.
    /// </summary>
    public static class WeaponFactory
    {
        const string WeaponDir = "Assets/_Project/Data/Weapons";
        const string CatalogPath = "Assets/_Project/Data/Weapons.asset";
        const string PrefabDir = "Assets/_Project/Prefabs/Weapons";
        const string ItemFolder = "Assets/_Project/Data/Items";

        /// <summary>
        /// One weapon. <c>Item</c> empty means it is not carried - which is only ever true of fists.
        /// </summary>
        readonly struct Seed
        {
            public readonly string Id;
            public readonly string Name;
            public readonly string Item;
            public readonly WeaponKind Kind;
            public readonly int Tier;

            // Hit
            public readonly float Damage;
            public readonly float Knockback;
            public readonly float UpwardBias;
            public readonly float Stun;

            // Timing and shape
            public readonly float Cooldown;
            public readonly float Windup;
            public readonly float Range;
            public readonly float Radius;
            public readonly float Cone;
            public readonly int MaxTargets;

            // Gun
            public readonly int Pellets;
            public readonly float Spread;
            public readonly float Recoil;
            public readonly float Rpm;
            public readonly int Magazine;
            public readonly string Ammo;

            // Model
            public readonly Vector3 Size;
            public readonly Color Colour;
            public readonly string Description;

            public Seed(string id, string name, string item, WeaponKind kind, int tier,
                        float damage, float knockback, float upwardBias, float stun,
                        float cooldown, float windup, float range, float radius, float cone,
                        int maxTargets, int pellets, float spread, float recoil, float rpm,
                        int magazine, string ammo, Vector3 size, Color colour, string description)
            {
                Id = id; Name = name; Item = item; Kind = kind; Tier = tier;
                Damage = damage; Knockback = knockback; UpwardBias = upwardBias; Stun = stun;
                Cooldown = cooldown; Windup = windup; Range = range; Radius = radius; Cone = cone;
                MaxTargets = maxTargets; Pellets = pellets; Spread = spread; Recoil = recoil;
                Rpm = rpm; Magazine = magazine; Ammo = ammo;
                Size = size; Colour = colour; Description = description;
            }
        }

        static readonly Seed[] Seeds =
        {
            // Bare hands. Weak, fast, wide, and the only weapon nobody can be disarmed of. The stun is
            // what matters here, not the damage: punching a friend off a ledge is the whole game.
            new("fists", "Fists", null, WeaponKind.Melee, 1,
                damage: 10f, knockback: 4f, upwardBias: 0.30f, stun: 1.5f,
                cooldown: 0.50f, windup: 0.12f, range: 2.0f, radius: 0.60f, cone: 60f, maxTargets: 4,
                pellets: 1, spread: 0f, recoil: 0f, rpm: 0f, magazine: 0, ammo: null,
                size: Vector3.zero, colour: default,
                description: "What you were born with. Wide, quick, and hilarious."),

            // Already in the bag from M3, so these two cost one row each and no new items.
            new("knife", "Knife", "knife", WeaponKind.Melee, 1,
                damage: 22f, knockback: 3f, upwardBias: 0.15f, stun: 0.8f,
                cooldown: 0.35f, windup: 0.08f, range: 1.8f, radius: 0.35f, cone: 35f, maxTargets: 1,
                pellets: 1, spread: 0f, recoil: 0f, rpm: 0f, magazine: 0, ammo: null,
                size: new Vector3(0.05f, 0.05f, 0.35f), colour: new Color(0.72f, 0.74f, 0.78f),
                description: "Fast and narrow. Hits one person properly instead of four badly."),

            new("hatchet", "Hatchet", "hatchet", WeaponKind.Melee, 1,
                damage: 34f, knockback: 9f, upwardBias: 0.28f, stun: 1.6f,
                cooldown: 0.75f, windup: 0.20f, range: 2.2f, radius: 0.50f, cone: 50f, maxTargets: 2,
                pellets: 1, spread: 0f, recoil: 0f, rpm: 0f, magazine: 0, ammo: null,
                size: new Vector3(0.08f, 0.10f, 0.45f), colour: new Color(0.55f, 0.42f, 0.28f),
                description: "Chops trees, chops friends. Slow enough that missing costs you."),

            // The one weapon #49 adds an item for, so the "new weapon = new asset" claim is tested by
            // a weapon that did not exist in any form before this issue.
            new("machete", "Machete", "machete", WeaponKind.Melee, 2,
                damage: 40f, knockback: 11f, upwardBias: 0.35f, stun: 1.8f,
                cooldown: 0.60f, windup: 0.16f, range: 2.6f, radius: 0.55f, cone: 70f, maxTargets: 3,
                pellets: 1, spread: 0f, recoil: 0f, rpm: 0f, magazine: 0, ammo: null,
                size: new Vector3(0.06f, 0.14f, 0.70f), colour: new Color(0.66f, 0.68f, 0.72f),
                description: "Long, wide, and it launches people. The tier-two answer to a crowd."),

            // The proof that the other branch is real. Balance is #51's problem, not this issue's.
            new("pistol", "Pistol", "pistol", WeaponKind.Hitscan, 1,
                damage: 26f, knockback: 6f, upwardBias: 0.10f, stun: 0.6f,
                cooldown: 0f, windup: 0f, range: 60f, radius: 0f, cone: 0f, maxTargets: 1,
                pellets: 1, spread: 1.5f, recoil: 1.2f, rpm: 300f, magazine: 12, ammo: "pistol_ammo",
                size: new Vector3(0.07f, 0.16f, 0.24f), colour: new Color(0.22f, 0.22f, 0.24f),
                description: "Reaches across the clearing. Reloading is #51's problem."),
        };

        public static void Build()
        {
            BuildAll();

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static WeaponCatalog BuildAll()
        {
            Directory.CreateDirectory(WeaponDir);
            Directory.CreateDirectory(PrefabDir);

            var built = new List<WeaponDef>();
            foreach (Seed seed in Seeds) built.Add(Ensure(seed));

            WeaponCatalog catalog = Rebuild(built);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[WeaponFactory] {catalog.Count} weapon(s), fists = "
                      + $"{(catalog.Fists != null ? catalog.Fists.Id : "MISSING")}.");

            for (int i = 0; i < catalog.Count; i++)
            {
                WeaponDef def = catalog.Weapons[i];
                Debug.Log($"[WeaponFactory]   [{i + 1}] {def.Describe()}"
                          + (def.Item != null ? $"  <- {def.Item.Id}" : "  <- bare hands"));
            }

            return catalog;
        }

        static WeaponDef Ensure(Seed seed)
        {
            string path = $"{WeaponDir}/{seed.Id}.asset";
            var def = AssetDatabase.LoadAssetAtPath<WeaponDef>(path);
            bool fresh = def == null;

            if (fresh)
            {
                def = ScriptableObject.CreateInstance<WeaponDef>();
                AssetDatabase.CreateAsset(def, path);
                Debug.Log($"[WeaponFactory] Created {path}.");
            }

            var so = new SerializedObject(def);

            // Balance belongs to whoever tuned it: numbers are written once, on creation. Structural
            // links - the item it equips from, the model it shows - are re-applied every run, the same
            // split BuffFactory and ShopFactory use.
            if (fresh)
            {
                so.FindProperty("_id").stringValue = seed.Id;
                so.FindProperty("_displayName").stringValue = seed.Name;
                so.FindProperty("_description").stringValue = seed.Description;
                so.FindProperty("_kind").enumValueIndex = (int)seed.Kind;
                so.FindProperty("_tier").intValue = seed.Tier;

                SerializedProperty hit = so.FindProperty("_hit");
                hit.FindPropertyRelative("_damage").floatValue = seed.Damage;
                hit.FindPropertyRelative("_knockback").floatValue = seed.Knockback;
                hit.FindPropertyRelative("_upwardBias").floatValue = seed.UpwardBias;
                hit.FindPropertyRelative("_stunDuration").floatValue = seed.Stun;

                so.FindProperty("_cooldown").floatValue = seed.Cooldown;
                so.FindProperty("_windup").floatValue = seed.Windup;
                so.FindProperty("_range").floatValue = seed.Range;
                so.FindProperty("_radius").floatValue = seed.Radius;
                so.FindProperty("_coneHalfAngle").floatValue = Mathf.Max(5f, seed.Cone);
                so.FindProperty("_maxTargets").intValue = seed.MaxTargets;

                so.FindProperty("_shotRange").floatValue = seed.Kind == WeaponKind.Hitscan
                    ? seed.Range : 60f;
                so.FindProperty("_pellets").intValue = seed.Pellets;
                so.FindProperty("_spread").floatValue = seed.Spread;
                so.FindProperty("_recoil").floatValue = seed.Recoil;
                so.FindProperty("_roundsPerMinute").floatValue = Mathf.Max(1f, seed.Rpm);
                so.FindProperty("_magazine").intValue = seed.Magazine;
            }

            so.FindProperty("_item").objectReferenceValue = Item(seed.Item, seed.Id);
            so.FindProperty("_ammo").objectReferenceValue = Item(seed.Ammo, seed.Id);
            so.FindProperty("_viewPrefab").objectReferenceValue = EnsureModel(seed);

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(def);

            return def;
        }

        static ItemDef Item(string id, string owner)
        {
            if (string.IsNullOrEmpty(id)) return null;

            var item = AssetDatabase.LoadAssetAtPath<ItemDef>($"{ItemFolder}/{id}.asset");
            if (item == null)
                Debug.LogError($"[WeaponFactory] '{owner}' wants the item '{id}', which does not "
                               + "exist. Run ItemFactory.Build first.");

            return item;
        }

        /// <summary>
        /// The "plus a prefab" half of the acceptance. A box of the right size and colour, held in the
        /// hand, until there is real art - the same greybox rule the stations and the arena follow.
        /// Fists get none, because a fist is already attached to you.
        /// </summary>
        static GameObject EnsureModel(Seed seed)
        {
            if (seed.Size == Vector3.zero) return null;

            string path = $"{PrefabDir}/{seed.Id}.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            var root = new GameObject(seed.Name);

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Body";
            cube.transform.SetParent(root.transform, false);
            cube.transform.localScale = seed.Size;
            Object.DestroyImmediate(cube.GetComponent<Collider>());

            var renderer = cube.GetComponent<Renderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.sharedMaterial =
                new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = seed.Colour };

            // A grip, so a machete reads as a machete and not as a plank.
            if (seed.Kind == WeaponKind.Melee)
            {
                var grip = GameObject.CreatePrimitive(PrimitiveType.Cube);
                grip.name = "Grip";
                grip.transform.SetParent(root.transform, false);
                grip.transform.localPosition = new Vector3(0f, 0f, -seed.Size.z * 0.55f);
                grip.transform.localScale = new Vector3(seed.Size.x * 1.4f, seed.Size.x * 1.4f,
                                                        seed.Size.z * 0.3f);
                Object.DestroyImmediate(grip.GetComponent<Collider>());

                var gripRenderer = grip.GetComponent<Renderer>();
                gripRenderer.shadowCastingMode = ShadowCastingMode.Off;
                gripRenderer.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"))
                    { color = new Color(0.28f, 0.20f, 0.14f) };
            }

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[WeaponFactory] Failed to save {path}.");
                return null;
            }

            Debug.Log($"[WeaponFactory] Built {path}.");
            return saved;
        }

        /// <summary>
        /// Rebuilds the catalog from everything in the folder, sorted by id. Reading the folder rather
        /// than the seed table is the point: a weapon somebody adds by hand in the inspector is in the
        /// catalog too, and a seed that gets deleted actually leaves.
        /// </summary>
        static WeaponCatalog Rebuild(List<WeaponDef> seeded)
        {
            WeaponDef[] all = AssetDatabase.FindAssets("t:WeaponDef", new[] { WeaponDir })
                                           .Select(AssetDatabase.GUIDToAssetPath)
                                           .Select(AssetDatabase.LoadAssetAtPath<WeaponDef>)
                                           .Where(w => w != null)
                                           .Concat(seeded)
                                           .Distinct()
                                           .OrderBy(w => w.Id, System.StringComparer.Ordinal)
                                           .ToArray();

            var catalog = AssetDatabase.LoadAssetAtPath<WeaponCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<WeaponCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
                Debug.Log($"[WeaponFactory] Created {CatalogPath}.");
            }

            WeaponDef fists = all.FirstOrDefault(w => w.Id == "fists");
            if (fists == null)
                Debug.LogError("[WeaponFactory] No 'fists' weapon. An unarmed player would have "
                               + "nothing to punch with.");

            catalog.Configure(all, fists);
            EditorUtility.SetDirty(catalog);

            foreach (WeaponDef def in all)
                if (!def.IsValid)
                    Debug.LogError($"[WeaponFactory] '{def.name}' is not a valid weapon: "
                                   + "check its id and its cooldown.");

            return catalog;
        }

        /// <summary>The catalog, for anything at bake time that needs it.</summary>
        internal static WeaponCatalog Catalog()
            => AssetDatabase.LoadAssetAtPath<WeaponCatalog>(CatalogPath) ?? BuildAll();
    }
}
