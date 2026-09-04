using System.Collections.Generic;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// Every buff in the game, in one asset, sorted by id.
    ///
    /// Deliberately the same shape as <see cref="ItemCatalog"/>, for the same reason and with the same
    /// rules: a network message cannot carry a ScriptableObject reference, so an active buff crossing
    /// the wire is an index into this list. **Index 0 means none**, real buffs run 1..N, the order is
    /// sorted by id and rebuilt whole, and nothing persists an index.
    ///
    /// Two catalogs rather than one shared registry because the two lists have different lifetimes and
    /// different authors: items are content somebody adds all afternoon, buffs are a much smaller set
    /// that mostly changes when a system does. Sharing one index space would mean adding a coconut
    /// renumbered every buff in flight.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Buff Catalog", fileName = "Buffs")]
    public class BuffCatalog : ScriptableObject
    {
        [Tooltip("Sorted by id and regenerated whole. Do not reorder by hand: the order is the wire format.")]
        [SerializeField] BuffDef[] _buffs = new BuffDef[0];

        Dictionary<BuffDef, ushort> _indices;
        Dictionary<string, BuffDef> _byId;

        /// <summary>The catalog the running game is using. Published by the first buff holder to wake.</summary>
        public static BuffCatalog Active { get; private set; }

        public IReadOnlyList<BuffDef> Buffs => _buffs;

        /// <summary>How many buffs exist. Indices run 1..Count.</summary>
        public int Count => _buffs.Length;

        public static void Use(BuffCatalog catalog)
        {
            if (catalog == null || Active == catalog) return;

            Active = catalog;
            catalog.Invalidate();
        }

        public ushort IndexOf(BuffDef def)
        {
            if (def == null) return 0;

            _indices ??= BuildIndices();
            return _indices.TryGetValue(def, out ushort index) ? index : (ushort)0;
        }

        public BuffDef At(ushort index)
        {
            if (index == 0 || index > _buffs.Length) return null;
            return _buffs[index - 1];
        }

        public BuffDef Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            _byId ??= BuildIds();
            return _byId.TryGetValue(id, out BuffDef def) ? def : null;
        }

        Dictionary<BuffDef, ushort> BuildIndices()
        {
            var map = new Dictionary<BuffDef, ushort>(_buffs.Length);
            for (int i = 0; i < _buffs.Length; i++)
                if (_buffs[i] != null) map[_buffs[i]] = (ushort)(i + 1);

            return map;
        }

        Dictionary<string, BuffDef> BuildIds()
        {
            var map = new Dictionary<string, BuffDef>(_buffs.Length);
            foreach (BuffDef def in _buffs)
            {
                if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                map[def.Id] = def;
            }

            return map;
        }

        /// <summary>Drops the cached lookups. Needed after the editor rewrites the array directly.</summary>
        public void Invalidate()
        {
            _indices = null;
            _byId = null;
        }
    }
}
