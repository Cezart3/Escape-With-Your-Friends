using System.Collections.Generic;
using EscapeWithYourFriends.Player;
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;

namespace EscapeWithYourFriends.Net
{
    /// <summary>
    /// Server side. Gives every connection a body, a name and a colour.
    ///
    /// FishNet ships a PlayerSpawner sample that does the first of those three. This one exists
    /// because the other two are the interesting part: four grey capsules are unplayable, and the
    /// colour has to be decided by the one machine that can see every other player — the host.
    ///
    /// Spawning is driven by <c>OnClientLoadedStartScenes</c> rather than by the connection state,
    /// because a connection that has not finished loading the scene has nowhere to put a body yet.
    /// The host is a client of itself and comes through the same event, so it needs no special case.
    /// </summary>
    public class PlayerSpawner : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Body spawned for each connection. Must be registered in the spawnable prefabs list.")]
        [SerializeField] NetworkObject _playerPrefab;

        [Tooltip("Where players appear. Empty means a ring around this object.")]
        [SerializeField] Transform[] _spawnPoints;

        [Header("Fallback ring")]
        [Tooltip("Radius of the generated ring used when no spawn points are assigned.")]
        [SerializeField] float _ringRadius = 4f;

        [Tooltip("Height the ring is placed at, so bodies do not spawn inside the floor.")]
        [SerializeField] float _ringHeight = 1.2f;

        NetworkManager _manager;

        /// <summary>Palette slots in use, by owner id. Freed on disconnect so slot 0 can be reused.</summary>
        readonly Dictionary<int, byte> _colorByOwner = new();

        /// <summary>Counts bodies handed out, so the ring fallback spreads them instead of stacking.</summary>
        int _spawnCounter;

        void Awake()
        {
            _manager = GetComponentInParent<NetworkManager>();
            if (_manager == null) _manager = InstanceFinder.NetworkManager;

            if (_manager == null)
            {
                Debug.LogError("[PlayerSpawner] No NetworkManager found; disabling.");
                enabled = false;
                return;
            }

            _manager.SceneManager.OnClientLoadedStartScenes += OnClientLoadedStartScenes;
            _manager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
            _manager.ServerManager.OnServerConnectionState += OnServerConnectionState;
        }

        void OnDestroy()
        {
            if (_manager == null) return;

            _manager.SceneManager.OnClientLoadedStartScenes -= OnClientLoadedStartScenes;
            _manager.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
            _manager.ServerManager.OnServerConnectionState -= OnServerConnectionState;
        }

        void OnClientLoadedStartScenes(NetworkConnection connection, bool asServer)
        {
            // The event fires on both sides. Only the server spawns.
            if (!asServer) return;

            if (_playerPrefab == null)
            {
                Debug.LogError("[PlayerSpawner] No player prefab assigned; nobody can spawn.");
                return;
            }

            GetSpawn(_spawnCounter, out Vector3 position, out Quaternion rotation);
            _spawnCounter++;

            NetworkObject body = _manager.GetPooledInstantiated(_playerPrefab, position, rotation, asServer: true);
            _manager.ServerManager.Spawn(body, connection);

            // Without this the body lives in the spawner's scene and the owning client, which loaded
            // its own copy of the start scenes, never becomes an observer of it.
            _manager.SceneManager.AddOwnerToDefaultScene(body);

            byte colorIndex = TakeColor(connection.ClientId);

            var identity = body.GetComponent<PlayerIdentity>();
            if (identity == null)
                Debug.LogError($"[PlayerSpawner] {_playerPrefab.name} has no PlayerIdentity; it will be grey and nameless.");
            else
                identity.ServerSetIdentity($"Player {connection.ClientId + 1}", colorIndex);

            Debug.Log($"[PlayerSpawner] Spawned body for connection {connection.ClientId} at {position}, colour slot {colorIndex}.");
        }

        void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState == RemoteConnectionState.Stopped)
                _colorByOwner.Remove(connection.ClientId);
        }

        void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Stopped) return;

            // A new session starts from an empty table, otherwise the second lobby of an evening
            // hands out colours starting from wherever the first one left off.
            _colorByOwner.Clear();
            _spawnCounter = 0;
        }

        /// <summary>
        /// Lowest palette slot nobody is using. Reusing freed slots matters more than variety: with
        /// four players the first four colours are the four most distinct ones in the palette.
        /// </summary>
        byte TakeColor(int ownerId)
        {
            if (_colorByOwner.TryGetValue(ownerId, out byte existing)) return existing;

            int paletteSize = PlayerIdentity.Palette.Length;
            for (byte candidate = 0; candidate < paletteSize; candidate++)
            {
                if (_colorByOwner.ContainsValue(candidate)) continue;

                _colorByOwner[ownerId] = candidate;
                return candidate;
            }

            // More players than colours. Wrapping is better than refusing to spawn them.
            var wrapped = (byte)(ownerId % paletteSize);
            _colorByOwner[ownerId] = wrapped;
            return wrapped;
        }

        void GetSpawn(int index, out Vector3 position, out Quaternion rotation)
        {
            if (_spawnPoints != null && _spawnPoints.Length > 0)
            {
                Transform point = _spawnPoints[index % _spawnPoints.Length];
                if (point != null)
                {
                    position = point.position;
                    rotation = point.rotation;
                    return;
                }
            }

            // No spawn points: put everyone on a ring facing the middle, so a fresh scene with
            // nothing but a floor still spawns four players who can see each other.
            const int ringSlots = 8;
            float angle = index % ringSlots * (360f / ringSlots);
            Vector3 offset = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * _ringRadius;

            position = transform.position + offset + Vector3.up * _ringHeight;
            rotation = Quaternion.LookRotation(-offset.normalized, Vector3.up);
        }
    }
}
