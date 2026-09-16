using System.IO;
using System.Linq;
using EscapeWithYourFriends.Data;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// #62's four parts, written from code into <c>Assets/_Project/Data/VehicleUpgrades/</c>.
    ///
    /// There is no catalog asset and no build entry point of its own: the only things that want
    /// these are the two vehicle builders, which ask for the list their vehicle accepts while they
    /// are baking its prefab. A part that no vehicle accepts would be an asset nobody references,
    /// which is the same as not existing.
    /// </summary>
    public static class VehicleUpgradeFactory
    {
        const string Folder = "Assets/_Project/Data/VehicleUpgrades";

        readonly struct Seed
        {
            public readonly string Item;
            public readonly string Name;
            public readonly VehiclePart Part;
            public readonly float Multiplier;
            public readonly string Description;

            public Seed(string item, string name, VehiclePart part, float multiplier, string description)
            {
                Item = item;
                Name = name;
                Part = part;
                Multiplier = multiplier;
                Description = description;
            }
        }

        /// <summary>
        /// One tier each, and the multipliers are the whole balance of the issue: big enough that a
        /// player feels it on the first corner, small enough that the stock vehicle is still a
        /// vehicle. Half again on the engine and the tank, a third more grip, and armour that turns
        /// two survivable crashes into three.
        /// </summary>
        static readonly Seed[] Seeds =
        {
            new("engine_kit", "Tuned Engine", VehiclePart.Engine, 1.5f,
                "Half again the torque, and the ceiling raised to match."),
            new("tyre_kit", "Grippy Tyres", VehiclePart.Tyres, 1.35f,
                "A third more grip. Corners you used to slide through, you now turn."),
            new("armour_kit", "Bolt-on Armour", VehiclePart.Armour, 1.6f,
                "Plate on every panel. The same crash leaves you driving."),
            new("tank_kit", "Long-range Tank", VehiclePart.Tank, 1.5f,
                "Half again the range on one fill."),
        };

        /// <summary>Everything a wheeled vehicle takes.</summary>
        internal static VehicleUpgradeDef[] Land() => Ensure(Seeds.Select(s => s.Item).ToArray());

        /// <summary>Everything a hull takes. No tyres, for reasons the hull is aware of.</summary>
        internal static VehicleUpgradeDef[] Sea()
            => Ensure(new[] { "engine_kit", "armour_kit", "tank_kit" });

        static VehicleUpgradeDef[] Ensure(string[] items)
        {
            Directory.CreateDirectory(Folder);

            var built = new VehicleUpgradeDef[items.Length];

            for (int i = 0; i < items.Length; i++)
            {
                Seed seed = Seeds.First(s => s.Item == items[i]);
                string path = $"{Folder}/{seed.Item}.asset";

                var def = AssetDatabase.LoadAssetAtPath<VehicleUpgradeDef>(path);
                bool fresh = def == null;

                if (fresh) def = ScriptableObject.CreateInstance<VehicleUpgradeDef>();

                def.Configure(seed.Item, seed.Name, seed.Part, tier: 1, seed.Multiplier,
                              seed.Description);

                if (fresh)
                {
                    AssetDatabase.CreateAsset(def, path);
                    Debug.Log($"[VehicleUpgradeFactory] Created {path}.");
                }
                else
                {
                    EditorUtility.SetDirty(def);
                }

                built[i] = def;
            }

            AssetDatabase.SaveAssets();

            return built;
        }
    }
}
