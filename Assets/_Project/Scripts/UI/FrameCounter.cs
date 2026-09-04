using EscapeWithYourFriends.Core;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// Two numbers in the corner: frames per second, and the 1% low.
    ///
    /// The second one is the point. #38's target is sixty frames a second on an integrated GPU, and a
    /// counter that only shows the average will happily read 61 through a run that stutters twice a
    /// second - which is the run people actually complain about. Showing both means the person
    /// playtesting can see the difference without reading a log.
    ///
    /// Only in development builds, or when <c>-perfLog</c> asked for measurement. A release build has
    /// no probe and this draws nothing.
    /// </summary>
    public class FrameCounter
    {
        Text _text;

        public void Build(RectTransform parent)
        {
            if (PerfProbe.Instance == null) return;

            _text = HudFactory.Label(parent, "Frames", 16, TextAnchor.UpperRight);
            _text.color = new Color(0.72f, 0.78f, 0.72f);
            HudFactory.Anchor((RectTransform)_text.transform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                              new Vector2(-16f, -12f), new Vector2(220f, 40f));
        }

        public void Refresh()
        {
            if (_text == null) return;

            PerfProbe probe = PerfProbe.Instance;
            if (probe == null)
            {
                _text.enabled = false;
                return;
            }

            // The 1% low is only meaningful once a window has been measured, and printing "0" until
            // then reads as a stall rather than as no data yet.
            float low = probe.OnePercentLow;
            _text.text = low > 0f
                ? $"{probe.Fps:F0} fps   1% low {1000f / low:F0}"
                : $"{probe.Fps:F0} fps";
        }
    }
}
