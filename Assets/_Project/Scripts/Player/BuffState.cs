using System;
using System.Text;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Data;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// One active buff. Four bytes plus a tick, because this replicates on every peer that can see you.
    /// </summary>
    [Serializable]
    public struct ActiveBuff : IEquatable<ActiveBuff>
    {
        /// <summary>Index into <see cref="BuffCatalog"/>. Zero is nothing.</summary>
        public ushort Index;

        /// <summary>
        /// Network tick at which this ends. A tick rather than a local timestamp, for the same reason
        /// <c>Health</c>'s bleed-out is one: every peer has to count down to the same moment, and the
        /// HUD has to show a number your friends agree with.
        /// </summary>
        public uint EndTick;

        public ActiveBuff(ushort index, uint endTick)
        {
            Index = index;
            EndTick = endTick;
        }

        public bool IsEmpty => Index == 0;

        public BuffDef Def => BuffCatalog.Active != null ? BuffCatalog.Active.At(Index) : null;

        public bool Equals(ActiveBuff other) => Index == other.Index && EndTick == other.EndTick;
        public override bool Equals(object obj) => obj is ActiveBuff other && Equals(other);
        public override int GetHashCode() => (Index * 397) ^ (int)EndTick;
    }

    /// <summary>
    /// What is currently affecting this player: heals over time, full stomachs, and eventually rum.
    ///
    /// This is the system #45's acceptance is really about. Eating a coconut is not a food mechanic
    /// here - it applies a <see cref="BuffDef"/>, and so will the casino's alcohol, a native's poison
    /// dart, and whatever else wants to change somebody's numbers for a while. Nothing in this class
    /// knows what a coconut is.
    ///
    /// Server-authoritative like everything else that can be cheated at: there is no RPC that lets a
    /// client give itself a buff. <see cref="ItemUse"/> asks the server to use an item, the server
    /// takes the item and then calls in here.
    ///
    /// The multipliers are read every frame by the motor and by <see cref="Health"/>, on every peer,
    /// from replicated state - so they are computed on demand over a list that is almost always
    /// empty and never more than a handful long.
    /// </summary>
    public class BuffState : NetworkBehaviour
    {
        [Tooltip("Every buff in the game. Assigned at bake time; the wire format is an index into it.")]
        [SerializeField] BuffCatalog _catalog;

        /// <summary>
        /// Active buffs. A SyncList rather than a fixed array because, unlike inventory slots, there
        /// is no natural capacity and the common case is zero - a fixed twenty-slot array would
        /// replicate twenty empties per player to describe nothing happening.
        /// </summary>
        readonly SyncList<ActiveBuff> _active = new();

        /// <summary>Fired on every peer when a buff starts or ends. The HUD redraws off this.</summary>
        public event Action Changed;

        public int Count => _active.Count;
        public BuffCatalog Catalog => _catalog;

        void Awake()
        {
            BuffCatalog.Use(_catalog);
            _active.OnChange += OnActiveChanged;
        }

        void OnDestroy() => _active.OnChange -= OnActiveChanged;

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Client-side proof for the #45 harness. The server can show its own list is correct;
            // only a client can show the list arrived, with the right buffs and the right time left,
            // on a machine that did not apply them.
            if (IsServerStarted || !Core.CommandLine.HasFlag("-buffTest")) return;

            string last = null;
            Changed += () =>
            {
                string now = Describe();
                if (now == last) return;

                last = now;
                Debug.Log($"[BuffState] client owner {OwnerId} sees: {now}");
            };
        }

        void OnActiveChanged(SyncListOperation op, int index, ActiveBuff older, ActiveBuff newer,
                             bool asServer)
        {
            if (asServer && IsClientStarted) return;

            Changed?.Invoke();
        }

        void Update()
        {
            if (!IsServerStarted) return;

            Expire();
            Tick(Time.deltaTime);
        }

        // ---------------------------------------------------------------- applying

        /// <summary>
        /// Applies a buff. Server only. The instant values land now; anything with a duration joins
        /// the list. Returns false when nothing happened, which is a real outcome - an Ignore-stacking
        /// buff applied twice is a wasted bandage, and the caller may want to refuse the use.
        /// </summary>
        [Server]
        public bool Apply(BuffDef def)
        {
            if (def == null) return false;

            ushort index = _catalog != null ? _catalog.IndexOf(def) : (ushort)0;
            if (index == 0)
            {
                Debug.LogError($"[BuffState] '{def}' is not in the buff catalog, so it cannot be "
                               + "applied. Run BuffFactory.Build.");
                return false;
            }

            int existing = IndexOfBuff(index);

            if (existing >= 0 && def.Stacking == BuffStacking.Ignore) return false;

            ApplyInstant(def);

            if (!def.Lasts) return true;

            uint end = TimeManager.Tick + (uint)Mathf.CeilToInt(def.Duration * TimeManager.TickRate);

            if (existing >= 0 && def.Stacking == BuffStacking.Refresh)
            {
                // Refresh extends from now, not from whatever was left. Two bandages in a row give one
                // bandage's worth of healing, twice - not a stacked double heal for the same duration.
                _active[existing] = new ActiveBuff(index, end);
                return true;
            }

            _active.Add(new ActiveBuff(index, end));
            return true;
        }

        [Server]
        void ApplyInstant(BuffDef def)
        {
            var health = GetComponent<Health>();
            if (health != null)
            {
                if (def.Health > 0f) health.Heal(def.Health);
                else if (def.Health < 0f)
                    health.TakeDamage(new Core.DamageInfo(-def.Health, Core.DamageType.Environment));
            }

            var stats = GetComponent<SurvivalStats>();
            if (stats != null)
                stats.ServerFeed(def.Hunger, def.Thirst, def.Warmth, def.Stamina);
        }

        /// <summary>Ends a buff early. What sobering up at the Revive Machine will call.</summary>
        [Server]
        public bool Clear(BuffDef def)
        {
            ushort index = _catalog != null ? _catalog.IndexOf(def) : (ushort)0;
            int at = IndexOfBuff(index);

            if (at < 0) return false;

            _active.RemoveAt(at);
            return true;
        }

        /// <summary>Ends everything. Called on death, so nobody wakes up from a revive still drunk.</summary>
        [Server]
        public void ClearAll()
        {
            if (_active.Count == 0) return;
            _active.Clear();
        }

        // ---------------------------------------------------------------- the tick

        [Server]
        void Expire()
        {
            uint now = TimeManager.Tick;

            // Backwards, so removing does not shift an index we have not looked at yet.
            for (int i = _active.Count - 1; i >= 0; i--)
                if (_active[i].EndTick <= now) _active.RemoveAt(i);
        }

        [Server]
        void Tick(float delta)
        {
            if (_active.Count == 0 || delta <= 0f) return;

            float health = 0f, hunger = 0f, thirst = 0f, warmth = 0f, stamina = 0f;

            for (int i = 0; i < _active.Count; i++)
            {
                BuffDef def = Resolve(_active[i]);
                if (def == null) continue;

                health += def.HealthPerSecond;
                hunger += def.HungerPerSecond;
                thirst += def.ThirstPerSecond;
                warmth += def.WarmthPerSecond;
                stamina += def.StaminaPerSecond;
            }

            // Summed first, applied once. Two buffs that each heal and each hurt should net out
            // rather than take turns pushing the health bar in opposite directions.
            if (!Mathf.Approximately(health, 0f))
            {
                var component = GetComponent<Health>();
                if (component != null)
                {
                    if (health > 0f) component.Heal(health * delta);
                    else component.TakeDamage(new Core.DamageInfo(-health * delta,
                                                                  Core.DamageType.Environment));
                }
            }

            if (Mathf.Approximately(hunger, 0f) && Mathf.Approximately(thirst, 0f)
                && Mathf.Approximately(warmth, 0f) && Mathf.Approximately(stamina, 0f))
                return;

            var stats = GetComponent<SurvivalStats>();
            if (stats != null)
                stats.ServerFeed(hunger * delta, thirst * delta, warmth * delta, stamina * delta);
        }

        // ---------------------------------------------------------------- what everything else reads

        /// <summary>Movement speed multiplier from every active buff, multiplied together.</summary>
        public float SpeedMultiplier => Combine(def => def.SpeedMultiplier);

        /// <summary>Incoming damage multiplier. Read by <see cref="Health"/> before it subtracts.</summary>
        public float DamageTakenMultiplier => Combine(def => def.DamageTakenMultiplier);

        /// <summary>Sprint cost multiplier. Read by <see cref="SurvivalStats"/>.</summary>
        public float StaminaCostMultiplier => Combine(def => def.StaminaCostMultiplier);

        /// <summary>
        /// Worst haze of any active buff, 0..1. Not multiplied and not summed: two drinks should not
        /// make the screen twice as blurry as one that already blurred it completely.
        /// </summary>
        public float Haze
        {
            get
            {
                float worst = 0f;
                for (int i = 0; i < _active.Count; i++)
                {
                    BuffDef def = Resolve(_active[i]);
                    if (def != null) worst = Mathf.Max(worst, def.Haze);
                }

                return worst;
            }
        }

        public bool Has(BuffDef def) => IndexOfBuff(_catalog != null ? _catalog.IndexOf(def) : (ushort)0) >= 0;

        /// <summary>Seconds left on a buff, or zero when it is not active.</summary>
        public float Remaining(BuffDef def)
        {
            int at = IndexOfBuff(_catalog != null ? _catalog.IndexOf(def) : (ushort)0);
            if (at < 0) return 0f;

            uint now = TimeManager.Tick;
            uint end = _active[at].EndTick;

            return end <= now ? 0f : (end - now) / (float)TimeManager.TickRate;
        }

        float Combine(Func<BuffDef, float> pick)
        {
            float total = 1f;

            for (int i = 0; i < _active.Count; i++)
            {
                BuffDef def = Resolve(_active[i]);
                if (def != null) total *= pick(def);
            }

            return total;
        }

        int IndexOfBuff(ushort index)
        {
            if (index == 0) return -1;

            for (int i = 0; i < _active.Count; i++)
                if (_active[i].Index == index) return i;

            return -1;
        }

        BuffDef Resolve(ActiveBuff buff) => _catalog != null ? _catalog.At(buff.Index) : null;

        /// <summary>One line for the log. Reads the same on every peer, which is the point.</summary>
        public string Describe()
        {
            if (_active.Count == 0) return "no buffs";

            var text = new StringBuilder();
            uint now = TimeManager.Tick;

            for (int i = 0; i < _active.Count; i++)
            {
                if (i > 0) text.Append(", ");

                BuffDef def = Resolve(_active[i]);
                uint end = _active[i].EndTick;
                float left = end <= now ? 0f : (end - now) / (float)TimeManager.TickRate;

                text.Append(def != null ? def.Id : $"#{_active[i].Index}")
                    .Append(' ').Append(left.ToString("F1")).Append('s');
            }

            return text.ToString();
        }

        /// <summary>Bake time only.</summary>
        public void Configure(BuffCatalog catalog) => _catalog = catalog;
    }
}
