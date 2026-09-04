using System.Collections.Generic;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// Every item in the game, in one asset, sorted by id.
    ///
    /// This exists because of one hard constraint: a network message cannot carry a
    /// ScriptableObject reference. An inventory slot crossing the wire has to be a number, so it is
    /// an index into this list - two bytes instead of a string per slot, on a list that replicates
    /// every time anybody picks anything up.
    ///
    /// **Index 0 means empty.** Real items start at 1, so a default-constructed
    /// <see cref="Items.ItemStack"/> is an empty slot without anybody having to say so.
    ///
    /// The order is sorted by id and rebuilt by <c>ItemFactory</c>, never hand-edited, which makes the
    /// index a pure function of the id set. Adding an item shifts the indices of everything after it -
    /// which is fine, because every peer in a session runs the same build, and nothing persists an
    /// index. Saves and recipes refer to ids.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Item Catalog", fileName = "Items")]
    public class ItemCatalog : ScriptableObject
    {
        [Tooltip("Sorted by id and regenerated whole. Do not reorder by hand: the order is the wire format.")]
        [SerializeField] ItemDef[] _items = new ItemDef[0];

        [Tooltip("The one networked prefab every dropped item spawns as. Assigned by WorldItemBuilder.")]
        [SerializeField] GameObject _worldItemPrefab;

        Dictionary<ItemDef, ushort> _indices;
        Dictionary<string, ItemDef> _byId;

        /// <summary>The catalog loaded by the running game. Set once, when the first inventory wakes up.</summary>
        public static ItemCatalog Active { get; private set; }

        public IReadOnlyList<ItemDef> Items => _items;

        /// <summary>
        /// The prefab a dropped stack becomes. **One prefab for every item in the game**, because a
        /// networked prefab has to be registered in FishNet's spawnable list on every peer, and a
        /// per-item networked prefab would make adding an item a registration step - which is exactly
        /// the thing #41 spent its effort removing. What an item *looks* like on the ground is
        /// <see cref="ItemDef.WorldPrefab"/>, an ordinary non-networked visual parented underneath.
        /// </summary>
        public GameObject WorldItemPrefab => _worldItemPrefab;

        /// <summary>How many real items there are. Indices run 1..Count.</summary>
        public int Count => _items.Length;

        public static void Use(ItemCatalog catalog)
        {
            if (catalog == null || Active == catalog) return;

            Active = catalog;
            catalog.Rebuild();
        }

        /// <summary>The wire index for a definition, or 0 when it is not in the catalog.</summary>
        public ushort IndexOf(ItemDef def)
        {
            if (def == null) return 0;

            _indices ??= BuildIndices();
            return _indices.TryGetValue(def, out ushort index) ? index : (ushort)0;
        }

        /// <summary>The definition an index refers to, or null for 0 and for anything out of range.</summary>
        public ItemDef At(ushort index)
        {
            if (index == 0 || index > _items.Length) return null;
            return _items[index - 1];
        }

        /// <summary>By id, for recipes, saves, shop listings and the console.</summary>
        public ItemDef Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            _byId ??= BuildIds();
            return _byId.TryGetValue(id, out ItemDef def) ? def : null;
        }

        void Rebuild()
        {
            _indices = BuildIndices();
            _byId = BuildIds();
        }

        Dictionary<ItemDef, ushort> BuildIndices()
        {
            var map = new Dictionary<ItemDef, ushort>(_items.Length);
            for (int i = 0; i < _items.Length; i++)
                if (_items[i] != null) map[_items[i]] = (ushort)(i + 1);

            return map;
        }

        Dictionary<string, ItemDef> BuildIds()
        {
            var map = new Dictionary<string, ItemDef>(_items.Length);
            foreach (ItemDef def in _items)
            {
                if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                map[def.Id] = def;
            }

            return map;
        }

        /// <summary>
        /// Drops the cached lookups so the next call rebuilds them. Needed after <c>ItemFactory</c>
        /// rewrites the array through a SerializedObject, which changes the field without going
        /// through any code here.
        /// </summary>
        public void Invalidate()
        {
            _indices = null;
            _byId = null;
        }
    }
}
