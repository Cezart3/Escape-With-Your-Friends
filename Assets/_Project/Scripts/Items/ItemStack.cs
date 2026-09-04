using System;
using EscapeWithYourFriends.Data;

namespace EscapeWithYourFriends.Items
{
    /// <summary>
    /// One inventory slot: what is in it and how many.
    ///
    /// Four bytes, because this is what replicates. An inventory is twenty of these on every player,
    /// resent whenever anybody picks anything up, and a slot that carried a string id would be an
    /// order of magnitude more traffic for information the catalog already has.
    ///
    /// A zero <see cref="Index"/> is an empty slot, which is also the default value - so a
    /// freshly created array of these is an empty inventory with nothing to initialise.
    /// </summary>
    [Serializable]
    public struct ItemStack : IEquatable<ItemStack>
    {
        /// <summary>Catalog index. 0 is empty; see <see cref="ItemCatalog"/> for why it is a number.</summary>
        public ushort Index;

        /// <summary>How many. Zero with a non-zero index should never exist; <see cref="Valid"/> checks it.</summary>
        public ushort Count;

        public static readonly ItemStack Empty = default;

        public ItemStack(ushort index, int count)
        {
            Index = index;
            Count = (ushort)Math.Max(0, count);
        }

        public bool IsEmpty => Index == 0 || Count == 0;

        /// <summary>An empty slot or a real one; the in-between state is a bug worth catching.</summary>
        public bool Valid => (Index == 0) == (Count == 0);

        public ItemDef Def => ItemCatalog.Active != null ? ItemCatalog.Active.At(Index) : null;

        public float Weight
        {
            get
            {
                ItemDef def = Def;
                return def != null ? def.Weight * Count : 0f;
            }
        }

        /// <summary>Room left before this slot hits the item's stack limit.</summary>
        public int Space
        {
            get
            {
                ItemDef def = Def;
                return def == null ? 0 : Math.Max(0, def.MaxStack - Count);
            }
        }

        public ItemStack With(int count) => count <= 0 ? Empty : new ItemStack(Index, count);

        public bool SameKind(ItemStack other) => Index == other.Index;

        public bool Equals(ItemStack other) => Index == other.Index && Count == other.Count;
        public override bool Equals(object obj) => obj is ItemStack other && Equals(other);
        public override int GetHashCode() => (Index << 16) | Count;

        public override string ToString()
        {
            if (IsEmpty) return "-";

            ItemDef def = Def;
            string name = def != null ? def.Id : $"#{Index}";
            return Count > 1 ? $"{name} x{Count}" : name;
        }
    }
}
