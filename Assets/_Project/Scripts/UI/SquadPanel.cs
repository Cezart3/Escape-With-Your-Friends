using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// The corner list: one row per player, with what happened to them and how long you have.
    ///
    /// Rows are built once and then reused. Four players is a small number, but this refreshes every
    /// frame and a panel that destroys and recreates its widgets would allocate garbage forever for
    /// no reason. Rows past the current player count are hidden rather than destroyed, so a player
    /// joining mid-session costs one row's worth of construction and never a rebuild of the panel.
    ///
    /// Order is <see cref="SquadModel"/>'s order, which is registration order. Sorting by "most
    /// urgent" was the obvious alternative and is wrong: rows that move while you are reading them
    /// are rows you have to re-find every time somebody goes down, which is exactly the moment you
    /// have no attention to spare.
    /// </summary>
    public class SquadPanel
    {
        /// <summary>Widgets for one player. Kept together so a refresh is a straight-line write.</summary>
        class Row
        {
            public RectTransform Rect;
            public Image Swatch;
            public Text Name;
            public Text Status;
            public RectTransform BarFill;
            public Image Bar;
        }

        const float RowHeight = 26f;
        const float RowSpacing = 4f;
        const float PanelWidth = 260f;

        readonly List<Row> _rows = new();

        RectTransform _root;

        /// <summary>Builds the panel under <paramref name="parent"/>, top-left of the screen.</summary>
        public void Build(RectTransform parent)
        {
            _root = HudFactory.Rect(parent, "SquadPanel");
            HudFactory.Anchor(_root, new Vector2(0f, 1f), new Vector2(0f, 1f),
                              new Vector2(24f, -24f), new Vector2(PanelWidth, 0f));
        }

        /// <summary>Redraws the panel from <paramref name="entries"/>. Cheap enough to call per frame.</summary>
        public void Refresh(List<SquadModel.Entry> entries)
        {
            if (_root == null) return;

            while (_rows.Count < entries.Count) _rows.Add(BuildRow(_rows.Count));

            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];

                if (i >= entries.Count)
                {
                    if (row.Rect.gameObject.activeSelf) row.Rect.gameObject.SetActive(false);
                    continue;
                }

                if (!row.Rect.gameObject.activeSelf) row.Rect.gameObject.SetActive(true);

                SquadModel.Entry entry = entries[i];

                row.Swatch.color = entry.Color;

                // The local player is marked in the list rather than pulled out of it. Every row in
                // the same place every session is worth more than the half-second of "which one am I".
                row.Name.text = entry.IsLocal ? $"{entry.Name} (you)" : entry.Name;
                row.Name.color = entry.IsLocal ? Color.white : new Color(0.82f, 0.84f, 0.86f);

                row.Status.text = SquadModel.StatusText(entry);
                row.Status.color = SquadModel.StatusColor(entry);

                // One bar, two meanings: how long is left, or how far the help has got. They never
                // overlap — the moment somebody starts helping, the countdown stops being the thing
                // you are watching.
                float fill = entry.IsBeingRescued ? entry.RescueProgress : entry.BleedOutNormalized;
                bool showBar = entry.State == LifeState.Downed;

                if (row.Bar.enabled != showBar) row.Bar.enabled = showBar;
                if (row.BarFill.gameObject.activeSelf != showBar)
                    row.BarFill.gameObject.SetActive(showBar);

                if (showBar)
                {
                    row.BarFill.anchorMax = new Vector2(Mathf.Clamp01(fill), 1f);
                    row.Bar.color = SquadModel.StatusColor(entry);
                }
            }
        }

        Row BuildRow(int index)
        {
            var row = new Row();

            row.Rect = HudFactory.Rect(_root, $"Row{index}");
            HudFactory.Anchor(row.Rect, new Vector2(0f, 1f), new Vector2(0f, 1f),
                              new Vector2(0f, -index * (RowHeight + RowSpacing)),
                              new Vector2(PanelWidth, RowHeight));

            // The swatch is the only place the player's own colour appears, which is what makes it
            // usable as identity: everything else on the row is coloured by what is happening.
            row.Swatch = HudFactory.Block(row.Rect, "Swatch", Color.white);
            HudFactory.Anchor(row.Swatch.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                              new Vector2(0f, 0f), new Vector2(6f, 18f));

            row.Name = HudFactory.Label(row.Rect, "Name", 15);
            HudFactory.Anchor(row.Name.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                              new Vector2(14f, 0f), new Vector2(150f, 18f));

            row.Status = HudFactory.Label(row.Rect, "Status", 15, TextAnchor.MiddleRight);
            HudFactory.Anchor(row.Status.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                              new Vector2(0f, 0f), new Vector2(110f, 18f));

            // Track and fill share a rect so the fill can be driven by an anchor rather than a width,
            // which keeps it correct at any resolution without a layout pass.
            Image track = HudFactory.Block(row.Rect, "BarTrack", new Color(0f, 0f, 0f, 0.45f));
            HudFactory.Anchor(track.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                              new Vector2(14f, 0f), new Vector2(PanelWidth - 14f, 4f));

            row.Bar = HudFactory.Block(track.rectTransform, "BarFill", Color.white);
            row.BarFill = row.Bar.rectTransform;
            row.BarFill.anchorMin = Vector2.zero;
            row.BarFill.anchorMax = new Vector2(1f, 1f);
            row.BarFill.offsetMin = Vector2.zero;
            row.BarFill.offsetMax = Vector2.zero;

            return row;
        }
    }
}
