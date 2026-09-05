using EscapeWithYourFriends.Items;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Economy
{
    /// <summary>
    /// The player's side of a trade. Two owner-callable requests and nothing else.
    ///
    /// It lives on the player rather than on the counter for the same reason the chest transfers live
    /// on <c>Inventory</c>: **a shop is owned by nobody**, so a <c>ServerRpc</c> on it would have no
    /// owner to require and would accept a message from anyone. The bag has an owner, so the request
    /// comes from the player and names the counter, and the server checks the player is actually
    /// standing at it.
    ///
    /// A request carries an offer index and a count - never a price. What something costs is the
    /// shop's business, read off the shop's own asset on the server, and a client that sends a
    /// cheaper number is sending a number nobody reads.
    /// </summary>
    public class Trading : NetworkBehaviour
    {
        [SerializeField] Inventory _inventory;
        [SerializeField] Wallet _wallet;

        void Awake()
        {
            if (_inventory == null) _inventory = GetComponent<Inventory>();
            if (_wallet == null) _wallet = GetComponent<Wallet>();
        }

        /// <summary>Owner side. Buy from an offer on a counter you are standing at.</summary>
        public void RequestBuy(ShopCounter counter, int offer, int count)
        {
            if (!IsOwner || counter == null || count <= 0) return;

            ServerBuy(counter.NetworkObject, offer, count);
        }

        /// <summary>Owner side. Sell one bag slot, or part of it.</summary>
        public void RequestSell(ShopCounter counter, int slot, int count)
        {
            if (!IsOwner || counter == null || count <= 0) return;

            ServerSell(counter.NetworkObject, slot, count);
        }

        [ServerRpc]
        void ServerBuy(NetworkObject counterObject, int offer, int count)
        {
            ShopCounter counter = Resolve(counterObject);
            if (counter == null) return;

            counter.ServerBuy(_inventory, _wallet, offer, count, out string why);
            if (why != null) Refused(why);
        }

        [ServerRpc]
        void ServerSell(NetworkObject counterObject, int slot, int count)
        {
            ShopCounter counter = Resolve(counterObject);
            if (counter == null) return;

            counter.ServerSell(_inventory, _wallet, slot, count, out string why);
            if (why != null) Refused(why);
        }

        [Server]
        ShopCounter Resolve(NetworkObject counterObject)
        {
            var counter = counterObject != null ? counterObject.GetComponent<ShopCounter>() : null;
            if (counter == null) return null;

            // Checked here as well as inside the transaction: the reach test is the one thing that
            // stops a client naming a counter on the other side of the island, and a check that only
            // exists in one place is a check somebody will refactor away.
            if (counter.InReach(transform.position)) return counter;

            Refused($"a counter {(counter.transform.position - transform.position).magnitude:F0}m away");
            return null;
        }

        /// <summary>
        /// Why the server said no, sent back to the one client that asked. Not a broadcast: the rest
        /// of the squad does not need to know you cannot afford a hatchet.
        /// </summary>
        [Server]
        void Refused(string why)
        {
            Debug.Log($"[Trading] {name} refused: {why}.");
            TargetRefused(Owner, why);
        }

        [TargetRpc]
        void TargetRefused(FishNet.Connection.NetworkConnection connection, string why)
        {
            LastRefusal = why;
            LastRefusalAt = Time.time;
        }

        /// <summary>What the server last said no to, for the shop screen to show. Client side.</summary>
        public string LastRefusal { get; private set; }

        public float LastRefusalAt { get; private set; }

        /// <summary>Bake time only.</summary>
        public void Configure(Inventory inventory, Wallet wallet)
        {
            _inventory = inventory;
            _wallet = wallet;
        }
    }
}
