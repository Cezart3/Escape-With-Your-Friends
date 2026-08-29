using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// Builds uGUI widgets in code, so no part of the HUD is a prefab someone assembled by hand.
    ///
    /// Same rule as the scene and the arena: a thing that only exists as a binary cannot be reviewed
    /// in a diff or rebuilt from a terminal. A HUD is the easiest place in a project to break that
    /// rule, because dragging rectangles around is faster than typing them — right up to the point
    /// where the layout has to change and nobody can tell what it used to be.
    ///
    /// **Legacy <see cref="Text"/> rather than TextMeshPro, deliberately.** TMP needs its essential
    /// resources imported into the project through an editor menu before a single character will
    /// render, and an asset that only appears when a human clicks a menu item is exactly the kind of
    /// dependency this project keeps out. <see cref="Font"/> here comes from a built-in resource that
    /// is always present in a build. The greybox HUD does not need SDF text; when it does, this file
    /// is the only one that changes.
    /// </summary>
    public static class HudFactory
    {
        static Font _font;

        /// <summary>The built-in font, resolved once. Null only if the runtime has no fonts at all.</summary>
        public static Font Font
        {
            get
            {
                if (_font != null) return _font;

                // Renamed from Arial.ttf in 2022.2. Both names are tried because the fallback costs
                // nothing and a HUD with no font is a HUD with no text at all.
                _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                        ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

                return _font;
            }
        }

        /// <summary>An empty stretchable rect parented under <paramref name="parent"/>.</summary>
        public static RectTransform Rect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.localScale = Vector3.one;
            return rect;
        }

        /// <summary>A flat colour block. Used for swatches, bars and panel backgrounds.</summary>
        public static Image Block(Transform parent, string name, Color color)
        {
            RectTransform rect = Rect(parent, name);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;

            // Nothing in the HUD is clickable yet, and a raycast target that swallows clicks is the
            // classic way a HUD quietly breaks the game underneath it.
            image.raycastTarget = false;

            return image;
        }

        /// <summary>A line of text. Never wraps: every string the HUD draws is a few words.</summary>
        public static Text Label(Transform parent, string name, int size,
                                 TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            RectTransform rect = Rect(parent, name);

            var text = rect.gameObject.AddComponent<Text>();
            text.font = Font;
            text.fontSize = size;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.color = Color.white;

            // A HUD sits over whatever the world happens to be, which on this island is bright sand
            // as often as it is night. An outline is cheaper than a backing plate and survives both.
            var outline = rect.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.75f);
            outline.effectDistance = new Vector2(1.2f, -1.2f);

            return text;
        }

        /// <summary>Anchors a rect to one corner with a pixel offset, sized in reference pixels.</summary>
        public static RectTransform Anchor(RectTransform rect, Vector2 anchor, Vector2 pivot,
                                           Vector2 offset, Vector2 size)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = offset;
            rect.sizeDelta = size;
            return rect;
        }
    }
}
