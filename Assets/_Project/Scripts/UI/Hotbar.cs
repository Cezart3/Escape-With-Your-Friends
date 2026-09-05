using EscapeWithYourFriends.Items;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// Five squares across the bottom of the screen, and the weight you are carrying above them.
    ///
    /// Bottom centre rather than a corner: the hotbar is the one part of the HUD you look at with
    /// your hands on the keys, and the middle of the bottom edge is where the eye already goes when
    /// it leaves the crosshair. The four survival bars stay in the corner precisely because they are
    /// the opposite kind of information.
    ///
    /// **It is not clickable, on purpose.** The always-on HUD canvas has no raycaster, so nothing
    /// here can eat a mouse click meant for the world - the hotbar is driven by the number keys and
    /// the wheel, which is how anybody actually uses one. Clicking slots is for the inventory screen,
    /// which brings its own raycaster and takes the mouse deliberately.
    ///
    /// The weight line sits above it rather than inside the bag screen alone, because "am I about to
    /// be overloaded" is a question you ask while picking things up, not while sorting them.
    /// </summary>
    public class Hotbar
    {
        const float Margin = 24f;

        readonly SlotView[] _slots = new SlotView[Inventory.HotbarSlots];

        RectTransform _root;
        Text _weight;
        Inventory _bag;

        public void Build(RectTransform parent)
        {
            float width = Inventory.HotbarSlots * SlotView.Size
                          + (Inventory.HotbarSlots - 1) * SlotView.Gap;

            _root = HudFactory.Rect(parent, "Hotbar");
            HudFactory.Anchor(_root, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(0f, Margin), new Vector2(width, SlotView.Size));

            // Anchored top-left inside the strip because SlotView.Create places from that corner,
            // which is the same maths the inventory grid uses.
            RectTransform row = HudFactory.Rect(_root, "Row");
            HudFactory.Anchor(row, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero,
                              new Vector2(width, SlotView.Size));

            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = SlotView.Create(row, host: null, SlotKind.Hotbar, i,
                                            new Vector2(i * (SlotView.Size + SlotView.Gap), 0f));

                // Nothing on this canvas is clickable; leaving the raycast target on would be a lie
                // rather than a bug, but it is a lie the next person would have to disprove.
                _slots[i].Background.raycastTarget = false;

                Label(_slots[i], i + 1);
            }

            _weight = HudFactory.Label(_root, "Weight", 15, TextAnchor.LowerCenter);
            HudFactory.Anchor((RectTransform)_weight.transform, new Vector2(0.5f, 1f),
                              new Vector2(0.5f, 0f), new Vector2(0f, 6f), new Vector2(260f, 20f));
        }

        /// <summary>The key that picks this slot, in its corner. Static, so it is drawn once.</summary>
        static void Label(SlotView slot, int number)
        {
            Text key = HudFactory.Label(slot.transform, "Key", 11, TextAnchor.UpperLeft);
            key.color = new Color(0.75f, 0.75f, 0.80f, 0.85f);
            HudFactory.Anchor((RectTransform)key.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                              new Vector2(4f, -3f), new Vector2(16f, 16f));
            key.text = number.ToString();
        }

        public void Refresh(Inventory bag)
        {
            _bag = bag;

            if (_root == null) return;

            bool has = bag != null && bag.IsSpawned;
            if (_root.gameObject.activeSelf != has) _root.gameObject.SetActive(has);
            if (!has) return;

            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i].Draw(bag[i]);
                _slots[i].SetSelected(bag.SelectedSlot == i);
            }

            _weight.text = WeightText(bag);
            _weight.color = bag.Overloaded
                ? new Color(1f, 0.45f, 0.35f)
                : new Color(0.85f, 0.85f, 0.88f);
        }

        /// <summary>
        /// "25.5 / 40 kg", or "25.5 / 40 kg - overloaded". Pure, so the harness can read it without
        /// a canvas.
        /// </summary>
        public static string WeightText(Inventory bag)
        {
            if (bag == null) return string.Empty;

            string text = $"{bag.Weight:0.#} / {bag.CarryLimit:0.#} kg";
            return bag.Overloaded ? text + "  -  overloaded" : text;
        }

        /// <summary>One line for the log, so -uiTest can say what the bar would be showing.</summary>
        public string Describe()
        {
            if (_bag == null) return "no bag";

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < Inventory.HotbarSlots; i++)
            {
                if (i > 0) text.Append(' ');
                text.Append(_bag.SelectedSlot == i ? '[' : ' ');
                text.Append(_bag[i].ToString());
                text.Append(_bag.SelectedSlot == i ? ']' : ' ');
            }

            return text.ToString();
        }
    }
}
