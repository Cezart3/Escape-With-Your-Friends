using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.World;
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Scened;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EscapeWithYourFriends.Net
{
    /// <summary>
    /// Loads the map everyone plays on, once, when the server starts.
    ///
    /// Bootstrap holds the NetworkManager and nothing else, so something has to decide what the game
    /// is actually played in. That is a server decision by definition - four clients cannot each pick
    /// their own island - so this runs on the server and loads the scene as a **global** scene, which
    /// is FishNet's word for "every connection gets this one, including the ones that arrive later".
    ///
    /// The order it produces is the order everything downstream assumes:
    ///   server starts -> global scene loads -> a client connects -> that client loads the global
    ///   scenes -> OnClientLoadedStartScenes -> PlayerSpawner spawns a body.
    ///
    /// Which means the island's spawn points, its POIs and its water are all in place before anybody
    /// has a body to put on them, without a single "wait until" anywhere.
    ///
    ///   -scene island   the real map (default)
    ///   -scene arena    the M1 greybox, for testing combat without a kilometre of terrain
    ///   -scene island2  the second island: half the size, twice the fog, natives that do not flee
    ///   -scene none     load nothing, which is what the old Bootstrap-only smoke tests expect
    /// </summary>
    public class GameSceneLoader : MonoBehaviour
    {
        [Tooltip("Scene loaded when nothing is asked for on the command line.")]
        [SerializeField] string _defaultScene = "Island";

        [Tooltip("Scene names this is allowed to load. Anything else is refused rather than guessed at.")]
        [SerializeField] string[] _known = { "Island", "Arena", "Island2" };

        NetworkManager _manager;
        bool _loaded;
        bool _travelling;

        /// <summary>What was actually loaded, for logs and for anything that behaves differently per map.</summary>
        public static string Current { get; private set; } = "";

        /// <summary>The one in the scene, so a boat can ask it to sail somewhere. #69.</summary>
        public static GameSceneLoader Instance { get; private set; }

        /// <summary>
        /// Where a boat leaving the map from here would arrive, or null if this map is not one of
        /// the two islands. Two entries hard-coded, because there are two islands and a table of one
        /// pair is a table nobody would ever add a third row to.
        /// </summary>
        public static string Crossing
            => Current == "Island" ? "Island2"
             : Current == "Island2" ? "Island"
             : null;

        /// <summary>True between casting off and everybody standing on the other beach.</summary>
        public bool Travelling => _travelling;

        void Awake()
        {
            Instance = this;
            _manager = InstanceFinder.NetworkManager;
            if (_manager == null)
            {
                Debug.LogError("[GameSceneLoader] No NetworkManager; no map will be loaded.");
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
            if (args.ConnectionState == LocalConnectionState.Started) Load();
            else if (args.ConnectionState == LocalConnectionState.Stopped) _loaded = false;
        }

        void Load()
        {
            if (_loaded) return;
            _loaded = true;

            string requested = CommandLine.GetString("-scene", _defaultScene);

            if (string.Equals(requested, "none", System.StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log("[GameSceneLoader] -scene none; loading no map.");
                return;
            }

            string scene = Resolve(requested);
            if (scene == null)
            {
                Debug.LogError($"[GameSceneLoader] '{requested}' is not a map this build knows about. "
                               + $"Known: {string.Join(", ", _known)}. Loading nothing.");
                return;
            }

            // Global rather than per-connection: everyone is on the same island, and a late joiner
            // gets it on connect without anybody having to remember to send it to them.
            //
            // The island is made the ACTIVE scene, which is not cosmetic. Unity puts an Instantiate
            // with no parent into whatever scene is active, and everything this project builds at
            // runtime - every POI, the animals, the natives, the dropped items - is instantiated
            // that way. Left on Bootstrap they would all belong to a scene that never unloads, and
            // #69's first working crossing found exactly that: the boat you left behind was still
            // afloat on the other island because it was never in the island scene at all.
            var data = new SceneLoadData(scene)
            {
                ReplaceScenes = ReplaceOption.None,
                PreferredActiveScene = new PreferredScene(new SceneLookupData(scene))
            };
            _manager.SceneManager.LoadGlobalScenes(data);

            Current = scene;
            Debug.Log($"[GameSceneLoader] Loading '{scene}' as a global scene for every connection.");
        }

        // ------------------------------------------------------------------ #69, the crossing

        /// <summary>
        /// Server only. Moves the whole session to another map, players and all.
        ///
        /// The islands are two scenes, so travel is a swap rather than streaming: nothing on one is
        /// ever visible from the other, and loading both to slide between them would be paying for a
        /// view nobody gets.
        ///
        /// Three things make it work, and all three are FishNet's rather than mine:
        ///
        /// * <c>MovedNetworkObjects</c> carries the player bodies into the new scene before the old
        ///   one goes. Without it they are despawned with the scene they were living in, which is the
        ///   island, because <see cref="PlayerSpawner"/> puts them there on purpose so their owners
        ///   observe them.
        /// * <c>ReplaceOption.OnlineOnly</c> unloads what FishNet loaded and leaves Bootstrap alone,
        ///   which is where the NetworkManager, the spawner and this component live.
        /// * the boat is a scene object and cannot be moved, which is not a limitation here but the
        ///   design: each island keeps its own hull at its own mooring, so there is always something
        ///   to leave on.
        ///
        /// Anybody sitting in a vehicle is put out of it first. You cannot take the boat with you,
        /// and a rider glued to an anchor in a scene that no longer exists is a body nobody can move.
        /// </summary>
        public bool ServerTravel(string requested)
        {
            if (_manager == null || !_manager.ServerManager.Started)
            {
                Debug.LogWarning("[GameSceneLoader] Nothing to travel from; there is no server.");
                return false;
            }

            if (_travelling) return false;

            // The demo is the first island. Every way off it - boat or plane - ends it here. #90.
            if (Core.Demo.On)
            {
                Debug.Log($"[GameSceneLoader] Demo: leaving {Current} for '{requested}' ends the run.");
                World.RunSummary.ServerEnd();
                return true;
            }

            string scene = Resolve(requested);
            if (scene == null || scene == Current)
            {
                Debug.LogWarning($"[GameSceneLoader] '{requested}' is not somewhere this build can sail to.");
                return false;
            }

            var moving = new List<NetworkObject>();

            foreach (NetworkConnection connection in _manager.ServerManager.Clients.Values)
            {
                if (connection == null) continue;

                // Copied before it is walked: ownership of a vehicle follows the wheel, so putting
                // the driver out of the boat hands the hull back and takes it out of this very set.
                foreach (NetworkObject owned in new List<NetworkObject>(connection.Objects))
                {
                    if (owned == null) continue;

                    var motor = owned.GetComponent<PlayerMotor>();
                    if (motor == null) continue;

                    // Off the boat before the boat stops existing.
                    var rider = owned.GetComponent<Vehicles.VehicleRider>();
                    if (rider != null && rider.IsSeated) rider.Vehicle.ServerExit(owned);

                    moving.Add(owned);
                }
            }

            var data = new SceneLoadData(scene)
            {
                ReplaceScenes = ReplaceOption.OnlineOnly,
                MovedNetworkObjects = moving.ToArray(),
                PreferredActiveScene = new PreferredScene(new SceneLookupData(scene))
            };

            _travelling = true;
            _from = Current;
            _manager.SceneManager.OnLoadEnd += OnTravelled;
            _manager.SceneManager.LoadGlobalScenes(data);

            Debug.Log($"[GameSceneLoader] Leaving {Current} for {scene} with {moving.Count} player(s) aboard.");
            return true;
        }

        string _from = "";

        void OnTravelled(SceneLoadEndEventArgs args)
        {
            if (!_travelling || !args.QueueData.AsServer) return;

            _manager.SceneManager.OnLoadEnd -= OnTravelled;
            _travelling = false;

            Scene arrived = default;
            foreach (Scene loaded in args.LoadedScenes)
                if (loaded.IsValid()) { arrived = loaded; break; }

            if (!arrived.IsValid())
            {
                Debug.LogError($"[GameSceneLoader] The crossing from {_from} loaded nothing; "
                               + "everybody is still where they were.");
                return;
            }

            Current = arrived.name;
            Ashore(arrived);
        }

        /// <summary>
        /// Puts the arrivals on the new island's spawn points.
        ///
        /// The points are read out of the scene that just loaded rather than from the spawner's
        /// registry, because the old island's <see cref="SceneSpawnPoints"/> hands the spawner a null
        /// on its way out and the order of that against this is not worth depending on. Handing them
        /// back afterwards is what makes the next death respawn on the right island.
        /// </summary>
        void Ashore(Scene arrived)
        {
            Transform[] points = null;
            string source = arrived.name;

            foreach (SceneSpawnPoints spawn in FindObjectsByType<SceneSpawnPoints>(FindObjectsSortMode.None))
            {
                if (spawn == null || spawn.gameObject.scene != arrived) continue;
                points = spawn.Points;
                source = spawn.name;
                break;
            }

            if (PlayerSpawner.Instance != null && points != null && points.Length > 0)
                PlayerSpawner.Instance.UseSpawnPoints(points, source);

            int placed = 0;

            foreach (NetworkConnection connection in _manager.ServerManager.Clients.Values)
            {
                if (connection == null) continue;

                foreach (NetworkObject owned in new List<NetworkObject>(connection.Objects))
                {
                    var motor = owned != null ? owned.GetComponent<PlayerMotor>() : null;
                    if (motor == null) continue;

                    Vector3 position;
                    Quaternion rotation;

                    if (points != null && points.Length > 0)
                    {
                        Transform point = points[placed % points.Length];
                        position = point.position;
                        rotation = point.rotation;
                    }
                    else if (PlayerSpawner.Instance != null)
                    {
                        PlayerSpawner.Instance.GetSpawn(placed, out position, out rotation);
                    }
                    else continue;

                    motor.ServerTeleport(position, rotation.eulerAngles.y);
                    placed++;
                }
            }

            // ReplaceOption is meant to take the island you left with it, and it does not always:
            // FishNet keeps a scene it still has connections registered in, and the first crossing
            // that worked left the whole of the first island - its boat included - loaded behind us.
            // So the old one is asked to go by name. Doing it here rather than in the load is what
            // makes it safe: everybody is already standing on the new island by now.
            Scene left = UnityEngine.SceneManagement.SceneManager.GetSceneByName(_from);
            if (left.IsValid() && left.isLoaded && left != arrived)
                _manager.SceneManager.UnloadGlobalScenes(new SceneUnloadData(_from));

            var loaded = new List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                loaded.Add(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name);

            Debug.Log($"[GameSceneLoader] {_from} -> {Current}: {placed} player(s) ashore"
                      + $" on {(points != null ? points.Length : 0)} spawn point(s) from {source};"
                      + $" scenes loaded: {string.Join(", ", loaded)}.");
        }

        string Resolve(string requested)
        {
            foreach (string known in _known)
                if (string.Equals(known, requested, System.StringComparison.OrdinalIgnoreCase)) return known;

            return null;
        }
    }
}
