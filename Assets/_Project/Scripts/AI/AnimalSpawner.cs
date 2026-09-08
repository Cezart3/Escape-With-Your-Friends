using System;
using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Net;
using FishNet;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.AI
{
    /// <summary>
    /// Server side. Keeps the island populated.
    ///
    /// Same family as <see cref="World.POISpawner"/> - it lives in the island scene, it spawns from a
    /// registered prefab rather than relying on scene objects, and its list is baked by an editor
    /// step. The difference is that a point of interest is placed once and an animal population is a
    /// **level**: something kills a boar, and a few minutes later there is another boar. So this is a
    /// top-up loop rather than a spawn-everything-once pass.
    ///
    /// The loop is also, deliberately, the thing that waits for the NavMesh. The island arrives as a
    /// global scene after the server is already running, so at the moment the server starts there is
    /// nowhere to put an animal. Rather than a "wait until the scene is loaded" special case, the
    /// tick simply fails to find ground and tries again a few seconds later, which is the same code
    /// path as a zone that is temporarily crowded. Fewer branches, and no ordering assumption that
    /// can rot.
    ///
    /// Animals are kept away from players when they spawn. Not for fairness - for the illusion: a
    /// deer that pops into existence eight metres in front of you is a spawner, and a deer you find
    /// is an animal.
    /// </summary>
    public class AnimalSpawner : MonoBehaviour
    {
        /// <summary>A population of one species in one area of the island.</summary>
        [Serializable]
        public class Zone
        {
            [Tooltip("For logs. Nothing keys off it.")]
            public string Id = "zone";

            public AnimalDef Species;

            [Tooltip("Centre in world space. Baked from the island's POIs, so it moves with the seed.")]
            public Vector3 Centre;

            [Tooltip("Metres out from the centre animals are placed in.")]
            public float Radius = 60f;

            [Tooltip("How many of this species this zone tries to keep alive.")]
            public int Population = 4;
        }

        [Tooltip("Baked by AnimalFactory from the island profile. Regenerated whole; do not hand-edit.")]
        [SerializeField] Zone[] _zones = Array.Empty<Zone>();

        [Tooltip("The one animal prefab. Every species wears it.")]
        [SerializeField] NetworkObject _prefab;

        [Tooltip("Every species, so a zone's definition can be turned into a wire index.")]
        [SerializeField] AnimalCatalog _catalog;

        [Header("Rules")]
        [Tooltip("Seconds between top-up passes. Also how long the loop waits for a NavMesh to appear.")]
        [SerializeField] float _tickSeconds = 5f;

        [Tooltip("Seconds after a death before that zone refills the gap.")]
        [SerializeField] float _respawnSeconds = 45f;

        [Tooltip("Metres a spawn point must be from every player. An animal you watch appear is a spawner.")]
        [SerializeField] float _playerClearance = 40f;

        [Tooltip("Attempts at finding standable ground before a zone gives up for this tick.")]
        [SerializeField] int _attempts = 12;

        [Tooltip("Off to leave the island empty. -noAnimals sets this from the command line.")]
        [SerializeField] bool _enabled = true;

        NetworkManager _manager;
        float _nextTick;

        readonly Dictionary<Zone, List<Animal>> _byZone = new();
        readonly Dictionary<Zone, float> _blockedUntil = new();

        int _spawnedTotal;

        /// <summary>The spawner in the loaded island scene. Null in the arena, which has no wildlife.</summary>
        public static AnimalSpawner Instance { get; private set; }

        /// <summary>How many animals this spawner has put into the world since the server started.</summary>
        public int SpawnedTotal => _spawnedTotal;

        public IReadOnlyList<Zone> Zones => _zones;

        void Awake()
        {
            Instance = this;

            // The tooltip on _enabled promises this flag, so it has to exist. An empty island is
            // what every other harness in this project wants: a boar wandering through a melee test
            // is a variable nobody asked for.
            if (CommandLine.HasFlag("-noAnimals")) _enabled = false;

            AnimalCatalog.Use(_catalog);

            _manager = InstanceFinder.NetworkManager;
            if (_manager == null)
            {
                Debug.LogWarning("[AnimalSpawner] No NetworkManager yet; waiting for one.");
                return;
            }

            _manager.ServerManager.OnServerConnectionState += OnServerState;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_manager != null) _manager.ServerManager.OnServerConnectionState -= OnServerState;
        }

        void OnServerState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Stopped) return;

            _byZone.Clear();
            _blockedUntil.Clear();
            _spawnedTotal = 0;
        }

        void Update()
        {
            if (!_enabled || _manager == null || !_manager.ServerManager.Started) return;
            if (_prefab == null || Time.time < _nextTick) return;

            _nextTick = Time.time + _tickSeconds;

            foreach (Zone zone in _zones)
            {
                if (zone == null || zone.Species == null || zone.Population <= 0) continue;

                Top(zone);
            }
        }

        void Top(Zone zone)
        {
            List<Animal> live = Live(zone);
            if (live.Count >= zone.Population) return;

            // A death holds the zone shut for a while. Without it the top-up is instant, and hunting
            // becomes standing in one place killing the same boar every five seconds - which would
            // pass the acceptance criterion and ruin the thing it is measuring.
            if (_blockedUntil.TryGetValue(zone, out float until) && Time.time < until) return;

            if (!Ground(zone, out Vector3 position)) return;

            Animal animal = ServerSpawn(zone.Species, position);
            if (animal == null) return;

            live.Add(animal);

            Debug.Log($"[AnimalSpawner] {zone.Species.Id} into {zone.Id} at {position}; "
                      + $"{live.Count}/{zone.Population} alive there.");
        }

        /// <summary>
        /// Puts one animal of a species on the ground at a point, ignoring zones, populations and
        /// clearance. Server only.
        ///
        /// Public because the harness needs a door: a test that waits for the island's own
        /// populations to wander into shot is a test that measures the wind, not the animal. The
        /// zone loop goes through here too, so there is one place that knows how a body is built and
        /// no second copy to drift.
        /// </summary>
        public Animal ServerSpawn(AnimalDef species, Vector3 position)
        {
            if (species == null || _prefab == null) return null;
            if (_manager == null || !_manager.ServerManager.Started) return null;

            if (NavMesh.SamplePosition(position, out NavMeshHit hit, 25f, NavMesh.AllAreas))
                position = hit.position;

            NetworkObject instance = Instantiate(_prefab, position,
                                                 Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));

            var animal = instance.GetComponent<Animal>();
            if (animal == null)
            {
                Debug.LogError("[AnimalSpawner] The animal prefab has no Animal component; disabling.");
                Destroy(instance.gameObject);
                _enabled = false;
                return null;
            }

            // Before the spawn, so the species index rides along in the spawn message and a client
            // never sees an unshaped body.
            animal.ServerConfigure(species, position);

            _manager.ServerManager.Spawn(instance);
            _spawnedTotal++;

            return animal;
        }

        /// <summary>
        /// The zone's animals, minus anything that has since died or despawned. Rebuilt by filtering
        /// rather than by an event, because a despawn can happen for reasons this spawner never hears
        /// about - a scene unload, a host shutdown - and a list that only shrinks on notification is a
        /// list that eventually claims a full island of ghosts.
        /// </summary>
        List<Animal> Live(Zone zone)
        {
            if (!_byZone.TryGetValue(zone, out List<Animal> live))
            {
                live = new List<Animal>();
                _byZone[zone] = live;
            }

            for (int i = live.Count - 1; i >= 0; i--)
            {
                Animal animal = live[i];
                bool gone = animal == null || animal.NetworkObject == null || !animal.NetworkObject.IsSpawned
                            || animal.State == AnimalState.Dead;

                if (!gone) continue;

                live.RemoveAt(i);
                _blockedUntil[zone] = Time.time + _respawnSeconds;
            }

            return live;
        }

        /// <summary>Somewhere in the zone that is on the NavMesh and not in anybody's face.</summary>
        bool Ground(Zone zone, out Vector3 position)
        {
            for (int i = 0; i < _attempts; i++)
            {
                Vector2 offset = UnityEngine.Random.insideUnitCircle * zone.Radius;
                Vector3 candidate = zone.Centre + new Vector3(offset.x, 0f, offset.y);

                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 25f, NavMesh.AllAreas)) continue;
                if (!Clear(hit.position)) continue;

                position = hit.position;
                return true;
            }

            position = zone.Centre;
            return false;
        }

        bool Clear(Vector3 position)
        {
            foreach (NetworkPlayerRegistry.PlayerBody body in NetworkPlayerRegistry.Players)
            {
                if (!body.IsValid) continue;
                if (Vector3.Distance(position, body.Object.transform.position) < _playerClearance) return false;
            }

            return true;
        }

        /// <summary>
        /// Bake time only. Called by the factory; also used by the harness to build a zone it can
        /// control, because a test that waits for the island's own populations to wander into shot is
        /// a test that measures the wind.
        /// </summary>
        public void Configure(Zone[] zones, NetworkObject prefab, AnimalCatalog catalog)
        {
            _zones = zones ?? Array.Empty<Zone>();
            _prefab = prefab;
            _catalog = catalog;
        }
    }
}
