using System.Collections.Generic;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Data;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Economy
{
    /// <summary>The five things a trader will bolt onto a weapon.</summary>
    public enum ModTrack : byte
    {
        /// <summary>More damage per hit, five levels. The only one a knife can take.</summary>
        Firepower,
        /// <summary>Iron sight at zero, then four scopes, each zooming further when you aim.</summary>
        Sight,
        /// <summary>One purchase. Aimed shots scatter half as much again.</summary>
        RedDot,
        /// <summary>One purchase. A light on the barrel, toggled with its own key.</summary>
        Flashlight,
        /// <summary>One purchase at three times the weapon's price. The kick drops to a third.</summary>
        Grip,
    }

    /// <summary>
    /// Per-weapon upgrades, bought at the trader with the weapon in your hand (the economy overhaul
    /// that followed #209).
    ///
    /// **The price of everything is the weapon's own shop price times a multiplier**, so the same
    /// table makes a pistol's scope a lunch and a sniper's a small fortune. A track with several
    /// levels climbs geometrically from its first multiplier to five times the weapon at the top,
    /// which is where a player either grinds for hours or goes to the casino. The weapon's price is
    /// read off the counter you are standing at, so a counter that does not sell the gun (the
    /// barman) cannot mod it either, and a gun nobody sells has no mods.
    ///
    /// **Upgrades belong to the player, per weapon id, not to the item.** Drop your rifle and pick
    /// up another and it is still your rifle. That is one dictionary instead of per-stack metadata the
    /// bag has never had, and it is what a player expects from "I upgraded my rifle".
    /// </summary>
    public class WeaponMods : NetworkBehaviour
    {
        /// <summary>
        /// Levels, first-level multiplier and top-level multiplier of the weapon price, per track.
        /// A one-level track costs its first multiplier.
        /// </summary>
        static readonly (int Levels, float First, float Last, string Label)[] Tracks =
        {
            (5, 0.25f, 5f, "Firepower"),
            (4, 0.5f, 5f, "Scope"),
            (1, 0.75f, 0.75f, "Red dot"),
            (1, 0.3f, 0.3f, "Flashlight"),
            (1, 3f, 3f, "Recoil grip"),
        };

        /// <summary>How far each sight level zooms when aiming. Zero is the iron sight.</summary>
        static readonly float[] Zooms = { 1.25f, 2f, 3f, 4.5f, 6f };

        public const float DamagePerLevel = 0.12f;
        public const float AimedSpread = 0.5f, RedDotSpread = 0.5f, GripRecoil = 0.35f;

        /// <summary>"rifle/Sight" to the level bought. Server writes, everyone reads.</summary>
        readonly SyncDictionary<string, byte> _levels = new();

        Weapon _weapon;
        Wallet _wallet;

        void Awake()
        {
            _weapon = GetComponent<Weapon>();
            _wallet = GetComponent<Wallet>();
        }

        WeaponDef _observed;

        /// <summary>Server: tell the <see cref="GameStage"/> whenever this player picks up a different gun.</summary>
        void Update()
        {
            if (!IsServerStarted || _weapon == null || _weapon.Equipped == _observed) return;

            _observed = _weapon.Equipped;
            if (_observed == null || _observed.Kind != WeaponKind.Hitscan) return;

            int price = 0;
            foreach (ShopCounter counter in FindObjectsByType<ShopCounter>(FindObjectsSortMode.None))
                if (counter != null) price = Mathf.Max(price, WeaponPrice(counter.Shop, _observed));
            GameStage.Observe(price, $"{name} holding the {_observed.Id}");
        }

        // ---------------------------------------------------------------- the table

        public static int Levels(ModTrack track) => Tracks[(int)track].Levels;

        public static string Label(ModTrack track) => Tracks[(int)track].Label;

        /// <summary>Whether this weapon takes this track at all. Melee only gets firepower.</summary>
        public static bool Fits(WeaponDef weapon, ModTrack track)
            => weapon != null && weapon.Item != null && (track == ModTrack.Firepower || weapon.Kind == WeaponKind.Hitscan);

        /// <summary>What level <paramref name="level"/> (1-based) costs on a weapon priced <paramref name="weaponPrice"/>.</summary>
        public static int Price(int weaponPrice, ModTrack track, int level)
        {
            var (levels, first, last, _) = Tracks[(int)track];
            float step = levels > 1 ? (level - 1) / (float)(levels - 1) : 0f;
            return Nice(weaponPrice * first * Mathf.Pow(last / first, step));
        }

        /// <summary>Three significant figures: 62,500 and 1,250,000 rather than 62,487.</summary>
        static int Nice(float value)
        {
            if (value < 100f) return Mathf.Max(1, Mathf.RoundToInt(value));
            float unit = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(value)) - 2f);
            return Mathf.RoundToInt(Mathf.Round(value / unit) * unit);
        }

        /// <summary>What the counter sells this weapon for, or 0 if it does not.</summary>
        public static int WeaponPrice(ShopDef shop, WeaponDef weapon)
        {
            if (shop == null || weapon == null || weapon.Item == null) return 0;

            foreach (ShopDef.Offer offer in shop.Offers)
                if (offer.Item == weapon.Item) return offer.Price;

            return 0;
        }

        public static float Zoom(int sightLevel) => Zooms[Mathf.Clamp(sightLevel, 0, Zooms.Length - 1)];

        // ---------------------------------------------------------------- what this player has

        public int Level(WeaponDef weapon, ModTrack track)
            => weapon != null && _levels.TryGetValue(Key(weapon.Id, track), out byte level) ? level : 0;

        public bool Has(WeaponDef weapon, ModTrack track) => Level(weapon, track) > 0;

        public float DamageScale(WeaponDef weapon) => 1f + DamagePerLevel * Level(weapon, ModTrack.Firepower);

        public float RecoilScale(WeaponDef weapon) => Has(weapon, ModTrack.Grip) ? GripRecoil : 1f;

        /// <summary>Multiplier on the weapon's own cone for one shot.</summary>
        public float SpreadScale(WeaponDef weapon, bool aiming)
            => !aiming ? 1f : Has(weapon, ModTrack.RedDot) ? AimedSpread * RedDotSpread : AimedSpread;

        static string Key(string weaponId, ModTrack track) => $"{weaponId}/{track}";

        // ---------------------------------------------------------------- buying

        /// <summary>Owner side. The next level of <paramref name="track"/> for the weapon in your hand.</summary>
        public void RequestBuy(ModTrack track)
        {
            if (IsOwner) ServerBuyRpc(track);
        }

        [ServerRpc]
        void ServerBuyRpc(ModTrack track)
        {
            if (!ServerBuy(track, out string why)) Refused(why);
        }

        /// <summary>
        /// Everything the client could lie about is read here: the weapon from the server's own copy
        /// of the hand, the price from the counter the server finds you at, the level from the
        /// dictionary. The message carries the track and nothing else.
        /// </summary>
        [Server]
        public bool ServerBuy(ModTrack track, out string why)
        {
            why = null;
            WeaponDef weapon = _weapon != null ? _weapon.Equipped : null;
            ShopCounter counter = ShopCounter.NearestInReach(transform.position);

            if (weapon == null || weapon.Item == null) why = "hold the weapon you want upgraded";
            else if (!Fits(weapon, track)) why = $"a {weapon.Id} takes no {Label(track).ToLowerInvariant()}";
            else if (counter == null) why = "you are not at a trader";
            else if (WeaponPrice(counter.Shop, weapon) <= 0) why = $"this counter does not work on a {weapon.Id}";
            else if (Level(weapon, track) >= Levels(track)) why = $"{Label(track).ToLowerInvariant()} is already maxed";
            if (why != null) return false;

            int level = Level(weapon, track) + 1;
            int price = Price(WeaponPrice(counter.Shop, weapon), track, level);

            if (_wallet == null || !_wallet.ServerTrySpend(price, $"{Label(track)} {level} on the {weapon.Id}"))
            {
                why = $"it costs {price} and you have {(_wallet != null ? _wallet.Balance : 0)}";
                return false;
            }

            _levels[Key(weapon.Id, track)] = (byte)level;
            Debug.Log($"[WeaponMods] {name} bought {Label(track)} {level}/{Levels(track)} on the {weapon.Id} for {price}.");
            return true;
        }

        [Server]
        void Refused(string why)
        {
            Debug.Log($"[WeaponMods] {name} refused: {why}.");
            TargetRefused(Owner, why);
        }

        [TargetRpc]
        void TargetRefused(FishNet.Connection.NetworkConnection connection, string why) => LastRefusal = why;

        /// <summary>What the server last said no to. Client side, for the shop panel.</summary>
        public string LastRefusal { get; private set; }

        // ---------------------------------------------------------------- saving

        /// <summary>"rifle/Sight=2" per bought level, for RunSave.</summary>
        public List<string> Saved()
        {
            var saved = new List<string>();
            foreach (KeyValuePair<string, byte> pair in _levels) saved.Add($"{pair.Key}={pair.Value}");
            return saved;
        }

        [Server]
        public void ServerRestore(IEnumerable<string> saved)
        {
            _levels.Clear();
            if (saved == null) return;

            foreach (string line in saved)
            {
                int at = line.LastIndexOf('=');
                if (at > 0 && byte.TryParse(line.Substring(at + 1), out byte level))
                    _levels[line.Substring(0, at)] = level;
            }
        }
    }
}
