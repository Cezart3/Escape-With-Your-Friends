using System;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace EscapeWithYourFriends.Net
{
    /// <summary>
    /// Starts the network connection from command-line arguments.
    ///
    /// This exists because the whole development loop for this project is a terminal, and a build
    /// that can only be started by clicking a button in a menu cannot be tested from one. With this,
    /// four headless clients can be launched from a script and the combat code can be exercised
    /// without the editor and without four people.
    ///
    /// Arguments:
    ///   -host                  server and client in one process (what a real session uses)
    ///   -server                dedicated server, no local player
    ///   -client                client only
    ///   -address 192.168.1.5   who to connect to (client only, default 127.0.0.1)
    ///   -port 7770             port to bind or connect to
    ///   -quitAfter 30          exit after this many seconds, for automated smoke tests
    ///
    /// With no arguments it does nothing and waits for the lobby to start the connection, which is
    /// what a shipped build does.
    /// </summary>
    public class NetworkBootstrap : MonoBehaviour
    {
        [Header("Defaults when no arguments are given")]
        [SerializeField] string _defaultAddress = "127.0.0.1";
        [SerializeField] ushort _defaultPort = 7770;

        [Tooltip("Start hosting on play even without -host. Convenient in the editor, off in builds.")]
        [SerializeField] bool _autoHostInEditor = true;

        [Tooltip("Log every player who joins or leaves. This is how a headless smoke test is read.")]
        [SerializeField] bool _logRoster = true;

        NetworkManager _manager;
        float _quitAt = -1f;

        /// <summary>Raised when the local connection state of the server changes.</summary>
        public event Action<LocalConnectionState> ServerStateChanged;

        /// <summary>Raised when the local connection state of the client changes.</summary>
        public event Action<LocalConnectionState> ClientStateChanged;

        void Awake()
        {
            _manager = GetComponent<NetworkManager>();
            if (_manager == null)
            {
                Debug.LogError("[NetworkBootstrap] No NetworkManager on this object; disabling.");
                enabled = false;
                return;
            }

            _manager.ServerManager.OnServerConnectionState += OnServerConnectionState;
            _manager.ClientManager.OnClientConnectionState += OnClientConnectionState;

            if (!_logRoster) return;
            NetworkPlayerRegistry.PlayerAdded += OnPlayerAdded;
            NetworkPlayerRegistry.PlayerRemoved += OnPlayerRemoved;
        }

        void OnDestroy()
        {
            NetworkPlayerRegistry.PlayerAdded -= OnPlayerAdded;
            NetworkPlayerRegistry.PlayerRemoved -= OnPlayerRemoved;

            if (_manager == null) return;
            _manager.ServerManager.OnServerConnectionState -= OnServerConnectionState;
            _manager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
        }

        void OnPlayerAdded(NetworkPlayerRegistry.PlayerBody body)
        {
            if (body.Identity == null) return;

            // The name and colour are SyncVars, so on a client they may still be defaults at this
            // moment. Listening for the change as well is what makes the log trustworthy.
            body.Identity.IdentityChanged += LogIdentity;
            LogIdentity(body.Identity);
        }

        void OnPlayerRemoved(NetworkPlayerRegistry.PlayerBody body)
        {
            if (body.Identity != null) body.Identity.IdentityChanged -= LogIdentity;
            Debug.Log($"[Roster] owner {body.OwnerId} left. {NetworkPlayerRegistry.Count} remaining.");
        }

        static void LogIdentity(Player.PlayerIdentity identity)
        {
            Debug.Log($"[Roster] owner {identity.OwnerId}: \"{identity.DisplayName}\" "
                      + $"colour {identity.ColorIndex} {identity.Color}. "
                      + $"{NetworkPlayerRegistry.Count} known.");
        }

        void Start()
        {
            string[] args = Environment.GetCommandLineArgs();

            bool host = HasFlag(args, "-host");
            bool server = HasFlag(args, "-server");
            bool client = HasFlag(args, "-client");

            string address = GetValue(args, "-address", _defaultAddress);
            ushort port = (ushort)GetInt(args, "-port", _defaultPort);

            int quitAfter = GetInt(args, "-quitAfter", 0);
            if (quitAfter > 0) _quitAt = Time.time + quitAfter;

            if (!host && !server && !client)
            {
                if (Application.isEditor && _autoHostInEditor) host = true;
                else return; // A shipped build waits for the lobby.
            }

            ConfigureTransport(address, port);

            if (host || server) StartServer(port);
            if (host || client) StartClient(address, port);
        }

        void Update()
        {
            if (_quitAt < 0f || Time.time < _quitAt) return;

            _quitAt = -1f;
            Debug.Log("[NetworkBootstrap] -quitAfter elapsed, shutting down.");
            Application.Quit();
        }

        void ConfigureTransport(string address, ushort port)
        {
            Transport transport = _manager.TransportManager.Transport;
            if (transport == null)
            {
                Debug.LogError("[NetworkBootstrap] NetworkManager has no transport configured.");
                return;
            }

            transport.SetPort(port);
            transport.SetClientAddress(address);
        }

        void StartServer(ushort port)
        {
            Debug.Log($"[NetworkBootstrap] Starting server on port {port}.");
            _manager.ServerManager.StartConnection(port);
        }

        void StartClient(string address, ushort port)
        {
            Debug.Log($"[NetworkBootstrap] Connecting to {address}:{port}.");
            _manager.ClientManager.StartConnection(address, port);
        }

        void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            Debug.Log($"[NetworkBootstrap] Server: {args.ConnectionState}.");
            ServerStateChanged?.Invoke(args.ConnectionState);
            ClearRegistryIfFullyStopped();
        }

        void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            Debug.Log($"[NetworkBootstrap] Client: {args.ConnectionState}.");
            ClientStateChanged?.Invoke(args.ConnectionState);
            ClearRegistryIfFullyStopped();
        }

        /// <summary>
        /// The player registry is static, so it outlives the session that filled it. Nothing else
        /// empties it: a clean disconnect despawns every body and each one unregisters itself, but a
        /// dropped connection or a torn-down transport does not, and the stale entries would then be
        /// visible in the next lobby.
        /// </summary>
        void ClearRegistryIfFullyStopped()
        {
            if (_manager.ServerManager.Started || _manager.ClientManager.Started) return;
            NetworkPlayerRegistry.Clear();
        }

        static bool HasFlag(string[] args, string flag)
        {
            foreach (string arg in args)
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string GetValue(string[] args, string key, string fallback)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return fallback;
        }

        static int GetInt(string[] args, string key, int fallback)
        {
            string raw = GetValue(args, key, null);
            return raw != null && int.TryParse(raw, out int value) ? value : fallback;
        }
    }
}
