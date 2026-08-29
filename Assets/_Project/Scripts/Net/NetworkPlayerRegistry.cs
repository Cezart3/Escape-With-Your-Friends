using System;
using System.Collections.Generic;
using EscapeWithYourFriends.Player;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Net
{
    /// <summary>
    /// Every player body currently in the world, on this machine, keyed by owner connection id.
    ///
    /// Almost every system needs this list eventually — the HUD draws a row per player, the Revive
    /// Machine has to find a corpse's owner, the natives pick a target, the scoreboard sums wallets.
    /// Without a registry each of those ends up calling <c>FindObjectsByType</c> every frame, which is
    /// both slow and subtly wrong: it also finds bodies that are mid-despawn.
    ///
    /// The registry is deliberately not networked. It is a local index of objects FishNet has already
    /// replicated here, so it holds exactly what this peer can see and nothing more. On the host that
    /// is everyone; on a client it is everyone in observer range.
    /// </summary>
    public static class NetworkPlayerRegistry
    {
        static readonly Dictionary<int, PlayerBody> _byOwner = new();
        static readonly List<PlayerBody> _players = new();

        /// <summary>A registered player: the identity component plus its network object.</summary>
        public readonly struct PlayerBody
        {
            public readonly int OwnerId;
            public readonly PlayerIdentity Identity;
            public readonly NetworkObject Object;

            public PlayerBody(int ownerId, PlayerIdentity identity, NetworkObject networkObject)
            {
                OwnerId = ownerId;
                Identity = identity;
                Object = networkObject;
            }

            public bool IsValid => Identity != null && Object != null;
        }

        /// <summary>Everyone currently registered. Stable order: registration order.</summary>
        public static IReadOnlyList<PlayerBody> Players => _players;

        public static int Count => _players.Count;

        /// <summary>Raised after a player is added.</summary>
        public static event Action<PlayerBody> PlayerAdded;

        /// <summary>Raised after a player is removed. The struct still carries the id that left.</summary>
        public static event Action<PlayerBody> PlayerRemoved;

        /// <summary>
        /// Called by <see cref="PlayerIdentity"/> when a body starts on this peer. Registering from
        /// the body itself rather than from the spawner means clients populate the registry too,
        /// without a second RPC telling them what they can already see.
        /// </summary>
        public static void Register(PlayerIdentity identity)
        {
            if (identity == null) return;

            NetworkObject networkObject = identity.NetworkObject;
            if (networkObject == null) return;

            int ownerId = networkObject.OwnerId;

            // A reconnect can hand the same connection id to a new body before the old one has
            // despawned. Last writer wins: the new body is the live one.
            if (_byOwner.TryGetValue(ownerId, out PlayerBody existing))
            {
                if (existing.Object == networkObject) return;
                RemoveAt(existing);
            }

            var body = new PlayerBody(ownerId, identity, networkObject);
            _byOwner[ownerId] = body;
            _players.Add(body);

            PlayerAdded?.Invoke(body);
        }

        /// <summary>Called when a body stops on this peer — despawn, disconnect, or scene unload.</summary>
        public static void Unregister(PlayerIdentity identity)
        {
            if (identity == null) return;

            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].Identity != identity) continue;
                RemoveAt(_players[i]);
                return;
            }
        }

        static void RemoveAt(PlayerBody body)
        {
            _players.Remove(body);

            // Only clear the owner slot if it still points at this body; a reconnect may have
            // already replaced it.
            if (_byOwner.TryGetValue(body.OwnerId, out PlayerBody current)
                && current.Identity == body.Identity)
            {
                _byOwner.Remove(body.OwnerId);
            }

            PlayerRemoved?.Invoke(body);
        }

        public static bool TryGet(int ownerId, out PlayerBody body) => _byOwner.TryGetValue(ownerId, out body);

        public static PlayerIdentity GetIdentity(int ownerId) =>
            _byOwner.TryGetValue(ownerId, out PlayerBody body) ? body.Identity : null;

        /// <summary>
        /// Clears everything. Called when the local connection stops, because leaving a session must
        /// not carry stale bodies into the next one — this is static state and nothing else empties it.
        /// </summary>
        public static void Clear()
        {
            // Copy first: handlers may unregister during iteration.
            var leaving = _players.ToArray();
            _players.Clear();
            _byOwner.Clear();

            foreach (PlayerBody body in leaving)
                PlayerRemoved?.Invoke(body);
        }

        /// <summary>Editor-only sanity check, cheap enough to leave in.</summary>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public static void LogContents()
        {
            foreach (PlayerBody body in _players)
                Debug.Log($"[Registry] owner {body.OwnerId}: {body.Identity.DisplayName}");
        }
    }
}
