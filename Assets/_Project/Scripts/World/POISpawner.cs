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
    /// Server side. Puts the island's points of interest into the session.
    ///
    /// This is what <see cref="WorldSpawner"/> was the first draft of. The difference is where the
    /// list comes from: WorldSpawner holds an array somebody wired into a scene, this holds a baked
    /// copy of <see cref="POICatalog"/>, which is a text asset anyone can append to from a terminal.
    ///
    /// The prefabs are resolved at bake time rather than looked up here, because a runtime lookup by
    /// path only works in the editor - a built player has no AssetDatabase - and Resources folders
    /// are a load-everything-always tax this project does not need to pay.
    ///
    /// Spawned rather than placed in the scene, same as the revive machine and for the same reason:
    /// FishNet identifies scene objects by an id baked at save time, and every scene here is written
    /// by an editor script in batchmode, which is a path where that baking is unproven. Spawning
    /// from a registered prefab is the path that already works every time a client connects.
    /// </summary>
    public class POISpawner : MonoBehaviour
    {
        [Serializable]
        public class Placement
        {
            [Tooltip("Which catalog entry this came from. For logs; nothing keys off it.")]
            public string Id;

            [Tooltip("Resolved from POIEntry.PrefabPath at bake time. Must be in the spawnable prefabs list.")]
            public NetworkObject Prefab;

            public Vector3 Position;
            public Vector3 Euler;
        }

        [Tooltip("Baked from the POI catalog. Regenerated whole on every island build; do not hand-edit.")]
        [SerializeField] Placement[] _placements = Array.Empty<Placement>();

        [Tooltip("The catalog these came from, for reference and for anything that wants the pads at run time.")]
        [SerializeField] POICatalog _catalog;

        NetworkManager _manager;
        bool _spawned;

        readonly List<NetworkObject> _live = new();

        /// <summary>Everything this spawner put into the world. Server-side only; empty on clients.</summary>
        public IReadOnlyList<NetworkObject> Spawned => _live;

        /// <summary>The spawner in the loaded island scene, for anything that needs to find a POI.</summary>
        public static POISpawner Instance { get; private set; }

        /// <summary>The catalog the island was built from. Null before the island scene is loaded.</summary>
        public POICatalog Catalog => _catalog;

        void Awake()
        {
            Instance = this;

            _manager = InstanceFinder.NetworkManager;
            if (_manager == null)
            {
                Debug.LogWarning("[POISpawner] No NetworkManager yet; waiting for one.");
                return;
            }

            _manager.ServerManager.OnServerConnectionState += OnServerState;
        }

        void Start()
        {
            // The island is loaded as a scene after the server is already up, so the state event that
            // would have started this has been and gone. Both paths are needed: this one for the
            // normal case, the event for a scene that happens to be loaded before the server starts.
            if (_manager != null && _manager.ServerManager.Started) SpawnAll();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_manager != null) _manager.ServerManager.OnServerConnectionState -= OnServerState;
        }

        void OnServerState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started) SpawnAll();
            else if (args.ConnectionState == LocalConnectionState.Stopped) _spawned = false;
        }

        void SpawnAll()
        {
            if (_spawned) return;
            _spawned = true;

            int placed = 0;
            int skipped = 0;

            foreach (Placement placement in _placements)
            {
                if (placement == null || placement.Prefab == null)
                {
                    skipped++;
                    continue;
                }

                NetworkObject instance = Instantiate(placement.Prefab, placement.Position,
                                                     Quaternion.Euler(placement.Euler));
                instance.name = placement.Prefab.name + " (" + placement.Id + ")";

                _manager.ServerManager.Spawn(instance);
                _live.Add(instance);
                placed++;
            }

            Debug.Log($"[POISpawner] Placed {placed} points of interest"
                      + (skipped > 0 ? $", skipped {skipped} with no prefab" : "") + ".");
        }

        /// <summary>Where a named POI stands, for spawn points and objectives. Zero if it is not in the list.</summary>
        public Vector3 PositionOf(string id)
        {
            foreach (Placement placement in _placements)
                if (placement != null && placement.Id == id) return placement.Position;

            return Vector3.zero;
        }
    }
}
