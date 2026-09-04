using System;
using System.Text;
using EscapeWithYourFriends.Data;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Items
{
    /// <summary>
    /// What a player is carrying. Host-authoritative, like everything else that can be cheated at.
    ///
    /// The rule is the one <see cref="Combat.Health"/> already sets: the server owns the numbers and
    /// nothing a client sends is trusted as a statement of fact. A client cannot add an item - there
    /// is no RPC that lets it - it can only ask to rearrange slots it already owns, and even that is
    /// validated. Picking things up (#42), crafting (#43) and shops (#M4) all call into the server-side
    /// methods here, after the server has decided the player was entitled to it.
    ///
    /// Slots are a fixed-length <see cref="SyncList{T}"/> rather than a growing list of occupied ones,
    /// because a fixed list means a slot change is one four-byte entry on the wire instead of a whole
    /// reordered array, and because the UI in #46 wants stable positions - an item should not move
    /// under your cursor because a friend's pickup shortened the list.
    ///
    /// Weight is a limit rather than a second grid: it is the thing that makes a second trip a
    /// decision, and it costs one float instead of a tetris minigame.
    /// </summary>
    public class Inventory : NetworkBehaviour
    {
        [Tooltip("How many slots. Fixed for the life of the object; the list is sized once on the server.")]
        [Min(1)]
        [SerializeField] int _slotCount = 20;

        [Tooltip("Kilograms. Past this, nothing more goes in - that is the whole point of it.")]
        [Min(1f)]
        [SerializeField] float _carryLimit = 40f;

        [Tooltip("Every item in the game. Assigned at bake time; the wire format is an index into it.")]
        [SerializeField] ItemCatalog _catalog;

        readonly SyncList<ItemStack> _slots = new();

        /// <summary>Fired on every peer after any change. The UI redraws off this rather than polling.</summary>
        public event Action Changed;

        public int SlotCount => _slots.Count;
        public float CarryLimit => _carryLimit;
        public ItemCatalog Catalog => _catalog;

        /// <summary>Total kilograms carried. Recomputed on demand; twenty slots is not worth caching.</summary>
        public float Weight
        {
            get
            {
                float total = 0f;
                for (int i = 0; i < _slots.Count; i++) total += _slots[i].Weight;
                return total;
            }
        }

        public bool Overloaded => Weight >= _carryLimit;

        public ItemStack this[int slot] => slot >= 0 && slot < _slots.Count ? _slots[slot] : ItemStack.Empty;

        void Awake()
        {
            // The catalog is a project-wide asset, so whichever inventory wakes up first publishes it
            // and the rest agree. ItemStack needs it to turn an index back into a definition, and it
            // needs it without a reference to any particular player.
            ItemCatalog.Use(_catalog);

            _slots.OnChange += OnSlotsChanged;
        }

        void OnDestroy() => _slots.OnChange -= OnSlotsChanged;

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Sized once, here, rather than in Awake: the list only replicates from the server, and
            // filling it on a client would be writing to something it does not own.
            _slots.Clear();
            for (int i = 0; i < _slotCount; i++) _slots.Add(ItemStack.Empty);

            InventoryTest.Offer(this);
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            InventoryTest.Watch(this);
        }

        void OnSlotsChanged(SyncListOperation op, int index, ItemStack older, ItemStack newer, bool asServer)
        {
            // Both the server and the client raise this on a host. Firing once, on the client pass,
            // keeps listeners from redrawing twice for one change.
            if (asServer && IsClientStarted) return;

            Changed?.Invoke();
        }

        // ---------------------------------------------------------------- server-side mutation

        /// <summary>
        /// Puts items in. Returns how many did not fit, so the caller can decide what to do with the
        /// remainder - drop it, refuse the pickup, refund the purchase.
        ///
        /// Partial stacks first, then empty slots. That order is not cosmetic: filling an empty slot
        /// while a half-full one of the same thing exists is how an inventory fills up with fragments.
        /// </summary>
        [Server]
        public int Add(ItemDef def, int count)
        {
            if (def == null || count <= 0) return count;

            ushort index = Index(def);
            if (index == 0)
            {
                Debug.LogError($"[Inventory] '{def}' is not in the catalog, so it cannot be carried. "
                               + "Run ItemFactory.Build.");
                return count;
            }

            int remaining = count;
            remaining -= Fill(index, def, remaining, emptySlots: false);
            remaining -= Fill(index, def, remaining, emptySlots: true);

            return remaining;
        }

        /// <summary>Takes items out, wherever they are. Returns how many were actually removed.</summary>
        [Server]
        public int Remove(ItemDef def, int count)
        {
            if (def == null || count <= 0) return 0;

            ushort index = Index(def);
            int taken = 0;

            for (int i = 0; i < _slots.Count && taken < count; i++)
            {
                ItemStack stack = _slots[i];
                if (stack.Index != index || stack.Count == 0) continue;

                int take = Mathf.Min(count - taken, stack.Count);
                _slots[i] = stack.With(stack.Count - take);
                taken += take;
            }

            return taken;
        }

        /// <summary>Empties one slot and hands back what was in it, for dropping and for containers.</summary>
        [Server]
        public ItemStack TakeSlot(int slot, int count = int.MaxValue)
        {
            if (slot < 0 || slot >= _slots.Count) return ItemStack.Empty;

            ItemStack stack = _slots[slot];
            if (stack.IsEmpty) return ItemStack.Empty;

            int take = Mathf.Clamp(count, 0, stack.Count);
            if (take == 0) return ItemStack.Empty;

            _slots[slot] = stack.With(stack.Count - take);
            return stack.With(take);
        }

        /// <summary>How many of something is being carried. Crafting and quests both ask this.</summary>
        public int CountOf(ItemDef def)
        {
            ushort index = Index(def);
            if (index == 0) return 0;

            int total = 0;
            for (int i = 0; i < _slots.Count; i++)
                if (_slots[i].Index == index) total += _slots[i].Count;

            return total;
        }

        public bool Has(ItemDef def, int count = 1) => CountOf(def) >= count;

        // ---------------------------------------------------------------- what a client may ask for

        /// <summary>
        /// Move or merge one slot into another. Ownership is required, so a client can only rearrange
        /// its own bag; every index is bounds-checked because a message is not a promise.
        /// </summary>
        [ServerRpc]
        public void MoveSlot(int from, int to) => ServerMove(from, to);

        /// <summary>Split half a stack into an empty slot. The other half of drag-and-drop in #46.</summary>
        [ServerRpc]
        public void SplitSlot(int from, int to) => ServerSplit(from, to);

        [Server]
        public void ServerMove(int from, int to)
        {
            if (!InRange(from) || !InRange(to) || from == to) return;

            ItemStack source = _slots[from];
            ItemStack target = _slots[to];
            if (source.IsEmpty) return;

            // Same kind and room to spare: merge rather than swap, because that is what dragging one
            // pile onto another obviously means.
            if (source.SameKind(target) && target.Space > 0)
            {
                int moved = Mathf.Min(source.Count, target.Space);
                _slots[to] = target.With(target.Count + moved);
                _slots[from] = source.With(source.Count - moved);
                return;
            }

            _slots[from] = target;
            _slots[to] = source;
        }

        [Server]
        public void ServerSplit(int from, int to)
        {
            if (!InRange(from) || !InRange(to) || from == to) return;

            ItemStack source = _slots[from];
            if (source.Count < 2 || !_slots[to].IsEmpty) return;

            int half = source.Count / 2;
            _slots[to] = source.With(half);
            _slots[from] = source.With(source.Count - half);
        }

        // ---------------------------------------------------------------- internals

        int Fill(ushort index, ItemDef def, int count, bool emptySlots)
        {
            if (count <= 0) return 0;

            int placed = 0;

            for (int i = 0; i < _slots.Count && placed < count; i++)
            {
                ItemStack stack = _slots[i];

                if (emptySlots)
                {
                    if (!stack.IsEmpty) continue;
                }
                else if (stack.Index != index || stack.Space <= 0)
                {
                    continue;
                }

                int room = emptySlots ? def.MaxStack : stack.Space;
                int wanted = Mathf.Min(count - placed, room);

                // Weight is checked per item rather than per stack, so a heavy stack half-fits instead
                // of being refused whole. Refusing whole is how a player ends up unable to pick up one
                // plank because they asked for ten.
                int allowed = Allowed(def, wanted);
                if (allowed <= 0) break;

                _slots[i] = new ItemStack(index, (emptySlots ? 0 : stack.Count) + allowed);
                placed += allowed;
            }

            return placed;
        }

        int Allowed(ItemDef def, int wanted)
        {
            if (def.Weight <= 0f) return wanted;

            float room = _carryLimit - Weight;
            if (room <= 0f) return 0;

            return Mathf.Clamp(Mathf.FloorToInt(room / def.Weight), 0, wanted);
        }

        bool InRange(int slot) => slot >= 0 && slot < _slots.Count;

        ushort Index(ItemDef def) => _catalog != null ? _catalog.IndexOf(def) : (ushort)0;

        /// <summary>One line for the log. The test in <see cref="InventoryTest"/> reads these.</summary>
        public string Describe()
        {
            var text = new StringBuilder();
            int used = 0;

            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].IsEmpty) continue;

                if (used > 0) text.Append(", ");
                text.Append(i).Append(':').Append(_slots[i]);
                used++;
            }

            if (used == 0) text.Append("empty");

            return $"{used}/{_slots.Count} slots, {Weight:F1}/{_carryLimit:F0}kg [{text}]";
        }

        /// <summary>Bake time only. The prefab builder wires the catalog in.</summary>
        public void Configure(ItemCatalog catalog, int slots, float carryLimit)
        {
            _catalog = catalog;
            _slotCount = Mathf.Max(1, slots);
            _carryLimit = Mathf.Max(1f, carryLimit);
        }
    }
}
