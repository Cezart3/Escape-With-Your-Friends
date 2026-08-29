using System;
using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace EscapeWithYourFriends.Net
{
    /// <summary>
    /// The Steam lobby: how four friends end up in the same session without typing an IP address.
    ///
    /// A lobby is a Steam-side room, not a game connection. It exists so the overlay has something to
    /// invite people into, and so a joiner can be told which SteamID to dial. The traffic still runs
    /// over the transports described in TransportSelector; this class only decides who connects to
    /// whom, and when.
    ///
    /// Host path:
    ///   CreateLobbyAsync -> friends only, joinable, lobby data records the host SteamID and the build
    ///   version -> start the FishNet server -> start the local client over Steam.
    ///
    /// Joiner path:
    ///   an overlay invite, or a friend clicking Join Game, raises OnGameLobbyJoinRequested -> join the
    ///   lobby -> OnLobbyEntered -> read the host SteamID out of the lobby data -> connect over Steam.
    ///
    /// Join in progress is the same path. The lobby stays joinable while the server runs, so a friend
    /// who joins twenty minutes in goes through exactly the code a friend who joined at the start did.
    /// There is no separate late-join branch to get wrong.
    ///
    /// Command line, for testing without an overlay:
    ///   -lobbyHost              create a lobby and host it
    ///   -lobbyJoin 109775...    join this lobby id
    ///   +connect_lobby 1097...  what Steam appends when an invite is accepted while the game is
    ///                           closed, read from our own argv and from SteamApps.CommandLine
    ///
    /// Without Steam this component disables itself and the game still hosts over Tugboat, which is
    /// what every headless test does.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    public class SteamLobby : MonoBehaviour
    {
        /// <summary>SteamID of the host, written by the host so joiners do not have to trust the owner field.</summary>
        public const string HostKey = "ewyf_host";

        /// <summary>Build version, so a joiner on an older zip is told why instead of desyncing.</summary>
        public const string VersionKey = "ewyf_version";

        /// <summary>Human readable name, for a lobby list later on.</summary>
        public const string NameKey = "ewyf_name";

        [Tooltip("Steam lobby capacity. The game targets 4; 8 is tested.")]
        [SerializeField] int _maxMembers = 4;

        [Tooltip("Port the host listens on for the LAN transport. Steam joiners never see it.")]
        [SerializeField] ushort _port = 7770;

        public static SteamLobby Instance { get; private set; }

        /// <summary>The lobby this process is in, or null.</summary>
        public Lobby? Current { get; private set; }

        /// <summary>True when this process created the lobby it is in.</summary>
        public bool IsHost { get; private set; }

        /// <summary>Raised on entering a lobby, on the host and on a joiner.</summary>
        public event Action<Lobby> Entered;

        /// <summary>Raised after leaving a lobby, for any reason.</summary>
        public event Action Left;

        /// <summary>Raised when someone joins or leaves. A player list redraws on this.</summary>
        public event Action MembersChanged;

        /// <summary>Raised with a human readable reason when a lobby operation fails.</summary>
        public event Action<string> Failed;

        NetworkBootstrap _bootstrap;
        bool _available;
        bool _creating;

        /// <summary>True when the command line asks for a lobby, so NetworkBootstrap stays out of the way.</summary>
        public static bool RequestedFromCommandLine =>
            CommandLine.HasFlag("-lobbyHost") || LobbyIdFromCommandLine() != 0UL;

        void Awake()
        {
            Instance = this;
            _bootstrap = GetComponent<NetworkBootstrap>();

            _available = SteamRuntime.Available;
            if (!_available)
            {
                // Not an error. A headless test and a LAN session both run without Steam on purpose.
                Debug.Log("[SteamLobby] Steam is not available; lobbies are disabled this run.");
                enabled = false;
                return;
            }

            SteamMatchmaking.OnLobbyEntered += OnLobbyEntered;
            SteamMatchmaking.OnLobbyMemberJoined += OnMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave += OnMemberLeft;
            SteamMatchmaking.OnLobbyMemberDisconnected += OnMemberLeft;
            SteamMatchmaking.OnLobbyInvite += OnInvited;
            SteamFriends.OnGameLobbyJoinRequested += OnJoinRequested;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (!_available) return;

            SteamMatchmaking.OnLobbyEntered -= OnLobbyEntered;
            SteamMatchmaking.OnLobbyMemberJoined -= OnMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave -= OnMemberLeft;
            SteamMatchmaking.OnLobbyMemberDisconnected -= OnMemberLeft;
            SteamMatchmaking.OnLobbyInvite -= OnInvited;
            SteamFriends.OnGameLobbyJoinRequested -= OnJoinRequested;

            // Steam keeps a lobby alive for a while after the process that made it is gone, and a
            // ghost lobby that still says joinable is worse than no lobby: a friend clicks Join and
            // waits for a session that does not exist.
            LeaveLobby();
        }

        void Start()
        {
            if (!_available) return;

            if (CommandLine.HasFlag("-lobbyHost")) { HostLobby(); return; }

            ulong requested = LobbyIdFromCommandLine();
            if (requested != 0UL) JoinLobby(requested);
        }

        /// <summary>Creates a friends-only lobby and starts hosting in it.</summary>
        public async void HostLobby()
        {
            if (!Ready("host")) return;
            if (Current != null) LeaveLobby();

            // OnLobbyEntered fires for the creator too, and it can arrive before this await returns.
            // Without this flag the host would take the joiner branch and dial itself.
            _creating = true;

            Lobby? created;
            try
            {
                created = await SteamMatchmaking.CreateLobbyAsync(_maxMembers);
            }
            catch (Exception e)
            {
                _creating = false;
                Fail($"Steam threw while creating the lobby: {e.Message}");
                return;
            }

            _creating = false;

            if (created == null)
            {
                Fail("Steam refused to create the lobby.");
                return;
            }

            Lobby lobby = created.Value;

            // Friends only. A public lobby list is a store-page feature, not an M1 feature, and an
            // open lobby in a game with no moderation is a bad first impression.
            lobby.SetFriendsOnly();
            lobby.SetJoinable(true);
            lobby.SetData(HostKey, SteamRuntime.LocalSteamId.ToString());
            lobby.SetData(VersionKey, Application.version);
            lobby.SetData(NameKey, SteamRuntime.LocalName);

            Current = lobby;
            IsHost = true;

            Debug.Log($"[SteamLobby] hosting lobby {lobby.Id} for up to {_maxMembers}.");

            // The local client rides the Steam transport on purpose. FishyFacepunch routes a client
            // whose own server is running through ClientHostSocket, which needs no socket and no
            // port, so hosting cannot fail because something else already holds the UDP port.
            _bootstrap.StartHost(NetLink.Steam, _port);

            Entered?.Invoke(lobby);
            MembersChanged?.Invoke();
        }

        /// <summary>Joins a lobby by id. The game connection follows from OnLobbyEntered.</summary>
        public async void JoinLobby(SteamId lobbyId)
        {
            if (!Ready("join")) return;
            if (Current != null && Current.Value.Id == lobbyId) return;
            if (Current != null) LeaveLobby();

            Debug.Log($"[SteamLobby] joining lobby {lobbyId}.");

            try
            {
                Lobby? joined = await SteamMatchmaking.JoinLobbyAsync(lobbyId);
                if (joined == null) Fail($"Could not join lobby {lobbyId}.");
            }
            catch (Exception e)
            {
                Fail($"Steam threw while joining lobby {lobbyId}: {e.Message}");
            }
        }

        /// <summary>Opens the Steam overlay on the invite dialog for the current lobby.</summary>
        public void OpenInviteOverlay()
        {
            if (Current == null)
            {
                Debug.LogWarning("[SteamLobby] no lobby to invite anyone into.");
                return;
            }

            SteamFriends.OpenGameInviteOverlay(Current.Value.Id);
        }

        /// <summary>Invites one friend directly, without the overlay.</summary>
        public bool InviteFriend(SteamId friendId)
        {
            if (Current == null) return false;
            return Current.Value.InviteFriend(friendId);
        }

        /// <summary>Leaves the current lobby and drops the game connection with it.</summary>
        public void LeaveLobby()
        {
            if (Current == null) return;

            Lobby lobby = Current.Value;
            bool wasHost = IsHost;

            // Cleared first: Disconnect below raises the connection-state handler, which calls back
            // into here, and a null Current is what stops that from recursing.
            Current = null;
            IsHost = false;

            if (wasHost) lobby.SetJoinable(false);
            lobby.Leave();

            Debug.Log($"[SteamLobby] left lobby {lobby.Id}.");

            if (_bootstrap != null) _bootstrap.Disconnect();

            Left?.Invoke();
        }

        /// <summary>Everyone in the lobby. Empty when there is no lobby.</summary>
        public IEnumerable<Friend> Members =>
            Current == null ? Array.Empty<Friend>() : Current.Value.Members;

        /// <summary>How many are in the lobby, which is not how many finished connecting.</summary>
        public int MemberCount => Current == null ? 0 : Current.Value.MemberCount;

        void OnLobbyEntered(Lobby lobby)
        {
            if (_creating || IsHost)
            {
                // HostLobby owns the host side; it finishes the setup when the await returns.
                return;
            }

            Current = lobby;
            IsHost = lobby.IsOwnedBy(SteamRuntime.LocalSteamId);

            string version = lobby.GetData(VersionKey);
            if (!IsHost && !string.IsNullOrEmpty(version) && version != Application.version)
            {
                // Builds are handed around as zips long before there is a Steam depot, so mismatched
                // versions are the normal case rather than an exotic one. Saying so beats desyncing.
                Fail($"That session runs build {version} and this one is {Application.version}.");
                LeaveLobby();
                return;
            }

            Entered?.Invoke(lobby);
            MembersChanged?.Invoke();

            if (IsHost) return;

            ulong host = HostIdOf(lobby);
            if (host == 0UL)
            {
                Fail("The lobby does not say who is hosting it.");
                LeaveLobby();
                return;
            }

            Debug.Log($"[SteamLobby] entered lobby {lobby.Id}, host is {host}.");
            _bootstrap.Connect(NetLink.Steam, host.ToString(), _port);
        }

        /// <summary>
        /// Reads the host SteamID. The host writes it into lobby data rather than letting joiners
        /// trust the owner field, because Steam hands ownership to another member when the owner
        /// leaves, and a lobby whose owner has changed is a lobby whose server is already gone.
        /// </summary>
        static ulong HostIdOf(Lobby lobby)
        {
            string raw = lobby.GetData(HostKey);
            if (ulong.TryParse(raw, out ulong parsed) && parsed != 0UL) return parsed;
            return lobby.Owner.Id.Value;
        }

        void OnMemberJoined(Lobby lobby, Friend member)
        {
            Debug.Log($"[SteamLobby] {member.Name} ({member.Id}) joined. {lobby.MemberCount} in lobby.");
            MembersChanged?.Invoke();
        }

        void OnMemberLeft(Lobby lobby, Friend member)
        {
            Debug.Log($"[SteamLobby] {member.Name} ({member.Id}) left. {lobby.MemberCount} in lobby.");
            MembersChanged?.Invoke();

            if (Current == null || IsHost) return;
            if (member.Id.Value != HostIdOf(Current.Value)) return;

            Fail("The host left the session.");
            LeaveLobby();
        }

        void OnInvited(Friend from, Lobby lobby)
        {
            Debug.Log($"[SteamLobby] invited by {from.Name} to lobby {lobby.Id}.");
        }

        /// <summary>
        /// Raised when a friend accepts an invite or clicks Join Game while this process is already
        /// running. Steam does not ask first, so this joins straight away and drops whatever session
        /// was running, which is what clicking Join Game means.
        /// </summary>
        void OnJoinRequested(Lobby lobby, SteamId invitedBy)
        {
            Debug.Log($"[SteamLobby] join requested into lobby {lobby.Id} by {invitedBy}.");
            JoinLobby(lobby.Id);
        }

        bool Ready(string what)
        {
            if (!_available)
            {
                Debug.LogWarning($"[SteamLobby] cannot {what}: Steam is not available.");
                return false;
            }

            if (_bootstrap != null) return true;

            Debug.LogError($"[SteamLobby] cannot {what}: no NetworkBootstrap on this object.");
            return false;
        }

        void Fail(string reason)
        {
            Debug.LogWarning($"[SteamLobby] {reason}");
            Failed?.Invoke(reason);
        }

        /// <summary>
        /// Finds a lobby id on the command line. Steam appends "+connect_lobby &lt;id&gt;" when an invite
        /// is accepted while the game is closed, and hands the same string back through
        /// SteamApps.CommandLine when it launched us; -lobbyJoin is our own flag, for testing.
        /// </summary>
        static ulong LobbyIdFromCommandLine()
        {
            string own = CommandLine.GetString("-lobbyJoin", string.Empty);
            if (ulong.TryParse(own, out ulong direct) && direct != 0UL) return direct;

            ulong fromArgs = ConnectLobbyIn(Environment.GetCommandLineArgs());
            if (fromArgs != 0UL) return fromArgs;

            if (!SteamRuntime.Available) return 0UL;

            string steamLine = SteamApps.CommandLine;
            if (string.IsNullOrEmpty(steamLine)) return 0UL;

            return ConnectLobbyIn(steamLine.Split(' '));
        }

        static ulong ConnectLobbyIn(string[] parts)
        {
            for (int i = 0; i < parts.Length - 1; i++)
                if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out ulong id))
                    return id;

            return 0UL;
        }
    }
}
