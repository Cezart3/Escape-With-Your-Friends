using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>What happens when the same buff is applied twice.</summary>
    public enum BuffStacking
    {
        /// <summary>Reset the timer. Two bandages in a row give you one bandage's worth, twice as long.</summary>
        Refresh,

        /// <summary>Run side by side, effects added. Three drinks are three drinks.</summary>
        Stack,

        /// <summary>The second application does nothing while the first is running.</summary>
        Ignore,
    }

    /// <summary>
    /// A timed effect: a heal over time, a full stomach, a speed boost, being drunk.
    ///
    /// #45's acceptance is that eating hooks into "the same BuffDef system the casino alcohol will
    /// use", so this is deliberately not a food type. It is a bag of deltas with a duration, and what
    /// applies it - a coconut, a bandage, a bottle of rum in #M6, a native's poison dart in #M4 - is
    /// somebody else's problem.
    ///
    /// Two kinds of number live here and they are not the same thing:
    ///
    /// - **Instant** values are applied once, the moment the buff lands. A bandage's first few points
    ///   of health, a drink's thirst.
    /// - **Per second** values run for the duration. A bandage's slow heal, alcohol's slow dehydration.
    ///
    /// The multipliers are what the casino needs and what nothing else has needed yet: they scale
    /// things rather than adding to them, they multiply together when several buffs are active, and
    /// they are all 1 by default so a buff that does not care about them costs nothing.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Buff", fileName = "Buff")]
    public class BuffDef : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable, lowercase, no spaces. The catalog is sorted by this; the wire carries an index.")]
        [SerializeField] string _id = "buff";

        [SerializeField] string _displayName = "Buff";

        [TextArea]
        [Tooltip("One line for the HUD. What it does to you, not what gave it to you.")]
        [SerializeField] string _description = "";

        [Header("Timing")]
        [Tooltip("Seconds. Zero means instant-only: the deltas land and nothing is tracked afterwards.")]
        [Min(0f)]
        [SerializeField] float _duration;

        [SerializeField] BuffStacking _stacking = BuffStacking.Refresh;

        [Header("Instant")]
        [Tooltip("Health restored the moment it lands. Negative to hurt.")]
        [SerializeField] float _health;

        [Tooltip("Hunger, thirst, warmth and stamina restored on landing. Bars run 0..100.")]
        [SerializeField] float _hunger;

        [SerializeField] float _thirst;
        [SerializeField] float _warmth;
        [SerializeField] float _stamina;

        [Header("Per second, for the duration")]
        [Tooltip("Health per second. A bandage heals over time rather than in one lump so that being "
                 + "shot mid-bandage actually costs you something.")]
        [SerializeField] float _healthPerSecond;

        [SerializeField] float _hungerPerSecond;
        [SerializeField] float _thirstPerSecond;
        [SerializeField] float _warmthPerSecond;
        [SerializeField] float _staminaPerSecond;

        [Header("Multipliers, while active")]
        [Tooltip("Movement speed. 1 is normal; the casino's rum will be below it and a stimulant above.")]
        [Min(0f)]
        [SerializeField] float _speedMultiplier = 1f;

        [Tooltip("Incoming damage. Below 1 is the drunk's famous resistance to being punched.")]
        [Min(0f)]
        [SerializeField] float _damageTakenMultiplier = 1f;

        [Tooltip("Stamina spent while sprinting. Below 1 means you can run for longer.")]
        [Min(0f)]
        [SerializeField] float _staminaCostMultiplier = 1f;

        [Header("Presentation")]
        [Tooltip("0..1. How badly this messes with your vision. #M6's alcohol drives a URP Volume "
                 + "off it - depth of field, chromatic aberration, grain. Nothing reads it yet.")]
        [Range(0f, 1f)]
        [SerializeField] float _haze;

        [Tooltip("Shown on the HUD while active. Missing is fine; the name is used instead.")]
        [SerializeField] Sprite _icon;

        public string Id => _id;
        public string DisplayName => _displayName;
        public string Description => _description;

        public float Duration => Mathf.Max(0f, _duration);
        public BuffStacking Stacking => _stacking;

        public float Health => _health;
        public float Hunger => _hunger;
        public float Thirst => _thirst;
        public float Warmth => _warmth;
        public float Stamina => _stamina;

        public float HealthPerSecond => _healthPerSecond;
        public float HungerPerSecond => _hungerPerSecond;
        public float ThirstPerSecond => _thirstPerSecond;
        public float WarmthPerSecond => _warmthPerSecond;
        public float StaminaPerSecond => _staminaPerSecond;

        public float SpeedMultiplier => Mathf.Max(0f, _speedMultiplier);
        public float DamageTakenMultiplier => Mathf.Max(0f, _damageTakenMultiplier);
        public float StaminaCostMultiplier => Mathf.Max(0f, _staminaCostMultiplier);

        public float Haze => _haze;
        public Sprite Icon => _icon;

        /// <summary>True when there is anything to track after the instant values have landed.</summary>
        public bool Lasts => Duration > 0f;

        public override string ToString() => string.IsNullOrEmpty(_id) ? name : _id;
    }
}
