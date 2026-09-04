using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>What kind of thing this is. Drives sorting, filters, and which systems care about it.</summary>
    public enum ItemCategory
    {
        Material,
        Food,
        Drink,
        Medical,
        Tool,
        Weapon,
        Quest,
        Misc,
    }

    /// <summary>
    /// One kind of item. Everything about it is data, so a new item is a text asset written from the
    /// terminal rather than a new script - which is the whole acceptance criterion of #41.
    ///
    /// The id is the identity, not the file name and not the object reference. A network message
    /// cannot carry a ScriptableObject reference, so an item crossing the wire is an index into
    /// <see cref="ItemCatalog"/>; the id is what that index is derived from, and what a save file,
    /// a recipe and a shop listing all refer to. Rename the asset freely; never rename the id.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Item", fileName = "Item")]
    public class ItemDef : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable, lowercase, no spaces. The catalog is sorted by this, and saves refer to it.")]
        [SerializeField] string _id = "item";

        [SerializeField] string _displayName = "Item";

        [TextArea]
        [Tooltip("One line, shown in the tooltip. What it is for, not what it looks like.")]
        [SerializeField] string _description = "";

        [SerializeField] ItemCategory _category = ItemCategory.Material;

        [Header("Carrying")]
        [Tooltip("How many fit in one slot. One means it always takes a slot of its own.")]
        [Min(1)]
        [SerializeField] int _maxStack = 1;

        [Tooltip("Kilograms each. The carry limit is what makes a second trip a decision.")]
        [Min(0f)]
        [SerializeField] float _weight = 1f;

        [Header("Presentation")]
        [Tooltip("Inventory icon. Missing is fine before the art pass; the UI falls back to the name.")]
        [SerializeField] Sprite _icon;

        [Tooltip("What it looks like lying on the ground. Spawned by the world item in #42.")]
        [SerializeField] GameObject _worldPrefab;

        [Header("Using it")]
        [Tooltip("What applying this item does. Null means it is not consumable - a plank is a plank. "
                 + "The same BuffDef type the casino's alcohol uses; see BuffDef.")]
        [SerializeField] BuffDef _effect;

        [Tooltip("Seconds the use takes. The item is spent at the end, not the start, so being "
                 + "punched mid-bandage costs you the time but not the bandage.")]
        [Min(0f)]
        [SerializeField] float _useSeconds = 1.5f;

        [Tooltip("What is left in the slot afterwards - an empty bottle, a stripped branch. Null "
                 + "means the item is simply gone.")]
        [SerializeField] ItemDef _leavesBehind;

        [Header("Economy")]
        [Tooltip("Base price at the shop in #M4. Zero means it cannot be sold.")]
        [Min(0)]
        [SerializeField] int _value;

        public string Id => _id;
        public string DisplayName => _displayName;
        public string Description => _description;
        public ItemCategory Category => _category;
        public int MaxStack => Mathf.Max(1, _maxStack);
        public float Weight => Mathf.Max(0f, _weight);
        public Sprite Icon => _icon;
        public GameObject WorldPrefab => _worldPrefab;
        public int Value => _value;

        public BuffDef Effect => _effect;
        public float UseSeconds => Mathf.Max(0f, _useSeconds);
        public ItemDef LeavesBehind => _leavesBehind;

        /// <summary>True when pressing Use on this does anything at all.</summary>
        public bool Consumable => _effect != null;

        /// <summary>True when this can share a slot with more of itself.</summary>
        public bool Stackable => MaxStack > 1;

        public override string ToString() => string.IsNullOrEmpty(_id) ? name : _id;
    }
}
