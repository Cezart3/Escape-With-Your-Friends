using EscapeWithYourFriends.Economy;
using UnityEngine;
using UnityEngine.UI;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// What you can spend, bottom-right.
    ///
    /// Opposite corner from the survival bars on purpose: those are the things that are killing you
    /// and this is the thing that gets you off the island, and a player learns quickly that the left
    /// corner is bad news and the right corner is progress.
    ///
    /// It flashes green when money comes in and red when it goes out, then settles back. That is the
    /// entire feedback for a transaction until the shop has a screen of its own (#48) - a number that
    /// changes silently is a number nobody notices changing, and "did that sale go through" should
    /// never be a question.
    /// </summary>
    public class Purse
    {
        const float Margin = 20f;
        const float FlashSeconds = 0.9f;

        static readonly Color Calm = new(0.92f, 0.86f, 0.55f);
        static readonly Color Gain = new(0.55f, 0.95f, 0.55f);
        static readonly Color Loss = new(1.00f, 0.45f, 0.40f);

        Text _label;
        Wallet _wallet;

        float _flashUntil;
        Color _flash;

        public void Build(RectTransform parent)
        {
            _label = HudFactory.Label(parent, "Purse", 24, TextAnchor.LowerRight);
            _label.color = Calm;

            HudFactory.Anchor((RectTransform)_label.transform, new Vector2(1f, 0f), new Vector2(1f, 0f),
                              new Vector2(-Margin, Margin), new Vector2(220f, 30f));
        }

        public void Refresh(Wallet wallet)
        {
            if (_label == null) return;

            if (_wallet != wallet)
            {
                // Subscribed rather than diffed per frame, because the flash needs to know the
                // direction and a per-frame comparison would miss two changes in one frame.
                if (_wallet != null) _wallet.Changed -= OnChanged;
                _wallet = wallet;
                if (_wallet != null) _wallet.Changed += OnChanged;
            }

            bool has = wallet != null && wallet.IsSpawned;
            if (_label.gameObject.activeSelf != has) _label.gameObject.SetActive(has);
            if (!has) return;

            _label.text = Text(wallet.Balance);
            _label.color = Time.time < _flashUntil ? _flash : Calm;
        }

        void OnChanged(int previous, int next)
        {
            _flash = next > previous ? Gain : Loss;
            _flashUntil = Time.time + FlashSeconds;
        }

        /// <summary>"$1,250". Pure, so the harness can read what the corner would be showing.</summary>
        public static string Text(int balance) => $"${balance:n0}";
    }
}
