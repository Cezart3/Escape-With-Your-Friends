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

        /// <summary>
        /// The spawner in the loaded scene. Spawn points are scene data, but the things that need to
        /// put a player back on the map — the fall guard now, the revive machine and the ghost
        /// respawn later — live on the player prefab, which cannot hold a scene reference. One
        /// server-side instance is simpler than threading the reference through every spawned body.
        /// </summary>
        public static PlayerSpawner Instance { get; private set; }

        /// <summary>Palette slots in use, by owner id. Freed on disconnect so slot 0 can be reused.</summary>
        readonly Dictionary<int, byte> _colorByOwner = new();

        /// <summary>Counts bodies handed out, so the ring fallback spreads them instead of stacking.</summary>
        int _spawnCounter;

        void Awake()
        {
            Instance = this;

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
            if (Instance == this) Instance = null;

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

            string key = ResolveKey(connection);
            if (TryReclaim(connection, key)) return;

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

            // Stamped after the spawn, because the component only accepts the key once the object is
            // networked. This is what makes the body findable again if this player drops (#111).
            var persistence = body.GetComponent<BodyPersistence>();
            if (persistence != null) persistence.ServerSetOwnerKey(key);

            Debug.Log($"[PlayerSpawner] Spawned body for connection {connection.ClientId} at {position}, "
                      + $"colour slot {colorIndex}, key {PlayerKey.Short(key)}.");
        }

        /// <summary>
        /// The key <paramref name="connection"/> authenticated with, or null when there is no key
        /// authenticator in the scene. Null is not an error: it means every connection gets a fresh
        /// body, which is exactly how this behaved before #111.
        /// </summary>
        string ResolveKey(NetworkConnection connection)
        {
            var authenticator = _manager.ServerManager.GetAuthenticator() as PlayerKeyAuthenticator;
            return authenticator != null && authenticator.TryGetKey(connection, out string key)
                ? key
                : null;
        }

        /// <summary>
        /// Hands this connection the body it left behind, if it left one. True when it did, in which
        /// case nothing new is spawned and the spawn ring is not advanced — the player comes back
        /// where they fell, not where the next free spawn point is.
        /// </summary>
        bool TryReclaim(NetworkConnection connection, string key)
        {
            BodyPersistence body = BodyPersistence.FindAbandoned(key);
            if (body == null) return false;

            // Ownership first, scene second. The client becomes an observer of the body when it is
            // added to the scene, and a body observed before it is owned arrives on the client with
            // IsOwner false — every owner-side component would start up in spectator mode.
            if (!body.ServerAdopt(connection)) return false;

            _manager.SceneManager.AddOwnerToDefaultScene(body.NetworkObject);

            // The body keeps the name and colour it died with, so the colour slot has to be booked
            // under the new connection id or the next player to join would be handed the same one.
            var identity = body.GetComponent<PlayerIdentity>();
            if (identity != null) _colorByOwner[connection.ClientId] = identity.ColorIndex;

            string state = body.Health != null ? body.Health.State.ToString() : "unknown";
            Debug.Log($"[PlayerSpawner] Connection {connection.ClientId} reclaimed the body of former "
                      + $"owner {body.SpawnOwnerId} (key {PlayerKey.Short(key)}) at "
                      + $"{body.transform.position}, state {state}, colour slot "
                      + $"{(identity != null ? identity.ColorIndex : (byte)0)}.");
            return true;
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
        ///
        /// A slot counts as in use while an abandoned body is still wearing it (#111). The table is
        /// keyed by connection id and a disconnect frees the entry, but the corpse on the ground
        /// keeps its colour and its owner may walk back in and reclaim it, so handing the slot to
        /// the next joiner would put two live players in the same colour.
        /// </summary>
        byte TakeColor(int ownerId)
        {
            if (_colorByOwner.TryGetValue(ownerId, out byte existing)) return existing;

            int paletteSize = PlayerIdentity.Palette.Length;
            for (byte candidate = 0; candidate < paletteSize; candidate++)
            {
                if (_colorByOwner.ContainsValue(candidate)) continue;
                if (IsWornByAbandonedBody(candidate)) continue;

                _colorByOwner[ownerId] = candidate;
                return candidate;
            }

            // More players than colours. Wrapping is better than refusing to spawn them.
            var wrapped = (byte)(ownerId % paletteSize);
            _colorByOwner[ownerId] = wrapped;
            return wrapped;
        }

        /// <summary>
        /// True when a body lying on the ground with no owner still has this colour. Cheap: the
        /// list holds corpses, not players, and it is empty in the common case.
        /// </summary>
        static bool IsWornByAbandonedBody(byte colorIndex)
        {
            foreach (BodyPersistence body in BodyPersistence.Abandoned)
            {
                if (body == null) continue;

                var identity = body.GetComponent<PlayerIdentity>();
                if (identity != null && identity.ColorIndex == colorIndex) return true;
            }

            return false;
        }

        /// <summary>
        /// Hands the spawner the spawn points of the gameplay scene that just loaded, or null when
        /// that scene is going away. Called by SceneSpawnPoints rather than wired in an inspector,
        /// because Unity has no cross-scene references and the spawner lives in Bootstrap while the
        /// points live in the arena or on the island.
        /// </summary>
        public void UseSpawnPoints(Transform[] points, string source)
        {
            _spawnPoints = points;

            Debug.Log(points == null || points.Length == 0
                ? $"[PlayerSpawner] {source} took its spawn points away; falling back to the ring."
                : $"[PlayerSpawner] Using {points.Length} spawn points from {source}.");
        }

        /// <summary>
        /// Where the body with this index belongs. Public because respawning is the same question as
        /// spawning, asked later.
        /// </summary>
        public void GetSpawn(int index, out Vector3 position, out Quaternion rotation)
        {
            if (index < 0) index = 0;

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
