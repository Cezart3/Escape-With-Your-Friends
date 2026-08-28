using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// A taser. Mechanically it is a ranged punch with a very long stun — the jitter and the camera
    /// shake are what make it worth carrying instead of a bat.
    ///
    /// Battery is the whole balance lever. A taser with no ammunition cost would be strictly better
    /// than every melee weapon, so the interesting decision is whether this target is worth a third
    /// of the charge.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Taser", fileName = "Taser")]
    public class TaserDef : ScriptableObject
    {
        [SerializeField] string _displayName = "Taser";

        [Header("Hit")]
        [Tooltip("Stun duration here is the shock duration — how long the victim twitches on the floor.")]
        [SerializeField] HitProfile _hit = new();

        [Header("Reach")]
        [SerializeField] float _range = 12f;

        [Tooltip("Radius of the probe cast. Generous, because leading a ragdolling target is unfair.")]
        [SerializeField] float _radius = 0.35f;

        [Header("Battery")]
        [SerializeField] float _capacity = 100f;

        [Tooltip("Charge spent per shot. At 34 against a capacity of 100 you get three shots.")]
        [SerializeField] float _shotCost = 34f;

        [Tooltip("Charge regained per second once recharging starts.")]
        [SerializeField] float _rechargeRate = 6f;

        [Tooltip("Seconds after a shot before the battery starts refilling.")]
        [SerializeField] float _rechargeDelay = 1.5f;

        [Header("Shock")]
        [Tooltip("Impulse applied to a random bone on every jitter step. Small — this is a twitch.")]
        [SerializeField] float _jitterForce = 3.5f;

        [Tooltip("Seconds between jitter impulses. Short enough to look electric, long enough to see.")]
        [SerializeField] float _jitterInterval = 0.08f;

        [Tooltip("Camera shake amplitude on the victim while shocked.")]
        [SerializeField] float _cameraShake = 1.2f;

        [Header("Timing")]
        [SerializeField] float _cooldown = 0.8f;

        public string DisplayName => _displayName;
        public HitProfile Hit => _hit;
        public float Range => _range;
        public float Radius => _radius;
        public float Capacity => _capacity;
        public float ShotCost => _shotCost;
        public float RechargeRate => _rechargeRate;
        public float RechargeDelay => _rechargeDelay;
        public float JitterForce => _jitterForce;
        public float JitterInterval => _jitterInterval;
        public float CameraShake => _cameraShake;
        public float Cooldown => _cooldown;
    }
}
