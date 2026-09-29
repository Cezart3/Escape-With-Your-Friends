using System;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// One colour tint for one weapon. #209.
    ///
    /// **A skin is a row of code, not an asset**, because it is four values and nothing references it
    /// by object: the shop sells it through a plain <see cref="ItemDef"/> whose id is
    /// <see cref="ItemId"/>, and everything else names it by <see cref="Id"/>. The price lives here so
    /// <c>ShopFactory</c> and the harness read one number.
    /// </summary>
    public readonly struct WeaponSkin
    {
        public const string ItemPrefix = "skin_";

        public readonly string Id;
        public readonly string Label;

        /// <summary><see cref="WeaponDef.Id"/> of the weapon this dresses.</summary>
        public readonly string WeaponId;

        public readonly Color Tint;
        public readonly int Price;

        public WeaponSkin(string id, string label, string weaponId, Color tint, int price)
        {
            Id = id;
            Label = label;
            WeaponId = weaponId;
            Tint = tint;
            Price = price;
        }

        /// <summary>The id of the stand-in item the trader lists. It never reaches a bag.</summary>
        public string ItemId => ItemPrefix + Id;
    }

    public static class SkinCatalog
    {
        public static readonly WeaponSkin[] All =
        {
            new("gold", "Gold", "pistol", new Color(1.00f, 0.79f, 0.20f), 250),
            new("jungle", "Jungle camo", "pistol", new Color(0.28f, 0.42f, 0.20f), 150),
            new("bone", "Bone", "rifle", new Color(0.92f, 0.89f, 0.80f), 200),
            new("obsidian", "Obsidian", "machete", new Color(0.07f, 0.06f, 0.09f), 180),
        };

        public static bool Find(string id, out WeaponSkin skin)
        {
            foreach (WeaponSkin candidate in All)
            {
                if (candidate.Id != id) continue;

                skin = candidate;
                return true;
            }

            skin = default;
            return false;
        }

        /// <summary>The skin a shop item stands for, or false when it is an ordinary item.</summary>
        public static bool ForItem(ItemDef item, out WeaponSkin skin)
        {
            skin = default;
            return item != null && item.Id.StartsWith(WeaponSkin.ItemPrefix, StringComparison.Ordinal)
                   && Find(item.Id.Substring(WeaponSkin.ItemPrefix.Length), out skin);
        }
    }
}
