using EscapeWithYourFriends.Combat;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Items
{
    /// <summary>
    /// The player's end of getting things *out* of the bag: drop and throw.
    ///
    /// Same shape as every other verb in this project. The owner asks, the server decides, and the
    /// server's version of the check is the only one that matters - a client that could name a slot
    /// and a position could drop items it does not have, wherever it liked.
    ///
    /// The two verbs are one key. A tap drops the selected stack at your feet; holding it past
    /// <see cref="HoldToThrow"/> and letting go throws it. That is one binding instead of two, and the
    /// hold reads naturally as winding up - which matters, because throwing a bag of scrap at a friend
    /// is not a logistics feature, it is the reason the issue says "trolling preserved".
    ///
    /// Note what is *not* here: any way to add an item. Picking things up lives on
    /// <see cref="WorldItem"/>, where the object being taken is a real spawned thing the server can
    /// verify exists and is within reach.
    /// </summary>
    public class ItemDropper : NetworkBehaviour
    {
        /// <summary>Seconds the drop key must be held before releasing throws instead of drops.</summary>
        public const float HoldToThrow = 0.25f;

        [Header("References")]
        [Tooltip("Eye-height transform items leave from - the same one weapons aim along.")]
        [SerializeField] Transform _aimOrigin;

        [SerializeField] Inventory _inventory;

        [Header("Rules")]
        [Tooltip("How far in front of the eyes a dropped stack is placed, in metres.")]
        [SerializeField] float _dropAhead = WorldItemSpawner.DropAhead;

        [Tooltip("Seconds between drops. Stops a held key from emptying a bag into one pile of physics.")]
        [SerializeField] float _cooldown = 0.25f;

        Health _health;
        StunState _stun;
        float _nextAllowedAt;

        void Awake()
        {
            _health = GetComponent<Health>();
            _stun = GetComponent<StunState>();

            if (_inventory == null) _inventory = GetComponent<Inventory>();
        }

        /// <summary>
        /// Owner-side entry point. Returns whether a request went out, so the input component can
        /// fall through to something else when the bag has nothing to give.
        /// </summary>
        public bool RequestDrop(bool thrown)
        {
            if (!IsOwner || _inventory == null) return false;

            // Checked here as well as on the server so a stunned player does not spam requests that
            // will all be refused - the same rule PlayerInteractor follows.
            if (_health != null && _health.IsIncapacitated) return false;
            if (_stun != null && _stun.IsStunned) return false;

            // The owner's copy of the slot is replicated and good enough to decide whether to bother
            // sending. The server re-reads its own before doing anything.
            if (_inventory.Selected.IsEmpty) return false;

            Transform origin = _aimOrigin != null ? _aimOrigin : transform;
            ServerDrop(_inventory.SelectedSlot, thrown, origin.forward);
            return true;
        }

        /// <summary>
        /// The direction is sent because only the client knows where its camera points - the server's
        /// copy of a remote player's aim is a tick behind, and throwing at where somebody *was* is
        /// worse than trusting a normalised vector. It is normalised on arrival; a client that sends
        /// a hundred-metre-long one gets a unit vector like everybody else.
        /// </summary>
        [ServerRpc]
        void ServerDrop(int slot, bool thrown, Vector3 aim) => ServerDropSlot(slot, thrown, aim);

        /// <summary>
        /// The server-side half, separate from the RPC so anything already on the server can use it -
        /// a corpse spilling its bag, a shop refunding into a full inventory, the test harness.
        /// </summary>
        [Server]
        public WorldItem ServerDropSlot(int slot, bool thrown, Vector3 aim)
        {
            if (_inventory == null) return null;

            if (_health != null && _health.IsIncapacitated) return null;
            if (_stun != null && _stun.IsStunned) return null;

            if (Time.time < _nextAllowedAt) return null;

            // The client named a slot; it does not get to name a stack. TakeSlot bounds-checks and
            // returns empty for anything out of range or already empty.
            ItemStack taken = _inventory.TakeSlot(slot);
            if (taken.IsEmpty) return null;

            _nextAllowedAt = Time.time + _cooldown;

            Transform origin = _aimOrigin != null ? _aimOrigin : transform;
            Vector3 direction = aim.sqrMagnitude > 0.001f ? aim.normalized : origin.forward;
            Vector3 position = origin.position + direction * _dropAhead;

            WorldItem item = thrown
                ? WorldItemSpawner.Throw(taken, position, direction, NetworkObject)
                : WorldItemSpawner.Drop(taken, position, Random.rotation);

            // Spawning can fail - no catalog, no prefab, no server. Putting the stack back is the only
            // honest outcome; the alternative is silently deleting a player's boat part.
            if (item == null) _inventory.Add(taken.Def, taken.Count);

            return item;
        }

        /// <summary>
        /// Spills everything on the floor. What #26's corpse does when a player dies, and what a
        /// broken chest will do in #44.
        /// </summary>
        [Server]
        public void ServerSpillAll(Vector3 around, float radius = 1f)
        {
            if (_inventory == null) return;

            for (int slot = 0; slot < _inventory.SlotCount; slot++)
            {
                ItemStack taken = _inventory.TakeSlot(slot);
                if (taken.IsEmpty) continue;

                Vector2 offset = Random.insideUnitCircle * radius;
                var position = new Vector3(around.x + offset.x, around.y + 0.4f, around.z + offset.y);

                WorldItemSpawner.Drop(taken, position, Random.rotation);
            }
        }

        /// <summary>Bake time only.</summary>
        public void Configure(Transform aimOrigin, Inventory inventory)
        {
            _aimOrigin = aimOrigin;
            _inventory = inventory;
        }
    }
}
