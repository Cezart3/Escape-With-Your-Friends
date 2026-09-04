using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.World;
using FishNet;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Items
{
    /// <summary>
    /// Turning what you are carrying into something better. Server-authoritative, timed, cancellable.
    ///
    /// The shape is deliberately the same as <see cref="ItemUse"/>, because it is the same gesture:
    /// stand still for a few seconds and something happens. The two rules that matter are also the
    /// same, and both were learned there rather than here:
    ///
    /// - **Inputs are taken at the end, not the start.** Being interrupted costs the seconds and not
    ///   the materials, and a cancelled craft cannot duplicate anything because nothing has left the
    ///   inventory yet.
    /// - **Everything is re-checked when the timer finishes.** Six seconds is long enough for a friend
    ///   to have stolen the planks out of a chest, for the bench to have been destroyed, or for you to
    ///   have walked away from it.
    ///
    /// A client sends a recipe index and nothing else - not what it costs, not what it makes, not
    /// whether it is standing at a bench. The server reads its own catalog.
    /// </summary>
    public class Crafting : NetworkBehaviour
    {
        [Tooltip("Every recipe in the game. Assigned at bake time; the wire carries an index into it.")]
        [SerializeField] RecipeCatalog _catalog;

        [SerializeField] Inventory _inventory;

        [Tooltip("How far in front a crafted structure is placed, in metres.")]
        [SerializeField] float _placeAhead = 2f;

        /// <summary>
        /// The recipe being made and when it finishes, replicated so every peer can show the progress
        /// and, later, an animation.
        /// </summary>
        readonly SyncVar<ushort> _craftingIndex = new(new SyncTypeSettings(0.1f));

        readonly SyncVar<uint> _endTick = new(new SyncTypeSettings(0.1f));

        Health _health;
        StunState _stun;
        RagdollController _ragdoll;
        uint _startTick;

        public bool Busy => _craftingIndex.Value != 0;

        public RecipeCatalog Catalog => _catalog;

        /// <summary>0..1 through the current craft, or zero when idle.</summary>
        public float Progress
        {
            get
            {
                if (!Busy || _startTick >= _endTick.Value) return 0f;

                float span = _endTick.Value - _startTick;
                return Mathf.Clamp01((TimeManager.Tick - _startTick) / span);
            }
        }

        void Awake()
        {
            _health = GetComponent<Health>();
            _stun = GetComponent<StunState>();
            _ragdoll = GetComponent<RagdollController>();

            if (_inventory == null) _inventory = GetComponent<Inventory>();

            RecipeCatalog.Use(_catalog);
        }

        // ---------------------------------------------------------------- what a client may ask for

        /// <summary>
        /// Owner-side entry point. #46's UI calls this with whatever the player clicked; there is
        /// deliberately no key bound to it, because "craft" without a list to choose from is not a verb.
        /// </summary>
        public bool RequestCraft(RecipeDef recipe)
        {
            if (!IsOwner || _catalog == null || recipe == null) return false;

            ushort index = _catalog.IndexOf(recipe);
            if (index == 0) return false;

            ServerCraft(index);
            return true;
        }

        [ServerRpc]
        void ServerCraft(ushort index) => ServerBeginCraft(index);

        /// <summary>
        /// Server side, separate from the RPC so the harness and any future NPC can use it. Returns
        /// whether a craft actually started.
        /// </summary>
        [Server]
        public bool ServerBeginCraft(ushort index)
        {
            if (Busy || _catalog == null || _inventory == null) return false;

            RecipeDef recipe = _catalog.At(index);
            if (recipe == null || !recipe.IsValid) return false;

            if (!CanCraft(recipe, out _)) return false;

            _startTick = TimeManager.Tick;
            _craftingIndex.Value = index;
            _endTick.Value = _startTick
                             + (uint)Mathf.Max(1, Mathf.CeilToInt(recipe.Seconds * TimeManager.TickRate));

            return true;
        }

        /// <summary>Convenience for the harness and for anything holding a definition rather than an index.</summary>
        [Server]
        public bool ServerBeginCraft(RecipeDef recipe)
            => _catalog != null && ServerBeginCraft(_catalog.IndexOf(recipe));

        [Server]
        public void ServerCancel()
        {
            if (!Busy) return;

            _craftingIndex.Value = 0;
            _endTick.Value = 0;
        }

        /// <summary>
        /// Whether this could be made right now. Also the reason it could not, because "you cannot
        /// craft that" is the least useful message a game can show.
        /// </summary>
        public bool CanCraft(RecipeDef recipe, out string why)
        {
            why = null;

            if (recipe == null || !recipe.IsValid || _inventory == null)
            {
                why = "that recipe is broken";
                return false;
            }

            if (_health != null && _health.IsIncapacitated)
            {
                why = "you are on the floor";
                return false;
            }

            if (!CraftingStation.InRange(recipe.Station, transform.position))
            {
                why = $"no {recipe.Station.ToString().ToLowerInvariant()} nearby";
                return false;
            }

            foreach (Ingredient input in recipe.Inputs)
            {
                if (_inventory.Has(input.Item, input.Count)) continue;

                why = $"you need {input.Count}x {input.Item.Id}";
                return false;
            }

            return true;
        }

        // ---------------------------------------------------------------- the timer

        void Update()
        {
            if (!IsServerStarted || !Busy) return;

            // Anything that puts you on the floor cancels it, same as a bandage. Walking away from the
            // bench is checked at the end rather than here: interrupting a craft because you took one
            // step too many while it was running would be maddening, and the end check still refuses.
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
            RecipeDef recipe = _catalog.At(_craftingIndex.Value);

            _craftingIndex.Value = 0;
            _endTick.Value = 0;

            // Re-checked rather than trusted: six seconds is long enough for the materials to have
            // been dropped, traded or stolen, and for the bench to have been walked away from.
            if (recipe == null)
            {
                Debug.Log($"[Crafting] {name} finished a recipe that no longer exists. Nothing was taken.");
                return;
            }

            if (!CanCraft(recipe, out string why))
            {
                Debug.Log($"[Crafting] {name} finished '{recipe}' but it no longer holds: {why}. "
                          + "Nothing was taken.");
                return;
            }

            // Room first, then payment. Taking the inputs and then discovering the output does not fit
            // is how a player loses four planks and gets nothing.
            if (!recipe.MakesStructure && !HasRoom(recipe))
            {
                Debug.Log($"[Crafting] {name} has no room for {recipe.OutputCount}x "
                          + $"{recipe.OutputItem.Id}. Nothing was taken.");
                return;
            }

            foreach (Ingredient input in recipe.Inputs)
                _inventory.Remove(input.Item, input.Count);

            if (recipe.MakesStructure) PlaceStructure(recipe);
            else _inventory.Add(recipe.OutputItem, recipe.OutputCount);
        }

        /// <summary>
        /// Whether the output fits. Asked before anything is taken, and asked in terms of weight and
        /// slots rather than guessed - the inputs coming out first would free both, but a heavy output
        /// from light inputs can still overload you.
        /// </summary>
        bool HasRoom(RecipeDef recipe)
        {
            ItemDef output = recipe.OutputItem;

            float freed = 0f;
            foreach (Ingredient input in recipe.Inputs) freed += input.Item.Weight * input.Count;

            float after = _inventory.Weight - freed + output.Weight * recipe.OutputCount;
            if (after > _inventory.CarryLimit + 0.001f) return false;

            // Slots: the inputs may not empty any, so count what is actually free plus whatever the
            // output can merge into.
            int free = 0;
            int mergeable = 0;

            for (int i = 0; i < _inventory.SlotCount; i++)
            {
                ItemStack slot = _inventory[i];

                if (slot.IsEmpty) free++;
                else if (slot.Def == output) mergeable += slot.Space;
            }

            int needed = recipe.OutputCount - mergeable;
            if (needed <= 0) return true;

            return free * output.MaxStack >= needed;
        }

        [Server]
        void PlaceStructure(RecipeDef recipe)
        {
            Vector3 ahead = transform.position + transform.forward * _placeAhead;

            // Dropped onto the ground rather than left where the player's feet are: a campfire spawned
            // at hip height on a slope ends up either floating or buried.
            Vector3 position = Physics.Raycast(ahead + Vector3.up * 3f, Vector3.down,
                                               out RaycastHit hit, 12f,
                                               ~0, QueryTriggerInteraction.Ignore)
                ? hit.point
                : ahead;

            GameObject instance = Instantiate(recipe.OutputStructure, position,
                                              Quaternion.Euler(0f, transform.eulerAngles.y + 180f, 0f));

            InstanceFinder.NetworkManager.ServerManager.Spawn(instance);

            Debug.Log($"[Crafting] {name} built {recipe.OutputStructure.name} at "
                      + $"{position.ToString("F1")}.");
        }

        /// <summary>One line for the log.</summary>
        public string Describe()
        {
            if (!Busy) return "idle";

            RecipeDef recipe = _catalog != null ? _catalog.At(_craftingIndex.Value) : null;
            return $"crafting {(recipe != null ? recipe.Id : "?")}, {Progress * 100f:F0}%";
        }

        /// <summary>Bake time only.</summary>
        public void Configure(RecipeCatalog catalog, Inventory inventory)
        {
            _catalog = catalog;
            _inventory = inventory;
        }
    }
}
