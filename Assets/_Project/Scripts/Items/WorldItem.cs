using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Items
{
    /// <summary>
    /// A stack of something lying on the ground, as a real physical object.
    ///
    /// The whole point of #42 is that loot is not an abstraction. A dropped bag of rope is a rigidbody
    /// that rolls down a hill, bounces off a friend, and sits there until somebody takes it - and
    /// **anybody** can take it. There is deliberately no owner check on pickup: a corpse's spilled
    /// inventory and a friend's carefully hoarded planks are the same object to this component, and
    /// robbing the pile is the feature, not a hole in it.
    ///
    /// Physics runs on the server only. Clients set the rigidbody kinematic and let
    /// <c>NetworkTransform</c> drive the transform, because two peers integrating the same collision
    /// independently is how a crate ends up in two places at once. The jitter of an interpolated
    /// bouncing crate is acceptable; a crate you can pick up on your screen and not on mine is not.
    ///
    /// Items persist. There is no despawn timer, because a timer means walking back to your loot and
    /// finding it gone - the single most annoying thing a survival game can do. What there is instead
    /// is a hard cap on how many can exist at once (see <see cref="Cap"/>), which trades a pathological
    /// case nobody will hit for an unbounded one that would eventually take the host down.
    /// </summary>
    public class WorldItem : NetworkBehaviour, IInteractable
    {
        /// <summary>
        /// How many dropped stacks may exist at once. Past this the oldest is removed.
        ///
        /// Four players emptying twenty slots each is eighty objects, so this is several full
        /// inventories of headroom. It exists for the case that has no natural end - a script, a
        /// duplication bug, or somebody standing at a chest pressing drop for an hour - where the
        /// alternative is the host's physics budget dying quietly.
        /// </summary>
        public const int Cap = 240;

        static readonly System.Collections.Generic.List<WorldItem> Live = new();

        [Header("References")]
        [Tooltip("Where the item's visual prefab is parented. Replaced whenever the stack changes.")]
        [SerializeField] Transform _visualRoot;

        [SerializeField] Rigidbody _body;
        [SerializeField] Collider _collider;

        [Header("Presentation")]
        [Tooltip("Fallback greybox size when the item has no world prefab yet. Metres.")]
        [SerializeField] float _greyboxSize = 0.3f;

        [Header("Rules")]
        [Tooltip("Seconds after a throw during which the thrower cannot pick it back up.")]
        [SerializeField] float _throwerCooldown = 0.6f;

        /// <summary>
        /// What is in it. A SyncVar rather than spawn-time arguments because a stack can *change*
        /// after it lands: a full bag takes what fits and leaves the rest on the floor, and the pile
        /// has to shrink on everybody's screen rather than vanish and respawn.
        /// </summary>
        readonly SyncVar<ItemStack> _stack = new();

        /// <summary>Who threw it and when, so a throw does not immediately re-enter the bag.</summary>
        readonly SyncVar<NetworkObject> _thrower = new();

        float _pickupAllowedAt;
        Collider _ignoring;
        ItemStack _shown;
        GameObject _visual;

        public ItemStack Stack => _stack.Value;

        public string Prompt
        {
            get
            {
                ItemStack stack = _stack.Value;
                if (stack.IsEmpty) return null;

                ItemDef def = stack.Def;
                string label = def != null ? def.DisplayName : stack.ToString();
                return stack.Count > 1 ? $"Take {label} x{stack.Count}" : $"Take {label}";
            }
        }

        void Awake()
        {
            _stack.OnChange += OnStackChanged;
        }

        void OnDestroy()
        {
            _stack.OnChange -= OnStackChanged;
            Live.Remove(this);
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            Live.Add(this);
            Trim();

            // Server owns the simulation. Everyone else is a spectator of it.
            if (_body != null) _body.isKinematic = false;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // On a host this is the same rigidbody the server is stepping, so leave it alone.
            if (!IsServerStarted && _body != null)
            {
                _body.isKinematic = true;
                _body.interpolation = RigidbodyInterpolation.None;
            }

            Refresh();

            // Client-side proof for #42's harness. The server can show that its own numbers add up;
            // only a client can show that the object actually arrived, with the right contents, on a
            // machine that did not spawn it.
            if (!IsServerStarted && CommandLine.HasFlag("-itemTest"))
                Debug.Log($"[WorldItem] client sees {_stack.Value} at {transform.position:F1}.");
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            // "No longer sees" rather than "was taken": this also fires when the session ends, and a
            // log line that claimed a pickup at shutdown would be a lie in the evidence.
            if (!IsServerStarted && CommandLine.HasFlag("-itemTest"))
                Debug.Log($"[WorldItem] client no longer sees {_stack.Value}.");
        }

        // ---------------------------------------------------------------- server side

        /// <summary>
        /// Called by <see cref="WorldItemSpawner"/> on the instantiated object **before** it is
        /// spawned, which is the whole point of it being a separate method.
        ///
        /// Setting the stack after <c>ServerManager.Spawn</c> does not work: the spawn message is
        /// built from the SyncVar values as they stand at that call, so a value written afterwards
        /// arrives as a follow-up update and every client sees the item exist, briefly, as an empty
        /// pile with no visual. Writing it first makes it part of the object's initial state.
        ///
        /// Deliberately not <c>[Server]</c>: that attribute checks the object is initialised, which
        /// before the spawn it is not, and the guard would refuse the one call that matters.
        /// </summary>
        internal void Initialise(ItemStack stack, NetworkObject thrower, Vector3 velocity,
                                 Vector3 spin)
        {
            _stack.Value = stack;
            _thrower.Value = thrower;
            _pickupAllowedAt = thrower != null ? Time.time + _throwerCooldown : 0f;

            if (_body == null) return;

            _body.isKinematic = false;
            _body.linearVelocity = velocity;
            _body.angularVelocity = spin;

            // A thrown item leaves from eye height, which is inside the thrower's own capsule. Without
            // this it hits you in the face and drops at your feet, which reads as the throw not
            // working rather than as a physics detail.
            if (thrower == null || _collider == null) return;

            _ignoring = thrower.GetComponent<Collider>();
            if (_ignoring != null) Physics.IgnoreCollision(_collider, _ignoring, true);
        }

        void Update()
        {
            // Server only, and only while something is being ignored: restoring the collision as soon
            // as the item is clear is what stops a thrown crate from passing through the thrower on
            // its way back down a slope.
            if (!IsServerStarted || _ignoring == null || Time.time < _pickupAllowedAt) return;

            if (_collider != null) Physics.IgnoreCollision(_collider, _ignoring, false);
            _ignoring = null;
        }

        public bool ServerCanInteract(NetworkObject actor)
        {
            if (actor == null || _stack.Value.IsEmpty) return false;

            // Your own throw does not come straight back. Without this, throwing at somebody's head
            // while walking forward hands the item back to you before it has travelled a metre.
            if (_thrower.Value == actor && Time.time < _pickupAllowedAt) return false;

            var health = actor.GetComponent<Health>();
            if (health != null && health.IsIncapacitated) return false;

            var inventory = actor.GetComponent<Inventory>();
            if (inventory == null) return false;

            // Deliberately no ownership test. Whoever dropped it, whoever died holding it, whoever
            // spent an hour gathering it - if you are standing over it, it is yours now.
            return true;
        }

        public void ServerInteract(NetworkObject actor)
        {
            if (!ServerCanInteract(actor)) return;

            var inventory = actor.GetComponent<Inventory>();
            ItemStack stack = _stack.Value;

            ItemDef def = stack.Def;
            if (def == null)
            {
                Debug.LogError($"[WorldItem] index {stack.Index} is not in the catalog; despawning it "
                               + "rather than leaving an object nobody can ever pick up.");
                ServerDespawn();
                return;
            }

            int left = inventory.Add(def, stack.Count);
            if (left == stack.Count) return;   // Nothing fitted. The pile stays exactly as it was.

            if (left <= 0)
            {
                ServerDespawn();
                return;
            }

            // Partial pickup: a full bag takes what it can and the rest stays on the floor. The
            // alternative - all or nothing - means an overloaded player cannot take a single bandage
            // off a pile of forty planks.
            _stack.Value = stack.With(left);
            _thrower.Value = null;
        }

        [Server]
        public void ServerDespawn()
        {
            Live.Remove(this);
            Despawn();
        }

        /// <summary>Oldest first, because the newest is the one somebody is standing next to.</summary>
        [Server]
        static void Trim()
        {
            while (Live.Count > Cap)
            {
                WorldItem oldest = Live[0];
                Live.RemoveAt(0);

                if (oldest == null) continue;

                Debug.LogWarning($"[WorldItem] {Live.Count + 1} dropped stacks is over the cap of {Cap}; "
                                 + $"removing the oldest ({oldest.Stack}).");
                oldest.Despawn();
            }
        }

        // ---------------------------------------------------------------- presentation

        void OnStackChanged(ItemStack older, ItemStack newer, bool asServer)
        {
            if (asServer && IsClientStarted) return;

            Refresh();

            if (!IsServerStarted && CommandLine.HasFlag("-itemTest"))
                Debug.Log($"[WorldItem] client sees the pile change: {older} -> {newer}.");
        }

        /// <summary>
        /// Rebuilds the visual when the kind changes. Only on a kind change, not a count change: a
        /// pile of five planks and a pile of four look the same, and rebuilding a mesh because
        /// somebody took one off the top would be work for nothing.
        /// </summary>
        void Refresh()
        {
            ItemStack stack = _stack.Value;
            if (_visual != null && stack.SameKind(_shown)) return;

            _shown = stack;

            if (_visual != null) Destroy(_visual);
            _visual = null;

            if (_visualRoot == null || stack.IsEmpty) return;

            ItemDef def = stack.Def;
            GameObject prefab = def != null ? def.WorldPrefab : null;

            _visual = prefab != null
                ? Instantiate(prefab, _visualRoot)
                : Greybox(def);

            _visual.transform.localPosition = Vector3.zero;
            _visual.transform.localRotation = Quaternion.identity;

            name = def != null ? $"WorldItem ({def.Id})" : "WorldItem";
        }

        /// <summary>
        /// A coloured cube, until there is art. Colour by category rather than by item so a glance
        /// tells you food from scrap without fifteen materials existing; the exact shades will be
        /// thrown away with the greybox.
        /// </summary>
        GameObject Greybox(ItemDef def)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Greybox";
            cube.transform.SetParent(_visualRoot, false);
            cube.transform.localScale = Vector3.one * _greyboxSize;

            // The networked object already has its own collider; a second one on the visual would
            // catch the interaction sphere cast and fight the physics body.
            Destroy(cube.GetComponent<Collider>());

            var renderer = cube.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = Tint(def);

            return cube;
        }

        static Color Tint(ItemDef def)
        {
            if (def == null) return Color.magenta;

            return def.Category switch
            {
                ItemCategory.Material => new Color(0.65f, 0.50f, 0.35f),
                ItemCategory.Food => new Color(0.85f, 0.60f, 0.25f),
                ItemCategory.Drink => new Color(0.35f, 0.65f, 0.90f),
                ItemCategory.Medical => new Color(0.90f, 0.35f, 0.40f),
                ItemCategory.Tool => new Color(0.55f, 0.58f, 0.62f),
                ItemCategory.Weapon => new Color(0.35f, 0.35f, 0.40f),
                ItemCategory.Quest => new Color(0.85f, 0.75f, 0.30f),
                _ => new Color(0.7f, 0.7f, 0.7f),
            };
        }

        /// <summary>Bake time only, from <c>WorldItemBuilder</c>.</summary>
        public void Configure(Transform visualRoot, Rigidbody body, Collider collider)
        {
            _visualRoot = visualRoot;
            _body = body;
            _collider = collider;
        }
    }
}
