using System;
using System.Collections.Generic;
using FishNet;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// Server side. Puts the world's fixed objects into the session when the host starts.
    ///
    /// The Revive Machine is the first thing in this game that is *part of the map* rather than part
    /// of a player, and it raises a question the project has not had to answer yet: does a networked
    /// prop live in the scene, or is it spawned? Scene objects are the obvious answer and the wrong
    /// one here. FishNet identifies them by a scene id baked at save time, and every scene this
    /// project has is written by an editor script running in batchmode — a path where that baking is
    /// unproven. Spawning from a registered prefab is the path the player body already proves works
    /// every time a client connects, so props take it too.
    ///
    /// That makes this the honest first draft of the POI spawner M2 needs (#31): the shop, the
    /// casino, the native village and the wreck are all "a prefab at a position", and the only thing
    /// that changes is where the list comes from — a serialized array now, a generated layout later.
    ///
    /// A MonoBehaviour, not a NetworkBehaviour, for the same reason
    /// <see cref="Net.PlayerSpawner"/> is one: it lives on the NetworkManager object and reacts to
    /// server state, and something that spawns networked objects does not need to be one.
    /// </summary>
    public class WorldSpawner : MonoBehaviour
    {
        [Serializable]
        public class Placement
        {
            [Tooltip("Must be registered in the spawnable prefabs list, like the player prefab.")]
            public NetworkObject Prefab;

            public Vector3 Position;
            public Vector3 Euler;
        }

        [Tooltip("What the world contains. Order is not meaningful.")]
        [SerializeField] Placement[] _placements = Array.Empty<Placement>();

        NetworkManager _manager;

        readonly List<NetworkObject> _spawned = new();

        /// <summary>Everything this spawner put into the world. Server-side only; empty on clients.</summary>
        public IReadOnlyList<NetworkObject> Spawned => _spawned;

        /// <summary>The spawner in the loaded scene, for anything that needs to find world props.</summary>
        public static WorldSpawner Instance { get; private set; }

        void Awake()
        {
            Instance = this;

            _manager = GetComponentInParent<NetworkManager>();
            if (_manager == null) _manager = InstanceFinder.NetworkManager;

            if (_manager == null)
            {
                Debug.LogError("[WorldSpawner] No NetworkManager found; disabling.");
                enabled = false;
                return;
            }

            _manager.ServerManager.OnServerConnectionState += OnServerConnectionState;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_manager == null) return;

            _manager.ServerManager.OnServerConnectionState -= OnServerConnectionState;
        }

        void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started) SpawnWorld();
            else if (args.ConnectionState == LocalConnectionState.Stopped) _spawned.Clear();
        }

        void SpawnWorld()
        {
            // Started can be raised more than once across a process's life; spawning the shop twice
            // would be funny exactly once.
            if (_spawned.Count > 0) return;

            foreach (Placement placement in _placements)
            {
                if (placement == null || placement.Prefab == null) continue;

                NetworkObject instance = _manager.GetPooledInstantiated(
                    placement.Prefab, placement.Position, Quaternion.Euler(placement.Euler),
                    asServer: true);

                // No owner: a world prop belongs to the session, not to whoever started it. When the
                // host migrates (far future) an ownerless object is the one that survives.
                _manager.ServerManager.Spawn(instance);

                _spawned.Add(instance);

                Debug.Log($"[WorldSpawner] Spawned {placement.Prefab.name} at {placement.Position}.");
            }

            Debug.Log($"[WorldSpawner] World ready: {_spawned.Count} object(s).");
        }
    }
}
