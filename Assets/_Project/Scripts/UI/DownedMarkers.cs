using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// The markers that float over teammates who are on the floor.
    ///
    /// The squad list tells you *that* someone is down; this tells you *where*, which is the half
    /// that decides whether the timer means anything. A countdown you cannot act on is only stress.
    ///
    /// Two details do most of the work:
    /// <list type="bullet">
    /// <item>a marker for someone off-screen is clamped to the edge of the screen rather than hidden,
    /// because the common case is that they went down behind you;</item>
    /// <item>a marker for someone behind the camera has its projected point mirrored first —
    /// <see cref="Camera.WorldToScreenPoint"/> returns a point that is upside-down and backwards when
    /// z is negative, and drawing it unmirrored sends the arrow the wrong way.</item>
    /// </list>
    ///
    /// The anchor comes from <see cref="SquadModel.Entry.Anchor"/>, which is the hip bone. Following
    /// the ragdoll rather than the body root is what makes a marker stay over a corpse that has been
    /// picked up and carried off.
    /// </summary>
    public class DownedMarkers
    {
        class Marker
        {
            public RectTransform Rect;
            public Image Dot;
            public Text Label;
        }

        /// <summary>Height above the hip the marker floats at. Roughly head height on a downed body.</summary>
        const float WorldOffset = 1.1f;

        /// <summary>How far from the screen edge a clamped marker sits, in reference pixels.</summary>
        const float EdgeMargin = 48f;

        readonly List<Marker> _markers = new();

        RectTransform _root;

        /// <summary>Builds the marker layer under <paramref name="parent"/>, filling the screen.</summary>
        public void Build(RectTransform parent)
        {
            _root = HudFactory.Rect(parent, "DownedMarkers");
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.one;
            _root.offsetMin = Vector2.zero;
            _root.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Places one marker per entry that needs one. <paramref name="camera"/> is whatever is
        /// currently rendering the local player's view; with none there is nothing to project through
        /// and every marker is hidden.
        /// </summary>
        public void Refresh(List<SquadModel.Entry> entries, Camera camera)
        {
            if (_root == null) return;

            int used = 0;

            if (camera != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    SquadModel.Entry entry = entries[i];
                    if (!entry.NeedsMarker) continue;

                    while (_markers.Count <= used) _markers.Add(BuildMarker(_markers.Count));

                    Marker marker = _markers[used];
                    Place(marker, entry, camera);
                    used++;
                }
            }

            for (int i = used; i < _markers.Count; i++)
            {
                if (_markers[i].Rect.gameObject.activeSelf)
                    _markers[i].Rect.gameObject.SetActive(false);
            }
        }

        void Place(Marker marker, in SquadModel.Entry entry, Camera camera)
        {
            if (!marker.Rect.gameObject.activeSelf) marker.Rect.gameObject.SetActive(true);

            Vector3 world = entry.Anchor.position + Vector3.up * WorldOffset;
            Vector3 screen = camera.WorldToScreenPoint(world);

            // Behind the camera the projection folds back on itself: the same point appears mirrored
            // through the screen centre. Mirroring it back is what makes the clamp below send the
            // marker to the correct edge rather than the opposite one.
            if (screen.z < 0f)
            {
                screen.x = Screen.width - screen.x;
                screen.y = Screen.height - screen.y;
            }

            bool offScreen = screen.z < 0f
                             || screen.x < 0f || screen.x > Screen.width
                             || screen.y < 0f || screen.y > Screen.height;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _root, screen, null, out Vector2 local))
                return;

            if (offScreen)
            {
                Vector2 half = _root.rect.size * 0.5f;
                local.x = Mathf.Clamp(local.x, -half.x + EdgeMargin, half.x - EdgeMargin);
                local.y = Mathf.Clamp(local.y, -half.y + EdgeMargin, half.y - EdgeMargin);
            }

            marker.Rect.anchoredPosition = local;

            Color color = SquadModel.StatusColor(entry);
            marker.Dot.color = color;
            marker.Label.color = color;

            // Distance on the marker, not on the list row: the list answers "who and how long", the
            // marker answers "can I get there in time", and that is a question about metres.
            string state = entry.State == LifeState.Dead
                ? SquadModel.StatusText(entry)
                : $"{SquadModel.StatusText(entry)}  {entry.Distance:0}m";

            marker.Label.text = $"{entry.Name}\n{state}";
        }

        Marker BuildMarker(int index)
        {
            var marker = new Marker();

            marker.Rect = HudFactory.Rect(_root, $"Marker{index}");
            marker.Rect.anchorMin = new Vector2(0.5f, 0.5f);
            marker.Rect.anchorMax = new Vector2(0.5f, 0.5f);
            marker.Rect.pivot = new Vector2(0.5f, 0.5f);
            marker.Rect.sizeDelta = new Vector2(160f, 48f);

            marker.Dot = HudFactory.Block(marker.Rect, "Dot", Color.white);
            HudFactory.Anchor(marker.Dot.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              Vector2.zero, new Vector2(10f, 10f));

            marker.Label = HudFactory.Label(marker.Rect, "Label", 15, TextAnchor.LowerCenter);
            HudFactory.Anchor(marker.Label.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(0f, 14f), new Vector2(160f, 40f));

            return marker;
        }
    }
}
