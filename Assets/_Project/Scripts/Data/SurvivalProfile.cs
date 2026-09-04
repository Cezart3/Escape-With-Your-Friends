using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// Every number behind hunger, thirst, stamina and warmth, in one text asset.
    ///
    /// #40's acceptance is "annoying enough to drive behaviour but never tedious", which is not a
    /// thing code can be correct about - it is a thing that gets tuned, in play, repeatedly, by
    /// somebody who is not the person who wrote the tick loop. So the rates live here rather than as
    /// serialised fields on the component, where they can be edited in a diff and reviewed without
    /// opening the editor.
    ///
    /// The defaults are set against the twenty-minute day in <c>WorldClock</c>, and the comments say
    /// what each one means in minutes, because "0.0417 per second" is not a number anybody can have
    /// an opinion about.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Survival Profile", fileName = "Survival")]
    public class SurvivalProfile : ScriptableObject
    {
        /// <summary>Every bar runs 0..100. One scale for all four keeps the HUD and the maths honest.</summary>
        public const float Max = 100f;

        [Header("Hunger")]
        [Tooltip("Points per second. 0.042 empties a full bar in about 40 minutes, or two days.")]
        [SerializeField] float _hungerDrain = 0.042f;

        [Tooltip("Extra points per second while sprinting. Running costs you lunch.")]
        [SerializeField] float _hungerSprintDrain = 0.05f;

        [Header("Thirst")]
        [Tooltip("Points per second. 0.067 empties a full bar in about 25 minutes - faster than hunger, "
                 + "because water is the thing that should send you back to the filter.")]
        [SerializeField] float _thirstDrain = 0.067f;

        [Tooltip("Extra points per second while sprinting.")]
        [SerializeField] float _thirstSprintDrain = 0.12f;

        [Header("Stamina")]
        [Tooltip("Points per second while sprinting. 12 gives about eight seconds of running.")]
        [SerializeField] float _staminaSprintDrain = 12f;

        [Tooltip("Points spent on a jump.")]
        [SerializeField] float _staminaJumpCost = 8f;

        [Tooltip("Points per second recovered when not spending any.")]
        [SerializeField] float _staminaRecovery = 15f;

        [Tooltip("Seconds after spending before recovery starts. Stops tapping sprint from being free.")]
        [SerializeField] float _staminaRecoveryDelay = 1f;

        [Tooltip("Stamina needed to *start* sprinting. Higher than zero so a drained player cannot "
                 + "stutter-sprint one tick at a time.")]
        [SerializeField] float _staminaSprintThreshold = 20f;

        [Tooltip("How far hunger and thirst can cut stamina recovery, at their worst. 0.6 means a "
                 + "starving player recovers at 40% of normal.")]
        [Range(0f, 1f)]
        [SerializeField] float _staminaStarvationPenalty = 0.6f;

        [Header("Warmth")]
        [Tooltip("Comfortable daytime warmth. The bar sits here and is not a clock you have to watch.")]
        [SerializeField] float _warmthDay = 100f;

        [Tooltip("Where warmth settles at night in the open. Cold, but survivable while you keep moving.")]
        [SerializeField] float _warmthNight = 40f;

        [Tooltip("Where warmth settles while in the sea. Zero on purpose: night settles at 40 and is "
                 + "only uncomfortable, and if the water settled anywhere above zero it could never "
                 + "actually hurt you - which is the whole reason the sea is dangerous.")]
        [SerializeField] float _warmthWater;

        [Tooltip("Points per second toward the target while losing warmth. Slow: about a minute and a "
                 + "half from comfortable to freezing in water.")]
        [SerializeField] float _warmthLossRate = 1.1f;

        [Tooltip("Points per second toward the target while regaining it. Faster than losing it, so "
                 + "getting out of the water is immediately the right move.")]
        [SerializeField] float _warmthGainRate = 3.5f;

        [Tooltip("Fraction of the day that counts as night for warmth. 0.25 is dusk to dawn.")]
        [Range(0f, 0.5f)]
        [SerializeField] float _nightFraction = 0.25f;

        [Header("Damage at zero")]
        [Tooltip("Health per second while hunger is empty.")]
        [SerializeField] float _starvationDamage = 0.8f;

        [Tooltip("Health per second while thirst is empty. Worse than hunger, as it is in life.")]
        [SerializeField] float _dehydrationDamage = 1.2f;

        [Tooltip("Health per second while warmth is empty.")]
        [SerializeField] float _hypothermiaDamage = 1.5f;

        [Tooltip("Seconds between damage applications. One per second, so the HUD ticks visibly and "
                 + "the log is readable, rather than a hundred fractional hits.")]
        [Min(0.1f)]
        [SerializeField] float _damageInterval = 1f;

        [Header("Warnings")]
        [Tooltip("Below this fraction a bar is 'low' - the HUD turns it and the log says so.")]
        [Range(0f, 1f)]
        [SerializeField] float _lowThreshold = 0.25f;

        public float HungerDrain => _hungerDrain;
        public float HungerSprintDrain => _hungerSprintDrain;
        public float ThirstDrain => _thirstDrain;
        public float ThirstSprintDrain => _thirstSprintDrain;

        public float StaminaSprintDrain => _staminaSprintDrain;
        public float StaminaJumpCost => _staminaJumpCost;
        public float StaminaRecovery => _staminaRecovery;
        public float StaminaRecoveryDelay => _staminaRecoveryDelay;
        public float StaminaSprintThreshold => _staminaSprintThreshold;
        public float StaminaStarvationPenalty => _staminaStarvationPenalty;

        public float WarmthDay => _warmthDay;
        public float WarmthNight => _warmthNight;
        public float WarmthWater => _warmthWater;
        public float WarmthLossRate => _warmthLossRate;
        public float WarmthGainRate => _warmthGainRate;
        public float NightFraction => _nightFraction;

        public float StarvationDamage => _starvationDamage;
        public float DehydrationDamage => _dehydrationDamage;
        public float HypothermiaDamage => _hypothermiaDamage;
        public float DamageInterval => Mathf.Max(0.1f, _damageInterval);

        public float LowThreshold => _lowThreshold;
    }
}
