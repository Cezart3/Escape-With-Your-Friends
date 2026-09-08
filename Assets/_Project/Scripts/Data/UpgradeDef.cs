using System;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>Where the work gets done. Two places, because they charge differently.</summary>
    public enum UpgradeVenue
    {
        /// <summary>At the bench in camp. Your own labour, so it costs materials and no money.</summary>
        Bench,

        /// <summary>At a trader's counter. Somebody else's labour, so it costs money.</summary>
        Trader,
    }

    /// <summary>
    /// One step up a weapon's line: what it was, what it becomes, and what that costs.
    ///
    /// **An upgrade produces a real weapon asset, not a modifier stack.** The alternative - keeping a
    /// per-player list of bonuses and asking every hit to add them up - would have put the numbers
    /// that decide damage somewhere other than the weapon the server is holding, and the whole of
    /// #49-#51 rests on "what landed is what the server's asset says". A <see cref="ScriptableObject"/>
    /// is also global: a modifier written onto <c>machete.asset</c> at runtime would upgrade the
    /// machete of every player in the session, including the three who did not pay for it. So an
    /// upgrade is a swap - the old weapon leaves your bag and the next one in its line arrives - and
    /// every number that follows is read off an asset that already existed at bake time.
    ///
    /// The delta fields below are therefore an **advertisement, not an instruction**. Nothing at
    /// runtime applies them; they exist so the shop screen can say "+31% damage, +6 rounds, 25% less
    /// recoil" without diffing two assets in the UI layer, and so that claim is a value somebody can
    /// check. <c>UpgradeFactory</c> computes them *from* <see cref="From"/> and <see cref="To"/>
    /// rather than the other way round, which makes it structurally impossible for the shelf to
    /// promise an upgrade it does not deliver - and <see cref="Verify"/> re-checks it at runtime in
    /// case somebody edits an asset by hand.
    /// </summary>
    [CreateAssetMenu(fileName = "Upgrade", menuName = "EWYF/Weapon Upgrade")]
    public class UpgradeDef : ScriptableObject
    {
        /// <summary>How close two advertised numbers have to be to count as the same number.</summary>
        public const float Tolerance = 0.01f;

        [Header("Identity")]
        [Tooltip("Stable, lowercase, no spaces. The catalog is sorted by this; the wire carries an index.")]
        [SerializeField] string _id = "upgrade";

        [SerializeField] string _displayName = "Upgrade";

        [TextArea(2, 4)]
        [SerializeField] string _description = "";

        [Header("The swap")]
        [Tooltip("The weapon you hand over. Its bag item is what actually leaves your bag.")]
        [SerializeField] WeaponDef _from;

        [Tooltip("What you get back. One tier higher, and better on every axis that matters.")]
        [SerializeField] WeaponDef _to;

        [Header("The price")]
        [SerializeField] UpgradeVenue _venue = UpgradeVenue.Bench;

        [Tooltip("Money. Zero at a bench, where your own time is the labour.")]
        [Min(0)]
        [SerializeField] int _price;

        [Tooltip("Consumed from the bag alongside the weapon. Never partially: all of it or nothing.")]
        [SerializeField] Ingredient[] _materials = Array.Empty<Ingredient>();

        [Header("What it advertises - derived by UpgradeFactory, never edited by hand")]
        [SerializeField] float _damageScale = 1f;
        [SerializeField] float _knockbackScale = 1f;
        [SerializeField] float _rangeScale = 1f;

        [Tooltip("Attacks per second, so above one is faster. Rate of fire for a gun.")]
        [SerializeField] float _rateScale = 1f;

        [SerializeField] int _magazineBonus;

        [Tooltip("Below one is less kick, which is the direction an upgrade goes.")]
        [SerializeField] float _recoilScale = 1f;

        public string Id => _id;
        public string DisplayName => _displayName;
        public string Description => _description;

        public WeaponDef From => _from;
        public WeaponDef To => _to;

        public UpgradeVenue Venue => _venue;
        public int Price => Mathf.Max(0, _price);
        public Ingredient[] Materials => _materials ?? Array.Empty<Ingredient>();

        public float DamageScale => _damageScale;
        public float KnockbackScale => _knockbackScale;
        public float RangeScale => _rangeScale;
        public float RateScale => _rateScale;
        public int MagazineBonus => _magazineBonus;
        public float RecoilScale => _recoilScale;

        /// <summary>
        /// An upgrade needs two weapons, and both ends have to be carriable - the bag is where the
        /// swap happens, so a weapon with no item cannot be either end of one. Fists are the only
        /// weapon that fails this, and an upgrade path for your hands is a different issue.
        /// </summary>
        public bool IsValid
            => !string.IsNullOrWhiteSpace(_id) && _from != null && _to != null && _from != _to
               && _from.Item != null && _to.Item != null;

        /// <summary>
        /// A ratio that means something even when the bottom is zero: a melee weapon has no recoil
        /// and no magazine, and "infinitely more recoil than none" is not a thing to show a player.
        /// Unchanged is the honest answer there.
        /// </summary>
        public static float Ratio(float from, float to) => from > 0.0001f ? to / from : 1f;

        /// <summary>
        /// Recomputes every advertised number from the two weapons and reports the first one that
        /// does not match. The factory derives these, so this can only fail if an asset was edited by
        /// hand - which is exactly the case worth catching, because a shelf that lies about what an
        /// upgrade gives is worse than one that sells nothing.
        /// </summary>
        public bool Verify(out string mismatch)
        {
            mismatch = null;

            if (!IsValid)
            {
                mismatch = "the upgrade has no from, no to, or an end that nobody can carry";
                return false;
            }

            if (!Same(_damageScale, Ratio(_from.Hit.Damage, _to.Hit.Damage), out mismatch, "damage")
                || !Same(_knockbackScale, Ratio(_from.Hit.Knockback, _to.Hit.Knockback),
                         out mismatch, "knockback")
                || !Same(_rangeScale, Ratio(_from.Range, _to.Range), out mismatch, "range")
                || !Same(_rateScale, Ratio(_to.Cooldown, _from.Cooldown), out mismatch, "rate")
                || !Same(_recoilScale, Ratio(_from.Recoil, _to.Recoil), out mismatch, "recoil"))
                return false;

            if (_magazineBonus != _to.Magazine - _from.Magazine)
            {
                mismatch = $"magazine says {_magazineBonus} but delivers "
                           + $"{_to.Magazine - _from.Magazine}";
                return false;
            }

            return true;
        }

        static bool Same(float advertised, float actual, out string mismatch, string what)
        {
            if (Mathf.Abs(advertised - actual) <= Tolerance)
            {
                mismatch = null;
                return true;
            }

            mismatch = $"{what} says x{advertised:F2} but delivers x{actual:F2}";
            return false;
        }

        /// <summary>
        /// Whether this is an upgrade rather than a sidegrade. The power curve #52 is asked for is
        /// this predicate being true of every step in every line: a tier higher, nothing worse.
        /// </summary>
        public bool IsStrictlyBetter
            => IsValid && _to.Tier > _from.Tier
               && _damageScale >= 1f - Tolerance && _knockbackScale >= 1f - Tolerance
               && _rangeScale >= 1f - Tolerance && _rateScale >= 1f - Tolerance
               && _magazineBonus >= 0 && _recoilScale <= 1f + Tolerance;

        /// <summary>"+31% damage, +6 rounds, 25% less recoil". The tooltip, and the log line.</summary>
        public string Advertisement()
        {
            var text = new System.Text.StringBuilder();

            Append(text, "damage", _damageScale);
            Append(text, "knockback", _knockbackScale);
            Append(text, "range", _rangeScale);
            Append(text, "rate", _rateScale);

            if (_magazineBonus != 0)
            {
                if (text.Length > 0) text.Append(", ");
                text.Append(_magazineBonus > 0 ? "+" : "").Append(_magazineBonus).Append(" rounds");
            }

            // Recoil is the one number where less is the improvement, so it is worded as a reduction
            // rather than as a multiplier below one, which reads as a downgrade at a glance.
            if (Mathf.Abs(_recoilScale - 1f) > Tolerance)
            {
                if (text.Length > 0) text.Append(", ");
                text.Append($"{(1f - _recoilScale) * 100f:F0}% less recoil");
            }

            return text.Length > 0 ? text.ToString() : "no change";
        }

        static void Append(System.Text.StringBuilder text, string what, float scale)
        {
            if (Mathf.Abs(scale - 1f) <= Tolerance) return;

            if (text.Length > 0) text.Append(", ");

            float percent = (scale - 1f) * 100f;
            text.Append(percent > 0f ? "+" : "").Append($"{percent:F0}% ").Append(what);
        }

        /// <summary>"bat -> bat_nailed at the bench: 4x scrap_metal, +86% damage". For the log.</summary>
        public string Describe()
        {
            string cost = _price > 0 ? $"{_price}c" : "free";

            if (_materials != null && _materials.Length > 0)
                cost += " + " + string.Join(" + ", Array.ConvertAll(_materials, m => m.ToString()));

            return $"{(_from != null ? _from.Id : "?")} -> {(_to != null ? _to.Id : "?")} "
                   + $"at the {_venue.ToString().ToLowerInvariant()}: {cost}, {Advertisement()}";
        }

        public override string ToString() => string.IsNullOrEmpty(_id) ? name : _id;

        /// <summary>Bake time only. The deltas are derived here, never typed in.</summary>
        public void Configure(WeaponDef from, WeaponDef to)
        {
            _from = from;
            _to = to;

            if (from == null || to == null) return;

            _damageScale = Ratio(from.Hit.Damage, to.Hit.Damage);
            _knockbackScale = Ratio(from.Hit.Knockback, to.Hit.Knockback);
            _rangeScale = Ratio(from.Range, to.Range);
            _rateScale = Ratio(to.Cooldown, from.Cooldown);
            _magazineBonus = to.Magazine - from.Magazine;
            _recoilScale = Ratio(from.Recoil, to.Recoil);
        }
    }
}
