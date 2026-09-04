using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using FishNet;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// The one component the scene holds for the HUD. Builds the canvas, drives the squad list and
    /// the world markers, and is the only place in the UI that touches Unity's frame loop.
    ///
    /// **The canvas is only built when there is a screen to draw on.** A headless run has no graphics
    /// device, and a uGUI canvas laying itself out against no screen is cost with nothing to show for
    /// it. What still runs headlessly is <see cref="SquadModel"/> — which is the interesting half,
    /// because the claim the HUD makes is about replicated state, not about rectangles: every peer
    /// must read the same bleed-out countdown for the same player. <c>-hudTest</c> prints exactly the
    /// rows a player would read, and running it on a client is what proves the number is not just
    /// the server talking to itself.
    ///
    /// Nothing here is networked. Every value comes from state FishNet has already replicated onto
    /// this peer, and a HUD that had to ask the server what to draw would be a HUD that lies for a
    /// round trip every time something happens.
    /// </summary>
    public class HudRoot : MonoBehaviour
    {
        [Header("Canvas")]
        [Tooltip("Design resolution the HUD is laid out against; scaled to whatever the screen is.")]
        [SerializeField] Vector2 _referenceResolution = new(1920f, 1080f);

        [Tooltip("Draw order. Above the world, below anything modal that arrives later.")]
        [SerializeField] int _sortOrder = 100;

        readonly List<SquadModel.Entry> _entries = new();

        readonly SquadPanel _panel = new();
        readonly DownedMarkers _markers = new();
        readonly ObjectiveBanner _objective = new();
        readonly FrameCounter _frames = new();
        readonly StatBars _stats = new();

        Canvas _canvas;
        Camera _camera;

        // -hudTest. See RunTest.
        float _testUntil;
        float _testNextLog;
        bool _testRunning;

        void Awake()
        {
            float seconds = CommandLine.GetFloat("-hudTest", -1f);
            if (seconds > 0f)
            {
                _testRunning = true;
                _testUntil = Time.time + seconds;
                _testNextLog = Time.time + 1f;
            }

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

            BuildCanvas();
        }

        void BuildCanvas()
        {
            var go = new GameObject("HudCanvas", typeof(RectTransform));
            go.transform.SetParent(transform, worldPositionStays: false);

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = _sortOrder;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = _referenceResolution;

            // Match width and height evenly: the squad list is anchored to a corner and the markers
            // are placed in screen space, so neither axis is more important than the other.
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // No GraphicRaycaster and no EventSystem: nothing in this HUD is clickable, and a raycaster
            // over the whole screen is a good way to eat a mouse click the game wanted.

            var root = (RectTransform)go.transform;
            _panel.Build(root);
            _markers.Build(root);
            _objective.Build(root);
            _frames.Build(root);
            _stats.Build(root);
        }

        void Update()
        {
            SquadModel.Collect(_entries);

            if (_canvas != null)
            {
                // Looked up rather than cached for the session: the local player's camera is created
                // when their body spawns, replaced when they die into the ghost (#26), and replaced
                // again on reconnect adoption (#111).
                if (_camera == null) _camera = Camera.main;

                _panel.Refresh(_entries);
                _markers.Refresh(_entries, _camera);
                _objective.Refresh(SquadModel.FindLocalAnchor());
                _frames.Refresh();
                _stats.Refresh(SquadModel.FindLocalStats());
            }

            if (_testRunning) RunTest();
        }

        /// <summary>
        /// <c>-hudTest &lt;seconds&gt;</c>. Prints the squad rows once a second for that long, on
        /// whichever peer the flag was passed to.
        ///
        /// The evidence this produces is not "the HUD did not throw". It is that a client which is
        /// not the server reads the same states and the same falling countdown as the host does, from
        /// replicated state alone, with no HUD-specific traffic between them.
        /// </summary>
        void RunTest()
        {
            if (Time.time < _testNextLog) return;

            _testNextLog = Time.time + 1f;

            if (Time.time > _testUntil)
            {
                _testRunning = false;
                return;
            }

            string peer = InstanceFinder.IsServerStarted ? "host" : "client";

            if (_entries.Count == 0)
            {
                Debug.Log($"[HudRoot] -hudTest {peer}: no players registered yet.");
                return;
            }

            for (int i = 0; i < _entries.Count; i++)
                Debug.Log($"[HudRoot] -hudTest {peer}: {SquadModel.Describe(_entries[i])}");

            // The survival bars, from the same replicated state the canvas draws. Printed even
            // headlessly, where there is no canvas at all, because the claim is about the numbers.
            Player.SurvivalStats stats = SquadModel.FindLocalStats();
            if (stats == null) return;

            var buffs = stats.GetComponent<Player.BuffState>();
            Debug.Log($"[HudRoot] -hudTest {peer}: bars {stats.Describe()}"
                      + (buffs != null ? $" | {buffs.Describe()}" : ""));
        }
    }
}
