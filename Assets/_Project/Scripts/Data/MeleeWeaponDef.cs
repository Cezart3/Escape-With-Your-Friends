using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// A melee weapon, including bare fists. Everything about how it feels lives here as data, so
    /// new weapons are a text asset written from the terminal rather than a new script.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Melee Weapon", fileName = "MeleeWeapon")]
    public class MeleeWeaponDef : ScriptableObject
    {
        [SerializeField] string _displayName = "Fists";

        [Header("Hit")]
        [SerializeField] HitProfile _hit = new();

        [Header("Reach")]
        [Tooltip("Distance from the aim origin to the far edge of the swing.")]
        [SerializeField] float _range = 2f;

        [Tooltip("Radius of the swing volume. Wide values forgive bad aim, which is what we want.")]
        [SerializeField] float _radius = 0.6f;

        [Tooltip("Half-angle of the swing cone, in degrees, measured from the aim direction.")]
        [Range(5f, 180f)]
        [SerializeField] float _coneHalfAngle = 60f;

        [Tooltip("How many victims one swing can connect with. Punching through a pile is the point.")]
        [SerializeField] int _maxTargets = 4;

        [Header("Timing")]
        [SerializeField] float _cooldown = 0.5f;

        [Tooltip("Delay between the input and the hit resolving, so the swing animation can land.")]
        [SerializeField] float _windup = 0.12f;

        public string DisplayName => _displayName;
        public HitProfile Hit => _hit;
        public float Range => _range;
        public float Radius => _radius;
        public float ConeHalfAngle => _coneHalfAngle;
        public int MaxTargets => _maxTargets;
        public float Cooldown => _cooldown;
        public float Windup => _windup;
    }
}
