using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// The fishing minigame, drawn just under the crosshair. Hidden entirely unless a line is out.
    ///
    /// **Fishing is the one system in the game whose state you cannot see in the world.** A punch has
    /// a fist, a gun has a tracer, a fire has a flame; a hooked tuna is a number on the server and a
    /// bobber thirty metres away. So this is the only HUD panel that is load-bearing rather than
    /// informational - without it the fight is invisible and the minigame does not exist.
    ///
    /// Centre-screen rather than in a corner with the other meters, for the opposite reason
    /// <see cref="StatBars"/> is in the corner: those are things you check between fights, and this is
    /// the fight. It also means the whole minigame is inside the same glance as the water, which is
    /// what keeps it feeling like looking at a lake rather than looking at a UI.
    ///
    /// **Two bars, and they mean opposite things.** Line is progress and shrinks towards zero as the
    /// catch comes in. Tension is danger and grows towards one, turning red on the way; at one the
    /// line snaps. Reeling moves both, which is the entire decision the minigame asks you to make
    /// about ten times per fish.
    /// </summary>
    public class FishingBar
    {
        const float Width = 220f;
        const float Height = 10f;
        const float Gap = 6f;

        // Below the middle of the screen. Far enough down that it never sits on top of what you are
        // aiming at, close enough that the tension bar is inside peripheral vision while you watch
        // the water.
        const float DropFromCentre = -150f;

        static readonly Color LineColor = new(0.55f, 0.80f, 0.95f);
        static readonly Color CalmColor = new(0.55f, 0.75f, 0.45f);
        static readonly Color TightColor = new(0.95f, 0.75f, 0.30f);
        static readonly Color SnappingColor = new(1.00f, 0.35f, 0.30f);

        RectTransform _root;
        Text _title;
        Text _note;
        RectTransform _lineFill;
        RectTransform _tensionFill;
        Image _tensionImage;

        // The last thing landed or lost, held for a couple of seconds after the state goes back to
        // idle so that "Tuna!" is still on screen when the line comes out of the water.
        string _flash;
        float _flashUntil;

        Fishing _bound;

        public void Build(RectTransform parent)
        {
            _root = HudFactory.Rect(parent, "Fishing");
            HudFactory.Anchor(_root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                              new Vector2(0f, DropFromCentre),
                              new Vector2(Width, (Height + Gap) * 2f + 26f));

            _title = HudFactory.Label(_root, "State", 16, TextAnchor.MiddleCenter);
            HudFactory.Anchor((RectTransform)_title.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                              Vector2.zero, new Vector2(Width * 2f, 22f));

            _lineFill = Bar(0, "Line", LineColor, out Image _);
            _tensionFill = Bar(1, "Tension", CalmColor, out _tensionImage);

            _note = HudFactory.Label(_root, "Note", 12, TextAnchor.MiddleCenter);
            _note.color = new Color(0.85f, 0.85f, 0.85f, 0.8f);
            HudFactory.Anchor((RectTransform)_note.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -4f), new Vector2(Width * 2f, 18f));

            _root.gameObject.SetActive(false);
        }

        /// <summary>Same bar idiom as the stat panel: a black backing with a left-anchored fill.</summary>
        RectTransform Bar(int row, string name, Color color, out Image image)
        {
            float y = -26f - row * (Height + Gap);

            Image back = HudFactory.Block(_root, $"{name}Back", new Color(0f, 0f, 0f, 0.5f));
            HudFactory.Anchor(back.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, y), new Vector2(Width, Height));

            image = HudFactory.Block(back.rectTransform, $"{name}Fill", color);

            var fill = (RectTransform)image.transform;
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(0f, 1f);
            fill.pivot = new Vector2(0f, 0.5f);
            fill.anchoredPosition = Vector2.zero;
            fill.sizeDelta = new Vector2(Width, 0f);

            return fill;
        }

        /// <summary>
        /// The local player's rod, or null. Looked up by the HUD every frame for the same reason the
        /// stat panel is: the body is replaced on death, revive and reconnect adoption.
        /// </summary>
        public void Refresh(Fishing fishing)
        {
            if (_root == null) return;

            Bind(fishing);

            bool showing = fishing != null && (fishing.Busy || Time.unscaledTime < _flashUntil);
            _root.gameObject.SetActive(showing);

            if (!showing) return;

            FishingState state = fishing.State;

            // Between casts the panel is only up because something was just landed or lost, so the
            // flash is the whole message and the bars would be two empty boxes under it.
            bool fighting = state == FishingState.Fighting;
            _lineFill.parent.gameObject.SetActive(fighting);
            _tensionFill.parent.gameObject.SetActive(fighting);

            switch (state)
            {
                case FishingState.Waiting:
                    _title.text = "…";
                    _title.color = new Color(1f, 1f, 1f, 0.55f);
                    _note.text = "wait for it";
                    break;

                case FishingState.Biting:
                    _title.text = "STRIKE";
                    _title.color = SnappingColor;
                    _note.text = "";
                    break;

                case FishingState.Fighting:
                    FishDef fish = fishing.Hooked;
                    _title.text = fish != null ? fish.DisplayName : "something";
                    _title.color = Color.white;
                    _note.text = "hold to reel · let go to rest";
                    Draw(fishing.Line, fishing.Tension);
                    break;

                default:
                    _title.text = _flash ?? "";
                    _title.color = Color.white;
                    _note.text = "";
                    break;
            }
        }

        void Draw(float line, float tension)
        {
            _lineFill.sizeDelta = new Vector2(Width * Mathf.Clamp01(line), 0f);
            _tensionFill.sizeDelta = new Vector2(Width * Mathf.Clamp01(tension), 0f);

            // Green until half, then amber, then red for the last quarter. Three steps rather than a
            // smooth gradient because the thing a player has to read in a fifth of a second is
            // "which of the three am I in", and a continuous colour ramp answers that worse.
            _tensionImage.color = tension >= 0.75f ? SnappingColor
                                : tension >= 0.5f ? TightColor
                                : CalmColor;
        }

        /// <summary>
        /// Subscribes to the rod that is actually the local player's, unsubscribing from any previous
        /// one. Events rather than polling for the two things that are not states: a catch and a loss
        /// both happen and are immediately over, so there is no frame in which polling would see them.
        /// </summary>
        void Bind(Fishing fishing)
        {
            if (ReferenceEquals(fishing, _bound)) return;

            if (_bound != null)
            {
                _bound.Caught -= OnCaught;
                _bound.Lost -= OnLost;
            }

            _bound = fishing;

            if (_bound == null) return;

            _bound.Caught += OnCaught;
            _bound.Lost += OnLost;
        }

        void OnCaught(FishDef fish, int count)
        {
            string what = fish != null ? fish.DisplayName : "something";
            Flash(count > 1 ? $"{count}x {what}!" : $"{what}!");
        }

        void OnLost(string why) => Flash(string.IsNullOrEmpty(why) ? "gone" : why);

        void Flash(string message)
        {
            _flash = message;
            _flashUntil = Time.unscaledTime + 2f;
        }
    }
}
