using System.Collections.Generic;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// Everything that can be on the end of a line, in one asset, sorted by id.
    ///
    /// Seventh catalog, same doctrine as <see cref="ItemCatalog"/> and the rest, and here for the
    /// usual reason: what you hooked has to cross the wire so your friends can see you lose it, and
    /// a network message cannot carry a ScriptableObject reference. It travels as a
    /// <see cref="ushort"/> index, and **index 0 means nothing is hooked**, which is the value the
    /// SyncVar holds while the bobber is still sitting there.
    ///
    /// It also owns the roll. The table is weights rather than percentages (see <see cref="FishDef"/>)
    /// and <see cref="Roll"/> is the only place that turns them into a pick - on the server, never on
    /// a client, for the same reason the roulette wheel in the plan is decided host-side and
    /// animated afterwards.
    ///
    /// The one thing here that is not a fish is <see cref="Rod"/>. The catalog owns it because "what
    /// opens this minigame" is a property of fishing, and the alternative - a second serialized
    /// ItemDef on the player prefab, wired by the prefab builder - would put the answer somewhere
    /// nothing else could read it.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Fish Catalog", fileName = "Fish")]
    public class FishCatalog : ScriptableObject
    {
        [Tooltip("Sorted by id and regenerated whole. Do not reorder by hand: the order is the wire format.")]
        [SerializeField] FishDef[] _fish = new FishDef[0];

        [Tooltip("The tool that opens the minigame. Holding it is what makes Attack mean 'cast'.")]
        [SerializeField] ItemDef _rod;

        Dictionary<FishDef, ushort> _indices;
        Dictionary<string, FishDef> _byId;

        /// <summary>The catalog loaded by the running game. Set once, when the first rod wakes up.</summary>
        public static FishCatalog Active { get; private set; }

        public IReadOnlyList<FishDef> Fish => _fish;

        /// <summary>How many entries there are. Indices run 1..Count.</summary>
        public int Count => _fish.Length;

        /// <summary>The fishing rod. Null before <c>FishFactory</c> has run.</summary>
        public ItemDef Rod => _rod;

        /// <summary>Sum of every rarity weight. The denominator of the table.</summary>
        public int TotalRarity
        {
            get
            {
                int total = 0;
                foreach (FishDef def in _fish)
                    if (def != null) total += def.Rarity;

                return total;
            }
        }

        public static void Use(FishCatalog catalog)
        {
            if (catalog == null || Active == catalog) return;

            Active = catalog;
            catalog.Rebuild();
        }

        /// <summary>The wire index for a definition, or 0 when it is not in the catalog.</summary>
        public ushort IndexOf(FishDef def)
        {
            if (def == null) return 0;

            _indices ??= BuildIndices();
            return _indices.TryGetValue(def, out ushort index) ? index : (ushort)0;
        }

        /// <summary>The definition an index refers to, or null for 0 and for anything out of range.</summary>
        public FishDef At(ushort index)
            => index == 0 || index > _fish.Length ? null : _fish[index - 1];

        /// <summary>Lookup by id, for tests and for anything that saved a name rather than a number.</summary>
        public FishDef Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            _byId ??= BuildIds();
            return _byId.TryGetValue(id, out FishDef def) ? def : null;
        }

        /// <summary>
        /// Picks one entry by rarity weight. <paramref name="roll"/> is 0..1; passing it in rather
        /// than calling Random here is what lets the harness walk the whole table deterministically
        /// and prove the odds are the odds.
        /// </summary>
        public FishDef Roll(float roll)
        {
            int total = TotalRarity;
            if (total <= 0) return null;

            // Clamped rather than repeated: a roll of exactly 1 must land on the last entry, not
            // wrap round to the first, or the rarest thing in the table is quietly the commonest.
            float target = Mathf.Clamp01(roll) * total;
            float seen = 0f;

            foreach (FishDef def in _fish)
            {
                if (def == null) continue;

                seen += def.Rarity;
                if (target <= seen) return def;
            }

            // Floating point landed past the end. The last non-null entry is the honest answer.
            for (int i = _fish.Length - 1; i >= 0; i--)
                if (_fish[i] != null) return _fish[i];

            return null;
        }

        /// <summary>The chance of one entry, 0..1. What the harness prints and the docs quote.</summary>
        public float ChanceOf(FishDef def)
        {
            int total = TotalRarity;
            return def == null || total <= 0 ? 0f : def.Rarity / (float)total;
        }

        /// <summary>Average value of one catch across the whole table, before the trader's cut.</summary>
        public float ExpectedValue
        {
            get
            {
                int total = TotalRarity;
                if (total <= 0) return 0f;

                float sum = 0f;
                foreach (FishDef def in _fish)
                    if (def != null) sum += def.Rarity * def.ExpectedValue;

                return sum / total;
            }
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

        Dictionary<FishDef, ushort> BuildIndices()
        {
            var map = new Dictionary<FishDef, ushort>(_fish.Length);
            for (int i = 0; i < _fish.Length; i++)
                if (_fish[i] != null) map[_fish[i]] = (ushort)(i + 1);

            return map;
        }

        Dictionary<string, FishDef> BuildIds()
        {
            var map = new Dictionary<string, FishDef>(_fish.Length);
            foreach (FishDef def in _fish)
                if (def != null && !string.IsNullOrEmpty(def.Id)) map[def.Id] = def;

            return map;
        }

        /// <summary>Bake time only.</summary>
        public void Configure(FishDef[] fish, ItemDef rod)
        {
            _fish = fish ?? new FishDef[0];
            _rod = rod;
            Invalidate();
        }
    }
}
