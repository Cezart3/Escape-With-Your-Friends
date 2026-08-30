using System;
using System.Collections.Generic;
using FishNet.Authenticating;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace EscapeWithYourFriends.Net
{
    /// <summary>Sent by a client the moment it connects, before it is allowed to do anything else.</summary>
    public struct PlayerKeyBroadcast : IBroadcast
    {
        public string Key;
    }

    /// <summary>Server's answer. Only useful for a log line on the client; the kick is the real answer.</summary>
    public struct PlayerKeyResultBroadcast : IBroadcast
    {
        public bool Passed;
        public string Reason;
    }

    /// <summary>
    /// Collects each connection's <see cref="PlayerKey"/> before it is allowed into the session, and
    /// keeps it where <see cref="PlayerSpawner"/> can read it.
    ///
    /// **Why an Authenticator and not a message after joining.** The only thing this has to guarantee
    /// is ordering: the key must be known before the server decides whether to spawn a fresh body or
    /// hand back an abandoned one. FishNet raises <c>OnClientLoadedStartScenes</c> — where that
    /// decision is made — only after authentication has passed, so putting the key here makes the
    /// ordering structural rather than a race that usually wins. A key that arrives one frame after
    /// the spawn decision would leave a player standing next to their own corpse, which is exactly
    /// the bug #111 exists to remove.
    ///
    /// **What is actually validated.** That the key is a sane, non-empty string, and that no live
    /// connection is already using it. That is not proof of anything — see the warning on
    /// <see cref="PlayerKey"/> — it is a duplicate check, and its job is to stop two processes on one
    /// machine (the usual test setup, and a shared-PC household) from fighting over the same body.
    ///
    /// The authenticator is found by FishNet automatically: <c>ServerManager</c> falls back to
    /// <c>GetComponent&lt;Authenticator&gt;()</c> on its own GameObject when nothing is assigned, and
    /// its sub-managers live on the NetworkManager's GameObject, which is where SceneBootstrap puts
    /// this. No serialized reference to break when the scene is regenerated.
    /// </summary>
    public class PlayerKeyAuthenticator : Authenticator
    {
        /// <inheritdoc/>
        public override event Action<NetworkConnection, bool> OnAuthenticationResult;

        /// <summary>
        /// Longest key accepted. A Steam id is 17 digits and a GUID key is 38 characters; anything
        /// past this is a client sending rubbish, and the dictionary that holds these is server memory
        /// an unauthenticated connection would otherwise get to choose the size of.
        /// </summary>
        const int MaxKeyLength = 128;

        /// <summary>Server side. Key per live connection, cleared as connections come and go.</summary>
        readonly Dictionary<int, string> _keyByClientId = new();

        public override void InitializeOnce(NetworkManager networkManager)
        {
            base.InitializeOnce(networkManager);

            // requireAuthentication: false, or the broadcast that performs the authentication would
            // itself require authentication.
            NetworkManager.ServerManager.RegisterBroadcast<PlayerKeyBroadcast>(OnKeyBroadcast, false);
            NetworkManager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
            NetworkManager.ServerManager.OnServerConnectionState += OnServerConnectionState;

            NetworkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;
            NetworkManager.ClientManager.RegisterBroadcast<PlayerKeyResultBroadcast>(OnResultBroadcast);
        }

        /// <summary>
        /// Server only. The key <paramref name="connection"/> authenticated with, if it is still here.
        /// </summary>
        public bool TryGetKey(NetworkConnection connection, out string key)
        {
            key = null;
            return connection != null && _keyByClientId.TryGetValue(connection.ClientId, out key);
        }

        void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Started) return;

            // The host's own client comes through here too, and should: the host is a player with a
            // body, and if it ever reconnects to a session it did not start it needs the same key.
            NetworkManager.ClientManager.Broadcast(new PlayerKeyBroadcast { Key = PlayerKey.Local });
        }

        void OnKeyBroadcast(NetworkConnection connection, PlayerKeyBroadcast broadcast, Channel channel)
        {
            // Connections are dropped from the server's table on disconnect, so an already
            // authenticated connection sending this again is either a bug or someone poking at it.
            if (connection.IsAuthenticated)
            {
                connection.Disconnect(true);
                return;
            }

            string key = broadcast.Key;
            string reason = Validate(key, connection);
            bool passed = reason == null;

            if (passed) _keyByClientId[connection.ClientId] = key;

            // Sent before the result is raised, because a failed result kicks the connection and an
            // unsent broadcast goes with it — the client would be told nothing and see a bare drop.
            NetworkManager.ServerManager.Broadcast(
                connection,
                new PlayerKeyResultBroadcast { Passed = passed, Reason = reason ?? string.Empty },
                false);

            Debug.Log($"[PlayerKeyAuthenticator] connection {connection.ClientId} "
                      + $"{(passed ? "accepted" : "rejected")} with key {PlayerKey.Short(key)}"
                      + $"{(passed ? "." : $": {reason}.")}");

            OnAuthenticationResult?.Invoke(connection, passed);
        }

        /// <summary>Null when the key is usable, otherwise why it is not.</summary>
        string Validate(string key, NetworkConnection connection)
        {
            if (string.IsNullOrWhiteSpace(key)) return "empty key";
            if (key.Length > MaxKeyLength) return $"key longer than {MaxKeyLength} characters";

            foreach (KeyValuePair<int, string> pair in _keyByClientId)
            {
                if (pair.Key == connection.ClientId || pair.Value != key) continue;

                // Two processes with one key would both claim the same abandoned body, and the loser
                // would silently get a fresh one. Refusing the second is the honest failure.
                return $"key already held by connection {pair.Key}";
            }

            return null;
        }

        void OnResultBroadcast(PlayerKeyResultBroadcast result, Channel channel)
        {
            if (result.Passed) Debug.Log($"[PlayerKeyAuthenticator] key {PlayerKey.Short(PlayerKey.Local)} accepted.");
            else Debug.LogWarning($"[PlayerKeyAuthenticator] key rejected: {result.Reason}.");
        }

        void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            // Freed on disconnect, which is what lets the same key come back and reclaim its body.
            if (args.ConnectionState == RemoteConnectionState.Stopped)
                _keyByClientId.Remove(connection.ClientId);
        }

        void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Stopped) _keyByClientId.Clear();
        }
    }
}
