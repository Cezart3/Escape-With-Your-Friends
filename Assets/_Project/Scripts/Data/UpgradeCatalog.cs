using System.Collections.Generic;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// Every weapon upgrade in the game, in one asset, sorted by id.
    ///
    /// The fifth catalog with these rules, after items, buffs, recipes and weapons, and identical for
    /// the same reason: an upgrade request crossing the wire is an index, index 0 means none, the sort
    /// order *is* the wire format, and nothing persists an index. Repeating the shape five times is
    /// still cheaper than a generic base class that has to be understood before any of the five makes
    /// sense.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Weapon Upgrade Catalog", fileName = "Upgrades")]
    public class UpgradeCatalog : ScriptableObject
    {
        [Tooltip("Sorted by id and regenerated whole. Do not reorder by hand: the order is the wire format.")]
        [SerializeField] UpgradeDef[] _upgrades = new UpgradeDef[0];

        Dictionary<UpgradeDef, ushort> _indices;
        Dictionary<string, UpgradeDef> _byId;
        Dictionary<WeaponDef, UpgradeDef> _byFrom;

        public static UpgradeCatalog Active { get; private set; }

        public IReadOnlyList<UpgradeDef> Upgrades => _upgrades;

        public int Count => _upgrades.Length;

        public static void Use(UpgradeCatalog catalog)
        {
            if (catalog == null || Active == catalog) return;

            Active = catalog;
            catalog.Invalidate();
        }

        public ushort IndexOf(UpgradeDef def)
        {
            if (def == null) return 0;

            _indices ??= BuildIndices();
            return _indices.TryGetValue(def, out ushort index) ? index : (ushort)0;
        }

        public UpgradeDef At(ushort index)
        {
            if (index == 0 || index > _upgrades.Length) return null;
            return _upgrades[index - 1];
        }

        public UpgradeDef Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            _byId ??= BuildIds();
            return _byId.TryGetValue(id, out UpgradeDef def) ? def : null;
        }

        /// <summary>
        /// The one step available from a weapon, or null at the end of a line.
        ///
        /// One rather than a list: a weapon has a single <see cref="WeaponDef.UpgradesTo"/>, and a
        /// branching tree would make "what does this become" a question with several answers and a UI
        /// that has to ask. Lines, not trees - the fork in the road is which weapon you carry.
        /// </summary>
        public UpgradeDef For(WeaponDef from)
        {
            if (from == null) return null;

            _byFrom ??= BuildFrom();
            return _byFrom.TryGetValue(from, out UpgradeDef def) ? def : null;
        }

        /// <summary>
        /// The whole line a weapon sits in, from where it is to the end. What the power curve is
        /// drawn from, and what the shop screen lists under a weapon you are carrying.
        /// </summary>
        public List<WeaponDef> LineFrom(WeaponDef start)
        {
            var line = new List<WeaponDef>();
            if (start == null) return line;

            WeaponDef at = start;
            line.Add(at);

            // Bounded by the catalog size: a hand-made asset that upgrades into itself would
            // otherwise spin here forever, and a data file should not be able to hang a server.
            for (int step = 0; step < _upgrades.Length + 1; step++)
            {
                UpgradeDef next = For(at);
                if (next == null || next.To == null || line.Contains(next.To)) break;

                at = next.To;
                line.Add(at);
            }

            return line;
        }

        Dictionary<UpgradeDef, ushort> BuildIndices()
        {
            var map = new Dictionary<UpgradeDef, ushort>(_upgrades.Length);
            for (int i = 0; i < _upgrades.Length; i++)
                if (_upgrades[i] != null) map[_upgrades[i]] = (ushort)(i + 1);

            return map;
        }

        Dictionary<string, UpgradeDef> BuildIds()
        {
            var map = new Dictionary<string, UpgradeDef>(_upgrades.Length);
            foreach (UpgradeDef def in _upgrades)
            {
                if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                map[def.Id] = def;
            }

            return map;
        }

        Dictionary<WeaponDef, UpgradeDef> BuildFrom()
        {
            var map = new Dictionary<WeaponDef, UpgradeDef>(_upgrades.Length);
            foreach (UpgradeDef def in _upgrades)
            {
                if (def == null || def.From == null || map.ContainsKey(def.From)) continue;
                map[def.From] = def;
            }

            return map;
        }

        public void Invalidate()
        {
            _indices = null;
            _byId = null;
            _byFrom = null;
        }

        /// <summary>Bake time only.</summary>
        public void Configure(UpgradeDef[] upgrades)
        {
            _upgrades = upgrades ?? new UpgradeDef[0];
            Invalidate();
        }
    }
}
