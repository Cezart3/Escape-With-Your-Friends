using System;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>What a native does when it has nothing else to do, and how it fights when it does.</summary>
    public enum NativeRole
    {
        /// <summary>Walks the perimeter, notices things first, and shouts rather than wins.</summary>
        Scout,

        /// <summary>Walks straight at you and hits hard. The body of any war party.</summary>
        Spearman,

        /// <summary>Hangs back and darts you. The threat is the stun, not the damage.</summary>
        Blowgunner,
    }

    /// <summary>
    /// One kind of native: the numbers a <see cref="AI.Native"/> body wears.
    ///
    /// Same shape as <see cref="AnimalDef"/> on purpose - one prefab, N roles, everything driven from
    /// an asset - but with one whole extra axis that an animal does not have: **the sun**. A boar is
    /// the same boar at noon and at midnight. A native is not, and #55's acceptance is entirely about
    /// that difference: *a real threat at night, not unfair in daylight*.
    ///
    /// So every sensing number here comes in pairs, and the pair is blended by
    /// <see cref="World.WorldClock.Night01"/> - which is a function of where the sun actually is,
    /// not of an arbitrary clock reading, so the danger ramps up exactly as the light a player can
    /// see goes away.
    ///
    /// The three levers that make daylight fair are all here rather than in code:
    ///
    /// - <see cref="DayNotice"/> is short and, by day, needs line of sight inside
    ///   <see cref="VisionAngle"/>. You can walk around a hill. At night the radius grows and the
    ///   sight rule is dropped: they hear you.
    /// - <see cref="DayLeash"/> is short, so a daylight chase ends when you leave their ground. The
    ///   night leash is long enough to be a problem.
    /// - Nothing here is faster than a sprinting player (7.5 m/s). Running away always works; it just
    ///   costs you the stamina and whatever you were doing.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Native", fileName = "Native")]
    public class NativeDef : ScriptableObject
    {
        [Header("Identity")]
        [SerializeField] string _id = "native";
        [SerializeField] string _displayName = "Native";

        [TextArea, SerializeField] string _description = "";

        [SerializeField] NativeRole _role = NativeRole.Spearman;

        [Header("Body")]
        [SerializeField] float _maxHealth = 70f;

        [Tooltip("Patrolling pace. Deliberately a walk: a native you can hear coming is a warning.")]
        [SerializeField] float _walkSpeed = 2.1f;

        [Tooltip("Chasing pace. Never above a player's sprint, which is what makes running away work.")]
        [SerializeField] float _runSpeed = 6.6f;

        [Tooltip("Metres from the camp this one patrols by day.")]
        [SerializeField] float _patrolRadius = 26f;

        [Tooltip("Seconds spent standing at a patrol point before picking the next one.")]
        [SerializeField] Vector2 _idleSeconds = new(2f, 6f);

        [Header("Senses - day")]
        [Tooltip("Metres they notice you from in daylight, and only with line of sight inside the cone.")]
        [SerializeField] float _dayNotice = 20f;

        [Tooltip("Half-angle of the daylight vision cone, degrees. Behind them is behind them.")]
        [SerializeField] float _visionAngle = 65f;

        [Tooltip("Metres at which you are noticed regardless of sight or sun. Standing on their toes.")]
        [SerializeField] float _earshot = 7f;

        [Header("Senses - night")]
        [Tooltip("Metres they notice you from at night. No sight check: this is hearing, not seeing.")]
        [SerializeField] float _nightNotice = 32f;

        [Header("Temper")]
        [Tooltip("Seconds they keep hunting the last place they saw you after losing you.")]
        [SerializeField] float _memorySeconds = 9f;

        [Tooltip("Seconds spent poking around a disturbance before giving up on it.")]
        [SerializeField] float _investigateSeconds = 6f;

        [Tooltip("Metres a shout carries. Every native inside it comes to look. The camp is the threat.")]
        [SerializeField] float _alarmRadius = 30f;

        [Tooltip("Metres from camp they will pursue in daylight before turning back.")]
        [SerializeField] float _dayLeash = 40f;

        [Tooltip("Metres from camp they will pursue at night. Long enough to follow you home.")]
        [SerializeField] float _nightLeash = 130f;

        [Tooltip("Fraction of health below which this one runs for the camp instead of fighting.")]
        [Range(0f, 1f), SerializeField] float _fleeHealth = 0.25f;

        [Header("Attack")]
        [Tooltip("Straight to the face. Tuned against 100 player health and a revive that costs money.")]
        [SerializeField] float _attackDamage = 20f;

        [Tooltip("Metres. A spear is longer than a fist; a blowgun is longer than a clearing.")]
        [SerializeField] float _attackRange = 2.4f;

        [Tooltip("Seconds between hits.")]
        [SerializeField] float _attackInterval = 1.8f;

        [Tooltip("Seconds of stun on the victim. The blowgun's whole point.")]
        [SerializeField] float _attackStun = 0.8f;

        [SerializeField] float _attackKnockback = 850f;

        [Tooltip("Seconds of standing still before the blow lands, so a hit can be seen coming.")]
        [SerializeField] float _windupSeconds = 0.35f;

        [Header("Ranged")]
        [Tooltip("Darts instead of stabbing. Wants distance rather than contact.")]
        [SerializeField] bool _ranged;

        [Tooltip("Metres it tries to hold when it has a clear shot. Backs off when you close.")]
        [SerializeField] float _standoff = 12f;

        [Tooltip("Degrees of spread. A blowgun that never misses is a sniper with a straw.")]
        [SerializeField] float _spreadDegrees = 4f;

        [Header("Abduction")]
        [Tooltip("Drags a downed player back to camp instead of standing over the body.")]
        [SerializeField] bool _abducts;

        [Tooltip("Metres from a body it will come for it. Zero for anything that does not abduct.")]
        [SerializeField] float _abductRadius = 22f;

        [Tooltip("Fraction of the run speed while carrying somebody. A haul nobody can catch is not a rescue.")]
        [Range(0.1f, 1f), SerializeField] float _haulFraction = 0.5f;

        [Header("Greybox")]
        [SerializeField] Vector3 _bodySize = new(0.55f, 1.75f, 0.4f);

        [SerializeField] Color _colour = new(0.42f, 0.30f, 0.24f);

        [Tooltip("Warpaint, on the head block, so a role is readable at a distance in fog.")]
        [SerializeField] Color _markColour = new(0.85f, 0.25f, 0.2f);

        [SerializeField] float _agentRadius = 0.4f;

        [Header("Loot")]
        [Tooltip("What any of this role is carrying. Dropped where they fall. Structure; rebuilt by the factory.")]
        [SerializeField] LootDrop[] _loot = Array.Empty<LootDrop>();

        [Tooltip("What this role drops when it was manning a camp with stores in it. The same lines "
                 + "plus the camp's own. Structure; rebuilt by the factory.")]
        [SerializeField] LootDrop[] _villageLoot = Array.Empty<LootDrop>();

        public string Id => _id;
        public string DisplayName => string.IsNullOrEmpty(_displayName) ? _id : _displayName;
        public string Description => _description;
        public NativeRole Role => _role;

        public float MaxHealth => Mathf.Max(1f, _maxHealth);
        public float WalkSpeed => Mathf.Max(0.1f, _walkSpeed);
        public float RunSpeed => Mathf.Max(WalkSpeed, _runSpeed);
        public float PatrolRadius => Mathf.Max(2f, _patrolRadius);
        public float IdleMin => Mathf.Max(0f, Mathf.Min(_idleSeconds.x, _idleSeconds.y));
        public float IdleMax => Mathf.Max(IdleMin, Mathf.Max(_idleSeconds.x, _idleSeconds.y));

        public float DayNotice => Mathf.Max(1f, _dayNotice);
        public float NightNotice => Mathf.Max(DayNotice, _nightNotice);
        public float VisionAngle => Mathf.Clamp(_visionAngle, 5f, 180f);
        public float Earshot => Mathf.Max(0f, _earshot);

        public float MemorySeconds => Mathf.Max(0f, _memorySeconds);
        public float InvestigateSeconds => Mathf.Max(0.5f, _investigateSeconds);
        public float AlarmRadius => Mathf.Max(0f, _alarmRadius);
        public float DayLeash => Mathf.Max(5f, _dayLeash);
        public float NightLeash => Mathf.Max(DayLeash, _nightLeash);
        public float FleeHealth => Mathf.Clamp01(_fleeHealth);

        public float AttackDamage => Mathf.Max(0f, _attackDamage);
        public float AttackRange => Mathf.Max(0.5f, _attackRange);
        public float AttackInterval => Mathf.Max(0.2f, _attackInterval);
        public float AttackStun => Mathf.Max(0f, _attackStun);
        public float AttackKnockback => Mathf.Max(0f, _attackKnockback);
        public float WindupSeconds => Mathf.Max(0f, _windupSeconds);

        /// <summary>
        /// Whether this one drags a downed player home rather than standing over the body.
        ///
        /// A role, not a difficulty knob. The spearman does it because it is the one with the free
        /// hands and the reason to; the blowgunner does not, because the fight #107 wants is one
        /// where somebody is being carried off *while* somebody else keeps shooting at whoever is
        /// running after them.
        /// </summary>
        public bool Abducts => _abducts;

        /// <summary>Metres from a fresh body this one will come for it. Zero when it will not.</summary>
        public float AbductRadius => _abducts ? Mathf.Max(0f, _abductRadius) : 0f;

        /// <summary>
        /// Metres a second while carrying a body. Well under a sprint on purpose: the same promise
        /// that makes running away always work makes chasing a kidnapper always work.
        /// </summary>
        public float HaulSpeed => RunSpeed * Mathf.Clamp(_haulFraction, 0.1f, 1f);

        public bool IsRanged => _ranged;
        public float Standoff => Mathf.Clamp(_standoff, 1f, AttackRange);
        public float SpreadDegrees => Mathf.Max(0f, _spreadDegrees);

        public Vector3 BodySize => new(Mathf.Max(0.1f, _bodySize.x), Mathf.Max(0.1f, _bodySize.y),
                                       Mathf.Max(0.1f, _bodySize.z));
        public float BodyHeight => BodySize.y;
        public Color Colour => _colour;
        public Color MarkColour => _markColour;
        public float AgentRadius => Mathf.Max(0.1f, _agentRadius);

        /// <summary>What a wandering body carries: what was on it when it walked out.</summary>
        public LootDrop[] Loot => _loot;

        /// <summary>
        /// What a body manning a stocked camp carries: the same lines plus the camp's own stores.
        ///
        /// A superset rather than a replacement, on purpose. The difference between the two tables is
        /// the *reason to go there*, and a village table that dropped different things instead of more
        /// things would make the wild table a separate economy rather than the poor end of one.
        /// </summary>
        public LootDrop[] VillageLoot => _villageLoot == null || _villageLoot.Length == 0
                                         ? _loot
                                         : _villageLoot;

        /// <summary>
        /// The table a particular body rolls, given where it was standing when it died.
        ///
        /// The whole of #109's "better drops from village natives than wandering ones" is this one
        /// bool, and it is decided at spawn by <see cref="AI.NativeSpawner.Camp.Stocked"/> rather
        /// than by geometry: a spearman that chased you two hundred metres out of the village is
        /// still a village spearman, because what it is carrying left the village with it.
        /// </summary>
        public LootDrop[] LootFor(bool stocked) => stocked ? VillageLoot : Loot;

        // ---------------------------------------------------------------- the sun

        /// <summary>
        /// How far they notice you, given how dark it is. The one number the whole acceptance
        /// criterion turns on, and it is a straight blend rather than a switch so that dusk is a
        /// slope you can feel rather than a moment somebody flips.
        /// </summary>
        public float NoticeRadius(float night01) => Mathf.Lerp(DayNotice, NightNotice, Mathf.Clamp01(night01));

        /// <summary>How far from camp they will follow you, given how dark it is.</summary>
        public float LeashRange(float night01) => Mathf.Lerp(DayLeash, NightLeash, Mathf.Clamp01(night01));

        /// <summary>
        /// Whether they still need to actually see you. True in daylight and false in the dark, with
        /// the switch at half-night - the point where the sun is properly down and a torch is the
        /// only reason you can see them either.
        /// </summary>
        public bool NeedsSight(float night01) => night01 < 0.5f;

        /// <summary>Damage per second if every swing lands. What the balance pass will argue about.</summary>
        public float DamagePerSecond => AttackDamage / AttackInterval;

        /// <summary>What a wandering body is worth on the ground, at full price rather than trade.</summary>
        public float ExpectedLootValue => Worth(_loot);

        /// <summary>What a body off a stocked camp is worth. The number the raid is priced against.</summary>
        public float ExpectedVillageLootValue => Worth(VillageLoot);

        static float Worth(LootDrop[] table)
        {
            if (table == null) return 0f;

            float total = 0f;
            foreach (LootDrop drop in table)
                if (drop != null) total += drop.ExpectedValue;

            return total;
        }

        /// <summary>Bake time only. Everything structural is re-applied; nothing tuned is overwritten.</summary>
        public void Configure(string id, string displayName, string description, NativeRole role,
                              float maxHealth, float walkSpeed, float runSpeed, float patrolRadius,
                              Vector2 idleSeconds, float dayNotice, float nightNotice, float visionAngle,
                              float earshot, float memorySeconds, float investigateSeconds, float alarmRadius,
                              float dayLeash, float nightLeash, float fleeHealth, float attackDamage,
                              float attackRange, float attackInterval, float attackStun, float attackKnockback,
                              float windupSeconds, bool ranged, float standoff, float spreadDegrees,
                              Vector3 bodySize, Color colour, Color markColour, float agentRadius)
        {
            _id = id;
            _displayName = displayName;
            _description = description;
            _role = role;
            _maxHealth = maxHealth;
            _walkSpeed = walkSpeed;
            _runSpeed = runSpeed;
            _patrolRadius = patrolRadius;
            _idleSeconds = idleSeconds;
            _dayNotice = dayNotice;
            _nightNotice = nightNotice;
            _visionAngle = visionAngle;
            _earshot = earshot;
            _memorySeconds = memorySeconds;
            _investigateSeconds = investigateSeconds;
            _alarmRadius = alarmRadius;
            _dayLeash = dayLeash;
            _nightLeash = nightLeash;
            _fleeHealth = fleeHealth;
            _attackDamage = attackDamage;
            _attackRange = attackRange;
            _attackInterval = attackInterval;
            _attackStun = attackStun;
            _attackKnockback = attackKnockback;
            _windupSeconds = windupSeconds;
            _ranged = ranged;
            _standoff = standoff;
            _spreadDegrees = spreadDegrees;
            _bodySize = bodySize;
            _colour = colour;
            _markColour = markColour;
            _agentRadius = agentRadius;
        }

        /// <summary>
        /// Bake time only, and both tables at once, because they are one decision. A rebuild that set
        /// one and left the other would leave a village dropping last week's table, which is exactly
        /// the kind of silent half-change the structure/tuning split exists to prevent.
        /// </summary>
        public void SetLoot(LootDrop[] loot, LootDrop[] villageLoot)
        {
            _loot = loot ?? Array.Empty<LootDrop>();
            _villageLoot = villageLoot ?? Array.Empty<LootDrop>();
        }

        /// <summary>
        /// Bake time only. Who drags bodies off is a decision about what the three roles are *for*
        /// rather than a number somebody tunes in an inspector, so - like the loot table - it is
        /// re-applied on every factory run instead of being seeded once and left alone.
        /// </summary>
        public void SetAbduction(bool abducts, float radius, float haulFraction)
        {
            _abducts = abducts;
            _abductRadius = radius;
            _haulFraction = haulFraction;
        }
    }
}
