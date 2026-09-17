using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using FishNet;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// The screen the game opens on, and the one it comes back to (#82).
    ///
    /// Everything under it already existed: <see cref="SteamLobby"/> hosts and invites,
    /// <see cref="NetworkBootstrap"/> starts the halves, <see cref="SettingsScreen"/> is the same
    /// screen the pause key opens. This is four buttons and a member list over those, because until
    /// now the only way into a session was a command line, which is not a thing a player has.
    ///
    /// **It builds itself only when nothing else already started a session.** A harness run, a
    /// `-host` build, a Steam invite accepted from the overlay - all of those go straight into the
    /// game, and a menu that appeared over them would be a menu covering a session already running.
    /// Then it watches: the moment a session ends, the menu is what is left, which is also the
    /// answer to "what happens after the ending panel".
    /// </summary>
    public class MenuScreen : MonoBehaviour
    {
        const int PanelWidth = 560;
        const int RowHeight = 56;
        const int RowGap = 12;

        static bool _started;

        /// <summary>The live menu, or null in a run that went straight into a session.</summary>
        internal static MenuScreen Instance { get; private set; }

        NetworkBootstrap _bootstrap;

        Canvas _canvas;
        RectTransform _root;
        Text _status;
        Text _members;
        Button _host;
        Button _invite;
        Button _leave;

        readonly SettingsScreen _settings = new();

        float _refreshAt;

        /// <summary>Whether the menu is on screen. The test reads this; so does nothing else.</summary>
        internal bool Shown => _root != null && _root.gameObject.activeSelf;

        /// <summary>The line under the title, for the test and for a screenshot to be readable.</summary>
        internal string Status => _status == null ? "" : _status.text;

        internal static void Begin(NetworkBootstrap bootstrap, bool sessionAlready)
        {
            if (_started) return;
            _started = true;

            // A session is already on its way. The menu would be covering it.
            if (sessionAlready) return;

            // Headless builds no canvas, exactly like HudRoot - except under the harness, which is
            // there to check the wiring rather than the pixels.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
                && !CommandLine.HasFlag("-menuTest")) return;

            var go = new GameObject("MenuScreen");
            DontDestroyOnLoad(go);

            var menu = go.AddComponent<MenuScreen>();
            menu._bootstrap = bootstrap;
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Start()
        {
            Build();
            _settings.Build(sortOrder: 420);
            Refresh();
        }

        // ---------------------------------------------------------------- the screen

        void Build()
        {
            var go = new GameObject("MenuCanvas", typeof(RectTransform));
            go.transform.SetParent(transform, worldPositionStays: false);

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 400;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            go.AddComponent<GraphicRaycaster>();
            InventoryScreen.EnsureEventSystem();

            _root = HudFactory.Stretch((RectTransform)go.transform);

            Image back = HudFactory.Block(_root, "Back", new Color(0.05f, 0.07f, 0.10f, 1f));
            HudFactory.Stretch(back.rectTransform);
            back.raycastTarget = true;

            Text title = HudFactory.Label(_root, "Title", 64, TextAnchor.MiddleCenter);
            HudFactory.Anchor(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -120f), new Vector2(PanelWidth * 2, 80f));
            title.text = "Escape With Your Friends";

            _status = HudFactory.Label(_root, "Status", 22, TextAnchor.MiddleCenter);
            HudFactory.Anchor(_status.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -200f), new Vector2(PanelWidth * 2, 30f));

            int row = 0;
            _host = Row(ref row, "Host", "Host a game", HostClicked);
            _invite = Row(ref row, "Invite", "Invite friends", InviteClicked);
            _leave = Row(ref row, "Leave", "Leave the lobby", LeaveClicked);
            Row(ref row, "Settings", "Settings", () => _settings.SetOpen(true));
            Row(ref row, "Quit", "Quit", Quit);

            _members = HudFactory.Label(_root, "Members", 22, TextAnchor.UpperCenter);
            HudFactory.Anchor(_members.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -(row * (RowHeight + RowGap)) - 40f),
                              new Vector2(PanelWidth, 160f));

            Text build = HudFactory.Label(_root, "Version", 18, TextAnchor.LowerRight);
            HudFactory.Anchor(build.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f),
                              new Vector2(-24f, 20f), new Vector2(400f, 24f));
            build.text = Demo.On ? $"demo {Application.version}" : Application.version;
            build.color = new Color(1f, 1f, 1f, 0.5f);
        }

        Button Row(ref int index, string name, string caption, UnityEngine.Events.UnityAction click)
        {
            Button button = HudFactory.Button(_root, name, caption, 26, click);

            // Centred column, growing down from the middle of the screen.
            HudFactory.Anchor((RectTransform)button.transform, new Vector2(0.5f, 0.5f),
                              new Vector2(0.5f, 1f),
                              new Vector2(0f, 80f - index * (RowHeight + RowGap)),
                              new Vector2(PanelWidth, RowHeight));

            index++;
            return button;
        }

        // ---------------------------------------------------------------- what the buttons do

        internal void HostClicked()
        {
            SteamLobby lobby = SteamLobby.Instance;

            if (lobby != null && lobby.isActiveAndEnabled)
            {
                lobby.HostLobby();
                return;
            }

            // No Steam: a LAN session on the default port, which is also how the harness sees it.
            Debug.Log("[MenuScreen] No Steam lobby available; hosting over the direct transport.");
            if (_bootstrap != null) _bootstrap.StartHost(NetLink.Tugboat);
        }

        internal void InviteClicked() => SteamLobby.Instance?.OpenInviteOverlay();

        internal void LeaveClicked()
        {
            SteamLobby.Instance?.LeaveLobby();
            _bootstrap?.Disconnect();
        }

        static void Quit()
        {
            Debug.Log("[MenuScreen] Quit.");
            Application.Quit();
        }

        // ---------------------------------------------------------------- staying honest

        void Update()
        {
            if (_root == null) return;

            bool session = InstanceFinder.NetworkManager != null
                           && (InstanceFinder.NetworkManager.IsServerStarted
                               || InstanceFinder.NetworkManager.IsClientStarted);

            if (session == Shown) SetShown(!session);

            if (!Shown || Time.unscaledTime < _refreshAt) return;

            _refreshAt = Time.unscaledTime + 0.5f;
            Refresh();
        }

        void SetShown(bool shown)
        {
            _root.gameObject.SetActive(shown);

            if (!shown)
            {
                _settings.SetOpen(false);
                return;
            }

            // Whatever the session did to the cursor, a menu needs it back.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            Refresh();
        }

        void Refresh()
        {
            SteamLobby lobby = SteamLobby.Instance;
            bool steam = lobby != null && lobby.isActiveAndEnabled;
            bool inLobby = steam && lobby.Current != null;

            _invite.gameObject.SetActive(inLobby);
            _leave.gameObject.SetActive(inLobby);
            _host.gameObject.SetActive(!inLobby);

            if (!steam)
            {
                _status.text = "Steam is not running. Hosting will use a direct connection.";
                _members.text = "";
                return;
            }

            if (!inLobby)
            {
                _status.text = "Host a game, or accept a friend's invite from the Steam overlay.";
                _members.text = "";
                return;
            }

            _status.text = lobby.IsHost
                ? "Your lobby is open. Invite friends, then the game starts for everybody."
                : "Waiting in the lobby.";

            _members.text = string.Join("\n", lobby.Members.Select(m => m.Name));
        }
    }
}
