using System;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.World;
using FishNet;
using FishNet.Managing;
using FishNet.Managing.Timing;
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
    ///   -transport steam       connect over Steam instead of raw UDP (default tugboat)
    ///   -steamId 7656119...    SteamID of the host, and implies -transport steam
    ///   -quitAfter 30          exit after this many seconds, for automated smoke tests
    ///   -latency 50            simulate this many milliseconds each way (development builds)
    ///   -fallTest 20           read by FallGuard: drop every body out of the world at this time
    ///   -clockLog 2            print the world clock every N seconds, to prove two processes agree
    ///
    /// With no arguments it does nothing and waits for the lobby to start the connection, which is
    /// what a shipped build does. SteamLobby drives it through StartHost, Connect and Disconnect,
    /// and -lobbyHost or -lobbyJoin keep this class out of the way entirely.
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
        TransportSelector _selector;
        float _quitAt = -1f;

        /// <summary>Raised when the local connection state of the server changes.</summary>
        public event Action<LocalConnectionState> ServerStateChanged;

        /// <summary>Raised when the local connection state of the client changes.</summary>
        public event Action<LocalConnectionState> ClientStateChanged;

        void Awake()
        {
            _manager = GetComponent<NetworkManager>();
            _selector = GetComponent<TransportSelector>();
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
            // The arena's own acceptance criterion (#27) is a load time, and the only honest place to
            // measure it is the first frame of the scene that had to load. Engine start to here.
            Debug.Log($"[NetworkBootstrap] Bootstrap scene live {Time.realtimeSinceStartup:0.00}s "
                      + "after process start.");

            bool host = CommandLine.HasFlag("-host");
            bool server = CommandLine.HasFlag("-server");
            bool client = CommandLine.HasFlag("-client");

            string address = CommandLine.GetString("-address", _defaultAddress);
            var port = (ushort)CommandLine.GetInt("-port", _defaultPort);

            int quitAfter = CommandLine.GetInt("-quitAfter", 0);
            if (quitAfter > 0) _quitAt = Time.time + quitAfter;

            ConfigureLatencySimulation(CommandLine.GetInt("-latency", 0));

            _clockLogEvery = CommandLine.GetFloat("-clockLog", 0f);

            if (SteamLobby.RequestedFromCommandLine)
            {
                // The lobby decides who connects to whom, and it knows a SteamID this class does
                // not. Two things racing to start the same client would be a coin flip.
                Debug.Log("[NetworkBootstrap] A lobby was asked for on the command line; "
                          + "SteamLobby starts the connection.");
                return;
            }

            if (!host && !server && !client)
            {
                if (Application.isEditor && _autoHostInEditor) host = true;
                else return; // A shipped build waits for the lobby.
            }

            // The server listens on every transport at once, so only the client half picks one.
            NetLink link = TransportSelector.ResolveFromCommandLine(NetLink.Tugboat);
            if (link == NetLink.Steam) address = CommandLine.GetString("-steamId", address);

            if (host || server) StartServer(port);
            if (host || client) StartClient(link, address, port);
        }

        float _clockLogEvery;
        float _clockLogAt;

        /// <summary>
        /// Prints the world clock on a fixed cadence. There is no other way to check that the day is
        /// synchronised: the sky is a pure function of the FishNet tick, so the test is that two
        /// processes started a few seconds apart report the same time of day, and they can only do
        /// that if the tick reached both of them.
        /// </summary>
        void LogClock()
        {
            if (_clockLogEvery <= 0f || Time.time < _clockLogAt) return;
            _clockLogAt = Time.time + _clockLogEvery;

            TimeManager time = InstanceFinder.TimeManager;
            Debug.Log($"[WorldClock] tick {(time != null ? (long)time.Tick : -1L)}, "
                      + $"day {WorldClock.Day}, {WorldClock.Clock24} (t={WorldClock.Normalized:F4})");
        }

        void Update()
        {
            LogClock();

            if (_quitAt < 0f || Time.time < _quitAt) return;

            _quitAt = -1f;
            Debug.Log("[NetworkBootstrap] -quitAfter elapsed, shutting down.");
            Application.Quit();
        }

        /// <summary>
        /// Hosts: server plus a local client, the way a real session runs.
        ///
        /// The local client goes over <paramref name="localLink"/>. Steam is the right answer when a
        /// Steam lobby is up: FishyFacepunch routes a client whose own server is running through its
        /// ClientHostSocket, so that half needs no socket, no port and no loopback.
        /// </summary>
        public void StartHost(NetLink localLink, ushort port = 0)
        {
            if (port == 0) port = _defaultPort;

            StartServer(port);

            string address = localLink == NetLink.Steam
                ? SteamRuntime.LocalSteamId.ToString()
                : _defaultAddress;

            StartClient(localLink, address, port);
        }

        /// <summary>Connects as a client. The address is a SteamID when the link is Steam.</summary>
        public void Connect(NetLink link, string address, ushort port = 0)
        {
            if (port == 0) port = _defaultPort;
            StartClient(link, address, port);
        }

        /// <summary>Stops whichever halves are running. Safe to call when nothing is.</summary>
        public void Disconnect()
        {
            if (_manager == null) return;

            if (_manager.ClientManager.Started) _manager.ClientManager.StopConnection();
            if (_manager.ServerManager.Started) _manager.ServerManager.StopConnection(true);
        }

        void StartServer(ushort port)
        {
            Debug.Log($"[NetworkBootstrap] Starting server on port {port}.");

            // StartConnection(port) sets the port on every transport under Multipass and starts them
            // all. A transport that cannot start, Steam on a machine with no Steam client for one,
            // declines and the rest carry on: FishNet reports the server started if any of them did.
            _manager.ServerManager.StartConnection(port);
        }

        void StartClient(NetLink link, string address, ushort port)
        {
            if (_selector == null)
            {
                // A scene built before #13. One transport, addressed directly.
                Debug.Log($"[NetworkBootstrap] Connecting to {address}:{port}.");
                _manager.ClientManager.StartConnection(address, port);
                return;
            }

            NetLink used = _selector.PrepareClient(link, address, port);

            Debug.Log(used == NetLink.Steam
                ? $"[NetworkBootstrap] Connecting to SteamID {address} over Steam."
                : $"[NetworkBootstrap] Connecting to {address}:{port} over Tugboat.");

            // No address here on purpose: ClientManager.StartConnection(address, port) would push the
            // address onto every transport under Multipass, overwriting the one just chosen.
            _manager.ClientManager.StartConnection();
        }

        void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            Debug.Log($"[NetworkBootstrap] Server: {args.ConnectionState}.");
            ServerStateChanged?.Invoke(args.ConnectionState);
            ClearRegistryIfFullyStopped();

            // #42's harness needs a server and a second player, and only the server can start it.
            // It no-ops without -itemTest, so this costs a flag check on a state change.
            if (args.ConnectionState != LocalConnectionState.Started) return;

            Items.WorldItemTest.Begin();
            Items.CraftingTest.Begin();
            Items.StorageTest.Begin();
            UI.UiTest.Begin();
            Economy.MoneyTest.Begin();
            Economy.ShopTest.Begin();
            Player.SurvivalTest.Begin();
            Player.BuffTest.Begin();
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

        /// <summary>
        /// Turns on FishNet's latency simulator.
        ///
        /// Prediction is only worth anything under latency, and on one machine there is none: without
        /// this a four-instance test on localhost proves that the code runs, not that it holds up.
        /// The value is one-way, so 50 here is the 100ms round trip #16 asks for.
        ///
        /// The simulator is compiled out of release builds by FishNet, so this is a no-op there.
        /// </summary>
        void ConfigureLatencySimulation(int oneWayMilliseconds)
        {
            if (oneWayMilliseconds <= 0) return;

            var simulator = _manager.TransportManager.LatencySimulator;
            simulator.SetLatency(oneWayMilliseconds);
            simulator.SetEnabled(true);

            Debug.Log($"[NetworkBootstrap] Simulating {oneWayMilliseconds}ms each way "
                      + $"({oneWayMilliseconds * 2}ms round trip).");
        }
    }
}
