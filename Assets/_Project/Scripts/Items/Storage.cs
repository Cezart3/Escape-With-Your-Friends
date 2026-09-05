using System;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Items
{
    /// <summary>
    /// A shared container. Four players, one chest, and no way to get two ropes out of one.
    ///
    /// **The whole issue is the last clause.** #44's acceptance is "no item duplication under
    /// concurrent access", and duplication in a container is not a race in the threading sense - the
    /// server runs one RPC at a time on one thread, so nothing interleaves. It is a race in the
    /// *stale view* sense: two players are both looking at the same replicated slot, both press take,
    /// and both requests arrive describing a pile that only one of them can have.
    ///
    /// Three rules answer it, and every transfer here obeys all three.
    ///
    /// 1. **A request names a slot and a count, never an item.** The server reads its own slot. The
    ///    second player to arrive reads what the first one left, which is usually nothing.
    /// 2. **Take, give, return the remainder - in one server call.** Nothing runs between those
    ///    lines, so the return cannot fail: the space it needs is space the take just made. This is
    ///    what stops the other half of the bug, which is not duplication but deletion - items voided
    ///    because the destination was full and nobody put them back.
    /// 3. **Nothing is ever added before it is removed.** Every unit exists in exactly one place at
    ///    every line of every method here.
    ///
    /// The chest has no weight limit. A bag is a decision about what to carry; a chest is where you
    /// stop making that decision, and a container that could be overloaded would just be a worse bag.
    /// </summary>
    public class Storage : NetworkBehaviour, IInteractable
    {
        [Tooltip("Slots. Thirty is a chest rather than a second backpack, and it is what #46 has to "
                 + "draw.")]
        [Min(1)]
        [SerializeField] int _slotCount = 30;

        [Tooltip("What the crosshair calls it.")]
        [SerializeField] string _label = "chest";

        /// <summary>
        /// What is inside, replicated to everybody. A chest is not owned, so this is not gated on an
        /// owner the way an inventory is - anyone who can see it can see into it.
        /// </summary>
        readonly SyncList<ItemStack> _slots = new();

        /// <summary>Raised on every peer when the contents change. #46's UI redraws off this.</summary>
        public event Action Changed;

        public int SlotCount => _slots.Count;

        public ItemStack this[int slot] => slot >= 0 && slot < _slots.Count ? _slots[slot] : ItemStack.Empty;

        public string Label => _label;

        public bool IsEmpty
        {
            get
            {
                for (int i = 0; i < _slots.Count; i++)
                    if (!_slots[i].IsEmpty) return false;

                return true;
            }
        }

        public int UsedSlots
        {
            get
            {
                int used = 0;
                for (int i = 0; i < _slots.Count; i++)
                    if (!_slots[i].IsEmpty) used++;

                return used;
            }
        }

        void Awake() => _slots.OnChange += OnSlotsChanged;

        void OnDestroy() => _slots.OnChange -= OnSlotsChanged;

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Built here rather than in the prefab: an empty ItemStack is the default value, so the
            // list only has to exist at the right length and there is nothing to author.
            if (_slots.Count == _slotCount) return;

            _slots.Clear();
            for (int i = 0; i < _slotCount; i++) _slots.Add(ItemStack.Empty);
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Evidence for the harness that a chest somebody else filled reads the same here. Costs
            // a flag check on spawn and says nothing in a real session.
            if (IsServerStarted || !CommandLine.HasFlag("-chestTest")) return;

            string last = Describe();
            Debug.Log($"[Storage] client sees {name}: {last}");

            Changed += () =>
            {
                string now = Describe();
                if (now == last) return;

                last = now;
                Debug.Log($"[Storage] client sees {name}: {now}");
            };
        }

        void OnSlotsChanged(SyncListOperation op, int index, ItemStack older, ItemStack newer,
                            bool asServer)
        {
            // Both passes run on a host; firing once keeps listeners from redrawing twice.
            if (asServer && IsClientStarted) return;

            Changed?.Invoke();
        }

        // ---------------------------------------------------------------- the two transfers

        /// <summary>
        /// Moves items out of the chest and into a bag. Returns how many actually moved.
        ///
        /// This is the method the acceptance criterion is about, and it is four lines long because
        /// every one of them is load-bearing. See the three rules on the class.
        /// </summary>
        [Server]
        public int ServerWithdraw(Inventory bag, int slot, int count = int.MaxValue)
        {
            if (bag == null) return 0;

            // Read this chest's own slot, not the one the asking client believed it saw. Two players
            // who both clicked the same pile arrive here one after the other, and the second one
            // finds what the first one left behind.
            ItemStack taken = TakeSlot(slot, count);
            if (taken.IsEmpty) return 0;

            int left = bag.Add(taken.Def, taken.Count);

            // Whatever did not fit goes straight back. Nothing runs between these two lines, and the
            // room it needs is the room the take just made, so this cannot fail.
            if (left > 0) Add(taken.Def, left);

            return taken.Count - left;
        }

        /// <summary>Moves items out of a bag and into the chest. The mirror image, same rules.</summary>
        [Server]
        public int ServerDeposit(Inventory bag, int slot, int count = int.MaxValue)
        {
            if (bag == null) return 0;

            ItemStack taken = bag.TakeSlot(slot, count);
            if (taken.IsEmpty) return 0;

            int left = Add(taken.Def, taken.Count);

            if (left > 0) bag.Add(taken.Def, left);

            return taken.Count - left;
        }

        /// <summary>Empties a bag into the chest. Returns how many items moved.</summary>
        [Server]
        public int ServerDepositAll(Inventory bag)
        {
            if (bag == null) return 0;

            int moved = 0;
            for (int i = 0; i < bag.SlotCount; i++) moved += ServerDeposit(bag, i);

            return moved;
        }

        // ---------------------------------------------------------------- the contents

        /// <summary>
        /// Puts items in. Returns how many did not fit. Same fill order as <see cref="Inventory"/>:
        /// partial stacks first, then empty slots, so a chest does not fill up with fragments.
        /// </summary>
        [Server]
        public int Add(ItemDef def, int count)
        {
            if (def == null || count <= 0) return Mathf.Max(0, count);

            ushort index = ItemCatalog.Active != null ? ItemCatalog.Active.IndexOf(def) : (ushort)0;
            if (index == 0)
            {
                Debug.LogError($"[Storage] '{def}' is not in the catalog, so it cannot be stored.");
                return count;
            }

            int remaining = count;
            remaining -= Fill(index, def, remaining, emptySlots: false);
            remaining -= Fill(index, def, remaining, emptySlots: true);

            return remaining;
        }

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

                    int put = Mathf.Min(count - placed, def.MaxStack);
                    _slots[i] = new ItemStack(index, put);
                    placed += put;
                    continue;
                }

                if (stack.Index != index || stack.Space <= 0) continue;

                int top = Mathf.Min(count - placed, stack.Space);
                _slots[i] = stack.With(stack.Count + top);
                placed += top;
            }

            return placed;
        }

        /// <summary>Empties one slot and hands back what was in it.</summary>
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

        public int CountOf(ItemDef def)
        {
            ushort index = ItemCatalog.Active != null ? ItemCatalog.Active.IndexOf(def) : (ushort)0;
            if (index == 0) return 0;

            int total = 0;
            for (int i = 0; i < _slots.Count; i++)
                if (_slots[i].Index == index) total += _slots[i].Count;

            return total;
        }

        /// <summary>Everything in it, of anything. The conservation check in the harness counts this.</summary>
        public int TotalItems
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _slots.Count; i++) total += _slots[i].Count;

                return total;
            }
        }

        /// <summary>The last non-empty slot, or -1. What Interact takes out when your hands are free.</summary>
        public int LastUsedSlot()
        {
            for (int i = _slots.Count - 1; i >= 0; i--)
                if (!_slots[i].IsEmpty) return i;

            return -1;
        }

        // ---------------------------------------------------------------- the key

        /// <summary>
        /// Full hands put something in; empty hands take something out.
        ///
        /// A chest with no UI is the same problem the crafting bench had, answered differently: a
        /// bench had nothing useful to do on a keypress, but a chest does. Interact deposits the
        /// selected hotbar stack, and if you are holding nothing it hands back the last thing in
        /// there. That is a complete loop - dump your loot at base, take it back out - with no
        /// screen to draw, and #46's UI is a better way to do the same thing rather than the only
        /// way to do it at all.
        /// </summary>
        public string Prompt
        {
            get
            {
                Inventory bag = LocalBag();
                ItemStack held = bag != null ? bag.Selected : ItemStack.Empty;

                if (!held.IsEmpty) return $"Store {held}";

                int last = LastUsedSlot();
                return last < 0 ? $"Empty {_label}" : $"Take {this[last]}";
            }
        }

        public bool ServerCanInteract(NetworkObject actor)
        {
            if (actor == null) return false;

            // No ownership test and no distance test beyond the interactor's own reach. A chest that
            // could be locked is #47's problem, and a shared chest in a co-op game is shared.
            var bag = actor.GetComponent<Inventory>();
            return bag != null;
        }

        public void ServerInteract(NetworkObject actor)
        {
            var bag = actor != null ? actor.GetComponent<Inventory>() : null;
            if (bag == null) return;

            int slot = bag.SelectedSlot;

            if (!bag[slot].IsEmpty)
            {
                int stored = ServerDeposit(bag, slot);
                Debug.Log($"[Storage] {actor.name} stored {stored} item(s) in {name}; "
                          + $"{UsedSlots}/{SlotCount} slots used.");
                return;
            }

            int last = LastUsedSlot();
            if (last < 0) return;

            int taken = ServerWithdraw(bag, last);
            Debug.Log($"[Storage] {actor.name} took {taken} item(s) out of {name}; "
                      + $"{UsedSlots}/{SlotCount} slots used.");
        }

        /// <summary>
        /// The local player's bag, for the prompt only. Cached per call rather than held, because a
        /// prompt is read on the frame the crosshair is on this and never in a loop.
        /// </summary>
        Inventory LocalBag()
        {
            NetworkObject local = ClientManager != null && ClientManager.Connection != null
                ? ClientManager.Connection.FirstObject
                : null;

            return local != null ? local.GetComponent<Inventory>() : null;
        }

        /// <summary>One line for the log.</summary>
        public string Describe()
        {
            var text = new System.Text.StringBuilder();
            text.Append($"{UsedSlots}/{SlotCount} slots [");

            bool first = true;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].IsEmpty) continue;

                if (!first) text.Append(", ");
                text.Append($"{i}:{_slots[i]}");
                first = false;
            }

            return text.Append(']').ToString();
        }

        /// <summary>Bake time only.</summary>
        public void Configure(int slots, string label)
        {
            _slotCount = Mathf.Max(1, slots);
            _label = label;
        }
    }
}
