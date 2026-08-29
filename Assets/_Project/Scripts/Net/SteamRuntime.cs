using EscapeWithYourFriends.Core;
using Steamworks;
using UnityEngine;

namespace EscapeWithYourFriends.Net
{
    /// <summary>
    /// Owns the Steam API for the whole process: bring it up once, take it down once, and tell the
    /// rest of the game whether it is usable.
    ///
    /// Steam is what makes this game shippable without paying for servers. Traffic runs over the
    /// Steam Datagram Relay through FishyFacepunch, so four friends connect by SteamID with no port
    /// forwarding, no dedicated host and no monthly bill. See #13.
    ///
    /// Steam is also optional, on purpose. Every automated test in this project is a headless build
    /// launched from a terminal script on a machine where the Steam client may not be running, and a
    /// game that cannot start without Steam cannot be tested that way. So this never throws and never
    /// blocks: it reports Available, and the transport layer falls back to Tugboat over IP.
    ///
    /// AppID 480 is Spacewar, Valve public test app. It works for anyone with Steam installed, which
    /// is what lets development proceed before the $100 Steam Direct fee is paid. Swap it for the real
    /// id at M9 by editing steam_appid.txt and the serialized field, or pass -steamAppId at runtime.
    ///
    /// Callbacks are pumped by Facepunch itself: SteamClient.Init is called with asyncCallbacks true,
    /// which starts its own dispatch loop, so there is deliberately no RunCallbacks call in Update.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class SteamRuntime : MonoBehaviour
    {
        [Tooltip("Steam application id. 480 is Spacewar, Valve public test app, used until the real "
                 + "app is bought. Must match steam_appid.txt at the project root.")]
        [SerializeField] uint _appId = 480;

        /// <summary>True when the Steam API is up and Steam transports can be used.</summary>
        public static bool Available { get; private set; }

        /// <summary>SteamID of the local user, or 0 when Steam is not available.</summary>
        public static ulong LocalSteamId { get; private set; }

        /// <summary>Steam persona name of the local user, or empty when Steam is not available.</summary>
        public static string LocalName { get; private set; } = string.Empty;

        static bool _ownsShutdown;

        void Awake()
        {
            _appId = (uint)CommandLine.GetInt("-steamAppId", (int)_appId);
            Initialize();
        }

        void Initialize()
        {
            // FishyFacepunch initialises Steam in its own Transport.Initialize, which NetworkManager
            // runs at short.MinValue execution order, so on a scene with a Steam transport this is
            // usually adopting an already-live API rather than starting one.
            if (SteamClient.IsValid)
            {
                Adopt();
                return;
            }

            try
            {
                SteamClient.Init(_appId, true);
                _ownsShutdown = true;
            }
            catch (System.Exception e)
            {
                Available = false;
                Debug.LogWarning($"[SteamRuntime] Steam app {_appId} unavailable, running without it: "
                                 + $"{e.Message}");
                return;
            }

            Adopt();
        }

        void Adopt()
        {
            Available = SteamClient.IsValid;
            if (!Available) return;

            LocalSteamId = SteamClient.SteamId.Value;
            LocalName = SteamClient.Name;

            Debug.Log($"[SteamRuntime] app {_appId} ready as \"{LocalName}\" ({LocalSteamId}).");
        }

        void OnApplicationQuit()
        {
            Shutdown();
        }

        void OnDestroy()
        {
            Shutdown();
        }

        /// <summary>
        /// Shutting Steam down twice is not harmless, and the transport also holds a reference to it,
        /// so this is guarded and idempotent.
        /// </summary>
        void Shutdown()
        {
            if (!Available) return;

            Available = false;
            LocalSteamId = 0;
            LocalName = string.Empty;

            if (!_ownsShutdown || !SteamClient.IsValid) return;

            _ownsShutdown = false;
            SteamClient.Shutdown();
            Debug.Log("[SteamRuntime] shut down.");
        }
    }
}
