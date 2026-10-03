using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Items;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// Radu's journal (#275): five torn pages left across the Ash Isles in 1957, each a Quest item.
    /// Using one in your hand opens it to read (<see cref="ItemUse.RequestUse"/> falls through to
    /// <see cref="Toggle"/>), and the first time anyone picks a page up every peer is told
    /// (<see cref="Inventory"/>'s found RPC): a hint names the page, and page one plays the flashback.
    ///
    /// Page one is the chart in the Act 1 cave, which the lifeboat needs; pages two to four lie on
    /// Temple Isle; page five waits for Act 3's last camp and is not placed yet. docs/STORY.md is the
    /// authority on what they say.
    /// </summary>
    public class Journal : MonoBehaviour
    {
        internal readonly struct Page
        {
            public readonly string Item, Heading, Text;

            public Page(string item, string heading, string text)
            {
                Item = item;
                Heading = heading;
                Text = text;
            }
        }

        internal static readonly Page[] Pages =
        {
            new("chart_page", "Wreck Island, May 1957",
                "Set her down on the beach at first light. There are lights on the hill at night - I did not go to them, and neither should you.\n\n"
                + "The reef opens only at low water, along the line I drew below. Nowhere else. I tried nowhere else once.\n\n"
                + "I am leaving the chart here, at the back of the cave, where the rain can't reach it.\n\n"
                + "Follow the line, not the lights.\n- R.V."),
            new("journal_page_2", "Temple Isle, the beach",
                "The engine quit a mile out and I put her down on the sand, which she did not enjoy.\n\n"
                + "By morning they had been. They carried the engine up to the temple, all of it, on poles, singing. "
                + "The propeller went to the chief's house like a gift. The wing they could not lift, so they rolled it into the ravine.\n\n"
                + "She is not lost. She is only in pieces."),
            new("journal_page_3", "Temple Isle, the cliffs",
                "There are cages by the village. People in them - some of ours, some of theirs. 'For the mountain', the old man said, and pointed.\n\n"
                + "The ones I left behind in '54 are not here. They were taken across to the third island, where the idol sits "
                + "inside the volcano. That is where I have to go."),
            new("journal_page_4", "Temple Isle, the cave",
                "I am taking a canoe across tonight. Alone; I will not ask anyone to come.\n\n"
                + "Whoever reads this has my plane, or what is left of her. Put her back together. "
                + "She flies if you're kind to her. Go home in her if I don't come back for her."),
            new("journal_page_5", "The last camp",
                "The idol is here. It is real, it is gold, and it is warm to the touch, and I won't take it. "
                + "Not with the mountain the way it is.\n\n"
                + "Whoever comes after me: take it, and run, and don't stop running until you're over the reef.\n\n"
                + "The box in the attic is for my grandson. Tell him I was always coming back."),
        };

        /// <summary>The page an item is, 1 to 5, or 0 if it is not a page.</summary>
        internal static int PageOf(string item)
        {
            for (int i = 0; i < Pages.Length; i++)
                if (Pages[i].Item == item) return i + 1;
            return 0;
        }

        /// <summary>
        /// Pages somebody has picked up, server side. ponytail: once per process, like StoryBeat's
        /// ids, so a second run after returning to the menu finds them already found and stays quiet.
        /// </summary>
        static readonly HashSet<int> _found = new();

        /// <summary>Server: whether this pickup is the first of its page, so the finder's inventory tells everyone.</summary>
        internal static bool FirstFind(string item)
        {
            int page = PageOf(item);
            return page > 0 && _found.Add(page);
        }

        /// <summary>Every peer: a page was found by the body at <paramref name="finder"/>.</summary>
        internal static void Found(int page, Transform finder)
        {
            if (page < 1 || page > Pages.Length) return;
            Debug.Log($"[Journal] page {page} of {Pages.Length} found: {Pages[page - 1].Heading}");
            Shown()._hint = page;
            Shown()._hintUntil = Time.time + 6f;
            if (page == 1) StoryBeat.Play("flashback", "Journal page 1 of 5", "Radu Voinea was here.", finder);
        }

        /// <summary>Opens <paramref name="item"/>'s page to read, or closes it if it is the one open. False if it is no page.</summary>
        internal static bool Toggle(string item, Inventory owner)
        {
            int page = PageOf(item);
            if (page == 0) return false;
            Journal shown = Shown();
            shown._open = shown._open == page ? 0 : page;
            shown._owner = owner;
            return true;
        }

        internal static int Open => _shown != null ? _shown._open : 0;

        static Journal _shown;

        static Journal Shown()
        {
            if (_shown == null) _shown = new GameObject("Journal").AddComponent<Journal>();
            return _shown;
        }

        int _open, _hint;
        float _hintUntil;
        Inventory _owner;
        GUIStyle _title, _body, _foot;
        Texture2D _paper;

        void Update()
        {
            if (_open == 0) return;

            // Closed by escape, or by putting the page away: the hand no longer holding it.
            bool away = _owner != null && _owner.Selected.Def?.Id != Pages[_open - 1].Item;
            bool escape = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
            if (escape || away || StoryBeat.Playing) _open = 0;
        }

        void OnGUI()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            float w = Screen.width, h = Screen.height;

            _title ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.BoldAndItalic, wordWrap = true };
            _body ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Italic, wordWrap = true };
            _foot ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerRight, fontStyle = FontStyle.Italic };
            _title.fontSize = Mathf.RoundToInt(h * 0.03f);
            _body.fontSize = Mathf.RoundToInt(h * 0.022f);
            _foot.fontSize = Mathf.RoundToInt(h * 0.017f);

            if (_hint > 0 && Time.time < _hintUntil && !StoryBeat.Playing)
            {
                string key = UseKey();
                string line = $"Journal page {_hint} of {Pages.Length} found" + (key.Length > 0 ? $"  -  hold it and press {key} to read" : "");
                var hint = new Rect(0f, h * 0.18f, w, h * 0.05f);
                _foot.alignment = TextAnchor.MiddleCenter;
                _foot.fontSize = Mathf.RoundToInt(h * 0.027f);
                Ink(new Rect(hint.x + 2f, hint.y + 2f, hint.width, hint.height), line, _foot, new Color(0f, 0f, 0f, 0.7f));
                Ink(hint, line, _foot, new Color(1f, 0.92f, 0.75f));
                _foot.alignment = TextAnchor.LowerRight;
                _foot.fontSize = Mathf.RoundToInt(h * 0.017f);
            }

            if (_open == 0) return;
            Page page = Pages[_open - 1];

            // A sheet of old paper in the middle of the screen, the hand a dark brown ink.
            float ph = h * 0.78f, pw = ph * 0.72f;
            var sheet = new Rect((w - pw) * 0.5f, (h - ph) * 0.5f, pw, ph);
            if (_paper == null) _paper = Paper();
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(sheet.x + 8f, sheet.y + 10f, pw, ph), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(sheet, _paper);

            var ink = new Color(0.24f, 0.15f, 0.08f);
            float pad = pw * 0.1f;
            Rect inner = new(sheet.x + pad, sheet.y + pad * 0.9f, pw - pad * 2f, ph - pad * 1.8f);
            float titleH = _title.CalcHeight(new GUIContent(page.Heading), inner.width);
            Ink(new Rect(inner.x, inner.y, inner.width, titleH), page.Heading, _title, ink);
            Ink(new Rect(inner.x, inner.y + titleH + h * 0.02f, inner.width, inner.height - titleH), page.Text, _body, ink);
            Ink(inner, $"page {_open} of {Pages.Length}", _foot, new Color(ink.r, ink.g, ink.b, 0.6f));
        }

        static void Ink(Rect rect, string text, GUIStyle style, Color colour)
        {
            style.normal.textColor = colour;
            GUI.Label(rect, text, style);
        }

        static string UseKey()
        {
            foreach (Player.PlayerInputReader reader in FindObjectsByType<Player.PlayerInputReader>(FindObjectsSortMode.None))
            {
                string label = reader.BindingLabel("Use");
                // "F | A": the keyboard's, the pad's after it.
                if (!string.IsNullOrEmpty(label)) return label.Split('|')[0].Trim();
            }

            return "";
        }

        /// <summary>Foxed paper: cream, darker and warmer towards the torn edges, a few stains. Made once.</summary>
        static Texture2D Paper()
        {
            const int W = 72, H = 100;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[W * H];
            var cream = new Color(0.93f, 0.87f, 0.72f);
            var edge = new Color(0.72f, 0.58f, 0.38f);
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float d = Mathf.Min(Mathf.Min(x, W - 1 - x) / (float)W, Mathf.Min(y, H - 1 - y) / (float)H) * 2f;
                float n = Mathf.PerlinNoise(x * 0.11f, y * 0.11f);
                float stain = Mathf.Clamp01((Mathf.PerlinNoise(x * 0.05f + 7f, y * 0.05f + 3f) - 0.62f) * 4f);
                Color c = Color.Lerp(edge, cream, Mathf.Clamp01(d * 5f + n * 0.4f - 0.1f));
                c = Color.Lerp(c, edge, stain * 0.5f);
                // The top edge torn out of a binding: a ragged line of missing paper.
                bool torn = y > H - 4 + Mathf.RoundToInt(Mathf.PerlinNoise(x * 0.4f, 1f) * 3f);
                c.a = torn ? 0f : 1f;
                pixels[y * W + x] = c;
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
