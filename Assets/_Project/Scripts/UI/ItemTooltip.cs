using System.Text;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// What an item actually is, in words, when the mouse rests on it.
    ///
    /// **The text is a pure function of the catalog**, which is the only reason any of #46 can be
    /// tested headlessly. A build with no graphics device has no canvas at all, so a harness cannot
    /// assert that a panel appeared - but it can assert that every item in the game produces a
    /// tooltip that names it, weighs it, and says what it does. Anything the panel adds beyond
    /// <see cref="Describe"/> is layout, and layout is a playtest question.
    ///
    /// The numbers on it are the ones a player makes decisions with: weight per unit and for the
    /// whole stack, because "should I carry this" is a weight question; the stack limit, because
    /// "will this merge" is a slot question; and what a consumable actually restores, because
    /// "cooked or raw" is a survival question and the answer should not need a wiki.
    /// </summary>
    public class ItemTooltip
    {
        const float Width = 300f;
        const float Padding = 10f;
        const float CursorGap = 16f;

        RectTransform _root;
        Image _panel;
        Text _text;

        public void Build(RectTransform parent)
        {
            _root = HudFactory.Rect(parent, "Tooltip");

            // Bottom-left pivot: the panel grows up and right from the corner nearest the cursor,
            // which is what keeps it on screen without a layout pass.
            HudFactory.Anchor(_root, new Vector2(0f, 0f), new Vector2(0f, 0f), Vector2.zero,
                              new Vector2(Width, 120f));

            _panel = HudFactory.Block(_root, "Back", new Color(0.05f, 0.05f, 0.07f, 0.94f));
            HudFactory.Anchor(_panel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                              Vector2.zero, new Vector2(Width, 120f));

            _text = HudFactory.Label(_root, "Text", 14, TextAnchor.UpperLeft);
            _text.horizontalOverflow = HorizontalWrapMode.Wrap;
            HudFactory.Anchor((RectTransform)_text.transform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                              new Vector2(Padding, Padding),
                              new Vector2(Width - Padding * 2f, 100f));

            Hide();
        }

        public void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
        }

        /// <summary>
        /// Shows the tooltip for a stack, next to a screen position. <paramref name="screen"/> is in
        /// pixels; the panel is positioned in canvas units, so it is converted through the parent.
        /// </summary>
        public void Show(ItemStack stack, Vector2 screen, RectTransform canvas)
        {
            if (_root == null) return;

            ItemDef def = stack.Def;
            if (def == null)
            {
                Hide();
                return;
            }

            _text.text = Describe(def, stack.Count);

            // Sized to the text rather than fixed: a coconut is three lines and a bandage is five,
            // and a panel sized for the worst case has a hole in it for everything else.
            float height = _text.preferredHeight + Padding * 2f;
            _panel.rectTransform.sizeDelta = new Vector2(Width, height);
            _root.sizeDelta = new Vector2(Width, height);

            RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, screen, null,
                                                                   out Vector2 local);

            // Flipped to the other side of the cursor near an edge, so the panel is never the thing
            // that runs off the screen.
            Rect area = canvas.rect;
            float x = local.x + CursorGap;
            float y = local.y + CursorGap;

            if (x + Width > area.xMax) x = local.x - CursorGap - Width;
            if (y + height > area.yMax) y = local.y - CursorGap - height;

            _root.anchoredPosition = new Vector2(x - area.xMin, y - area.yMin);
            _root.gameObject.SetActive(true);
        }

        // ---------------------------------------------------------------- the words

        /// <summary>
        /// The whole tooltip as text. Pure: no canvas, no scene, no network - which is what makes it
        /// the part of #46 a headless harness can actually check.
        /// </summary>
        public static string Describe(ItemDef def, int count = 1)
        {
            if (def == null) return string.Empty;

            var text = new StringBuilder();

            string name = string.IsNullOrWhiteSpace(def.DisplayName) ? def.Id : def.DisplayName;
            text.Append(count > 1 ? $"{name}  x{count}" : name);

            if (!string.IsNullOrWhiteSpace(def.Description))
                text.Append('\n').Append(def.Description);

            text.Append('\n');

            if (def.Weight > 0f)
            {
                text.Append($"\n{def.Weight:0.##} kg each");
                if (count > 1) text.Append($"  ({def.Weight * count:0.#} kg)");
            }
            else
            {
                text.Append("\nweighs nothing");
            }

            if (def.Stackable) text.Append($"\nstacks to {def.MaxStack}");

            BuffDef effect = def.Effect;
            if (effect == null) return text.ToString();

            text.Append($"\n\nUse ({def.UseSeconds:0.#}s): ");
            text.Append(DescribeEffect(effect));

            if (def.LeavesBehind != null)
                text.Append($"\nleaves {def.LeavesBehind.Id}");

            return text.ToString();
        }

        /// <summary>What a buff does, in the same words the HUD uses for a running one.</summary>
        public static string DescribeEffect(BuffDef effect)
        {
            if (effect == null) return "nothing";

            var parts = new StringBuilder();

            Instant(parts, effect.Health, "health");
            Instant(parts, effect.Hunger, "food");
            Instant(parts, effect.Thirst, "water");
            Instant(parts, effect.Warmth, "warmth");
            Instant(parts, effect.Stamina, "stamina");

            if (parts.Length == 0) parts.Append("no immediate effect");

            if (!effect.Lasts) return parts.ToString();

            string name = string.IsNullOrWhiteSpace(effect.DisplayName) ? effect.Id : effect.DisplayName;
            parts.Append($", then {name} for {effect.Duration:0}s");

            return parts.ToString();
        }

        static void Instant(StringBuilder text, float amount, string label)
        {
            if (Mathf.Approximately(amount, 0f)) return;

            if (text.Length > 0) text.Append(", ");
            text.Append(amount > 0f ? $"+{amount:0.#} {label}" : $"{amount:0.#} {label}");
        }
    }
}
