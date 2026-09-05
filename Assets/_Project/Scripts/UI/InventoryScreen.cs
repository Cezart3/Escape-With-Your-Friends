using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Player;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// The bag, on Tab. Twenty slots, drag and drop, tooltips, and the chest you are standing at.
    ///
    /// **Its own canvas, and only its own canvas has a raycaster.** The always-on HUD deliberately
    /// has none, because a full-screen raycaster is the classic way a HUD quietly eats the click that
    /// was meant to swing a fist. This screen brings one with it and takes the mouse on purpose: when
    /// it opens it tells <see cref="PlayerInputReader.SetUiOpen"/>, which frees the cursor and stops
    /// the world reading input at all. Closing gives both back. There is no in-between state where
    /// the mouse is doing two jobs.
    ///
    /// **What a drag means, in one rule:** the stack under the cursor goes to the slot you released
    /// it on. Same container, and the server merges or swaps; different container, and it is a
    /// transfer. Holding Shift moves half. That is the whole grammar, and it is the same on both
    /// grids so there is nothing to learn twice.
    ///
    /// **Nothing here decides anything.** Every drop is a request: <c>MoveSlot</c>, <c>SplitSlot</c>,
    /// <c>RequestStore</c>, <c>RequestTake</c>. The screen redraws from replicated state on the next
    /// frame, so a refused move simply does not happen and the squares snap back - there is no
    /// optimistic local copy to get out of step.
    ///
    /// Laid out against 1920x1080 through the canvas scaler, at 76px squares with 6px gaps: a
    /// four-by-five grid is 404px wide, which leaves the chest's six-by-five beside it and still fits
    /// on a 1280x720 screen after scaling.
    /// </summary>
    public class InventoryScreen : ISlotHost
    {
        const int Columns = 5;
        const float PanelPad = 16f;
        const float HeaderHeight = 30f;
        const float PanelGap = 26f;

        readonly ItemTooltip _tooltip = new();

        SlotView[] _bagSlots;
        SlotView[] _chestSlots;
        SlotView[] _shopRows;

        Canvas _canvas;
        RectTransform _root;
        RectTransform _bagPanel;
        RectTransform _chestPanel;
        RectTransform _shopPanel;
        Text _bagHeader;
        Text _chestHeader;
        Text _shopHeader;
        Text _hint;
        Text _message;

        RectTransform _dragGhost;
        Text _dragLabel;
        SlotView _dragFrom;

        Inventory _bag;
        Economy.Trading _trading;
        Storage _chest;
        Economy.ShopCounter _counter;
        SlotView _hovered;
        string _shownRefusal;
        float _messageUntil;

        public bool IsOpen { get; private set; }

        /// <summary>How many chest slots the screen can draw. A chest is 30; this is the ceiling.</summary>
        public const int ChestSlots = 30;

        public const int BagSlots = 20;

        /// <summary>How many shelf lines the screen can draw. The trader has twelve.</summary>
        public const int ShopRows = 16;

        const float RowHeight = 34f;
        const float RowGap = 4f;

        // ---------------------------------------------------------------- building

        public void Build(int sortOrder)
        {
            var go = new GameObject("InventoryCanvas", typeof(RectTransform));
            Object.DontDestroyOnLoad(go);

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = sortOrder;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;

            // Match height rather than evenly: this screen is a fixed grid, and on a wide monitor it
            // should stay the size it was designed at rather than growing with the extra width.
            scaler.matchWidthOrHeight = 1f;

            go.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();

            _root = (RectTransform)go.transform;

            Image dim = HudFactory.Block(_root, "Dim", new Color(0f, 0f, 0f, 0.55f));
            dim.rectTransform.anchorMin = Vector2.zero;
            dim.rectTransform.anchorMax = Vector2.one;
            dim.rectTransform.offsetMin = Vector2.zero;
            dim.rectTransform.offsetMax = Vector2.zero;

            // The dim layer eats clicks that land on nothing, so a drag released outside a slot is a
            // cancelled drag rather than a click that reaches whatever is behind the screen.
            dim.raycastTarget = true;

            BuildPanels();
            BuildDragGhost();
            _tooltip.Build(_root);

            SetOpen(false);
        }

        void BuildPanels()
        {
            float gridWidth = Columns * SlotView.Size + (Columns - 1) * SlotView.Gap;

            int bagRows = Mathf.CeilToInt(BagSlots / (float)Columns);
            int chestRows = Mathf.CeilToInt(ChestSlots / (float)Columns);

            float bagHeight = bagRows * SlotView.Size + (bagRows - 1) * SlotView.Gap;
            float chestHeight = chestRows * SlotView.Size + (chestRows - 1) * SlotView.Gap;

            float panelWidth = gridWidth + PanelPad * 2f;

            // Both panels are placed relative to the centre so that with the chest hidden the bag is
            // centred, and with it shown the pair is. A player who walks up to a chest should not
            // watch their own bag jump across the screen.
            _bagPanel = Panel("Bag", new Vector2(-(panelWidth + PanelGap) * 0.5f, 0f),
                              panelWidth, bagHeight + PanelPad * 2f + HeaderHeight,
                              out _bagHeader, out RectTransform bagGrid);

            _chestPanel = Panel("Chest", new Vector2((panelWidth + PanelGap) * 0.5f, 0f),
                                panelWidth, chestHeight + PanelPad * 2f + HeaderHeight,
                                out _chestHeader, out RectTransform chestGrid);

            _shopPanel = Panel("Shop", new Vector2((panelWidth + PanelGap) * 0.5f, 0f),
                               panelWidth, chestHeight + PanelPad * 2f + HeaderHeight,
                               out _shopHeader, out RectTransform shopGrid);

            _bagSlots = Grid(bagGrid, SlotKind.Bag, BagSlots);
            _chestSlots = Grid(chestGrid, SlotKind.Chest, ChestSlots);

            // The shelf shares the chest's rectangle, because only one of them is ever open: you are
            // either at a chest or at a counter, never both.
            _shopRows = new SlotView[ShopRows];
            for (int i = 0; i < ShopRows; i++)
                _shopRows[i] = SlotView.Create(shopGrid, this, SlotKind.Shop, i,
                                               new Vector2(0f, -i * (RowHeight + RowGap)),
                                               new Vector2(gridWidth, RowHeight));

            _hint = HudFactory.Label(_root, "Hint", 14, TextAnchor.UpperCenter);
            _hint.color = new Color(0.72f, 0.72f, 0.78f);
            HudFactory.Anchor((RectTransform)_hint.transform, new Vector2(0.5f, 0.5f),
                              new Vector2(0.5f, 1f),
                              new Vector2(0f, -(bagHeight + PanelPad * 2f + HeaderHeight) * 0.5f - 14f),
                              new Vector2(900f, 20f));
            _hint.text = "drag to move  -  shift-drag for half  -  right-click to store or sell  -  tab to close";

            // One line under the hint for whatever the server just said no to. Shown for a few
            // seconds and then gone: a refusal is news, not state.
            _message = HudFactory.Label(_root, "Message", 15, TextAnchor.UpperCenter);
            _message.color = new Color(1f, 0.55f, 0.45f);
            HudFactory.Anchor((RectTransform)_message.transform, new Vector2(0.5f, 0.5f),
                              new Vector2(0.5f, 1f),
                              new Vector2(0f, -(bagHeight + PanelPad * 2f + HeaderHeight) * 0.5f - 36f),
                              new Vector2(900f, 20f));
        }

        RectTransform Panel(string name, Vector2 centre, float width, float height,
                            out Text header, out RectTransform grid)
        {
            RectTransform panel = HudFactory.Rect(_root, name);
            HudFactory.Anchor(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), centre,
                              new Vector2(width, height));

            Image back = HudFactory.Block(panel, "Back", new Color(0.07f, 0.07f, 0.09f, 0.93f));
            back.rectTransform.anchorMin = Vector2.zero;
            back.rectTransform.anchorMax = Vector2.one;
            back.rectTransform.offsetMin = Vector2.zero;
            back.rectTransform.offsetMax = Vector2.zero;

            header = HudFactory.Label(panel, "Header", 20, TextAnchor.MiddleLeft);
            HudFactory.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                              new Vector2(PanelPad, -PanelPad * 0.5f),
                              new Vector2(width - PanelPad * 2f, HeaderHeight));

            grid = HudFactory.Rect(panel, "Grid");
            HudFactory.Anchor(grid, new Vector2(0f, 1f), new Vector2(0f, 1f),
                              new Vector2(PanelPad, -(PanelPad * 0.5f + HeaderHeight)),
                              new Vector2(width - PanelPad * 2f, height));

            return panel;
        }

        SlotView[] Grid(RectTransform parent, SlotKind kind, int count)
        {
            var slots = new SlotView[count];

            for (int i = 0; i < count; i++)
            {
                int column = i % Columns;
                int row = i / Columns;

                slots[i] = SlotView.Create(parent, this, kind, i,
                                           new Vector2(column * (SlotView.Size + SlotView.Gap),
                                                       -row * (SlotView.Size + SlotView.Gap)));
            }

            return slots;
        }

        void BuildDragGhost()
        {
            _dragGhost = HudFactory.Rect(_root, "Drag");
            HudFactory.Anchor(_dragGhost, new Vector2(0f, 0f), new Vector2(0.5f, 0.5f), Vector2.zero,
                              new Vector2(SlotView.Size, SlotView.Size));

            Image back = HudFactory.Block(_dragGhost, "Back", new Color(0.30f, 0.34f, 0.42f, 0.85f));
            back.rectTransform.anchorMin = Vector2.zero;
            back.rectTransform.anchorMax = Vector2.one;
            back.rectTransform.offsetMin = Vector2.zero;
            back.rectTransform.offsetMax = Vector2.zero;

            _dragLabel = HudFactory.Label(_dragGhost, "Label", 12, TextAnchor.MiddleCenter);
            _dragLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            HudFactory.Anchor((RectTransform)_dragLabel.transform, new Vector2(0.5f, 0.5f),
                              new Vector2(0.5f, 0.5f), Vector2.zero,
                              new Vector2(SlotView.Size - 6f, SlotView.Size - 6f));

            _dragGhost.gameObject.SetActive(false);
        }

        /// <summary>
        /// uGUI needs exactly one EventSystem in the scene and it is nobody's obvious job to make it.
        /// Created here rather than baked into Bootstrap because this screen is the only thing in the
        /// game that needs pointer events, and a scene carrying one for a screen that may never open
        /// is a dependency the next person has to work out the reason for.
        /// </summary>
        static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;

            var go = new GameObject("EventSystem", typeof(EventSystem));
            Object.DontDestroyOnLoad(go);

            // The Input System package replaces the old StandaloneInputModule; using the old one with
            // the new backend gives a screen that draws perfectly and ignores the mouse.
            go.AddComponent<InputSystemUIInputModule>();
        }

        // ---------------------------------------------------------------- open and close

        public void Refresh(NetworkObjectHolder holder)
        {
            if (_root == null) return;

            _bag = holder.Bag;
            _trading = holder.Trading;

            if (_bag == null || !_bag.IsSpawned)
            {
                if (IsOpen) SetOpen(false);
                return;
            }

            if (holder.Reader != null && holder.Reader.ConsumeToggleInventory()) SetOpen(!IsOpen);

            if (!IsOpen) return;

            // Re-asked every frame rather than latched on open: walk away from the chest and the
            // panel goes with it, which is the same rule the server enforces on the transfer.
            _counter = Economy.ShopCounter.NearestInReach(_bag.transform.position);
            _chest = _counter != null ? null : Storage.NearestInReach(_bag.transform.position);

            DrawBag();
            DrawChest();
            DrawShop();
            DrawMessage();
        }

        public void SetOpen(bool open)
        {
            IsOpen = open;

            if (_root != null) _root.gameObject.SetActive(open);
            if (!open)
            {
                _dragFrom = null;
                if (_dragGhost != null) _dragGhost.gameObject.SetActive(false);
                _tooltip.Hide();
            }

            PlayerInputReader reader = FindReader();
            if (reader != null) reader.SetUiOpen(open);
        }

        static PlayerInputReader FindReader()
        {
            FishNet.Object.NetworkObject body = SquadModel.FindLocalBody();
            return body != null ? body.GetComponent<PlayerInputReader>() : null;
        }

        void DrawBag()
        {
            _bagHeader.text = $"Backpack   {Hotbar.WeightText(_bag)}";
            _bagHeader.color = _bag.Overloaded ? new Color(1f, 0.5f, 0.4f) : Color.white;

            for (int i = 0; i < _bagSlots.Length; i++)
            {
                _bagSlots[i].Draw(_bag[i]);
                _bagSlots[i].SetSelected(_bag.SelectedSlot == i);
            }
        }

        void DrawChest()
        {
            bool has = _chest != null;
            if (_chestPanel.gameObject.activeSelf != has) _chestPanel.gameObject.SetActive(has);

            // With nothing on the right the bag slides back to the middle, so the screen is not
            // permanently off-centre for the ninety percent of the game spent nowhere near either.
            bool paired = _chest != null || _counter != null;
            _bagPanel.anchoredPosition = paired
                ? new Vector2(-(_bagPanel.sizeDelta.x + PanelGap) * 0.5f, 0f)
                : Vector2.zero;

            if (!has) return;

            _chestHeader.text = $"Chest   {_chest.UsedSlots}/{_chest.SlotCount}";

            for (int i = 0; i < _chestSlots.Length; i++)
                _chestSlots[i].Draw(i < _chest.SlotCount ? _chest[i] : ItemStack.Empty);
        }

        void DrawShop()
        {
            bool has = _counter != null && _counter.Shop != null;
            if (_shopPanel.gameObject.activeSelf != has) _shopPanel.gameObject.SetActive(has);
            if (!has) return;

            Data.ShopDef shop = _counter.Shop;
            _shopHeader.text = $"{shop.DisplayName}   pays {shop.BuyBackFraction:P0} of value";

            for (int i = 0; i < _shopRows.Length; i++)
            {
                SlotView row = _shopRows[i];
                bool live = i < _counter.OfferCount;

                if (row.gameObject.activeSelf != live) row.gameObject.SetActive(live);
                if (!live) continue;

                Data.ShopDef.Offer offer = _counter.OfferAt(i);
                ushort index = offer.Item != null && Data.ItemCatalog.Active != null
                    ? Data.ItemCatalog.Active.IndexOf(offer.Item)
                    : (ushort)0;

                row.Draw(new ItemStack(index, 1));

                int left = _counter.Remaining(i);
                row.SetNote(offer.Unlimited
                                ? $"${offer.Price}"
                                : left > 0 ? $"${offer.Price}   {left} left" : "sold out");
            }
        }

        void DrawMessage()
        {
            if (_trading != null && _trading.LastRefusal != null
                && _trading.LastRefusal != _shownRefusal)
            {
                _shownRefusal = _trading.LastRefusal;
                _messageUntil = Time.time + 3f;
            }

            bool showing = Time.time < _messageUntil;
            if (_message.gameObject.activeSelf != showing) _message.gameObject.SetActive(showing);
            if (showing) _message.text = _shownRefusal;
        }

        // ---------------------------------------------------------------- what a drag means

        public void SlotEnter(SlotView slot)
        {
            _hovered = slot;

            if (_dragFrom != null || slot.Stack.IsEmpty) return;

            _tooltip.Show(slot.Stack, MousePosition(), _root);
        }

        public void SlotExit(SlotView slot)
        {
            if (_hovered == slot) _hovered = null;

            _tooltip.Hide();
        }

        public void SlotBeginDrag(SlotView slot, PointerEventData pointer)
        {
            if (slot.Stack.IsEmpty) return;

            _dragFrom = slot;
            _tooltip.Hide();

            _dragLabel.text = slot.Stack.ToString();
            _dragGhost.gameObject.SetActive(true);
            SlotDrag(pointer);
        }

        public void SlotDrag(PointerEventData pointer)
        {
            if (_dragFrom == null) return;

            RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, pointer.position, null,
                                                                   out Vector2 local);

            _dragGhost.anchoredPosition = local - _root.rect.min;
        }

        public void SlotEndDrag(SlotView slot)
        {
            // Raised after OnDrop, so by the time this runs the transfer has already been asked for.
            // Clearing here rather than there is what makes a drag released over nothing a no-op.
            _dragFrom = null;
            _dragGhost.gameObject.SetActive(false);
        }

        public void SlotDrop(SlotView target)
        {
            if (_dragFrom == null || target == _dragFrom || _bag == null) return;

            ItemStack moving = _dragFrom.Stack;
            if (moving.IsEmpty) return;

            bool half = IsShiftHeld();
            int count = half ? Mathf.Max(1, moving.Count / 2) : moving.Count;

            if (_dragFrom.Kind == target.Kind)
            {
                // Within one container. The bag has its own move and split; a chest-to-chest drag is
                // a take followed by a store, which the server does in one call each way.
                if (_dragFrom.Kind == SlotKind.Bag)
                {
                    if (half) _bag.SplitSlot(_dragFrom.Index, target.Index);
                    else _bag.MoveSlot(_dragFrom.Index, target.Index);
                }

                return;
            }

            if (_chest == null) return;

            if (_dragFrom.Kind == SlotKind.Bag) _bag.RequestStore(_chest, _dragFrom.Index, count);
            else _bag.RequestTake(_chest, _dragFrom.Index, count);
        }

        /// <summary>
        /// A click with no drag, in one rule: **left is here, right is over there.**
        ///
        /// Left-clicking a bag slot picks it for the hotbar. Right-clicking one sends it to whatever
        /// is open on the right - into the chest, or across the counter to be sold. A chest slot or a
        /// shelf line takes or buys on either button, because there is only one thing either of them
        /// can mean. Shift buys five.
        ///
        /// One rule rather than three because the alternative is a screen where the same click does
        /// different things depending on what you happen to be standing next to.
        /// </summary>
        public void SlotClick(SlotView slot, PointerEventData pointer)
        {
            if (_bag == null || pointer.dragging) return;

            bool right = pointer.button == PointerEventData.InputButton.Right;

            switch (slot.Kind)
            {
                case SlotKind.Bag when !right:
                    if (slot.Index < Inventory.HotbarSlots) _bag.SelectSlot(slot.Index);
                    return;

                case SlotKind.Bag:
                    if (slot.Stack.IsEmpty) return;

                    if (_counter != null && _trading != null)
                        _trading.RequestSell(_counter, slot.Index, slot.Stack.Count);
                    else if (_chest != null)
                        _bag.RequestStore(_chest, slot.Index, slot.Stack.Count);

                    return;

                case SlotKind.Chest:
                    if (_chest != null && !slot.Stack.IsEmpty)
                        _bag.RequestTake(_chest, slot.Index, slot.Stack.Count);

                    return;

                case SlotKind.Shop:
                    if (_counter != null && _trading != null)
                        _trading.RequestBuy(_counter, slot.Index, IsShiftHeld() ? 5 : 1);

                    return;
            }
        }

        static bool IsShiftHeld()
        {
            UnityEngine.InputSystem.Keyboard keyboard = UnityEngine.InputSystem.Keyboard.current;
            return keyboard != null && keyboard.shiftKey.isPressed;
        }

        static Vector2 MousePosition()
        {
            UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null ? mouse.position.ReadValue() : Vector2.zero;
        }

        /// <summary>
        /// What the screen is looking at, for the log. Says the same things the panels do, which is
        /// the only part of this a headless run can report.
        /// </summary>
        public string Describe()
        {
            if (_bag == null) return "no bag";

            string beside = _counter != null
                ? _counter.Describe()
                : _chest != null ? _chest.Describe() : "nothing in reach";

            return $"{(IsOpen ? "open" : "closed")} | {_bag.Describe()} | {beside}";
        }

        /// <summary>
        /// The two components the screen needs off the local body, gathered once by the HUD so this
        /// class never walks the registry itself.
        /// </summary>
        public readonly struct NetworkObjectHolder
        {
            public readonly Inventory Bag;
            public readonly PlayerInputReader Reader;
            public readonly Economy.Wallet Purse;
            public readonly Economy.Trading Trading;

            public NetworkObjectHolder(Inventory bag, PlayerInputReader reader, Economy.Wallet purse,
                                       Economy.Trading trading)
            {
                Bag = bag;
                Reader = reader;
                Purse = purse;
                Trading = trading;
            }

            public static NetworkObjectHolder FromLocal()
            {
                FishNet.Object.NetworkObject body = SquadModel.FindLocalBody();
                if (body == null) return default;

                return new NetworkObjectHolder(body.GetComponent<Inventory>(),
                                               body.GetComponent<PlayerInputReader>(),
                                               body.GetComponent<Economy.Wallet>(),
                                               body.GetComponent<Economy.Trading>());
            }
        }
    }
}
