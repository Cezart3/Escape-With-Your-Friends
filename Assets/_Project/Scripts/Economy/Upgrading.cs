using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.World;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Economy
{
    /// <summary>
    /// The player's side of an upgrade. One owner-callable request, and a server transaction that
    /// takes before it gives.
    ///
    /// It lives on the player for the same reason <see cref="Trading"/> does: a bench and a counter
    /// are owned by nobody, so a <c>ServerRpc</c> on either would have no owner to require and would
    /// accept a message from anyone. The bag has an owner. So the request comes from the player,
    /// carries **an index into the upgrade catalog and nothing else**, and the server reads the price,
    /// the materials and the venue off its own asset. There is nowhere in the message to put a
    /// discount, the same way there was nowhere in #48's.
    ///
    /// The transaction is the one #44 and #48 already settled, in the same order and for the same
    /// reason: **take the weapon, take the materials, take the money, then give the new weapon** - and
    /// if the last step does not fit, put all three back exactly. Two players upgrading in the same
    /// tick arrive here back to back on one thread, and the second one reads what the first one left.
    /// </summary>
    public class Upgrading : NetworkBehaviour
    {
        [Tooltip("Every upgrade in the game. The wire carries an index into this, so every peer needs "
                 + "the identical asset.")]
        [SerializeField] UpgradeCatalog _catalog;

        [SerializeField] Inventory _inventory;
        [SerializeField] Wallet _wallet;
        [SerializeField] Weapon _weapon;

        void Awake()
        {
            if (_inventory == null) _inventory = GetComponent<Inventory>();
            if (_wallet == null) _wallet = GetComponent<Wallet>();
            if (_weapon == null) _weapon = GetComponent<Weapon>();

            UpgradeCatalog.Use(_catalog);
        }

        public UpgradeCatalog Catalog => _catalog != null ? _catalog : UpgradeCatalog.Active;

        /// <summary>What is available for the weapon in your hand, or null. What the screen lists.</summary>
        public UpgradeDef Available
        {
            get
            {
                UpgradeCatalog catalog = Catalog;
                WeaponDef held = _weapon != null ? _weapon.Equipped : null;

                return catalog != null ? catalog.For(held) : null;
            }
        }

        /// <summary>Owner side. Ask for one step up a line, standing wherever it has to be done.</summary>
        public void RequestUpgrade(UpgradeDef def)
        {
            UpgradeCatalog catalog = Catalog;
            if (!IsOwner || def == null || catalog == null) return;

            ushort index = catalog.IndexOf(def);
            if (index == 0) return;

            ServerUpgradeRpc(index);
        }

        [ServerRpc]
        void ServerUpgradeRpc(ushort index)
        {
            UpgradeCatalog catalog = Catalog;
            UpgradeDef def = catalog != null ? catalog.At(index) : null;

            if (def == null)
            {
                Refused("no such upgrade");
                return;
            }

            ServerUpgrade(def, out string why);
            if (why != null) Refused(why);
        }

        /// <summary>
        /// Swaps one weapon for the next in its line. Returns what the player is now carrying, or
        /// null with a reason.
        ///
        /// Nothing here is clamped the way a purchase is: you are upgrading one weapon, and there is
        /// no sensible "you could afford most of it". It either happens or it is refused with a
        /// sentence saying which of the four things was missing.
        /// </summary>
        [Server]
        public WeaponDef ServerUpgrade(UpgradeDef def, out string why)
        {
            why = null;

            if (def == null || !def.IsValid)
            {
                why = "that upgrade is broken";
                return null;
            }

            if (_inventory == null || _wallet == null)
            {
                why = "you have no bag to upgrade out of";
                return null;
            }

            if (!InVenue(def.Venue, transform.position))
            {
                why = def.Venue == UpgradeVenue.Bench
                    ? "you are not at a workbench"
                    : "you are not at a trader";
                return null;
            }

            ItemDef fromItem = def.From.Item;
            ItemDef toItem = def.To.Item;

            if (!_inventory.Has(fromItem))
            {
                why = $"you are not carrying a {fromItem.Id}";
                return null;
            }

            foreach (Ingredient material in def.Materials)
            {
                if (!material.IsValid)
                {
                    why = "that upgrade is broken";
                    return null;
                }

                if (!_inventory.Has(material.Item, material.Count))
                {
                    why = $"you need {material.Count}x {material.Item.Id}";
                    return null;
                }
            }

            if (_wallet.Balance < def.Price)
            {
                why = $"it costs {def.Price} and you have {_wallet.Balance}";
                return null;
            }

            // Everything was checked above, so nothing below should fail - but a check is a claim
            // about a moment and this is the next moment, so each step still reports what it did and
            // the whole thing rolls back exactly if one of them disagrees.
            if (_inventory.Remove(fromItem, 1) != 1)
            {
                why = $"you are not carrying a {fromItem.Id}";
                return null;
            }

            int returned = 0;
            foreach (Ingredient material in def.Materials)
            {
                if (_inventory.Remove(material.Item, material.Count) == material.Count)
                {
                    returned++;
                    continue;
                }

                // Half a set of materials came out. Put back what did come out, and the weapon.
                Refund(def, returned, fromItem, 0);
                why = $"you need {material.Count}x {material.Item.Id}";
                return null;
            }

            if (def.Price > 0
                && !_wallet.ServerTrySpend(def.Price, $"upgraded {fromItem.Id} to {toItem.Id}"))
            {
                Refund(def, returned, fromItem, 0);
                why = $"it costs {def.Price} and you have {_wallet.Balance}";
                return null;
            }

            int left = _inventory.Add(toItem, 1);
            if (left > 0)
            {
                // The new weapon is heavier than the old one on every line, so a bag that was exactly
                // at the limit can refuse the thing it just paid for. Everything goes back.
                Refund(def, returned, fromItem, def.Price);
                why = "your bag is full";
                return null;
            }

            // Rounds already in the magazine follow the weapon, clamped to the new one and only if it
            // eats the same ammunition. Losing a half-magazine to an upgrade would make upgrading
            // something you do carefully rather than something you do.
            int carried = _weapon != null ? _weapon.ServerCarryMagazine(def.From, def.To) : 0;

            Debug.Log($"[Upgrading] {name} upgraded {fromItem.Id} to {toItem.Id} at the "
                      + $"{def.Venue.ToString().ToLowerInvariant()}"
                      + (def.Price > 0 ? $" for {def.Price}" : "")
                      + (carried > 0 ? $", carrying {carried} round(s) across" : "") + ".");

            return def.To;
        }

        /// <summary>Puts back exactly what came out. Money through the refund door, never as income.</summary>
        [Server]
        void Refund(UpgradeDef def, int materialsTaken, ItemDef weapon, int price)
        {
            for (int i = 0; i < materialsTaken && i < def.Materials.Length; i++)
                _inventory.Add(def.Materials[i].Item, def.Materials[i].Count);

            _inventory.Add(weapon, 1);

            if (price > 0) _wallet.ServerRefund(price, "the upgrade did not fit");
        }

        /// <summary>
        /// Whether the work can be done where you are standing.
        ///
        /// Two venues rather than one because they are two different transactions: a bench costs
        /// materials and your own time, a trader costs money. Which one an upgrade uses is on the
        /// asset, so "who does this work" is a balance decision in a data file rather than a rule in
        /// this method.
        /// </summary>
        public static bool InVenue(UpgradeVenue venue, Vector3 position)
            => venue == UpgradeVenue.Bench
                ? CraftingStation.InRange(CraftStation.Bench, position)
                : ShopCounter.NearestInReach(position) != null;

        /// <summary>
        /// Why the server said no, sent back to the one client that asked. Not a broadcast: the rest
        /// of the squad does not need to know you could not afford a chainsaw.
        /// </summary>
        [Server]
        void Refused(string why)
        {
            Debug.Log($"[Upgrading] {name} refused: {why}.");
            TargetRefused(Owner, why);
        }

        [TargetRpc]
        void TargetRefused(FishNet.Connection.NetworkConnection connection, string why)
        {
            LastRefusal = why;
            LastRefusalAt = Time.time;
        }

        /// <summary>What the server last said no to, for the screen to show. Client side.</summary>
        public string LastRefusal { get; private set; }

        public float LastRefusalAt { get; private set; }

        /// <summary>Bake time only.</summary>
        public void Configure(UpgradeCatalog catalog, Inventory inventory, Wallet wallet, Weapon weapon)
        {
            _catalog = catalog;
            _inventory = inventory;
            _wallet = wallet;
            _weapon = weapon;
        }
    }
}
