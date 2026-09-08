using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// One thing that can be on the end of the line: a fish, a boot, or an oyster with a pearl in it.
    ///
    /// Same doctrine as every other definition here - the asset is the truth and <c>FishFactory</c>
    /// only seeds it - but this one carries a whole minigame's tuning, so it is worth saying what the
    /// numbers are tuned *against*.
    ///
    /// **Rarity is a weight, not a percentage.** Percentages have to add up to one hundred, which
    /// means adding a species is a change to every other species' number and a bug the first time
    /// somebody forgets. Weights are local: a new fish is a row, and everything else keeps the odds
    /// it had relative to everything else.
    ///
    /// **The fight is a rhythm, not a reflex test.** A hooked fish alternates between calm - when
    /// reeling costs you a little tension and buys a lot of line - and a *run*, when reeling costs a
    /// great deal. The whole minigame is noticing which one you are in. That is why the run is a
    /// separate duration rather than half the period: a run has to be short enough to wait out and
    /// long enough to see, and those are two different numbers on two different fish. Everything is
    /// tuned so a sardine lands in one continuous pull and a tuna does not.
    /// </summary>
    [CreateAssetMenu(menuName = "EWYF/Fish", fileName = "Fish")]
    public class FishDef : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable id. The catalog sorts on it, so it is also the wire order.")]
        [SerializeField] string _id = "fish";

        [SerializeField] string _displayName = "Fish";

        [TextArea]
        [SerializeField] string _description = "";

        [Header("Rarity")]
        [Tooltip("Relative weight in the table. Twice the number is twice as often, and nothing has "
                 + "to add up to anything.")]
        [Min(0)]
        [SerializeField] int _rarity = 10;

        [Header("What you get")]
        [Tooltip("The item this becomes in the bag. Several species can share one - a bigger fish is "
                 + "more of the same raw fish, not a second food item to cook a second way.")]
        [SerializeField] ItemDef _catch;

        [Min(0)]
        [SerializeField] int _min = 1;

        [Min(0)]
        [SerializeField] int _max = 1;

        [Header("The wait")]
        [Tooltip("Seconds between the cast and the bite, rolled per cast. The long end is the whole "
                 + "of 'relaxing'.")]
        [SerializeField] Vector2 _biteSeconds = new(2f, 6f);

        [Tooltip("Seconds you have to strike once it bites. Missing it loses the fish, not the rod.")]
        [Min(0.1f)]
        [SerializeField] float _hookSeconds = 1.2f;

        [Header("The fight")]
        [Tooltip("Metres of line out the moment it is hooked. Reel this to zero and it is yours.")]
        [Min(0.5f)]
        [SerializeField] float _distance = 8f;

        [Tooltip("Metres per second the line comes in while you are reeling.")]
        [Min(0.1f)]
        [SerializeField] float _reelSpeed = 2.2f;

        [Tooltip("Metres per second it takes back while you are not. Small: letting go is meant to be "
                 + "the safe move, just not a free one.")]
        [Min(0f)]
        [SerializeField] float _slipSpeed = 0.3f;

        [Tooltip("Tension per second added by reeling while it is calm.")]
        [Min(0f)]
        [SerializeField] float _calmPull = 0.2f;

        [Tooltip("Tension per second added by reeling during a run. This is the number that makes a "
                 + "tuna a tuna.")]
        [Min(0f)]
        [SerializeField] float _runPull = 0.8f;

        [Tooltip("Seconds from the start of one run to the start of the next.")]
        [Min(0.2f)]
        [SerializeField] float _struggleSeconds = 3f;

        [Tooltip("Seconds a run lasts. Zero means this thing does not fight at all - see the boot.")]
        [Min(0f)]
        [SerializeField] float _runSeconds = 0.8f;

        public string Id => _id;
        public string DisplayName => _displayName;
        public string Description => _description;

        public int Rarity => Mathf.Max(0, _rarity);

        public ItemDef Catch => _catch;
        public int Low => Mathf.Max(0, Mathf.Min(_min, _max));
        public int High => Mathf.Max(Low, Mathf.Max(_min, _max));

        public Vector2 BiteSeconds => new(Mathf.Min(_biteSeconds.x, _biteSeconds.y),
                                          Mathf.Max(_biteSeconds.x, _biteSeconds.y));

        public float HookSeconds => Mathf.Max(0.1f, _hookSeconds);

        public float Distance => Mathf.Max(0.5f, _distance);
        public float ReelSpeed => Mathf.Max(0.1f, _reelSpeed);
        public float SlipSpeed => Mathf.Max(0f, _slipSpeed);
        public float CalmPull => Mathf.Max(0f, _calmPull);
        public float RunPull => Mathf.Max(0f, _runPull);
        public float StruggleSeconds => Mathf.Max(0.2f, _struggleSeconds);
        public float RunSeconds => Mathf.Clamp(_runSeconds, 0f, StruggleSeconds * 0.9f);

        /// <summary>True while this thing is bolting, at <paramref name="fightSeconds"/> into the fight.</summary>
        /// <remarks>
        /// The run sits at the *end* of the period so every fight opens calm. A fish that starts
        /// running the instant it is hooked reads as the game cheating rather than as a fish.
        /// </remarks>
        public bool Running(float fightSeconds)
        {
            float run = RunSeconds;
            if (run <= 0f) return false;

            return Mathf.Repeat(fightSeconds, StruggleSeconds) >= StruggleSeconds - run;
        }

        /// <summary>Average count per catch.</summary>
        public float Expected => Catch == null ? 0f : (Low + High) * 0.5f;

        /// <summary>Average worth of one catch, in the item's own <see cref="ItemDef.Value"/>.</summary>
        public float ExpectedValue => Catch == null ? 0f : Expected * Catch.Value;

        /// <summary>Average kilograms one catch adds to the bag.</summary>
        public float ExpectedWeight => Catch == null ? 0f : Expected * Catch.Weight;

        /// <summary>
        /// Seconds a perfect fight takes: reel through every calm phase, let go through every run.
        /// Used by the harness to price fishing per minute, which is the only way "profitable"
        /// means anything.
        /// </summary>
        public float PerfectFightSeconds
        {
            get
            {
                float calm = StruggleSeconds - RunSeconds;
                float gained = ReelSpeed * calm - SlipSpeed * RunSeconds;

                // A fish that cannot be beaten by ideal play is a tuning bug, not a hard fish. Say so
                // with a big number rather than dividing by zero.
                if (gained <= 0f) return 999f;

                return Distance / gained * StruggleSeconds;
            }
        }

        /// <summary>Bake time only. Everything a factory is allowed to seed, in one call.</summary>
        public void Configure(string id, string displayName, string description, int rarity,
                              Vector2 biteSeconds, float hookSeconds, float distance, float reelSpeed,
                              float slipSpeed, float calmPull, float runPull, float struggleSeconds,
                              float runSeconds)
        {
            _id = id;
            _displayName = displayName;
            _description = description;
            _rarity = rarity;
            _biteSeconds = biteSeconds;
            _hookSeconds = hookSeconds;
            _distance = distance;
            _reelSpeed = reelSpeed;
            _slipSpeed = slipSpeed;
            _calmPull = calmPull;
            _runPull = runPull;
            _struggleSeconds = struggleSeconds;
            _runSeconds = runSeconds;
        }

        /// <summary>Bake time only. Structural, so it is re-applied on every run.</summary>
        public void SetCatch(ItemDef item, int min, int max)
        {
            _catch = item;
            _min = min;
            _max = max;
        }

        public override string ToString() => string.IsNullOrEmpty(_id) ? name : _id;
    }
}
