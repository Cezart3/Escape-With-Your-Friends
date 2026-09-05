using System.Collections.Generic;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// Every weapon in the game, in one asset, sorted by id.
    ///
    /// Fourth catalog, same doctrine as <see cref="ItemCatalog"/>, <see cref="BuffCatalog"/> and
    /// <see cref="RecipeCatalog"/>, and for the same hard reason: a network message cannot carry a
    /// ScriptableObject reference, so "what is in your hand" crosses the wire as a
    /// <see cref="ushort"/> index. **Index 0 means unarmed**, which is not the same as fists - fists
    /// are a real weapon with a real asset; 0 is the value a slot has before anybody has decided
    /// anything.
    ///
    /// Sorted by id and rebuilt whole by <c>WeaponFactory</c>, never hand-edited, so the index is a
    /// pure function of the id set. Adding a weapon shifts the indices after it, which is fine: every
    /// peer runs the same build and nothing persists an index.
    ///
    /// It also answers the one question the equip path asks - <see cref="ForItem"/>: given the thing
    /// in the selected hotbar slot, which weapon is that? That lookup is what makes "a new weapon is
    /// one asset" true, because it is the only place weapons and items are joined and it is built
    /// from the folder rather than from a list somebody maintains.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Weapon Catalog", fileName = "Weapons")]
    public class WeaponCatalog : ScriptableObject
    {
        [Tooltip("Sorted by id and regenerated whole. Do not reorder by hand: the order is the wire format.")]
        [SerializeField] WeaponDef[] _weapons = new WeaponDef[0];

        [Tooltip("What you swing with an empty hand. Also the fallback for an item that is not a weapon.")]
        [SerializeField] WeaponDef _fists;

        Dictionary<WeaponDef, ushort> _indices;
        Dictionary<string, WeaponDef> _byId;
        Dictionary<ItemDef, WeaponDef> _byItem;

        /// <summary>The catalog loaded by the running game. Set once, when the first weapon wakes up.</summary>
        public static WeaponCatalog Active { get; private set; }

        public IReadOnlyList<WeaponDef> Weapons => _weapons;

        /// <summary>Bare hands. Never null in a built game; the factory refuses to finish without it.</summary>
        public WeaponDef Fists => _fists;

        /// <summary>How many weapons there are. Indices run 1..Count.</summary>
        public int Count => _weapons.Length;

        public static void Use(WeaponCatalog catalog)
        {
            if (catalog == null || Active == catalog) return;

            Active = catalog;
            catalog.Rebuild();
        }

        /// <summary>The wire index for a definition, or 0 when it is not in the catalog.</summary>
        public ushort IndexOf(WeaponDef def)
        {
            if (def == null) return 0;

            _indices ??= BuildIndices();
            return _indices.TryGetValue(def, out ushort index) ? index : (ushort)0;
        }

        /// <summary>The definition an index refers to, or null for 0 and for anything out of range.</summary>
        public WeaponDef At(ushort index)
            => index == 0 || index > _weapons.Length ? null : _weapons[index - 1];

        /// <summary>Lookup by id, for tests and for anything that saved a name rather than a number.</summary>
        public WeaponDef Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            _byId ??= BuildIds();
            return _byId.TryGetValue(id, out WeaponDef def) ? def : null;
        }

        /// <summary>
        /// The weapon a held item is, or null when the item is not a weapon. Null is the interesting
        /// answer: it means "you are holding a fish", and the caller falls back to
        /// <see cref="Fists"/> rather than refusing to let you punch while carrying groceries.
        /// </summary>
        public WeaponDef ForItem(ItemDef item)
        {
            if (item == null) return null;

            _byItem ??= BuildItems();
            return _byItem.TryGetValue(item, out WeaponDef def) ? def : null;
        }

        /// <summary>Drops the lookup tables. Called by the factory after it rewrites the list.</summary>
        public void Invalidate()
        {
            _indices = null;
            _byId = null;
            _byItem = null;
        }

        void Rebuild()
        {
            Invalidate();
            _indices = BuildIndices();
            _byId = BuildIds();
            _byItem = BuildItems();
        }

        Dictionary<WeaponDef, ushort> BuildIndices()
        {
            var map = new Dictionary<WeaponDef, ushort>(_weapons.Length);
            for (int i = 0; i < _weapons.Length; i++)
                if (_weapons[i] != null) map[_weapons[i]] = (ushort)(i + 1);

            return map;
        }

        Dictionary<string, WeaponDef> BuildIds()
        {
            var map = new Dictionary<string, WeaponDef>(_weapons.Length);
            foreach (WeaponDef def in _weapons)
                if (def != null && !string.IsNullOrEmpty(def.Id)) map[def.Id] = def;

            return map;
        }

        Dictionary<ItemDef, WeaponDef> BuildItems()
        {
            var map = new Dictionary<ItemDef, WeaponDef>(_weapons.Length);
            foreach (WeaponDef def in _weapons)
                if (def != null && def.Item != null) map[def.Item] = def;

            return map;
        }

        /// <summary>Bake time only.</summary>
        public void Configure(WeaponDef[] weapons, WeaponDef fists)
        {
            _weapons = weapons ?? new WeaponDef[0];
            _fists = fists;
            Invalidate();
        }
    }
}
