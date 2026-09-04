using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Player;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Items
{
    /// <summary>
    /// Eating, drinking and bandaging: turning the selected item into a <see cref="BuffDef"/>.
    ///
    /// **Using something takes time.** A bandage is a second and a half of standing still, and that is
    /// the entire reason bandages are interesting: the decision is not "do I have one" but "do I have
    /// time". The timer runs on the server, and being punched, tased, downed or knocked over during it
    /// cancels the use.
    ///
    /// The item is spent at the **end** of the use, not the start. Being interrupted costs you the
    /// seconds and not the bandage, which is the version of this rule that does not make players
    /// furious. It also means a cancelled use cannot duplicate anything: nothing has left the
    /// inventory yet.
    ///
    /// The whole path is server-authoritative. The client sends "use slot N" and nothing else - not
    /// which item, not which effect, not how much it heals. The server reads its own copy of the slot.
    /// </summary>
    public class ItemUse : NetworkBehaviour
    {
        [SerializeField] Inventory _inventory;
        [SerializeField] BuffState _buffs;

        /// <summary>
        /// The slot being used and when it finishes, replicated so every peer can show the progress -
        /// the HUD for you, and eventually a bandaging animation for everybody watching.
        /// </summary>
        readonly SyncVar<int> _usingSlot = new(new SyncTypeSettings(0.1f));

        readonly SyncVar<uint> _endTick = new(new SyncTypeSettings(0.1f));

        Health _health;
        StunState _stun;
        RagdollController _ragdoll;

        /// <summary>True while a use is in progress. Read on every peer.</summary>
        public bool Busy => _usingSlot.Value >= 0;

        /// <summary>0..1 through the current use, or zero when idle.</summary>
        public float Progress
        {
            get
            {
                if (!Busy || _startTick >= _endTick.Value) return 0f;

                float span = _endTick.Value - _startTick;
                float done = TimeManager.Tick - _startTick;

                return Mathf.Clamp01(done / span);
            }
        }

        uint _startTick;

        void Awake()
        {
            _health = GetComponent<Health>();
            _stun = GetComponent<StunState>();
            _ragdoll = GetComponent<RagdollController>();

            if (_inventory == null) _inventory = GetComponent<Inventory>();
            if (_buffs == null) _buffs = GetComponent<BuffState>();

            // -1 rather than 0: slot zero is a real slot, and a default of zero would mean every
            // player spawns apparently mid-use on their first inventory slot.
            _usingSlot.SetInitialValues(-1);
        }

        /// <summary>
        /// Owner-side entry point. Returns whether a request went out, so the input component can fall
        /// through to something else when the selected slot holds a plank.
        /// </summary>
        public bool RequestUse()
        {
            if (!IsOwner || _inventory == null) return false;
            if (Busy) return false;

            if (_health != null && _health.IsIncapacitated) return false;
            if (_stun != null && _stun.IsStunned) return false;

            // The owner's copy is replicated and good enough to decide whether to bother sending.
            ItemStack selected = _inventory.Selected;
            ItemDef def = selected.Def;

            if (def == null || !def.Consumable) return false;

            ServerUse(_inventory.SelectedSlot);
            return true;
        }

        [ServerRpc]
        void ServerUse(int slot) => ServerBeginUse(slot);

        /// <summary>
        /// Server side, separate from the RPC so the test harness and any future NPC can call it.
        /// Returns whether a use actually started.
        /// </summary>
        [Server]
        public bool ServerBeginUse(int slot)
        {
            if (_inventory == null || Busy) return false;

            if (_health != null && _health.IsIncapacitated) return false;
            if (_stun != null && _stun.IsStunned) return false;

            // The client named a slot, not an item. Everything about what happens next comes from the
            // server's own copy of that slot.
            ItemStack stack = _inventory[slot];
            ItemDef def = stack.Def;

            if (def == null || !def.Consumable) return false;

            _startTick = TimeManager.Tick;
            _usingSlot.Value = slot;
            _endTick.Value = _startTick + (uint)Mathf.Max(1, Mathf.CeilToInt(def.UseSeconds * TimeManager.TickRate));

            return true;
        }

        /// <summary>Stops a use without spending anything. What being punched does.</summary>
        [Server]
        public void ServerCancel()
        {
            if (!Busy) return;

            _usingSlot.Value = -1;
            _endTick.Value = 0;
        }

        void Update()
        {
            if (!IsServerStarted || !Busy) return;

            // Anything that puts you on the floor or takes the controls away cancels it. Checked here
            // rather than hooked onto each system's event because there are four of them and they can
            // all end a use for the same reason - you stopped being upright and in control.
            if ((_health != null && _health.IsIncapacitated)
                || (_stun != null && _stun.IsStunned)
                || (_ragdoll != null && _ragdoll.IsRagdolled))
            {
                ServerCancel();
                return;
            }

            if (TimeManager.Tick < _endTick.Value) return;

            Finish();
        }

        [Server]
        void Finish()
        {
            int slot = _usingSlot.Value;

            _usingSlot.Value = -1;
            _endTick.Value = 0;

            // Re-read rather than trusting what was there when the use started: a whole second and a
            // half has passed, and in that time the stack could have been moved, split or dropped.
            ItemStack stack = _inventory[slot];
            ItemDef def = stack.Def;

            if (def == null || !def.Consumable) return;

            if (_buffs == null || !_buffs.Apply(def.Effect))
            {
                // The buff refused - an Ignore-stacking effect that is already running. The item stays
                // in the bag, because charging somebody for a bandage that did nothing is a bug.
                return;
            }

            _inventory.TakeSlot(slot, 1);

            // An empty bottle, a stripped branch. Goes wherever it fits rather than back into the same
            // slot: the slot may now hold the rest of the stack it came from.
            if (def.LeavesBehind != null) _inventory.Add(def.LeavesBehind, 1);
        }

        /// <summary>One line for the log.</summary>
        public string Describe()
        {
            if (!Busy) return "idle";

            ItemStack stack = _inventory != null ? _inventory[_usingSlot.Value] : ItemStack.Empty;
            ItemDef def = stack.Def;

            return $"using {(def != null ? def.Id : "?")} in slot {_usingSlot.Value}, "
                   + $"{Progress * 100f:F0}%";
        }

        /// <summary>Bake time only.</summary>
        public void Configure(Inventory inventory, BuffState buffs)
        {
            _inventory = inventory;
            _buffs = buffs;
        }
    }
}
