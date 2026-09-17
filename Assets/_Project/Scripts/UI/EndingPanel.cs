using EscapeWithYourFriends.World;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// What you see when the aeroplane clears the map with everybody in it. #74.
    ///
    /// The issue asks for an ending that is not a fade to black, and the difference between the two
    /// is whether the screen is about *this* run. So the first thing on it is four numbers that
    /// could not have come from any other afternoon: how long it took, how many times you died,
    /// what you left at the roulette table, and how many of your friends you drove into.
    ///
    /// **It is a HUD element, not a scene.** A cutscene scene would mean a second camera rig, a
    /// second lighting setup and a transition to get back out of, all to show a panel over an
    /// aeroplane that is already flying away from an island. The aeroplane is the cutscene; this
    /// draws on top of it, which also means the four of you watch your own copy fly out.
    ///
    /// It reads <see cref="RunSummary"/>, which every peer has had filled in by one RPC, so nothing
    /// here waits on the network.
    /// </summary>
    public class EndingPanel
    {
        /// <summary>Seconds the black takes to arrive. Slow: the aeroplane is worth watching.</summary>
        const float FadeSeconds = 2.5f;

        /// <summary>Seconds after the fade before the credits start moving.</summary>
        const float HoldSeconds = 4f;

        const float ScrollSpeed = 40f;

        /// <summary>
        /// The credits. Written here rather than in an asset because they are five lines and a
        /// ScriptableObject for five lines is a file to go and find later.
        /// </summary>
        static readonly string[] Credits =
        {
            "ESCAPE WITH YOUR FRIENDS",
            "",
            "A game about four people and one aeroplane",
            "",
            "Cast",
            "The four of you",
            "",
            "Built with Unity and FishNet",
            "",
            "Thank you for playing",
        };

        RectTransform _root;
        Image _black;
        Text _title;
        Text _figures;
        Text _credits;
        RectTransform _creditsRect;

        float _started;
        float _creditsY;

        public void Build(RectTransform parent)
        {
            _root = HudFactory.Stretch(HudFactory.Rect(parent, "Ending"));

            _black = HudFactory.Block(_root.transform, "Black", new Color(0f, 0f, 0f, 1f));
            HudFactory.Stretch(_black.rectTransform);

            _title = HudFactory.Label(_root.transform, "Title", 44, TextAnchor.UpperCenter);
            HudFactory.Anchor((RectTransform)_title.transform, new Vector2(0.5f, 1f),
                              new Vector2(0.5f, 1f), new Vector2(0f, -120f), new Vector2(900f, 60f));
            _title.text = "You got off the island.";

            _figures = HudFactory.Label(_root.transform, "Figures", 24, TextAnchor.UpperCenter);
            HudFactory.Anchor((RectTransform)_figures.transform, new Vector2(0.5f, 1f),
                              new Vector2(0.5f, 1f), new Vector2(0f, -210f), new Vector2(900f, 180f));
            _figures.color = new Color(0.82f, 0.86f, 0.9f);

            _credits = HudFactory.Label(_root.transform, "Credits", 22, TextAnchor.UpperCenter);
            _creditsRect = (RectTransform)_credits.transform;
            HudFactory.Anchor(_creditsRect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(0f, -40f), new Vector2(900f, 520f));
            _credits.color = new Color(0.72f, 0.76f, 0.82f);
            _credits.text = string.Join("\n", Credits);

            _root.gameObject.SetActive(false);
        }

        public void Refresh()
        {
            if (_root == null) return;

            if (!RunSummary.Over)
            {
                if (_root.gameObject.activeSelf) _root.gameObject.SetActive(false);
                return;
            }

            if (!_root.gameObject.activeSelf)
            {
                _root.gameObject.SetActive(true);
                _started = Time.time;
                _creditsY = -40f;
                _figures.text = Figures();
            }

            float since = Time.time - _started;

            // The black arrives first and the words arrive with it, so nothing is ever legible over
            // a moving aeroplane.
            float in_ = Mathf.Clamp01(since / FadeSeconds);
            _black.color = new Color(0f, 0f, 0f, in_);
            _title.color = new Color(1f, 1f, 1f, in_);
            _figures.color = new Color(0.82f, 0.86f, 0.9f, in_);
            _credits.color = new Color(0.72f, 0.76f, 0.82f, in_);

            if (since < FadeSeconds + HoldSeconds) return;

            _creditsY += ScrollSpeed * Time.deltaTime;
            _creditsRect.anchoredPosition = new Vector2(0f, -40f + _creditsY);
        }

        /// <summary>
        /// The run in four lines. Plurals are spelled out rather than left as "1 deaths", because
        /// this is the last screen anybody sees and it should not read like a debug dump.
        /// </summary>
        static string Figures()
        {
            int minutes = RunSummary.Seconds / 60;
            int seconds = RunSummary.Seconds % 60;

            return $"{minutes}m {seconds}s on the island\n"
                   + $"{Count(RunSummary.Deaths, "death", "deaths")}\n"
                   + $"{RunSummary.Gambled} chips left at the table\n"
                   + $"{Count(RunSummary.RanOver, "friend", "friends")} run over";
        }

        static string Count(int many, string one, string more)
            => $"{many} {(many == 1 ? one : more)}";
    }
}
