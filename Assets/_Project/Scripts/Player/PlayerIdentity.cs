using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// Who this body belongs to: a name and a colour.
    ///
    /// Four identical grey capsules punching each other is unreadable, and telling players apart is
    /// what makes the comedy land — you have to know whose ragdoll just went off the cliff. Both
    /// values are assigned by the server on spawn and replicated, so late joiners see everyone
    /// correctly coloured without a handshake of their own.
    ///
    /// The colour is a palette *index*, not an RGB value. One byte instead of sixteen, and it
    /// guarantees the palette stays the set of colours that were actually chosen to be distinct.
    /// </summary>
    public class PlayerIdentity : NetworkBehaviour
    {
        /// <summary>
        /// Player colours. Picked to stay distinguishable on a small dark screen and to survive the
        /// blur the alcohol buff puts over the camera.
        /// </summary>
        public static readonly Color[] Palette =
        {
            new(0.95f, 0.30f, 0.25f), // red
            new(0.25f, 0.55f, 0.95f), // blue
            new(0.40f, 0.80f, 0.35f), // green
            new(0.98f, 0.80f, 0.25f), // yellow
            new(0.75f, 0.40f, 0.90f), // purple
            new(0.35f, 0.85f, 0.85f), // cyan
            new(0.98f, 0.55f, 0.20f), // orange
            new(0.95f, 0.55f, 0.75f), // pink
        };

        [Header("Appearance")]
        [Tooltip("Renderers tinted with the player colour. Empty means every renderer under this object.")]
        [SerializeField] Renderer[] _tintedRenderers;

        readonly SyncVar<string> _displayName = new();
        readonly SyncVar<byte> _colorIndex = new();

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int LegacyColorId = Shader.PropertyToID("_Color");

        MaterialPropertyBlock _properties;

        /// <summary>Raised on every peer when the name or the colour changes.</summary>
        public event Action<PlayerIdentity> IdentityChanged;

        public string DisplayName => string.IsNullOrEmpty(_displayName.Value)
            ? $"Player {OwnerId}"
            : _displayName.Value;

        public byte ColorIndex => _colorIndex.Value;

        public Color Color => Palette[_colorIndex.Value % Palette.Length];

        void Awake()
        {
            if (_tintedRenderers == null || _tintedRenderers.Length == 0)
                _tintedRenderers = GetComponentsInChildren<Renderer>(includeInactive: true);

            _displayName.OnChange += OnNameChanged;
            _colorIndex.OnChange += OnColorChanged;
        }

        void OnDestroy()
        {
            _displayName.OnChange -= OnNameChanged;
            _colorIndex.OnChange -= OnColorChanged;
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            // OnStartNetwork rather than OnStartClient, so the registry is populated on a dedicated
            // server too, where no client callback ever runs.
            Net.NetworkPlayerRegistry.Register(this);
        }

        public override void OnStopNetwork()
        {
            Net.NetworkPlayerRegistry.Unregister(this);
            base.OnStopNetwork();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            // SyncVars arrive before OnStartClient, so the change callbacks have already fired for
            // anything set before this object reached us. Apply once here to catch that.
            ApplyColor();
            IdentityChanged?.Invoke(this);
        }

        /// <summary>Server only. Called by the spawner once the connection is known.</summary>
        public void ServerSetIdentity(string displayName, byte colorIndex)
        {
            if (!IsServerStarted) return;

            _displayName.Value = displayName;
            _colorIndex.Value = colorIndex;

            // The host is its own client and would otherwise never see its own colour, because
            // SyncVar callbacks do not fire back into the process that wrote them.
            if (IsClientStarted) return;
            ApplyColor();
        }

        void OnNameChanged(string previous, string next, bool asServer)
        {
            if (asServer && IsClientStarted) return;
            IdentityChanged?.Invoke(this);
        }

        void OnColorChanged(byte previous, byte next, bool asServer)
        {
            if (asServer && IsClientStarted) return;
            ApplyColor();
            IdentityChanged?.Invoke(this);
        }

        /// <summary>
        /// Tints through a property block rather than by writing to the material. Touching
        /// <c>renderer.material</c> clones it, which would leave four leaked materials per session
        /// and break batching for every capsule on screen.
        /// </summary>
        void ApplyColor()
        {
            if (_tintedRenderers == null) return;

            _properties ??= new MaterialPropertyBlock();
            Color color = Color;

            foreach (Renderer renderer in _tintedRenderers)
            {
                if (renderer == null) continue;

                renderer.GetPropertyBlock(_properties);
                _properties.SetColor(BaseColorId, color);   // URP
                _properties.SetColor(LegacyColorId, color); // built-in, and some URP shaders
                renderer.SetPropertyBlock(_properties);
            }
        }
    }
}
