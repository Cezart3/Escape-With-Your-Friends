using EscapeWithYourFriends.Player;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// Four bars in the bottom-left: food, water, stamina, warmth.
    ///
    /// Bottom-left rather than around the crosshair, because these are things you check between
    /// fights rather than during one, and a ring of meters in the middle of the screen turns every
    /// glance at the world into a glance at the UI.
    ///
    /// **A bar that is fine gets out of the way.** Above the low threshold it draws dim and unlabelled;
    /// at or below it, it brightens and shows its name. That is the whole answer to "annoying enough
    /// to drive behaviour but never tedious" on the presentation side - the HUD is quiet until one of
    /// them is actually a problem, and then it is not quiet at all.
    ///
    /// Stamina is the exception: it is bright whenever it is not full, because it is a thing you
    /// watch while it happens rather than a thing you notice has gone wrong.
    /// </summary>
    public class StatBars
    {
        const float Width = 190f;
        const float Height = 12f;
        const float Gap = 6f;
        const float Margin = 20f;

        class Bar
        {
            public Image Fill;
            public Text Label;
            public Color Calm;
            public Color Alarm;
        }

        readonly Bar _food = new();
        readonly Bar _water = new();
        readonly Bar _stamina = new();
        readonly Bar _warmth = new();

        RectTransform _root;
        SurvivalStats _stats;

        public void Build(RectTransform parent)
        {
            _root = HudFactory.Rect(parent, "Stats");
            HudFactory.Anchor(_root, new Vector2(0f, 0f), new Vector2(0f, 0f),
                              new Vector2(Margin, Margin),
                              new Vector2(Width, (Height + Gap) * 4f));

            // Bottom to top, so the order reads upward as stamina, warmth, water, food - the two you
            // check constantly nearest the bottom edge where the eye already is.
            Make(_stamina, 0, "stamina", new Color(0.45f, 0.75f, 0.95f), new Color(0.45f, 0.75f, 0.95f));
            Make(_warmth, 1, "warmth", new Color(0.80f, 0.55f, 0.30f), new Color(0.45f, 0.70f, 1.00f));
            Make(_water, 2, "water", new Color(0.35f, 0.65f, 0.90f), new Color(1.00f, 0.65f, 0.25f));
            Make(_food, 3, "food", new Color(0.65f, 0.75f, 0.40f), new Color(1.00f, 0.45f, 0.35f));
        }

        void Make(Bar bar, int row, string label, Color calm, Color alarm)
        {
            float y = row * (Height + Gap);

            Image back = HudFactory.Block(_root, $"{label}Back", new Color(0f, 0f, 0f, 0.45f));
            HudFactory.Anchor((RectTransform)back.transform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                              new Vector2(0f, y), new Vector2(Width, Height));

            bar.Fill = HudFactory.Block(back.rectTransform, $"{label}Fill", calm);

            // Anchored to the left edge and stretched vertically, so setting the width is the whole
            // of "how full is it" - no Image.fillAmount, which needs a sprite with a fill origin.
            var fill = (RectTransform)bar.Fill.transform;
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(0f, 1f);
            fill.pivot = new Vector2(0f, 0.5f);
            fill.anchoredPosition = Vector2.zero;
            fill.sizeDelta = new Vector2(Width, 0f);

            bar.Label = HudFactory.Label(_root, $"{label}Label", 12, TextAnchor.MiddleLeft);
            bar.Label.text = label;
            HudFactory.Anchor((RectTransform)bar.Label.transform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                              new Vector2(Width + 8f, y), new Vector2(110f, Height));

            bar.Calm = calm;
            bar.Alarm = alarm;
        }

        /// <summary>
        /// The local player's stats, or null. Looked up every frame rather than cached for the same
        /// reason the HUD looks up the camera: the body is replaced on death, on revive and on
        /// reconnect adoption, and a cached reference would draw a corpse's numbers.
        /// </summary>
        public void Refresh(SurvivalStats stats)
        {
            _stats = stats;

            if (_root == null) return;

            _root.gameObject.SetActive(stats != null);
            if (stats == null) return;

            float low = stats.Profile != null ? stats.Profile.LowThreshold : 0.25f;

            Draw(_food, stats.HungerFraction, low, alarmOnlyWhenLow: true);
            Draw(_water, stats.ThirstFraction, low, alarmOnlyWhenLow: true);
            Draw(_warmth, stats.WarmthFraction, low, alarmOnlyWhenLow: true);
            Draw(_stamina, stats.StaminaFraction, 1f, alarmOnlyWhenLow: false);
        }

        static void Draw(Bar bar, float fraction, float lowThreshold, bool alarmOnlyWhenLow)
        {
            if (bar.Fill == null) return;

            fraction = Mathf.Clamp01(fraction);

            var rect = (RectTransform)bar.Fill.transform;
            rect.sizeDelta = new Vector2(Width * fraction, 0f);

            bool worrying = fraction <= lowThreshold;

            bar.Fill.color = worrying && alarmOnlyWhenLow ? bar.Alarm : bar.Calm;

            // Dim and nameless while it is fine; named and solid once it is not. Stamina passes
            // lowThreshold 1f so it is always "worrying" and therefore always readable - it is a
            // number you watch, not a warning you wait for.
            float alpha = worrying ? 1f : 0.45f;

            Color fill = bar.Fill.color;
            fill.a = alpha;
            bar.Fill.color = fill;

            if (bar.Label == null) return;

            bar.Label.enabled = worrying && alarmOnlyWhenLow;
        }

        /// <summary>What the bars currently say, for <c>-hudTest</c>. Empty when there is nothing to draw.</summary>
        public string Describe() => _stats != null ? _stats.Describe() : "";
    }
}
