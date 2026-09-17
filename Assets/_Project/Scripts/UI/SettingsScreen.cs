using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Player;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// The settings menu. Escape opens it. #84.
    ///
    /// Built the same way as <see cref="InventoryScreen"/> and for the same reasons: its own canvas
    /// above the HUD, its own <see cref="GraphicRaycaster"/>, and it tells
    /// <see cref="PlayerInputReader.SetUiOpen"/> when it is up so the cursor comes back and the body
    /// stops walking. The HUD proper has no raycaster at all - see <see cref="HudRoot"/> - and this
    /// screen is the second of the two places in the project allowed to be clickable.
    ///
    /// **Every row reads and writes <see cref="GameSettings"/> and nothing else.** There is no local
    /// copy of a value, no apply button and no cancel: moving a slider changes the game and saves the
    /// preference on the same line. An apply button is a second source of truth and a way to lose
    /// twenty minutes of somebody's rebinds to a misplaced click.
    ///
    /// **One widget for three jobs.** Toggles, the quality cycler and the rebind rows are all
    /// <see cref="HudFactory.Button"/> with a caption that redraws itself. A toggle and a dropdown
    /// would be two more widgets to build out of uGUI primitives for no gain a player could name.
    /// </summary>
    public class SettingsScreen
    {
        const int RowHeight = 44;
        const int RowGap = 10;
        const int PanelWidth = 720;
        const int LabelWidth = 250;

        /// <summary>The actions offered for rebinding. Not all of them: these are the ones people ask about.</summary>
        static readonly string[] Rebindable = { "Jump", "Sprint", "Crouch", "Interact", "Use", "Drop" };

        Canvas _canvas;
        RectTransform _root;
        RectTransform _rows;

        Text _quality;
        Text _resolution;
        Text _fullscreen;
        Text _colourblind;
        Text _sensitivityValue;
        Text _fovValue;
        Text _masterValue;
        Text _voiceValue;

        readonly Text[] _rebinds = new Text[Rebindable.Length];

        PlayerInputReader _reader;
        int _listening = -1;
        int _next;

        public bool IsOpen { get; private set; }

        public void Build(int sortOrder)
        {
            var go = new GameObject("SettingsCanvas", typeof(RectTransform));

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = sortOrder;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            go.AddComponent<GraphicRaycaster>();
            InventoryScreen.EnsureEventSystem();

            _root = HudFactory.Stretch((RectTransform)go.transform);

            Image dim = HudFactory.Block(_root, "Dim", new Color(0f, 0f, 0f, 0.7f));
            HudFactory.Stretch(dim.rectTransform);

            // The dim layer eats clicks that land on nothing, so a stray click behind the menu does
            // not punch somebody in the world you cannot see.
            dim.raycastTarget = true;

            Image panel = HudFactory.Block(_root, "Panel", new Color(0.10f, 0.11f, 0.14f, 0.97f));
            HudFactory.Anchor(panel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                              Vector2.zero, new Vector2(PanelWidth, 760f));

            Text title = HudFactory.Label(panel.transform, "Title", 34, TextAnchor.UpperCenter);
            HudFactory.Anchor(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -24f), new Vector2(PanelWidth - 40f, 44f));
            title.text = "Settings";

            _rows = HudFactory.Rect(panel.transform, "Rows");
            HudFactory.Anchor(_rows, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -84f), new Vector2(PanelWidth - 40f, 640f));

            BuildRows();

            _root.gameObject.SetActive(false);
        }

        void BuildRows()
        {
            _sensitivityValue = Slider("Look sensitivity", GameSettings.MinSensitivity,
                                       GameSettings.MaxSensitivity, GameSettings.Sensitivity,
                                       value =>
                                       {
                                           GameSettings.Sensitivity = value;
                                           _sensitivityValue.text = $"{GameSettings.Sensitivity:0.00}x";
                                       },
                                       $"{GameSettings.Sensitivity:0.00}x");

            _fovValue = Slider("Field of view", GameSettings.MinFov, GameSettings.MaxFov,
                               GameSettings.Fov,
                               value =>
                               {
                                   GameSettings.Fov = value;
                                   _fovValue.text = $"{GameSettings.Fov:0}°";
                               },
                               $"{GameSettings.Fov:0}°");

            _masterValue = Slider("Volume", 0f, 1f, GameSettings.MasterVolume,
                                  value =>
                                  {
                                      GameSettings.MasterVolume = value;
                                      _masterValue.text = Percent(GameSettings.MasterVolume);
                                  },
                                  Percent(GameSettings.MasterVolume));

            _voiceValue = Slider("Your friends' voices", 0f, 1f, GameSettings.VoiceVolume,
                                 value =>
                                 {
                                     GameSettings.VoiceVolume = value;
                                     _voiceValue.text = Percent(GameSettings.VoiceVolume);
                                 },
                                 Percent(GameSettings.VoiceVolume));

            _quality = Button("Quality", QualityName(), () =>
            {
                int count = Mathf.Max(1, QualitySettings.names.Length);
                GameSettings.Quality = (GameSettings.Quality + 1) % count;
                _quality.text = QualityName();
            });

            _resolution = Button("Resolution", SizeName(), () =>
            {
                Vector2Int[] sizes = Sizes();
                if (sizes.Length == 0) return;

                Vector2Int now = GameSettings.Size;
                int at = System.Array.IndexOf(sizes, now);

                GameSettings.Size = sizes[(at + 1) % sizes.Length];
                _resolution.text = SizeName();
            });

            _fullscreen = Button("Full screen", OnOff(GameSettings.Fullscreen), () =>
            {
                GameSettings.Fullscreen = !GameSettings.Fullscreen;
                _fullscreen.text = OnOff(GameSettings.Fullscreen);
            });

            _colourblind = Button("Colourblind colours", OnOff(GameSettings.Colourblind), () =>
            {
                GameSettings.Colourblind = !GameSettings.Colourblind;
                _colourblind.text = OnOff(GameSettings.Colourblind);
            });

            for (int i = 0; i < Rebindable.Length; i++)
            {
                int index = i;
                _rebinds[i] = Button(Rebindable[i], "…", () => Listen(index));
            }

            Button("", "Reset everything", () =>
            {
                GameSettings.Reset();
                Redraw();
            });
        }

        // ---------------------------------------------------------------- rows

        /// <summary>A label on the left, a control in the middle, a value on the right.</summary>
        Text Slider(string caption, float min, float max, float value,
                    UnityEngine.Events.UnityAction<float> onChange, string shown)
        {
            RectTransform row = Row(caption);

            HudFactory.Anchor((RectTransform)HudFactory.Slider(row, "Slider", min, max, value, onChange).transform,
                              new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                              new Vector2(LabelWidth, 0f), new Vector2(330f, 18f));

            Text readout = HudFactory.Label(row, "Value", 20, TextAnchor.MiddleRight);
            HudFactory.Anchor(readout.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                              Vector2.zero, new Vector2(90f, RowHeight));
            readout.text = shown;

            return readout;
        }

        Text Button(string caption, string shown, UnityEngine.Events.UnityAction onClick)
        {
            RectTransform row = Row(caption);

            UnityEngine.UI.Button button = HudFactory.Button(row, "Button", shown, 20, onClick);
            HudFactory.Anchor(button.image.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                              new Vector2(string.IsNullOrEmpty(caption) ? 0f : LabelWidth, 0f),
                              new Vector2(string.IsNullOrEmpty(caption) ? 420f : 330f, RowHeight - 8f));

            return button.GetComponentInChildren<Text>();
        }

        RectTransform Row(string caption)
        {
            RectTransform row = HudFactory.Rect(_rows, string.IsNullOrEmpty(caption) ? "Row" : caption);
            HudFactory.Anchor(row, new Vector2(0f, 1f), new Vector2(0f, 1f),
                              new Vector2(0f, -_next * (RowHeight + RowGap)),
                              new Vector2(PanelWidth - 40f, RowHeight));
            _next++;

            if (string.IsNullOrEmpty(caption)) return row;

            Text label = HudFactory.Label(row, "Label", 20);
            HudFactory.Anchor(label.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                              Vector2.zero, new Vector2(LabelWidth, RowHeight));
            label.text = caption;

            return row;
        }

        // ---------------------------------------------------------------- open, close, rebind

        public void Refresh(InventoryScreen.NetworkObjectHolder holder)
        {
            if (_root == null) return;

            _reader = holder.Reader;

            if (_reader == null)
            {
                if (IsOpen) SetOpen(false);
                return;
            }

            // Not while waiting for a key: escape is how a rebind is cancelled, and it would
            // otherwise also close the menu out from under it.
            if (_listening >= 0) return;

            if (_reader.ConsumeToggleSettings()) SetOpen(!IsOpen);
        }

        public void SetOpen(bool open)
        {
            if (IsOpen == open) return;

            IsOpen = open;
            _root.gameObject.SetActive(open);

            if (open) Redraw();

            if (_reader != null) _reader.SetUiOpen(open);
        }

        void Listen(int index)
        {
            if (_reader == null || _listening >= 0) return;

            _listening = index;
            _rebinds[index].text = "press a key…";

            _reader.RebindInteractive(Rebindable[index], overrides =>
            {
                _listening = -1;

                // Null means they pressed escape. The action keeps whatever it had, and the stored
                // JSON is not touched - a cancelled rebind must not be able to clear the others.
                if (overrides != null) GameSettings.Rebinds = overrides;

                Redraw();
            });
        }

        void Redraw()
        {
            _sensitivityValue.text = $"{GameSettings.Sensitivity:0.00}x";
            _fovValue.text = $"{GameSettings.Fov:0}°";
            _masterValue.text = Percent(GameSettings.MasterVolume);
            _voiceValue.text = Percent(GameSettings.VoiceVolume);
            _quality.text = QualityName();
            _resolution.text = SizeName();
            _fullscreen.text = OnOff(GameSettings.Fullscreen);
            _colourblind.text = OnOff(GameSettings.Colourblind);

            for (int i = 0; i < Rebindable.Length; i++)
            {
                string key = _reader != null ? _reader.BindingLabel(Rebindable[i]) : "";
                _rebinds[i].text = string.IsNullOrEmpty(key) ? "unbound" : key;
            }
        }

        static string Percent(float value) => $"{Mathf.RoundToInt(value * 100f)}%";

        static string OnOff(bool on) => on ? "On" : "Off";

        static string SizeName()
        {
            Vector2Int size = GameSettings.Size;
            return $"{size.x} x {size.y}";
        }

        /// <summary>
        /// The distinct sizes this monitor offers, largest first. Distinct because
        /// <see cref="Screen.resolutions"/> lists one entry per refresh rate, so a 144Hz monitor
        /// gives four identical-looking rows and a cycle button that appears to be stuck.
        /// </summary>
        static Vector2Int[] Sizes()
        {
            var seen = new System.Collections.Generic.List<Vector2Int>();

            foreach (Resolution resolution in Screen.resolutions)
            {
                var size = new Vector2Int(resolution.width, resolution.height);
                if (!seen.Contains(size)) seen.Add(size);
            }

            seen.Sort((a, b) => b.x * b.y - a.x * a.y);
            return seen.ToArray();
        }

        static string QualityName()
        {
            string[] names = QualitySettings.names;
            int level = GameSettings.Quality;

            return level >= 0 && level < names.Length ? names[level] : level.ToString();
        }
    }
}
