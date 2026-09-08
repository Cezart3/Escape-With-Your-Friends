using System.Collections.Generic;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// Every species in the game, in one asset, sorted by id.
    ///
    /// Sixth catalog, same doctrine as <see cref="ItemCatalog"/>, <see cref="BuffCatalog"/>,
    /// <see cref="RecipeCatalog"/>, <see cref="WeaponCatalog"/> and <see cref="UpgradeCatalog"/>,
    /// and here for the same hard reason: there is one animal prefab and N species, so "which
    /// species is this thing" has to cross the wire, and a network message cannot carry a
    /// ScriptableObject reference. It travels as a <see cref="ushort"/> index. **Index 0 means no
    /// species**, which is the value a freshly instantiated body has before the server has said
    /// what it is.
    ///
    /// Sorted by id and rebuilt whole by <c>AnimalFactory</c>, never hand-edited, so the index is a
    /// pure function of the id set. Adding a species shifts the indices after it, which is fine:
    /// every peer runs the same build and nothing persists an index.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Animal Catalog", fileName = "Animals")]
    public class AnimalCatalog : ScriptableObject
    {
        [Tooltip("Sorted by id and regenerated whole. Do not reorder by hand: the order is the wire format.")]
        [SerializeField] AnimalDef[] _animals = new AnimalDef[0];

        Dictionary<AnimalDef, ushort> _indices;
        Dictionary<string, AnimalDef> _byId;

        /// <summary>The catalog loaded by the running game. Set once, when the first animal wakes up.</summary>
        public static AnimalCatalog Active { get; private set; }

        public IReadOnlyList<AnimalDef> Animals => _animals;

        /// <summary>How many species there are. Indices run 1..Count.</summary>
        public int Count => _animals.Length;

        public static void Use(AnimalCatalog catalog)
        {
            if (catalog == null || Active == catalog) return;

            Active = catalog;
            catalog.Rebuild();
        }

        /// <summary>The wire index for a definition, or 0 when it is not in the catalog.</summary>
        public ushort IndexOf(AnimalDef def)
        {
            if (def == null) return 0;

            _indices ??= BuildIndices();
            return _indices.TryGetValue(def, out ushort index) ? index : (ushort)0;
        }

        /// <summary>The definition an index refers to, or null for 0 and for anything out of range.</summary>
        public AnimalDef At(ushort index)
            => index == 0 || index > _animals.Length ? null : _animals[index - 1];

        /// <summary>Lookup by id, for tests and for anything that saved a name rather than a number.</summary>
        public AnimalDef Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            _byId ??= BuildIds();
            return _byId.TryGetValue(id, out AnimalDef def) ? def : null;
        }

        /// <summary>Drops the lookup tables. Called by the factory after it rewrites the list.</summary>
        public void Invalidate()
        {
            _indices = null;
            _byId = null;
        }

        void Rebuild()
        {
            Invalidate();
            _indices = BuildIndices();
            _byId = BuildIds();
        }

        Dictionary<AnimalDef, ushort> BuildIndices()
        {
            var map = new Dictionary<AnimalDef, ushort>(_animals.Length);
            for (int i = 0; i < _animals.Length; i++)
                if (_animals[i] != null) map[_animals[i]] = (ushort)(i + 1);

            return map;
        }

        Dictionary<string, AnimalDef> BuildIds()
        {
            var map = new Dictionary<string, AnimalDef>(_animals.Length);
            foreach (AnimalDef def in _animals)
                if (def != null && !string.IsNullOrEmpty(def.Id)) map[def.Id] = def;

            return map;
        }

        /// <summary>Bake time only.</summary>
        public void Configure(AnimalDef[] animals)
        {
            _animals = animals ?? new AnimalDef[0];
            Invalidate();
        }
    }
}
