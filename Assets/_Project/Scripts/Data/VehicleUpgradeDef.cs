using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// The four things you can bolt to a vehicle. The index of each one is a slot in
    /// <see cref="EscapeWithYourFriends.Vehicles.VehicleUpgrades"/>'s replicated tier list, so the
    /// order is the wire format: append, never reorder.
    /// </summary>
    public enum VehiclePart
    {
        /// <summary>Power and top speed.</summary>
        Engine,

        /// <summary>Grip, which is how hard you can corner before the back end leaves.</summary>
        Tyres,

        /// <summary>How much crashing a vehicle can absorb before it is a wreck.</summary>
        Armour,

        /// <summary>How far a full tank goes.</summary>
        Tank,
    }

    /// <summary>
    /// One part you can buy and fit, from #62.
    ///
    /// **A part is an item you hold, not a menu entry.** The fitting interaction is the one #61
    /// already built - stand next to the vehicle with the thing in your hand and press the key - so
    /// this def is mostly a lookup from an item id to "which number does it move, and to what". No
    /// new screen, no new key, no new RPC, and the trader sells it through the shelf that already
    /// sells fuel.
    ///
    /// **The multiplier is absolute, over stock, not a factor to stack.** Tier 2 means "1.5x the
    /// buggy as it was baked", not "1.5x whatever is fitted now". Stacking multipliers is how a
    /// second fit of the same part doubles a number nobody meant to double, and absolute values mean
    /// a vehicle's handling is a pure function of its fitted tiers - which is what makes reapplying
    /// them after a reload, or a client joining late, a non-event.
    /// </summary>
    [CreateAssetMenu(fileName = "VehicleUpgrade", menuName = "EWYF/Vehicle Upgrade")]
    public class VehicleUpgradeDef : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("The bag item you buy and hold. This is what the vehicle matches against.")]
        [SerializeField] string _itemId = "";

        [SerializeField] string _displayName = "Part";

        [TextArea(2, 3)]
        [SerializeField] string _description = "";

        [Header("What it does")]
        [SerializeField] VehiclePart _part = VehiclePart.Engine;

        [Tooltip("Fits onto tier-1-below. Tier 1 is the first thing bolted onto a stock vehicle.")]
        [Min(1)]
        [SerializeField] int _tier = 1;

        [Tooltip("Multiplier over the stock number, absolute. 1.5 means half again as good as the "
                 + "vehicle was baked, whatever else is fitted.")]
        [Min(0.1f)]
        [SerializeField] float _multiplier = 1.25f;

        public string ItemId => _itemId;
        public string DisplayName => _displayName;
        public string Description => _description;
        public VehiclePart Part => _part;
        public int Tier => _tier;
        public float Multiplier => _multiplier;

        /// <summary>Bake time only. See <c>VehicleUpgradeFactory</c>.</summary>
        public void Configure(string itemId, string displayName, VehiclePart part, int tier,
                              float multiplier, string description)
        {
            _itemId = itemId;
            _displayName = displayName;
            _part = part;
            _tier = Mathf.Max(1, tier);
            _multiplier = multiplier;
            _description = description;
        }
    }
}
