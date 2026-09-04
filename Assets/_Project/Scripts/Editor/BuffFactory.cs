using System.Collections.Generic;
using System.IO;
using System.Linq;
using EscapeWithYourFriends.Data;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The starter buff set, the catalog that indexes it, and the wiring that makes five of the
    /// existing items consumable.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.BuffFactory.Build
    ///
    /// Same rules as <see cref="ItemFactory"/>, and for the same reasons: it creates assets that do
    /// not exist and rebuilds the catalog, but it never overwrites a buff somebody has already tuned.
    /// A sixth consumable is a row in one of the tables below, or a pair of .asset files edited by
    /// hand - both end up in the catalog, and neither is a code change.
    ///
    /// The one thing it *does* re-apply is the link from an item to its effect, because that is
    /// structure rather than balance: a coconut without an effect is not a design decision, it is a
    /// missing reference.
    /// </summary>
    public static class BuffFactory
    {
        const string Folder = "Assets/_Project/Data/Buffs";
        const string CatalogPath = "Assets/_Project/Data/Buffs.asset";
        const string ItemFolder = "Assets/_Project/Data/Items";

        readonly struct Seed
        {
            public readonly string Id;
            public readonly string Name;
            public readonly string Description;
            public readonly float Duration;
            public readonly BuffStacking Stacking;

            // Instant.
            public readonly float Health, Hunger, Thirst, Warmth, Stamina;

            // Per second.
            public readonly float HealthRate, HungerRate, ThirstRate, WarmthRate, StaminaRate;

            // Multipliers.
            public readonly float Speed, DamageTaken, StaminaCost, Haze;

            public Seed(string id, string name, string description, float duration,
                        BuffStacking stacking,
                        float health = 0f, float hunger = 0f, float thirst = 0f, float warmth = 0f,
                        float stamina = 0f,
                        float healthRate = 0f, float hungerRate = 0f, float thirstRate = 0f,
                        float warmthRate = 0f, float staminaRate = 0f,
                        float speed = 1f, float damageTaken = 1f, float staminaCost = 1f,
                        float haze = 0f)
            {
                Id = id;
                Name = name;
                Description = description;
                Duration = duration;
                Stacking = stacking;
                Health = health;
                Hunger = hunger;
                Thirst = thirst;
                Warmth = warmth;
                Stamina = stamina;
                HealthRate = healthRate;
                HungerRate = hungerRate;
                ThirstRate = thirstRate;
                WarmthRate = warmthRate;
                StaminaRate = staminaRate;
                Speed = speed;
                DamageTaken = damageTaken;
                StaminaCost = staminaCost;
                Haze = haze;
            }
        }

        /// <summary>
        /// Six to start with. Between them they exercise every field on <see cref="BuffDef"/> that the
        /// casino will need later - instant, over time, all three multipliers, the haze, and all three
        /// stacking rules - so #M6's alcohol is an asset rather than a system.
        /// </summary>
        static readonly Seed[] Seeds =
        {
            // Food is mostly instant, with a small trailing top-up so eating while walking is not
            // strictly worse than standing still to do it.
            new("well_fed", "Well Fed", "A full stomach and a little more coming.",
                20f, BuffStacking.Refresh, hunger: 22f, hungerRate: 0.4f),

            new("cooked_meal", "Cooked Meal", "Worth the fire. Worth the fish.",
                45f, BuffStacking.Refresh, hunger: 35f, health: 5f, hungerRate: 0.4f,
                healthRate: 0.3f),

            new("hydrated", "Hydrated", "Water. The whole point of the filter.",
                0f, BuffStacking.Refresh, thirst: 45f),

            // A coconut is food and drink at once, which is why it is worth carrying two of.
            new("coconut_water", "Coconut Water", "Food and water in one inconvenient shell.",
                12f, BuffStacking.Refresh, hunger: 12f, thirst: 20f, hungerRate: 0.3f),

            // The interesting one: slow, cancellable, and Ignore-stacking so a second bandage while the
            // first is still working is refused rather than wasted.
            new("bandaged", "Bandaged", "Closing up. Do not get hit.",
                15f, BuffStacking.Ignore, health: 8f, healthRate: 1.6f, staminaCost: 1.15f),

            // Nothing applies this yet. It exists so #M6 has to write an asset and not a system, and
            // so the multipliers and the haze are exercised by something before then.
            new("drunk", "Drunk", "Braver, slower, and much harder to aim.",
                90f, BuffStacking.Stack, thirst: -10f, thirstRate: -0.25f,
                speed: 0.88f, damageTaken: 0.75f, staminaCost: 0.85f, haze: 0.5f),
        };

        /// <summary>
        /// Which item does what. Structure rather than balance, so this *is* re-applied on every run -
        /// a coconut with no effect is a missing reference, not a design decision.
        ///
        /// The seconds are the whole feel of using something. A drink is almost instant, a meal is a
        /// moment of standing still, and a bandage is long enough that doing it mid-fight is a bad
        /// idea rather than a free action.
        /// </summary>
        static readonly (string Item, string Buff, float Seconds, string Leaves)[] Links =
        {
            ("coconut", "coconut_water", 2.5f, null),
            ("fish_raw", "well_fed", 3f, null),
            ("fish_cooked", "cooked_meal", 2.5f, null),
            ("water_bottle", "hydrated", 1.5f, "empty_bottle"),
            ("bandage", "bandaged", 3f, null),
        };

        /// <summary>
        /// The empty bottle. Not in <see cref="ItemFactory"/>'s table because it only exists as the
        /// other half of drinking, and #M4's water filter is the thing that will turn it back.
        /// </summary>
        static readonly (string Id, string Name, string Description) EmptyBottle =
            ("empty_bottle", "Empty Bottle", "Refillable at the filter. Worth carrying back.");

        public static void Build()
        {
            Directory.CreateDirectory(Folder);

            int created = Seeds.Count(Ensure);

            bool bottle = EnsureEmptyBottle();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // A new item means the item catalog is out of date, and an item that is not in the catalog
            // cannot be carried at all - the index is the wire format. Rebuilt here rather than left
            // for the next ItemFactory run, because the item this file creates is one nobody would
            // think to go and regenerate.
            if (bottle) Debug.Log($"[BuffFactory] item catalog rebuilt: {ItemFactory.Rebuild()} item(s).");

            int total = Rebuild();
            int linked = Link();

            Debug.Log($"[BuffFactory] {total} buff(s) in the catalog"
                      + (created > 0 ? $", {created} created just now" : ", none created - they all existed")
                      + $"; {linked} item(s) are consumable. Another one is a row in BuffFactory or a "
                      + $"pair of .asset files; both end up here.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static bool Ensure(Seed seed)
        {
            string path = $"{Folder}/{seed.Id}.asset";
            if (AssetDatabase.LoadAssetAtPath<BuffDef>(path) != null) return false;

            var def = ScriptableObject.CreateInstance<BuffDef>();
            var so = new SerializedObject(def);

            so.FindProperty("_id").stringValue = seed.Id;
            so.FindProperty("_displayName").stringValue = seed.Name;
            so.FindProperty("_description").stringValue = seed.Description;
            so.FindProperty("_duration").floatValue = seed.Duration;
            so.FindProperty("_stacking").enumValueIndex = (int)seed.Stacking;

            so.FindProperty("_health").floatValue = seed.Health;
            so.FindProperty("_hunger").floatValue = seed.Hunger;
            so.FindProperty("_thirst").floatValue = seed.Thirst;
            so.FindProperty("_warmth").floatValue = seed.Warmth;
            so.FindProperty("_stamina").floatValue = seed.Stamina;

            so.FindProperty("_healthPerSecond").floatValue = seed.HealthRate;
            so.FindProperty("_hungerPerSecond").floatValue = seed.HungerRate;
            so.FindProperty("_thirstPerSecond").floatValue = seed.ThirstRate;
            so.FindProperty("_warmthPerSecond").floatValue = seed.WarmthRate;
            so.FindProperty("_staminaPerSecond").floatValue = seed.StaminaRate;

            so.FindProperty("_speedMultiplier").floatValue = seed.Speed;
            so.FindProperty("_damageTakenMultiplier").floatValue = seed.DamageTaken;
            so.FindProperty("_staminaCostMultiplier").floatValue = seed.StaminaCost;
            so.FindProperty("_haze").floatValue = seed.Haze;

            so.ApplyModifiedPropertiesWithoutUndo();

            AssetDatabase.CreateAsset(def, path);
            return true;
        }

        /// <summary>Returns whether it actually created the asset, so the caller knows to rebuild.</summary>
        static bool EnsureEmptyBottle()
        {
            string path = $"{ItemFolder}/{EmptyBottle.Id}.asset";
            if (AssetDatabase.LoadAssetAtPath<ItemDef>(path) != null) return false;

            Directory.CreateDirectory(ItemFolder);

            var def = ScriptableObject.CreateInstance<ItemDef>();
            var so = new SerializedObject(def);

            so.FindProperty("_id").stringValue = EmptyBottle.Id;
            so.FindProperty("_displayName").stringValue = EmptyBottle.Name;
            so.FindProperty("_description").stringValue = EmptyBottle.Description;
            so.FindProperty("_category").enumValueIndex = (int)ItemCategory.Drink;
            so.FindProperty("_maxStack").intValue = 4;
            so.FindProperty("_weight").floatValue = 0.3f;
            so.FindProperty("_value").intValue = 3;
            so.ApplyModifiedPropertiesWithoutUndo();

            AssetDatabase.CreateAsset(def, path);
            Debug.Log($"[BuffFactory] Created {path}; drinking a bottle now leaves one behind.");
            return true;
        }

        static int Rebuild()
        {
            List<BuffDef> found = AssetDatabase.FindAssets("t:BuffDef", new[] { Folder })
                                               .Select(AssetDatabase.GUIDToAssetPath)
                                               .Select(AssetDatabase.LoadAssetAtPath<BuffDef>)
                                               .Where(def => def != null)
                                               .ToList();

            var seen = new HashSet<string>();
            foreach (BuffDef def in found)
            {
                if (string.IsNullOrWhiteSpace(def.Id))
                    Debug.LogError($"[BuffFactory] {def.name} has no id and will not survive a rebuild.");
                else if (!seen.Add(def.Id))
                    Debug.LogError($"[BuffFactory] Two buffs share the id '{def.Id}'; one is unreachable.");
            }

            found.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            var catalog = AssetDatabase.LoadAssetAtPath<BuffCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<BuffCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
                Debug.Log($"[BuffFactory] Created {CatalogPath}.");
            }

            var so = new SerializedObject(catalog);
            SerializedProperty buffs = so.FindProperty("_buffs");
            buffs.arraySize = found.Count;
            for (int i = 0; i < found.Count; i++)
                buffs.GetArrayElementAtIndex(i).objectReferenceValue = found[i];

            so.ApplyModifiedPropertiesWithoutUndo();
            catalog.Invalidate();

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            for (int i = 0; i < found.Count; i++)
                Debug.Log($"[BuffFactory]   {i + 1,3}  {found[i].Id,-14} {found[i].Duration,5:F0}s  "
                          + $"{found[i].Stacking,-7}  speed x{found[i].SpeedMultiplier:F2}  "
                          + $"dmg x{found[i].DamageTakenMultiplier:F2}  haze {found[i].Haze:F2}");

            return found.Count;
        }

        static int Link()
        {
            int linked = 0;

            foreach ((string item, string buff, float seconds, string leaves) in Links)
            {
                var target = AssetDatabase.LoadAssetAtPath<ItemDef>($"{ItemFolder}/{item}.asset");
                var effect = AssetDatabase.LoadAssetAtPath<BuffDef>($"{Folder}/{buff}.asset");

                if (target == null)
                {
                    Debug.LogError($"[BuffFactory] No item '{item}'; run ItemFactory.Build first.");
                    continue;
                }

                if (effect == null)
                {
                    Debug.LogError($"[BuffFactory] No buff '{buff}' for item '{item}'.");
                    continue;
                }

                var so = new SerializedObject(target);
                so.FindProperty("_effect").objectReferenceValue = effect;
                so.FindProperty("_useSeconds").floatValue = seconds;

                so.FindProperty("_leavesBehind").objectReferenceValue = leaves == null
                    ? null
                    : AssetDatabase.LoadAssetAtPath<ItemDef>($"{ItemFolder}/{leaves}.asset");

                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(target);

                linked++;
            }

            AssetDatabase.SaveAssets();
            return linked;
        }

        /// <summary>The catalog, for anything at bake time that needs to wire it into a prefab.</summary>
        internal static BuffCatalog Catalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<BuffCatalog>(CatalogPath);
            if (catalog == null)
            {
                Directory.CreateDirectory(Folder);
                foreach (Seed seed in Seeds) Ensure(seed);
                AssetDatabase.SaveAssets();
                Rebuild();
                catalog = AssetDatabase.LoadAssetAtPath<BuffCatalog>(CatalogPath);
            }

            return catalog;
        }
    }
}
