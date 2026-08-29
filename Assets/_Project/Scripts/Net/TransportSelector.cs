using EscapeWithYourFriends.Core;
using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transporting.Multipass;
using FishNet.Transporting.Tugboat;
using UnityEngine;

namespace EscapeWithYourFriends.Net
{
    /// <summary>How a client reaches the host.</summary>
    public enum NetLink
    {
        /// <summary>Raw UDP to an IP address. Localhost tests, LAN, and the fallback when Steam is down.</summary>
        Tugboat,

        /// <summary>Steam Datagram Relay, addressed by SteamID. What a shipped session uses.</summary>
        Steam,
    }

    /// <summary>
    /// Chooses which transport a client connects over.
    ///
    /// Both transports are always present, under Multipass. That is the point of Multipass: a server
    /// listens on all of them at once, so one host accepts friends over Steam and a machine on the
    /// same LAN over IP without deciding in advance which it will be. Only the client has to pick,
    /// because a client connects to exactly one address.
    ///
    /// The alternative was one transport per build, chosen at scene-build time. It was rejected
    /// because TransportManager finds its transport with GetComponent inside NetworkManager.Awake,
    /// and NetworkManager runs at short.MinValue execution order, so no ordinary component can swap
    /// the transport before it is read. Everything else would have meant two builds. See #13.
    ///
    /// Steam is never assumed. If the Steam client is not running, or the API failed to start, this
    /// falls back to Tugboat and says so, which is what keeps the headless four-instance test in this
    /// project working on a machine with no Steam at all.
    /// </summary>
    public class TransportSelector : MonoBehaviour
    {
        /// <summary>The link the last client connection was prepared for.</summary>
        public NetLink Active { get; private set; } = NetLink.Tugboat;

        NetworkManager _manager;

        void Awake()
        {
            _manager = GetComponent<NetworkManager>();
        }

        /// <summary>
        /// Reads -transport steam|tugboat.
        ///
        /// With no flag, a -steamId argument implies Steam, since a SteamID is not something Tugboat
        /// can dial. Otherwise the fallback wins, which for a build launched with no arguments at all
        /// is whatever the caller considers normal for that build.
        /// </summary>
        public static NetLink ResolveFromCommandLine(NetLink fallback)
        {
            string requested = CommandLine.GetString("-transport", string.Empty).ToLowerInvariant();

            if (requested == "steam") return NetLink.Steam;
            if (requested == "tugboat" || requested == "udp" || requested == "ip") return NetLink.Tugboat;

            if (requested.Length > 0)
                Debug.LogWarning($"[TransportSelector] unknown -transport \"{requested}\"; ignoring.");

            if (CommandLine.GetString("-steamId", string.Empty).Length > 0) return NetLink.Steam;

            return fallback;
        }

        /// <summary>
        /// Points the client half of the transport at a host, and returns the link actually used,
        /// which is not always the one asked for.
        /// </summary>
        /// <param name="link">Preferred link.</param>
        /// <param name="address">IP for Tugboat, SteamID for Steam.</param>
        /// <param name="port">Ignored by Steam, which relays rather than binding a public port.</param>
        public NetLink PrepareClient(NetLink link, string address, ushort port)
        {
            Transport transport = _manager == null ? null : _manager.TransportManager.Transport;

            if (transport == null)
            {
                Debug.LogError("[TransportSelector] no transport on the NetworkManager.");
                return Active;
            }

            if (link == NetLink.Steam && !SteamRuntime.Available)
            {
                Debug.LogWarning("[TransportSelector] Steam requested but not available; "
                                 + "falling back to Tugboat.");
                link = NetLink.Tugboat;
            }

            // A scene built before #13, or a deliberately minimal one, may carry a bare transport.
            // Honour it rather than refusing to connect.
            if (transport is not Multipass multipass)
            {
                transport.SetClientAddress(address);
                transport.SetPort(port);
                Active = NetLink.Tugboat;
                return Active;
            }

            int index = IndexOf(multipass, link);
            if (index < 0 && link == NetLink.Steam)
            {
                Debug.LogWarning("[TransportSelector] no Steam transport in Multipass; "
                                 + "falling back to Tugboat.");
                link = NetLink.Tugboat;
                index = IndexOf(multipass, link);
            }

            if (index < 0)
            {
                Debug.LogError($"[TransportSelector] Multipass has no {link} transport.");
                return Active;
            }

            multipass.SetClientTransport(index);
            multipass.SetClientAddress(address, index);

            // Port is set on every transport, not just the chosen one, because the server listens on
            // all of them. Steam ignores it.
            multipass.SetPort(port);

            Active = link;
            return Active;
        }

        static int IndexOf(Multipass multipass, NetLink link)
        {
            var wanted = link == NetLink.Steam
                ? typeof(global::FishyFacepunch.FishyFacepunch)
                : typeof(Tugboat);

            for (int i = 0; i < multipass.Transports.Count; i++)
                if (multipass.Transports[i] != null && multipass.Transports[i].GetType() == wanted)
                    return i;

            return -1;
        }
    }
}
