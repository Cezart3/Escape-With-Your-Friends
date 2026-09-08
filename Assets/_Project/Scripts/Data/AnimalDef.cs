using System;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// How a species reacts to a person walking towards it. Two answers, because a third would need
    /// a third set of behaviour and this game does not have a species that wants one yet.
    /// </summary>
    public enum Temperament
    {
        /// <summary>Runs. Deer and birds: the challenge is closing the distance, not surviving it.</summary>
        Skittish,

        /// <summary>Charges. Boars: the challenge is that it also hits back.</summary>
        Aggressive,
    }

    /// <summary>
    /// One line of a loot table: an item, how many of it, and how often it appears at all.
    ///
    /// The chance is separate from the range on purpose. "Two to three meat, always" and "one hide,
    /// two times in three" are different statements, and folding them into one range would make the
    /// second unsayable - a range that includes zero also says the animal sometimes gives nothing,
    /// which is a different and much more annoying experience.
    /// </summary>
    [Serializable]
    public class LootDrop
    {
        public ItemDef Item;

        [Tooltip("Fewest of this item a kill gives, when it gives any.")]
        public int Min = 1;

        [Tooltip("Most of this item a kill gives.")]
        public int Max = 1;

        [Tooltip("How often this line drops at all. One means every kill.")]
        [Range(0f, 1f)]
        public float Chance = 1f;

        public int Low => Mathf.Max(0, Mathf.Min(Min, Max));
        public int High => Mathf.Max(Low, Mathf.Max(Min, Max));

        /// <summary>Average count per kill, counting the misses as zero.</summary>
        public float Expected => Item == null ? 0f : Mathf.Clamp01(Chance) * (Low + High) * 0.5f;

        /// <summary>Average worth of this line per kill, in the item's own <see cref="ItemDef.Value"/>.</summary>
        public float ExpectedValue => Item == null ? 0f : Expected * Item.Value;
    }

    /// <summary>
    /// A species: what it is worth killing, how hard that is, and what it does while you try.
    ///
    /// Same doctrine as every other definition in this project - the asset is the truth, the factory
    /// only seeds it - but with one extra job. There is a single animal *prefab*, and the shape it
    /// wears comes from here: the body size, the colour, the collider it fills. That is what makes
    /// "adding a species is a data change" true rather than aspirational. A fourth animal is a row in
    /// <c>AnimalFactory</c>'s table or a .asset file dropped in the folder, and never a new prefab.
    ///
    /// The numbers split into three groups that are tuned against different things:
    ///   - the fight (health, damage, reach) is tuned against the tier-1 weapons in #49;
    ///   - the chase (speeds, radii) is tuned against the player's own run speed, and a skittish
    ///     animal faster than a player would simply never be caught;
    ///   - the loot is tuned against what the trader pays, which is half of <see cref="ItemDef.Value"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Animal", fileName = "Animal")]
    public class AnimalDef : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable id. The catalog sorts on it, so it is also the wire order.")]
        [SerializeField] string _id = "animal";

        [SerializeField] string _displayName = "Animal";

        [TextArea]
        [SerializeField] string _description = "";

        [Header("Nature")]
        [SerializeField] Temperament _temperament = Temperament.Skittish;

        [Tooltip("Hit points. Compare against 14 for a bat swing and 34 for a hatchet.")]
        [SerializeField] float _maxHealth = 40f;

        [Header("Movement")]
        [Tooltip("Metres per second while wandering. Slow enough to be worth approaching.")]
        [SerializeField] float _walkSpeed = 1.8f;

        [Tooltip("Metres per second while fleeing or charging.")]
        [SerializeField] float _runSpeed = 6f;

        [Tooltip("Metres it will stray from where it was spawned.")]
        [SerializeField] float _wanderRadius = 22f;

        [Tooltip("Seconds of standing still between wanders, as a range.")]
        [SerializeField] Vector2 _idleSeconds = new(2f, 6f);

        [Header("Senses")]
        [Tooltip("Metres at which it is aware of you at all. Beyond this it does not care.")]
        [SerializeField] float _senseRadius = 26f;

        [Tooltip("Metres at which it reacts - runs, or charges. Inside sense range by definition.")]
        [SerializeField] float _reactRadius = 14f;

        [Tooltip("Seconds it stays alarmed after losing you. Stops a boar giving up mid-charge.")]
        [SerializeField] float _calmSeconds = 6f;

        [Header("Attack (aggressive only)")]
        [Tooltip("Damage a connecting charge does. Zero on anything that cannot fight back.")]
        [SerializeField] float _attackDamage = 18f;

        [Tooltip("Metres from body centre at which the hit lands.")]
        [SerializeField] float _attackRange = 2.2f;

        [Tooltip("Seconds between hits from the same animal.")]
        [SerializeField] float _attackInterval = 1.6f;

        [Tooltip("Seconds of stun a hit applies. This is the goofy half: a boar should put you down.")]
        [SerializeField] float _attackStun = 0.9f;

        [Tooltip("Impulse behind the hit, in newton-seconds. What actually launches the ragdoll.")]
        [SerializeField] float _attackKnockback = 900f;

        [Header("Body")]
        [Tooltip("Greybox dimensions in metres: length is Z, width X, height Y.")]
        [SerializeField] Vector3 _bodySize = new(0.8f, 0.8f, 1.6f);

        [SerializeField] Color _colour = new(0.45f, 0.32f, 0.22f);

        [Tooltip("Metres the navigation agent takes up. Too wide and it will not fit between trees.")]
        [SerializeField] float _agentRadius = 0.5f;

        [Header("Loot")]
        [Tooltip("What the carcass leaves behind. Rolled once per kill, on the server.")]
        [SerializeField] LootDrop[] _loot = Array.Empty<LootDrop>();

        public string Id => _id;
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? _id : _displayName;
        public string Description => _description;

        public Temperament Temperament => _temperament;
        public bool IsAggressive => _temperament == Temperament.Aggressive;
        public float MaxHealth => Mathf.Max(1f, _maxHealth);

        public float WalkSpeed => Mathf.Max(0.1f, _walkSpeed);
        public float RunSpeed => Mathf.Max(WalkSpeed, _runSpeed);
        public float WanderRadius => Mathf.Max(1f, _wanderRadius);
        public float IdleMin => Mathf.Max(0f, Mathf.Min(_idleSeconds.x, _idleSeconds.y));
        public float IdleMax => Mathf.Max(IdleMin, Mathf.Max(_idleSeconds.x, _idleSeconds.y));

        public float SenseRadius => Mathf.Max(ReactRadius, _senseRadius);
        public float ReactRadius => Mathf.Max(1f, _reactRadius);
        public float CalmSeconds => Mathf.Max(0f, _calmSeconds);

        public float AttackDamage => Mathf.Max(0f, _attackDamage);
        public float AttackRange => Mathf.Max(0.5f, _attackRange);
        public float AttackInterval => Mathf.Max(0.2f, _attackInterval);
        public float AttackStun => Mathf.Max(0f, _attackStun);
        public float AttackKnockback => Mathf.Max(0f, _attackKnockback);

        public Vector3 BodySize => new(Mathf.Max(0.1f, _bodySize.x), Mathf.Max(0.1f, _bodySize.y),
                                       Mathf.Max(0.1f, _bodySize.z));
        public Color Colour => _colour;
        public float AgentRadius => Mathf.Max(0.1f, _agentRadius);

        /// <summary>Height of the collider and of the point an attack is aimed at.</summary>
        public float BodyHeight => BodySize.y;

        public LootDrop[] Loot => _loot;

        /// <summary>
        /// What one kill is worth on average, in item value. Not money: the trader pays a fraction of
        /// this (see <see cref="ShopDef.PriceFor"/>), and cooking the meat changes it. It exists so
        /// the harness can state the acceptance criterion as arithmetic rather than as a feeling.
        /// </summary>
        public float ExpectedLootValue
        {
            get
            {
                float total = 0f;
                if (_loot == null) return 0f;

                foreach (LootDrop drop in _loot)
                    if (drop != null) total += drop.ExpectedValue;

                return total;
            }
        }

        /// <summary>Bake time only. Everything a factory is allowed to seed, in one call.</summary>
        public void Configure(string id, string displayName, string description, Temperament temperament,
                              float maxHealth, float walkSpeed, float runSpeed, float wanderRadius,
                              Vector2 idleSeconds, float senseRadius, float reactRadius, float calmSeconds,
                              float attackDamage, float attackRange, float attackInterval, float attackStun,
                              float attackKnockback, Vector3 bodySize, Color colour, float agentRadius)
        {
            _id = id;
            _displayName = displayName;
            _description = description;
            _temperament = temperament;
            _maxHealth = maxHealth;
            _walkSpeed = walkSpeed;
            _runSpeed = runSpeed;
            _wanderRadius = wanderRadius;
            _idleSeconds = idleSeconds;
            _senseRadius = senseRadius;
            _reactRadius = reactRadius;
            _calmSeconds = calmSeconds;
            _attackDamage = attackDamage;
            _attackRange = attackRange;
            _attackInterval = attackInterval;
            _attackStun = attackStun;
            _attackKnockback = attackKnockback;
            _bodySize = bodySize;
            _colour = colour;
            _agentRadius = agentRadius;
        }

        /// <summary>Bake time only. Structural, so it is re-applied on every run.</summary>
        public void SetLoot(LootDrop[] loot) => _loot = loot ?? Array.Empty<LootDrop>();
    }
}
