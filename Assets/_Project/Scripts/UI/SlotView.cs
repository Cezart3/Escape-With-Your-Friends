using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>Which container a slot belongs to. The wire the drag is carrying, in one enum.</summary>
    public enum SlotKind
    {
        Bag,
        Chest,
        Hotbar,

        /// <summary>A line on the trader's shelf. Wider than a square, and priced.</summary>
        Shop
    }

    /// <summary>
    /// What a slot widget reports to whoever owns the screen. An interface rather than a pile of
    /// delegates, because a slot raises seven different things and six of them need the slot itself.
    /// </summary>
    public interface ISlotHost
    {
        void SlotEnter(SlotView slot);
        void SlotExit(SlotView slot);
        void SlotBeginDrag(SlotView slot, PointerEventData pointer);
        void SlotDrag(PointerEventData pointer);
        void SlotEndDrag(SlotView slot);
        void SlotDrop(SlotView target);
        void SlotClick(SlotView slot, PointerEventData pointer);
    }

    /// <summary>
    /// One square. Draws a stack and turns mouse events into calls on the screen that owns it.
    ///
    /// This is a MonoBehaviour, unlike every other widget in this HUD, because uGUI's drag and drop
    /// is delivered through interfaces on components under the pointer - there is no way to receive
    /// <see cref="IBeginDragHandler"/> without being a component. It stays a dumb one: it knows what
    /// it looks like and where it is in a grid, and nothing at all about inventories.
    ///
    /// Draws the item's icon when it has one and its name when it does not, a count, and a strip
    /// in the item's category colour along the bottom.
    /// </summary>
    public class SlotView : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
                            IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler,
                            IPointerClickHandler
    {
        public const float Size = 76f;
        public const float Gap = 6f;

        static readonly Color Idle = new(0.11f, 0.12f, 0.15f, 0.94f);
        static readonly Color Hover = new(0.19f, 0.21f, 0.27f, 0.97f);
        static readonly Color Chosen = new(0.24f, 0.22f, 0.13f, 0.97f);

        // The frame does the talking: faint at rest, bright under the cursor, gold when chosen.
        static readonly Color IdleEdge = new(1f, 1f, 1f, 0.10f);
        static readonly Color HoverEdge = new(1f, 1f, 1f, 0.55f);
        static readonly Color ChosenEdge = new(1f, 0.80f, 0.35f, 0.95f);

        public SlotKind Kind { get; private set; }
        public int Index { get; private set; }
        public ItemStack Stack { get; private set; }

        public Image Background { get; private set; }
        public Image Icon { get; private set; }

        Text _name;
        Image _edge;
        Image _kindBar;
        bool _wide;
        Text _count;
        Text _note;
        ISlotHost _host;
        bool _hovered;
        bool _selected;

        /// <summary>Builds the widget under <paramref name="parent"/> at a grid position.</summary>
        public static SlotView Create(RectTransform parent, ISlotHost host, SlotKind kind, int index,
                                      Vector2 position, Vector2? size = null)
        {
            Vector2 box = size ?? new Vector2(Size, Size);
            bool wide = box.x > box.y * 1.5f;

            RectTransform rect = HudFactory.Rect(parent, $"{kind}{index}");
            HudFactory.Anchor(rect, new Vector2(0f, 1f), new Vector2(0f, 1f), position, box);

            var view = rect.gameObject.AddComponent<SlotView>();
            view._host = host;
            view.Kind = kind;
            view.Index = index;
            view._wide = wide;

            // The background is the raycast target, so the whole square is grabbable rather than
            // only the letters on it.
            view.Background = rect.gameObject.AddComponent<Image>();
            view.Background.color = Idle;
            view.Background.raycastTarget = true;
            view.Background.sprite = HudFactory.RoundFill;
            view.Background.type = Image.Type.Sliced;

            view._edge = HudFactory.Rounded(rect, "Edge", IdleEdge, ring: true);

            // A thin line along the bottom in the item's category colour, so a bag can be read at a
            // glance - red for weapons, green for food - without opening a single tooltip.
            view._kindBar = HudFactory.Block(rect, "Kind", Color.clear);
            HudFactory.Anchor(view._kindBar.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(0f, 3f), new Vector2(box.x - 20f, 3f));

            view.Icon = HudFactory.Block(rect, "Icon", new Color(1f, 1f, 1f, 0f));
            HudFactory.Anchor(view.Icon.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                              new Vector2(wide ? 6f : 8f, 0f),
                              wide ? new Vector2(box.y - 8f, box.y - 8f)
                                   : new Vector2(box.x - 16f, box.y - 16f));
            view.Icon.preserveAspect = true;

            // A square reads name-over-count; a row reads name on the left and price on the right,
            // which is how every shelf anybody has ever looked at is laid out.
            view._name = HudFactory.Label(rect, "Name", wide ? 15 : 12,
                                          wide ? TextAnchor.MiddleLeft : TextAnchor.UpperCenter);
            view._name.horizontalOverflow = HorizontalWrapMode.Wrap;
            HudFactory.Anchor((RectTransform)view._name.transform,
                              wide ? new Vector2(0f, 0.5f) : new Vector2(0.5f, 1f),
                              wide ? new Vector2(0f, 0.5f) : new Vector2(0.5f, 1f),
                              wide ? new Vector2(box.y + 4f, 0f) : new Vector2(0f, -6f),
                              wide ? new Vector2(box.x * 0.5f, box.y) : new Vector2(box.x - 8f, box.y - 20f));

            view._count = HudFactory.Label(rect, "Count", 14, TextAnchor.LowerRight);
            HudFactory.Anchor((RectTransform)view._count.transform, new Vector2(1f, 0f),
                              new Vector2(1f, 0f), new Vector2(-5f, 4f), new Vector2(40f, 18f));

            view._note = HudFactory.Label(rect, "Note", wide ? 15 : 13,
                                          wide ? TextAnchor.MiddleRight : TextAnchor.LowerLeft);
            view._note.color = new Color(0.88f, 0.84f, 0.60f);
            HudFactory.Anchor((RectTransform)view._note.transform,
                              wide ? new Vector2(1f, 0.5f) : new Vector2(0f, 0f),
                              wide ? new Vector2(1f, 0.5f) : new Vector2(0f, 0f),
                              wide ? new Vector2(-8f, 0f) : new Vector2(5f, 4f),
                              wide ? new Vector2(box.x * 0.45f, box.y) : new Vector2(50f, 16f));

            view.Draw(ItemStack.Empty);
            return view;
        }

        /// <summary>The line on the right of a row: a price, a stock count, whatever it is worth.</summary>
        public void SetNote(string note) => _note.text = note ?? string.Empty;

        /// <summary>Redraws for a stack. Called every frame the screen is open; cheap enough.</summary>
        public void Draw(ItemStack stack)
        {
            Stack = stack;

            ItemDef def = stack.Def;

            Sprite icon = def != null ? def.Icon : null;

            // A square with a picture in it needs no caption written across the picture; a shop row
            // keeps its name beside it.
            _name.text = def == null || (icon != null && !_wide)
                ? string.Empty
                : string.IsNullOrWhiteSpace(def.DisplayName) ? def.Id : def.DisplayName;

            _count.text = stack.Count > 1 ? stack.Count.ToString() : string.Empty;

            Icon.sprite = icon;
            Icon.color = icon != null ? Color.white : new Color(1f, 1f, 1f, 0f);

            _kindBar.color = def == null || _wide ? Color.clear : KindColour(def.Category);

            Repaint();
        }

        /// <summary>Marks this as the chosen hotbar slot.</summary>
        public void SetSelected(bool selected)
        {
            if (_selected == selected) return;

            _selected = selected;
            Repaint();
        }

        void Repaint()
        {
            Background.color = _selected ? Chosen : _hovered ? Hover : Idle;
            _edge.color = _selected ? ChosenEdge : _hovered ? HoverEdge : IdleEdge;
        }

        static Color KindColour(ItemCategory category) => category switch
        {
            ItemCategory.Weapon => new Color(0.95f, 0.36f, 0.30f, 0.9f),
            ItemCategory.Food => new Color(0.48f, 0.82f, 0.38f, 0.9f),
            ItemCategory.Drink => new Color(0.38f, 0.70f, 0.98f, 0.9f),
            ItemCategory.Medical => new Color(0.98f, 0.50f, 0.66f, 0.9f),
            ItemCategory.Tool => new Color(0.95f, 0.66f, 0.26f, 0.9f),
            ItemCategory.Quest => new Color(1f, 0.86f, 0.32f, 0.9f),
            ItemCategory.Material => new Color(0.72f, 0.62f, 0.48f, 0.9f),
            _ => new Color(0.70f, 0.70f, 0.74f, 0.9f),
        };

        // ---------------------------------------------------------------- pointer

        public void OnPointerEnter(PointerEventData pointer)
        {
            _hovered = true;
            Repaint();
            _host?.SlotEnter(this);
        }

        public void OnPointerExit(PointerEventData pointer)
        {
            _hovered = false;
            Repaint();
            _host?.SlotExit(this);
        }

        public void OnBeginDrag(PointerEventData pointer) => _host?.SlotBeginDrag(this, pointer);

        public void OnDrag(PointerEventData pointer) => _host?.SlotDrag(pointer);

        public void OnEndDrag(PointerEventData pointer) => _host?.SlotEndDrag(this);

        /// <summary>Raised on the slot the pointer was released over, not the one it started on.</summary>
        public void OnDrop(PointerEventData pointer) => _host?.SlotDrop(this);

        public void OnPointerClick(PointerEventData pointer) => _host?.SlotClick(this, pointer);
    }
}
