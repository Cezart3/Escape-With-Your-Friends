using System.Collections.Generic;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// Every recipe in the game, in one asset, sorted by id.
    ///
    /// The third catalog with these rules, after <see cref="ItemCatalog"/> and
    /// <see cref="BuffCatalog"/>, and identical for the same reason: a craft request crossing the wire
    /// is an index, index 0 means none, the sort order is the wire format, and nothing persists an
    /// index. The repetition is deliberate - three lists that behave the same way are easier to reason
    /// about than one generic base class that has to be read before any of them makes sense.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Recipe Catalog", fileName = "Recipes")]
    public class RecipeCatalog : ScriptableObject
    {
        [Tooltip("Sorted by id and regenerated whole. Do not reorder by hand: the order is the wire format.")]
        [SerializeField] RecipeDef[] _recipes = new RecipeDef[0];

        Dictionary<RecipeDef, ushort> _indices;
        Dictionary<string, RecipeDef> _byId;

        public static RecipeCatalog Active { get; private set; }

        public IReadOnlyList<RecipeDef> Recipes => _recipes;

        public int Count => _recipes.Length;

        public static void Use(RecipeCatalog catalog)
        {
            if (catalog == null || Active == catalog) return;

            Active = catalog;
            catalog.Invalidate();
        }

        public ushort IndexOf(RecipeDef def)
        {
            if (def == null) return 0;

            _indices ??= BuildIndices();
            return _indices.TryGetValue(def, out ushort index) ? index : (ushort)0;
        }

        public RecipeDef At(ushort index)
        {
            if (index == 0 || index > _recipes.Length) return null;
            return _recipes[index - 1];
        }

        public RecipeDef Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            _byId ??= BuildIds();
            return _byId.TryGetValue(id, out RecipeDef def) ? def : null;
        }

        /// <summary>Everything makeable at one station. What #46's list is built from.</summary>
        public List<RecipeDef> For(CraftStation station)
        {
            var found = new List<RecipeDef>();

            foreach (RecipeDef def in _recipes)
                if (def != null && def.Station == station) found.Add(def);

            return found;
        }

        Dictionary<RecipeDef, ushort> BuildIndices()
        {
            var map = new Dictionary<RecipeDef, ushort>(_recipes.Length);
            for (int i = 0; i < _recipes.Length; i++)
                if (_recipes[i] != null) map[_recipes[i]] = (ushort)(i + 1);

            return map;
        }

        Dictionary<string, RecipeDef> BuildIds()
        {
            var map = new Dictionary<string, RecipeDef>(_recipes.Length);
            foreach (RecipeDef def in _recipes)
            {
                if (def == null || string.IsNullOrEmpty(def.Id)) continue;
                map[def.Id] = def;
            }

            return map;
        }

        public void Invalidate()
        {
            _indices = null;
            _byId = null;
        }
    }
}
