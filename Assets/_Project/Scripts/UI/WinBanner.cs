using EscapeWithYourFriends.Casino;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// BIG WIN / MEGA WIN / EPIC WIN across the middle of the screen, the number counting up under
    /// it, for anybody standing near a hit. It reads <see cref="BigWin.Latest"/> every frame, like
    /// the slot board reads a cabinet, so nothing has to tell it anything.
    /// </summary>
    public class WinBanner
    {
        /// <summary>How long the finished number stays up after the count.</summary>
        const float Hold = 1.5f;

        static readonly Color[] Colours =
        {
            Color.white,
            new(1f, 0.85f, 0.3f),
            new(1f, 0.55f, 0.2f),
            new(1f, 0.35f, 0.75f),
        };

        Text _title;
        Text _amount;

        public void Build(RectTransform parent)
        {
            _title = HudFactory.Label(parent, "WinTitle", 72, TextAnchor.MiddleCenter);
            HudFactory.Anchor((RectTransform)_title.transform, new Vector2(0.5f, 0.5f),
                              new Vector2(0.5f, 0.5f), new Vector2(0f, 150f), new Vector2(900f, 90f));

            _amount = HudFactory.Label(parent, "WinAmount", 54, TextAnchor.MiddleCenter);
            _amount.color = Colours[1];
            HudFactory.Anchor((RectTransform)_amount.transform, new Vector2(0.5f, 0.5f),
                              new Vector2(0.5f, 0.5f), new Vector2(0f, 80f), new Vector2(900f, 70f));

            _title.gameObject.SetActive(false);
            _amount.gameObject.SetActive(false);
        }

        public void Refresh(Camera camera)
        {
            if (_title == null) return;

            var hit = BigWin.Latest;
            float elapsed = Time.time - hit.Started;
            float seconds = BigWin.Seconds(hit.Tier);

            bool showing = camera != null && hit.Tier > 0 && elapsed < seconds + Hold
                           && (camera.transform.position - hit.Where).sqrMagnitude < BigWin.Near * BigWin.Near;

            if (_title.gameObject.activeSelf != showing)
            {
                _title.gameObject.SetActive(showing);
                _amount.gameObject.SetActive(showing);
            }

            if (!showing) return;

            _title.text = BigWin.Name(hit.Tier);
            _title.color = Colours[Mathf.Clamp(hit.Tier, 0, Colours.Length - 1)];
            _amount.text = BigWin.Counted(hit.Win, elapsed, seconds).ToString("N0");

            // Slams in, then throbs while it counts; still once the number is final.
            float slam = 1f + 1.5f * Mathf.Pow(1f - Mathf.Clamp01(elapsed / 0.25f), 2f);
            float throb = elapsed < seconds ? 1f + 0.06f * hit.Tier * Mathf.Abs(Mathf.Sin(elapsed * 8f)) : 1f;
            _title.transform.localScale = Vector3.one * (slam * throb);
            _amount.transform.localScale = Vector3.one * throb;
        }
    }
}
