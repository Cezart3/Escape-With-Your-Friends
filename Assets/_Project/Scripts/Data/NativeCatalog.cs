using System.Collections.Generic;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// Every kind of native, in one asset, sorted by id.
    ///
    /// Eighth catalog, identical doctrine to <see cref="ItemCatalog"/>, <see cref="BuffCatalog"/>,
    /// <see cref="RecipeCatalog"/>, <see cref="WeaponCatalog"/>, <see cref="UpgradeCatalog"/>,
    /// <see cref="AnimalCatalog"/> and <see cref="FishCatalog"/>, and here for the same reason as the
    /// animals: one prefab wears every role, so "which role is this body" has to cross the wire, and
    /// a spawn message cannot carry a ScriptableObject reference. It travels as a
    /// <see cref="ushort"/>; **index 0 means no role**, which is what a body has before the server
    /// has said what it is.
    ///
    /// Sorted by id and rebuilt whole by <c>NativeFactory</c>. Nothing persists an index, so adding
    /// a role and shifting the ones after it costs nothing.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Native Catalog", fileName = "Natives")]
    public class NativeCatalog : ScriptableObject
    {
        [Tooltip("Sorted by id and regenerated whole. Do not reorder by hand: the order is the wire format.")]
        [SerializeField] NativeDef[] _natives = new NativeDef[0];

        Dictionary<NativeDef, ushort> _indices;
        Dictionary<string, NativeDef> _byId;

        /// <summary>The catalog the running game is using. Set when the first native wakes up.</summary>
        public static NativeCatalog Active { get; private set; }

        public IReadOnlyList<NativeDef> Natives => _natives;

        /// <summary>How many roles there are. Indices run 1..Count.</summary>
        public int Count => _natives.Length;

        public static void Use(NativeCatalog catalog)
        {
            if (catalog == null || Active == catalog) return;

            Active = catalog;
            catalog.Rebuild();
        }

        /// <summary>The wire index for a definition, or 0 when it is not in the catalog.</summary>
        public ushort IndexOf(NativeDef def)
        {
            if (def == null) return 0;

            _indices ??= BuildIndices();
            return _indices.TryGetValue(def, out ushort index) ? index : (ushort)0;
        }

        /// <summary>The definition an index refers to, or null for 0 and for anything out of range.</summary>
        public NativeDef At(ushort index)
            => index == 0 || index > _natives.Length ? null : _natives[index - 1];

        /// <summary>Lookup by id, for tests and for anything that saved a name rather than a number.</summary>
        public NativeDef Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            _byId ??= BuildIds();
            return _byId.TryGetValue(id, out NativeDef def) ? def : null;
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

        Dictionary<NativeDef, ushort> BuildIndices()
        {
            var map = new Dictionary<NativeDef, ushort>(_natives.Length);
            for (int i = 0; i < _natives.Length; i++)
                if (_natives[i] != null) map[_natives[i]] = (ushort)(i + 1);

            return map;
        }

        Dictionary<string, NativeDef> BuildIds()
        {
            var map = new Dictionary<string, NativeDef>(_natives.Length);
            foreach (NativeDef def in _natives)
                if (def != null && !string.IsNullOrEmpty(def.Id)) map[def.Id] = def;

            return map;
        }

        /// <summary>Bake time only.</summary>
        public void Configure(NativeDef[] natives)
        {
            _natives = natives ?? new NativeDef[0];
            Invalidate();
        }
    }
}
