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

        /// <summary>
        /// Stretches a rect over its whole parent, with an optional inset in reference pixels.
        ///
        /// <see cref="Anchor"/> cannot do this and never could: it sets <c>anchorMin</c> and
        /// <c>anchorMax</c> to the same point, which is right for a corner-pinned widget and gives a
        /// rect of size zero when somebody passes it (0,0) and (1,1) meaning "fill the screen". That
        /// was a real bug in the ending screen (#74) and it could not be caught by a harness, because
        /// a headless run builds no canvas at all - so the full-screen idiom gets a name of its own.
        /// </summary>
        public static RectTransform Stretch(RectTransform rect, float inset = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
            return rect;
        }

        /// <summary>
        /// A clickable plate with a caption. The settings screen's toggles, its quality cycler and
        /// its rebind rows are all this one widget, because a button that changes its own caption is
        /// three fewer kinds of thing to build than a button, a toggle and a dropdown.
        /// </summary>
        public static Button Button(Transform parent, string name, string caption, int size,
                                    UnityEngine.Events.UnityAction onClick)
        {
            Image plate = Block(parent, name, new Color(0.18f, 0.20f, 0.24f, 0.95f));

            // Block turns raycasts off, which is right for every other widget in this HUD and wrong
            // for the one that exists to be clicked.
            plate.raycastTarget = true;

            var button = plate.gameObject.AddComponent<Button>();
            button.targetGraphic = plate;
            button.onClick.AddListener(() => Audio.Sfx.Play2D(Audio.Sound.Click));
            if (onClick != null) button.onClick.AddListener(onClick);

            Text label = Label(plate.transform, "Caption", size, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);
            label.text = caption;

            return button;
        }

        /// <summary>A horizontal slider, built from the three rects uGUI insists on.</summary>
        public static Slider Slider(Transform parent, string name, float min, float max, float value,
                                    UnityEngine.Events.UnityAction<float> onChange)
        {
            RectTransform rect = Rect(parent, name);
            var slider = rect.gameObject.AddComponent<Slider>();

            Image track = Block(rect, "Track", new Color(0.12f, 0.13f, 0.16f, 0.95f));
            Stretch(track.rectTransform, 0f);
            track.raycastTarget = true;

            RectTransform area = Rect(rect, "Fill Area");
            Stretch(area, 4f);

            Image fill = Block(area, "Fill", new Color(0.45f, 0.65f, 0.85f, 0.95f));
            Stretch(fill.rectTransform);

            RectTransform handleArea = Rect(rect, "Handle Slide Area");
            Stretch(handleArea, 4f);

            Image handle = Block(handleArea, "Handle", new Color(0.85f, 0.88f, 0.92f, 1f));
            handle.rectTransform.sizeDelta = new Vector2(18f, 0f);
            handle.raycastTarget = true;

            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = Mathf.Clamp(value, min, max);

            if (onChange != null) slider.onValueChanged.AddListener(onChange);

            return slider;
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
