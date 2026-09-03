using EscapeWithYourFriends.Core;
using FishNet;
using FishNet.Managing;
using FishNet.Managing.Scened;
using FishNet.Transporting;
using UnityEngine;

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
    ///   -scene none     load nothing, which is what the old Bootstrap-only smoke tests expect
    /// </summary>
    public class GameSceneLoader : MonoBehaviour
    {
        [Tooltip("Scene loaded when nothing is asked for on the command line.")]
        [SerializeField] string _defaultScene = "Island";

        [Tooltip("Scene names this is allowed to load. Anything else is refused rather than guessed at.")]
        [SerializeField] string[] _known = { "Island", "Arena" };

        NetworkManager _manager;
        bool _loaded;

        /// <summary>What was actually loaded, for logs and for anything that behaves differently per map.</summary>
        public static string Current { get; private set; } = "";

        void Awake()
        {
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
            var data = new SceneLoadData(scene) { ReplaceScenes = ReplaceOption.None };
            _manager.SceneManager.LoadGlobalScenes(data);

            Current = scene;
            Debug.Log($"[GameSceneLoader] Loading '{scene}' as a global scene for every connection.");
        }

        string Resolve(string requested)
        {
            foreach (string known in _known)
                if (string.Equals(known, requested, System.StringComparison.OrdinalIgnoreCase)) return known;

            return null;
        }
    }
}
