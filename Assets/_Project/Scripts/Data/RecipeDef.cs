using System;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// Where a recipe can be made. Not a bitmask: a recipe belongs to one place, and "anywhere" is
    /// what <see cref="Hand"/> already means.
    /// </summary>
    public enum CraftStation
    {
        /// <summary>Anywhere, out of what you are carrying. The field set.</summary>
        Hand,

        /// <summary>At the bench in camp. Tools and structures.</summary>
        Bench,

        /// <summary>At a campfire. Cooking.</summary>
        Fire,

        /// <summary>At a water filter. Filling bottles.</summary>
        Filter,
    }

    /// <summary>One line of a recipe: how many of what.</summary>
    [Serializable]
    public struct Ingredient
    {
        public ItemDef Item;

        [Min(1)]
        public int Count;

        public Ingredient(ItemDef item, int count)
        {
            Item = item;
            Count = Mathf.Max(1, count);
        }

        public bool IsValid => Item != null && Count > 0;

        public override string ToString() => Item != null ? $"{Count}x {Item.Id}" : "?";
    }

    /// <summary>
    /// One thing you can make. Inputs, an output, a place, and a duration.
    ///
    /// A recipe produces **either** an item or a structure, never both. That is not a limitation so
    /// much as the distinction the game actually has: a hatchet goes in your bag, a campfire goes on
    /// the ground and stays there. Structures are what turn crafting into a progression rather than a
    /// list - the campfire you build is the station the next recipe needs.
    ///
    /// Everything here is data, so the tier-1 progression the issue asks for is a folder of .asset
    /// files rather than a switch statement, and rebalancing it is a diff.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Recipe", fileName = "Recipe")]
    public class RecipeDef : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable, lowercase, no spaces. The catalog is sorted by this; the wire carries an index.")]
        [SerializeField] string _id = "recipe";

        [SerializeField] string _displayName = "Recipe";

        [TextArea]
        [SerializeField] string _description = "";

        [Header("Where and how long")]
        [SerializeField] CraftStation _station = CraftStation.Hand;

        [Tooltip("Seconds of standing still. Short for field work, longer at the bench.")]
        [Min(0f)]
        [SerializeField] float _seconds = 3f;

        [Tooltip("Rough progression order, for sorting the list in #46. Not a gate.")]
        [Min(1)]
        [SerializeField] int _tier = 1;

        [Header("Inputs")]
        [Tooltip("All of these are consumed. An empty list is a recipe that makes something from nothing, "
                 + "which the factory refuses to create.")]
        [SerializeField] Ingredient[] _inputs = Array.Empty<Ingredient>();

        [Header("Output - one of these, not both")]
        [SerializeField] ItemDef _outputItem;

        [Min(1)]
        [SerializeField] int _outputCount = 1;

        [Tooltip("A networked prefab placed on the ground in front of you. Campfires, filters, benches.")]
        [SerializeField] GameObject _outputStructure;

        public string Id => _id;
        public string DisplayName => _displayName;
        public string Description => _description;

        public CraftStation Station => _station;
        public float Seconds => Mathf.Max(0f, _seconds);
        public int Tier => Mathf.Max(1, _tier);

        public Ingredient[] Inputs => _inputs;

        public ItemDef OutputItem => _outputItem;
        public int OutputCount => Mathf.Max(1, _outputCount);
        public GameObject OutputStructure => _outputStructure;

        public bool MakesStructure => _outputStructure != null;

        /// <summary>A recipe with no inputs or no output is a bug, not a freebie.</summary>
        public bool IsValid
        {
            get
            {
                if (_inputs == null || _inputs.Length == 0) return false;

                foreach (Ingredient input in _inputs)
                    if (!input.IsValid) return false;

                return _outputItem != null || _outputStructure != null;
            }
        }

        /// <summary>"2x plank + 1x flint -> campfire". For the log and for #46's tooltip.</summary>
        public string Describe()
        {
            string inputs = _inputs == null || _inputs.Length == 0
                ? "nothing"
                : string.Join(" + ", Array.ConvertAll(_inputs, i => i.ToString()));

            string output = _outputStructure != null
                ? _outputStructure.name
                : _outputItem != null
                    ? (OutputCount > 1 ? $"{OutputCount}x {_outputItem.Id}" : _outputItem.Id)
                    : "?";

            return $"{inputs} -> {output}";
        }

        public override string ToString() => string.IsNullOrEmpty(_id) ? name : _id;
    }
}
