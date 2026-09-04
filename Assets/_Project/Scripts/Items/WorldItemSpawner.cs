using EscapeWithYourFriends.Data;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Items
{
    /// <summary>
    /// Turns an <see cref="ItemStack"/> into a thing on the ground. Server only, always.
    ///
    /// Static, and reached through <see cref="ItemCatalog.Active"/>, because every future caller of it
    /// is somewhere different: a player pressing drop, a corpse spilling its bag, a tree being chopped,
    /// a chest being broken, a shop refusing a purchase. None of those should have to hold a reference
    /// to a spawner component, and a singleton MonoBehaviour would be one more thing to wire into
    /// every scene that can contain items - which is all of them.
    ///
    /// The prefab lives on the catalog for the same reason: the catalog is already published globally
    /// and already assigned to every inventory at bake time, so there is exactly one asset to wire.
    /// </summary>
    public static class WorldItemSpawner
    {
        /// <summary>How far in front of the eyes a dropped stack appears, in metres.</summary>
        public const float DropAhead = 0.6f;

        /// <summary>Metres per second a thrown stack leaves at. Comfortably further than you can spit.</summary>
        public const float ThrowSpeed = 9f;

        /// <summary>Upward bias on a throw, so it arcs instead of being fired flat at the floor.</summary>
        public const float ThrowLift = 0.25f;

        /// <summary>
        /// Drops a stack at a position with no velocity. What a player pressing the drop key gets, and
        /// what a corpse spilling its inventory gets.
        /// </summary>
        public static WorldItem Drop(ItemStack stack, Vector3 position, Quaternion rotation)
            => Spawn(stack, position, rotation, Vector3.zero, Vector3.zero, null);

        /// <summary>
        /// Throws a stack along a direction. The thrower is remembered so it does not immediately
        /// bounce off them and come straight back.
        /// </summary>
        public static WorldItem Throw(ItemStack stack, Vector3 position, Vector3 direction,
                                      NetworkObject thrower)
        {
            Vector3 aim = (direction.normalized + Vector3.up * ThrowLift).normalized;

            // Random spin, because a crate that flies through the air perfectly level looks like a
            // projectile and this is meant to look like somebody threw a bag of rope at your head.
            var spin = new Vector3(Random.Range(-8f, 8f), Random.Range(-8f, 8f), Random.Range(-8f, 8f));

            return Spawn(stack, position, Random.rotation, aim * ThrowSpeed, spin, thrower);
        }

        static WorldItem Spawn(ItemStack stack, Vector3 position, Quaternion rotation,
                               Vector3 velocity, Vector3 spin, NetworkObject thrower)
        {
            if (stack.IsEmpty) return null;

            ItemCatalog catalog = ItemCatalog.Active;
            if (catalog == null)
            {
                Debug.LogError("[WorldItemSpawner] No active item catalog, so a dropped stack would "
                               + "have nothing to become. It has been discarded.");
                return null;
            }

            GameObject prefab = catalog.WorldItemPrefab;
            if (prefab == null)
            {
                Debug.LogError("[WorldItemSpawner] The catalog has no world item prefab. Run "
                               + "EscapeWithYourFriends.EditorTools.WorldItemBuilder.Build.");
                return null;
            }

            var networkManager = InstanceFinder.NetworkManager;
            if (networkManager == null || !networkManager.IsServerStarted)
            {
                Debug.LogError("[WorldItemSpawner] Called with no server running. Dropping an item is "
                               + "a server decision; a client that could spawn loot could spawn anything.");
                return null;
            }

            GameObject instance = Object.Instantiate(prefab, position, rotation);
            var item = instance.GetComponent<WorldItem>();

            if (item == null)
            {
                Debug.LogError($"[WorldItemSpawner] {prefab.name} has no WorldItem component.");
                Object.Destroy(instance);
                return null;
            }

            // Initialised before spawning, not after. FishNet builds the spawn message from the
            // SyncVar values at the moment Spawn is called, so a stack written afterwards arrives as
            // a separate update and clients see the item exist for a frame as an empty pile - which
            // is exactly what the first version of this did, and what the client log caught.
            item.Initialise(stack, thrower, velocity, spin);
            networkManager.ServerManager.Spawn(instance);

            return item;
        }
    }
}
