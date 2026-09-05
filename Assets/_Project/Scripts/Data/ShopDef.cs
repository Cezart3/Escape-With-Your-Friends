using System;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// What a trader sells, for how much, and how many of it there are.
    ///
    /// **The shop only lists what it sells.** Selling *to* it needs no list: anything with a
    /// <see cref="ItemDef.Value"/> above zero can be handed over at <see cref="BuyBackFraction"/> of
    /// that value. Listing every fish and every scrap the shop would take is a table that goes stale
    /// the moment somebody adds an item, and the failure mode is invisible - a new item that quietly
    /// cannot be sold. A fraction of the item's own value cannot go stale.
    ///
    /// That fraction is also the entire economy in one number. Below one it is a spread the player
    /// pays for the convenience of a shop; at one the shop is a free storage box you get paid to use.
    /// Half is the starting guess, and #56 is where it gets argued about with real playtest numbers.
    /// </summary>
    [CreateAssetMenu(fileName = "Shop", menuName = "EWYF/Shop")]
    public class ShopDef : ScriptableObject
    {
        /// <summary>One line on the shelf: an item, its price, and how many are left to sell.</summary>
        [Serializable]
        public struct Offer
        {
            public ItemDef Item;

            [Tooltip("What the player pays for one.")]
            public int Price;

            [Tooltip("How many the shop starts with. -1 is unlimited.")]
            public int Stock;

            public bool Unlimited => Stock < 0;

            public bool IsValid => Item != null && Price > 0;

            public override string ToString()
                => Item == null ? "?" : $"{Item.Id} @ {Price}" + (Unlimited ? "" : $" x{Stock}");
        }

        [SerializeField] string _id = "shop";

        [SerializeField] string _displayName = "Trader";

        [Tooltip("What the shop pays, as a fraction of an item's value. The spread is the economy.")]
        [Range(0.05f, 1f)]
        [SerializeField] float _buyBackFraction = 0.5f;

        [Tooltip("Seconds between restocks. One of each depleted line comes back each time.")]
        [Min(1f)]
        [SerializeField] float _restockSeconds = 90f;

        [SerializeField] Offer[] _offers = Array.Empty<Offer>();

        public string Id => _id;
        public string DisplayName => _displayName;
        public float BuyBackFraction => Mathf.Clamp(_buyBackFraction, 0.05f, 1f);
        public float RestockSeconds => Mathf.Max(1f, _restockSeconds);
        public Offer[] Offers => _offers;

        public int Count => _offers.Length;

        /// <summary>
        /// What the shop pays for one of something. Zero means it will not take it - a boat part is
        /// worth nothing to a trader on the island the boat is leaving.
        /// </summary>
        public int PriceFor(ItemDef def)
        {
            if (def == null || def.Value <= 0) return 0;

            // Floored, then floored again at one: a trader who pays zero for something they will take
            // is a trader who has stolen it.
            return Mathf.Max(1, Mathf.FloorToInt(def.Value * BuyBackFraction));
        }

        public override string ToString() => string.IsNullOrEmpty(_id) ? name : _id;
    }
}
