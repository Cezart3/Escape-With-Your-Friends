using EscapeWithYourFriends.World;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// One line at the top of the screen saying what to do, and how far away it is.
    ///
    /// It reads <see cref="Objective"/>, which is static and local. That is not a shortcut: an
    /// objective is a conclusion every peer can reach from state it already has, and a networked one
    /// would mean four clients waiting a round trip to be told something each of them could have
    /// worked out on the spot.
    ///
    /// Top centre rather than a corner, because #39 asks for an *obvious* first objective and a
    /// player who has just spawned is looking at the middle of the screen.
    /// </summary>
    public class ObjectiveBanner
    {
        RectTransform _root;
        Text _text;
        Text _distance;
        Image _backing;

        public void Build(RectTransform parent)
        {
            _root = HudFactory.Rect(parent, "Objective");
            HudFactory.Anchor(_root, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -28f), new Vector2(720f, 64f));

            _backing = HudFactory.Block(_root.transform, "Backing", new Color(0f, 0f, 0f, 0.42f));
            HudFactory.Anchor((RectTransform)_backing.transform, new Vector2(0.5f, 0.5f),
                              new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(720f, 64f));

            _text = HudFactory.Label(_root.transform, "Text", 26, TextAnchor.UpperCenter);
            HudFactory.Anchor((RectTransform)_text.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -8f), new Vector2(700f, 32f));

            _distance = HudFactory.Label(_root.transform, "Distance", 18, TextAnchor.UpperCenter);
            _distance.color = new Color(0.78f, 0.82f, 0.86f);
            HudFactory.Anchor((RectTransform)_distance.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                              new Vector2(0f, -38f), new Vector2(700f, 24f));
        }

        /// <summary>
        /// Redrawn every frame from two fields, which is cheaper than the event plumbing that would
        /// avoid it and cannot go stale.
        /// </summary>
        public void Refresh(Transform localPlayer)
        {
            if (_root == null) return;

            bool active = Objective.Active;
            if (_root.gameObject.activeSelf != active) _root.gameObject.SetActive(active);
            if (!active) return;

            _text.text = Objective.Text;

            float distance = localPlayer != null ? Objective.DistanceFrom(localPlayer.position) : -1f;

            // No number until there is something to measure to and something to measure from: an
            // objective marker reading 0m because the body has not spawned is worse than no marker.
            _distance.text = distance < 0f ? "" : $"{distance:F0} m";
            _distance.enabled = distance >= 0f;
        }
    }
}
