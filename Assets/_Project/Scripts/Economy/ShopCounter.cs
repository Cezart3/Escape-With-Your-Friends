using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Economy
{
    /// <summary>
    /// The trader's counter. Four players can stand at it at once, which is the whole problem.
    ///
    /// #48's acceptance is that "buy/sell round-trips correctly with 4 players shopping
    /// simultaneously", and that is the same race the chests had in #44 wearing a different hat: four
    /// clients are all looking at a replicated stock count of one, all click buy, and all four
    /// requests arrive describing a rifle only one of them can have. The answer is the same, and it is
    /// worth saying in the same words:
    ///
    /// 1. **A request names an offer index and a count, never a price.** The server reads its own
    ///    shelf. The player does not get to say what something costs.
    /// 2. **Take before give, in one server call.** Stock comes off the shelf, then the money comes
    ///    out of the wallet, then the item goes in the bag - and anything that does not fit is put
    ///    back on both, exactly, on the next line.
    /// 3. **Nothing is created that was not destroyed.** A purchase that half-fits refunds the half it
    ///    did not deliver through <see cref="Wallet.ServerRefund"/>, which counts against the burn
    ///    rather than as income, so #47's ledger stays honest about it.
    ///
    /// Stock is a <see cref="SyncList{T}"/> so every client can see the shelf empty as somebody else
    /// buys, which is the only thing that makes the race visible to a player rather than mysterious.
    /// </summary>
    public class ShopCounter : NetworkBehaviour, IInteractable
    {
        /// <summary>How close you have to be for the server to accept a trade, in metres.</summary>
        public const float Reach = 5f;

        [Tooltip("What this trader sells. Assigned at bake time.")]
        [SerializeField] ShopDef _shop;

        /// <summary>
        /// How many of each offer are left. Index-for-index with <see cref="ShopDef.Offers"/>; -1 is
        /// unlimited and never moves.
        /// </summary>
        readonly SyncList<int> _remaining = new();

        float _nextRestock;

        public ShopDef Shop => _shop;

        public int OfferCount => _shop != null ? _shop.Count : 0;

        public string Prompt => _shop != null ? $"Trade with {_shop.DisplayName}" : string.Empty;

        /// <summary>How many of an offer are left, or -1 for unlimited.</summary>
        public int Remaining(int offer)
            => offer >= 0 && offer < _remaining.Count ? _remaining[offer] : 0;

        public ShopDef.Offer OfferAt(int offer)
            => _shop != null && offer >= 0 && offer < _shop.Count ? _shop.Offers[offer] : default;

        public override void OnStartServer()
        {
            base.OnStartServer();

            _remaining.Clear();
            if (_shop == null) return;

            foreach (ShopDef.Offer offer in _shop.Offers) _remaining.Add(offer.Stock);

            _nextRestock = Time.time + _shop.RestockSeconds;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Evidence for the harness that a shelf somebody else emptied reads the same here. The
            // race is only interesting if the other three players can see it happen; costs a flag
            // check on spawn and says nothing in a real session.
            if (IsServerStarted || !CommandLine.HasFlag("-shopTest")) return;

            Debug.Log($"[ShopCounter] client sees {name}: {Describe()}");

            _remaining.OnChange += (SyncListOperation op, int index, int older, int newer,
                                    bool asServer) =>
            {
                if (asServer) return;

                Debug.Log($"[ShopCounter] client sees {name}: {Describe()}");
            };
        }

        void Update()
        {
            if (!IsServerStarted || _shop == null || Time.time < _nextRestock) return;

            _nextRestock = Time.time + _shop.RestockSeconds;
            ServerRestock();
        }

        /// <summary>
        /// One of each depleted line comes back. Slow on purpose: a shelf that refills instantly is a
        /// shelf with no stock at all, and the point of a limited line is that the group has to decide
        /// who gets the hatchet.
        /// </summary>
        [Server]
        public void ServerRestock()
        {
            int restocked = 0;

            for (int i = 0; i < _remaining.Count && i < _shop.Count; i++)
            {
                ShopDef.Offer offer = _shop.Offers[i];
                if (offer.Unlimited || _remaining[i] >= offer.Stock) continue;

                _remaining[i] = _remaining[i] + 1;
                restocked++;
            }

            if (restocked > 0)
                Debug.Log($"[ShopCounter] {name} restocked {restocked} line(s).");
        }

        // ---------------------------------------------------------------- buying

        /// <summary>
        /// Sells <paramref name="count"/> of an offer to a player. Returns how many they actually got.
        ///
        /// Everything is clamped rather than refused: asking for five when there are two, or when you
        /// can afford three, buys what is possible instead of nothing. A shop that refuses the whole
        /// order because one of the numbers was optimistic is a shop nobody uses twice.
        /// </summary>
        [Server]
        public int ServerBuy(Inventory bag, Wallet wallet, int offer, int count, out string why)
        {
            why = null;

            if (_shop == null || bag == null || wallet == null)
            {
                why = "the shop is not open";
                return 0;
            }

            if (offer < 0 || offer >= _shop.Count || offer >= _remaining.Count)
            {
                why = "no such offer";
                return 0;
            }

            ShopDef.Offer line = _shop.Offers[offer];
            if (!line.IsValid)
            {
                why = "that offer is broken";
                return 0;
            }

            if (!InReach(bag.transform.position))
            {
                why = "you are not at the counter";
                return 0;
            }

            int stock = _remaining[offer];
            int want = Mathf.Max(0, count);

            if (!line.Unlimited) want = Mathf.Min(want, stock);
            if (want <= 0)
            {
                why = "sold out";
                return 0;
            }

            want = Mathf.Min(want, wallet.Balance / line.Price);
            if (want <= 0)
            {
                why = "you cannot afford it";
                return 0;
            }

            // Off the shelf first. Two players arriving in the same tick both get here, and the second
            // one reads what the first one left.
            if (!line.Unlimited) _remaining[offer] = stock - want;

            if (!wallet.ServerTrySpend(want * line.Price, $"bought {want}x {line.Item.Id}"))
            {
                if (!line.Unlimited) _remaining[offer] = stock;
                why = "you cannot afford it";
                return 0;
            }

            int left = bag.Add(line.Item, want);

            // Whatever did not fit goes back on the shelf and back in the wallet, exactly. A refund,
            // not income - see Wallet.ServerRefund.
            if (left > 0)
            {
                if (!line.Unlimited) _remaining[offer] = _remaining[offer] + left;
                wallet.ServerRefund(left * line.Price, "did not fit");
            }

            int sold = want - left;
            if (sold <= 0)
            {
                why = "your bag is full";
                return 0;
            }

            Debug.Log($"[ShopCounter] {bag.name} bought {sold}x {line.Item.Id} for "
                      + $"{sold * line.Price}"
                      + (line.Unlimited ? "." : $"; {_remaining[offer]} left on the shelf."));

            return sold;
        }

        // ---------------------------------------------------------------- selling

        /// <summary>
        /// Buys a stack out of a player's bag. Returns what they were paid.
        ///
        /// Named by slot, like every other transfer in this game, so the shop reads what is actually
        /// there rather than what the client says is there.
        /// </summary>
        [Server]
        public int ServerSell(Inventory bag, Wallet wallet, int slot, int count, out string why)
        {
            why = null;

            if (_shop == null || bag == null || wallet == null)
            {
                why = "the shop is not open";
                return 0;
            }

            if (!InReach(bag.transform.position))
            {
                why = "you are not at the counter";
                return 0;
            }

            ItemStack stack = bag[slot];
            if (stack.IsEmpty)
            {
                why = "there is nothing in that slot";
                return 0;
            }

            ItemDef def = stack.Def;
            int each = _shop.PriceFor(def);

            if (each <= 0)
            {
                why = $"the trader will not take {(def != null ? def.Id : "that")}";
                return 0;
            }

            ItemStack taken = bag.TakeSlot(slot, Mathf.Max(1, count));
            if (taken.IsEmpty)
            {
                why = "there is nothing in that slot";
                return 0;
            }

            int paid = each * taken.Count;
            wallet.ServerAdd(paid, $"sold {taken.Count}x {def.Id}");

            Debug.Log($"[ShopCounter] {bag.name} sold {taken.Count}x {def.Id} for {paid}.");

            return paid;
        }

        // ---------------------------------------------------------------- where it is

        public bool InReach(Vector3 position)
            => (transform.position - position).sqrMagnitude <= Reach * Reach;

        /// <summary>The counter you are standing at, or null. What the UI opens.</summary>
        public static ShopCounter NearestInReach(Vector3 position)
        {
            ShopCounter best = null;
            float bestDistance = float.MaxValue;

            foreach (ShopCounter counter in FindObjectsByType<ShopCounter>(FindObjectsSortMode.None))
            {
                if (counter == null || !counter.IsSpawned) continue;

                float distance = (counter.transform.position - position).sqrMagnitude;
                if (distance > Reach * Reach || distance >= bestDistance) continue;

                best = counter;
                bestDistance = distance;
            }

            return best;
        }

        // ---------------------------------------------------------------- the key

        public bool ServerCanInteract(NetworkObject actor)
            => actor != null && _shop != null && actor.GetComponent<Inventory>() != null;

        /// <summary>
        /// Interact opens the screen, and the screen is client-side - so the server has nothing to do
        /// here. It stays an <see cref="IInteractable"/> anyway so the crosshair says what the counter
        /// is for, which is the difference between a trader and a table.
        /// </summary>
        public void ServerInteract(NetworkObject actor)
        {
        }

        /// <summary>One line for the log.</summary>
        public string Describe()
        {
            if (_shop == null) return "no stock";

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < _shop.Count; i++)
            {
                if (i > 0) text.Append(", ");

                ShopDef.Offer offer = _shop.Offers[i];
                text.Append(offer.Item != null ? offer.Item.Id : "?").Append('@').Append(offer.Price);

                if (!offer.Unlimited) text.Append(" x").Append(Remaining(i));
            }

            return text.ToString();
        }

        /// <summary>Bake time only.</summary>
        public void Configure(ShopDef shop) => _shop = shop;
    }
}
