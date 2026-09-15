using System;
using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.World;
using FishNet;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.AI
{
    /// <summary>
    /// Server side. Keeps the camps manned.
    ///
    /// Same family as <see cref="AnimalSpawner"/> - a baked list of places, a top-up loop rather than
    /// a spawn-once pass, and a hold-down after a death so a camp cannot be farmed by standing in it.
    /// Two things are different, and both come out of #55's acceptance:
    ///
    /// **A camp has a roster, not a population.** A zone of boars is "five boars"; a camp is "two
    /// spearmen, one blowgunner and a scout", because the fight you get is decided by who is standing
    /// in it. Each line tops up on its own, so killing the blowgunner buys you a quiet minute against
    /// exactly the role you removed.
    ///
    /// **The camp grows at night.** <see cref="NativeSpawner.Camp.NightExtra"/> is how many more of
    /// that line the camp keeps once the sun is down - the ambient half of "a real threat at night".
    /// The extra bodies are not spawned on top of you; they are spawned in the camp like everybody
    /// else and walk out, so what actually reaches you is a patrol that got bigger, not a surprise.
    ///
    /// The clearance rule is the same as the wildlife's and for the same reason: a native you watch
    /// appear is a spawner, and a native you walk into is an ambush.
    /// </summary>
    public class NativeSpawner : MonoBehaviour
    {
        /// <summary>One role's worth of one camp.</summary>
        [Serializable]
        public class Camp
        {
            [Tooltip("For logs. Nothing keys off it.")]
            public string Id = "camp";

            public NativeDef Role;

            [Tooltip("Centre in world space. Baked from the island's POIs, so it moves with the seed.")]
            public Vector3 Centre;

            [Tooltip("Metres out from the centre bodies are placed in.")]
            public float Radius = 22f;

            [Tooltip("How many of this role the camp keeps in daylight.")]
            public int Population = 2;

            [Tooltip("How many more it keeps once it is dark.")]
            public int NightExtra = 1;

            [Tooltip("Whether this camp has stores in it. Village yes, cave outpost no; decides which "
                     + "of a role's two loot tables its bodies roll.")]
            public bool Stocked;

            /// <summary>What this line is trying to hold right now, given how dark it is.</summary>
            public int Wanted(float night01) => Population + Mathf.RoundToInt(NightExtra * Mathf.Clamp01(night01));
        }

        [Tooltip("Baked by NativeFactory from the island profile. Regenerated whole; do not hand-edit.")]
        [SerializeField] Camp[] _camps = Array.Empty<Camp>();

        [Tooltip("The one native prefab. Every role wears it.")]
        [SerializeField] NetworkObject _prefab;

        [Tooltip("Every role, so a camp's definition can be turned into a wire index.")]
        [SerializeField] NativeCatalog _catalog;

        [Header("Rules")]
        [Tooltip("Seconds between top-up passes. Also how long the loop waits for a NavMesh to appear.")]
        [SerializeField] float _tickSeconds = 5f;

        [Tooltip("Seconds after a death before that camp refills the gap.")]
        [SerializeField] float _respawnSeconds = 60f;

        [Tooltip("Metres a spawn point must be from every player. One you watch appear is a spawner.")]
        [SerializeField] float _playerClearance = 45f;

        [Tooltip("Attempts at finding standable ground before a camp gives up for this tick.")]
        [SerializeField] int _attempts = 12;

        [Tooltip("Off to leave the island empty. -noNatives sets this from the command line.")]
        [SerializeField] bool _enabled = true;

        NetworkManager _manager;
        float _nextTick;

        readonly Dictionary<Camp, List<Native>> _byCamp = new();
        readonly Dictionary<Camp, float> _blockedUntil = new();

        int _spawnedTotal;

        /// <summary>The spawner in the loaded island scene. Null in the arena, which has no villages.</summary>
        public static NativeSpawner Instance { get; private set; }

        /// <summary>How many natives this spawner has put into the world since the server started.</summary>
        public int SpawnedTotal => _spawnedTotal;

        public IReadOnlyList<Camp> Camps => _camps;

        void Awake()
        {
            Instance = this;

            // Every other harness in the project wants an empty island: a spearman wandering through
            // a melee test is a variable nobody asked for.
            if (CommandLine.HasFlag("-noNatives")) _enabled = false;

            NativeCatalog.Use(_catalog);

            _manager = InstanceFinder.NetworkManager;
            if (_manager == null)
            {
                Debug.LogWarning("[NativeSpawner] No NetworkManager yet; waiting for one.");
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

            _byCamp.Clear();
            _blockedUntil.Clear();
            _spawnedTotal = 0;
        }

        void Update()
        {
            if (!_enabled || _manager == null || !_manager.ServerManager.Started) return;
            if (_prefab == null || Time.time < _nextTick) return;

            _nextTick = Time.time + _tickSeconds;

            float night = WorldClock.Night01;

            foreach (Camp camp in _camps)
            {
                if (camp == null || camp.Role == null) continue;

                Top(camp, night);
            }
        }

        void Top(Camp camp, float night)
        {
            List<Native> live = Live(camp);
            if (live.Count >= camp.Wanted(night)) return;

            if (_blockedUntil.TryGetValue(camp, out float until) && Time.time < until) return;

            if (!Ground(camp, out Vector3 position)) return;

            Native native = ServerSpawn(camp.Role, position, camp.Centre, camp.Stocked);
            if (native == null) return;

            live.Add(native);

            Debug.Log($"[NativeSpawner] {camp.Role.Id} into {camp.Id} at {position}; "
                      + $"{live.Count}/{camp.Wanted(night)} there (night {night:F2}).");
        }

        /// <summary>
        /// Puts one native on the ground at a point, ignoring camps, rosters and clearance. Server
        /// only, and public for the same reason the wildlife's version is: a test that waits for a
        /// village to notice it is a test that measures the weather.
        /// </summary>
        public Native ServerSpawn(NativeDef role, Vector3 position, Vector3 camp, bool stocked = false)
        {
            if (role == null || _prefab == null) return null;
            if (_manager == null || !_manager.ServerManager.Started) return null;

            if (NavMesh.SamplePosition(position, out NavMeshHit hit, 25f, NavMesh.AllAreas))
                position = hit.position;

            NetworkObject instance = Instantiate(_prefab, position,
                                                 Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));

            var native = instance.GetComponent<Native>();
            if (native == null)
            {
                Debug.LogError("[NativeSpawner] The native prefab has no Native component; disabling.");
                Destroy(instance.gameObject);
                _enabled = false;
                return null;
            }

            // Before the spawn, so the role index rides along in the spawn message and no client ever
            // sees an unpainted body.
            native.ServerConfigure(role, camp, stocked);

            _manager.ServerManager.Spawn(instance);
            _spawnedTotal++;

            return native;
        }

        /// <summary>The camp's natives, minus anything that has since died or despawned.</summary>
        List<Native> Live(Camp camp)
        {
            if (!_byCamp.TryGetValue(camp, out List<Native> live))
            {
                live = new List<Native>();
                _byCamp[camp] = live;
            }

            for (int i = live.Count - 1; i >= 0; i--)
            {
                Native native = live[i];
                bool gone = native == null || native.NetworkObject == null || !native.NetworkObject.IsSpawned
                            || native.State == NativeState.Dead;

                if (!gone) continue;

                live.RemoveAt(i);
                _blockedUntil[camp] = Time.time + _respawnSeconds;
            }

            return live;
        }

        /// <summary>Somewhere in the camp that is on the NavMesh and not in anybody's face.</summary>
        bool Ground(Camp camp, out Vector3 position)
        {
            for (int i = 0; i < _attempts; i++)
            {
                Vector2 offset = UnityEngine.Random.insideUnitCircle * camp.Radius;
                Vector3 candidate = camp.Centre + new Vector3(offset.x, 0f, offset.y);

                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 25f, NavMesh.AllAreas)) continue;
                if (!Clear(hit.position)) continue;

                position = hit.position;
                return true;
            }

            position = camp.Centre;
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

        /// <summary>Bake time only, and the harness's door to a camp it controls.</summary>
        public void Configure(Camp[] camps, NetworkObject prefab, NativeCatalog catalog)
        {
            _camps = camps ?? Array.Empty<Camp>();
            _prefab = prefab;
            _catalog = catalog;
        }
    }
}
