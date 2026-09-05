using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>How a weapon decides what it touched. The only branch in <c>Weapon</c>.</summary>
    public enum WeaponKind
    {
        /// <summary>A cone swept in front of you. Fists, machete, bat, shovel.</summary>
        Melee,

        /// <summary>A ray fired the instant you pull the trigger. Pistol, shotgun, rifle, SMG.</summary>
        Hitscan,
    }

    /// <summary>
    /// One weapon, of any kind, as data.
    ///
    /// **The acceptance for #49 is that a new weapon is one asset plus a prefab and no new code**, so
    /// this is deliberately one type rather than a `MeleeWeaponDef` and a `GunDef` and whatever the
    /// crossbow would have needed. A second definition type means a second component that reads it,
    /// a second equip path, and a second place to forget something; the honest cost of one type is a
    /// handful of fields that only one kind uses, which is a cost paid in the inspector and nowhere
    /// else. This replaced <c>MeleeWeaponDef</c> outright rather than sitting beside it.
    ///
    /// **Holding it is equipping it.** <see cref="Item"/> points at the <see cref="ItemDef"/> this
    /// weapon *is*, and <c>Weapon</c> watches the selected hotbar slot. That is what makes the
    /// acceptance true in play: drop a new asset in, point it at an item, and the thing swings. There
    /// is no equip code to write per weapon, and no list of weapons anywhere that a new one must be
    /// added to - the catalog is rebuilt from the folder.
    ///
    /// Fists are the exception: no item, because the hand you punch with is not in your bag.
    /// </summary>
    [CreateAssetMenu(fileName = "Weapon", menuName = "EWYF/Weapon")]
    public class WeaponDef : ScriptableObject
    {
        [SerializeField] string _id = "weapon";

        [SerializeField] string _displayName = "Weapon";

        [TextArea(2, 4)]
        [SerializeField] string _description = "";

        [SerializeField] WeaponKind _kind = WeaponKind.Melee;

        [Tooltip("The bag item that equips this. Empty for fists, which nobody carries.")]
        [SerializeField] ItemDef _item;

        [Header("Hit")]
        [SerializeField] HitProfile _hit = new();

        [Header("Timing")]
        [Tooltip("Seconds between attacks. For a gun this is derived from rounds per minute instead.")]
        [SerializeField] float _cooldown = 0.5f;

        [Tooltip("Delay between the input and the hit resolving, so a swing animation can land. "
                 + "Guns leave this at zero: a trigger pull is not a wind-up.")]
        [SerializeField] float _windup = 0.12f;

        [Header("Melee reach")]
        [Tooltip("Distance from the aim origin to the far edge of the swing.")]
        [SerializeField] float _range = 2f;

        [Tooltip("Radius of the swing volume. Wide values forgive bad aim, which is what we want.")]
        [SerializeField] float _radius = 0.6f;

        [Tooltip("Half-angle of the swing cone, in degrees, measured from the aim direction.")]
        [Range(5f, 180f)]
        [SerializeField] float _coneHalfAngle = 60f;

        [Tooltip("How many victims one swing can connect with. Punching through a pile is the point.")]
        [SerializeField] int _maxTargets = 4;

        [Header("Ranged")]
        [Tooltip("How far the shot carries, in metres.")]
        [SerializeField] float _shotRange = 60f;

        [Tooltip("Rays per shot. One for a rifle, eight or so for a shotgun.")]
        [Min(1)]
        [SerializeField] int _pellets = 1;

        [Tooltip("Cone the shot scatters into, in degrees. Zero is a laser and reads as a bug.")]
        [Range(0f, 30f)]
        [SerializeField] float _spread = 1.5f;

        [Tooltip("Upward camera kick per shot, in degrees. Feel, not damage.")]
        [SerializeField] float _recoil = 1.2f;

        [Tooltip("Rate of fire. The cooldown for a gun is 60 / this.")]
        [Min(1f)]
        [SerializeField] float _roundsPerMinute = 300f;

        [Tooltip("Rounds per magazine. Zero means it never needs reloading.")]
        [SerializeField] int _magazine = 12;

        [SerializeField] float _reloadSeconds = 1.8f;

        [Tooltip("What it eats. Consumed from the bag on reload - see #51.")]
        [SerializeField] ItemDef _ammo;

        [Header("Presentation")]
        [Tooltip("The model that appears in the hand. The 'plus a prefab' half of the acceptance.")]
        [SerializeField] GameObject _viewPrefab;

        [Header("Progression")]
        [Tooltip("1 for island-one gear, higher for what comes later. Drives the power curve in #52.")]
        [Min(1)]
        [SerializeField] int _tier = 1;

        [Tooltip("What this becomes when upgraded. Null means it is the end of its line.")]
        [SerializeField] WeaponDef _upgradesTo;

        public string Id => _id;
        public string DisplayName => _displayName;
        public string Description => _description;
        public WeaponKind Kind => _kind;
        public ItemDef Item => _item;
        public HitProfile Hit => _hit;

        public float Windup => Mathf.Max(0f, _windup);

        /// <summary>
        /// Seconds between attacks. A gun states a rate of fire because that is how guns are talked
        /// about; a bat states a cooldown because "rounds per minute" is a strange thing to say about
        /// a bat. Both arrive here as the same number.
        /// </summary>
        public float Cooldown => _kind == WeaponKind.Hitscan
            ? 60f / Mathf.Max(1f, _roundsPerMinute)
            : Mathf.Max(0f, _cooldown);

        public float Range => _kind == WeaponKind.Hitscan ? _shotRange : _range;
        public float Radius => _radius;
        public float ConeHalfAngle => _coneHalfAngle;
        public int MaxTargets => _kind == WeaponKind.Hitscan ? Pellets : Mathf.Max(1, _maxTargets);

        public int Pellets => Mathf.Max(1, _pellets);
        public float Spread => Mathf.Max(0f, _spread);
        public float Recoil => Mathf.Max(0f, _recoil);
        public float RoundsPerMinute => Mathf.Max(1f, _roundsPerMinute);
        public int Magazine => Mathf.Max(0, _magazine);
        public float ReloadSeconds => Mathf.Max(0f, _reloadSeconds);
        public ItemDef Ammo => _ammo;

        public GameObject ViewPrefab => _viewPrefab;

        public int Tier => Mathf.Max(1, _tier);
        public WeaponDef UpgradesTo => _upgradesTo;

        /// <summary>A weapon with no id or no damage is a weapon somebody forgot to finish.</summary>
        public bool IsValid => !string.IsNullOrWhiteSpace(_id) && _hit != null && Cooldown > 0f;

        /// <summary>One line for a log or a tooltip.</summary>
        public string Describe()
            => _kind == WeaponKind.Hitscan
                ? $"{_id} t{Tier} gun {_hit.Damage:F0}dmg {RoundsPerMinute:F0}rpm "
                  + $"{Pellets}x{Spread:F1}deg {_shotRange:F0}m mag {Magazine}"
                : $"{_id} t{Tier} melee {_hit.Damage:F0}dmg {Cooldown:F2}s "
                  + $"{Range:F1}m cone {ConeHalfAngle:F0}deg x{MaxTargets}";

        public override string ToString() => string.IsNullOrEmpty(_id) ? name : _id;
    }
}
