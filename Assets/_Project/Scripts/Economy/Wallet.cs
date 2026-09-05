using System;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Economy
{
    /// <summary>
    /// What one player can spend. Host-authoritative, like everything that can be cheated at.
    ///
    /// This is the minimum the Revive Machine (#25) needs and nothing more. The full economy (#47)
    /// adds where money comes from — selling fish, loot, casino payouts — and where it goes: the
    /// shop, weapon tiers, vehicle upgrades. All of that is a caller of <see cref="ServerTrySpend"/>
    /// and <see cref="ServerAdd"/>, so it can be built on top of this without touching it.
    ///
    /// There is no client-callable spend, and there never will be. The balance is a SyncVar that
    /// only the server writes; a modified client can lie about its balance on its own screen and the
    /// server will still refuse the purchase. That is the same shape as <see cref="Combat.Health"/>:
    /// clients observe, the host decides.
    ///
    /// The wallet is per-player rather than shared. A shared pot sounds friendlier and is worse:
    /// half the comedy of this game is one player refusing to pay to revive another.
    ///
    /// **#47's acceptance is "a client cannot mint money", and the way that is kept true is
    /// structural rather than by checking.** There is exactly one client-callable money verb in the
    /// game - <see cref="RequestPay"/> - and it cannot create a coin by construction: it takes from
    /// the sender before it gives to the receiver, in one server call, so the total across every
    /// wallet is the same on the line after as it was on the line before. Everything else that moves
    /// money (<see cref="ServerAdd"/>, <see cref="ServerTrySpend"/>, <see cref="ServerSetBalance"/>)
    /// is server-side and is called by something that already decided a sale, a payout or a charge
    /// was earned.
    ///
    /// Every mutation carries a <c>reason</c> string and is counted into <see cref="Minted"/> and
    /// <see cref="Burned"/>. That is not book-keeping for its own sake: with it, "did anything create
    /// money this session" is a question with a number for an answer, which is exactly what the
    /// harness asks and what a live session can be asked later when the casino (#64) is paying out.
    /// </summary>
    public class Wallet : NetworkBehaviour
    {
        [Tooltip("Balance every player starts a run with. Enough for a first death, not a second.")]
        [SerializeField] int _startingBalance = 500;

        readonly SyncVar<int> _balance = new();

        /// <summary>What this player can spend. Replicated; only the server writes it.</summary>
        public int Balance => _balance.Value;

        /// <summary>Raised on every peer when the balance changes. (previous, current)</summary>
        public event Action<int, int> Changed;

        /// <summary>
        /// Money created and destroyed on this server since it started, by reason. A transfer between
        /// two players touches neither, which is the whole point of counting them.
        /// </summary>
        public static int Minted { get; private set; }

        public static int Burned { get; private set; }

        /// <summary>Everything in every wallet on this peer. The number a conservation check watches.</summary>
        public static int TotalInWallets()
        {
            int total = 0;
            foreach (Wallet wallet in FindObjectsByType<Wallet>(FindObjectsSortMode.None))
                if (wallet != null && wallet.IsSpawned) total += wallet.Balance;

            return total;
        }

        /// <summary>Forgets the session counters. For the harness, which wants a clean baseline.</summary>
        public static void ResetLedger()
        {
            Minted = 0;
            Burned = 0;
        }

        void Awake() => _balance.OnChange += OnBalanceChanged;

        void OnDestroy() => _balance.OnChange -= OnBalanceChanged;

        public override void OnStartServer()
        {
            base.OnStartServer();

            // -startingMoney 0 is how the headless test proves the machine refuses a broke player.
            _balance.Value = CommandLine.GetInt("-startingMoney", _startingBalance);

            // Counted, because a starting balance is money created out of nothing and the ledger is
            // only useful if it is honest about that.
            Minted += _balance.Value;
        }

        /// <summary>
        /// Server only. Takes <paramref name="amount"/> if it is there. Returns false and changes
        /// nothing if it is not, so a caller can use this as both the check and the charge and never
        /// end up having half-charged someone.
        /// </summary>
        public bool ServerTrySpend(int amount, string reason = "spent")
        {
            if (!IsServerStarted) return false;
            if (amount < 0) return false;
            if (_balance.Value < amount) return false;

            _balance.Value -= amount;
            Burned += amount;

            if (amount > 0) Debug.Log($"[Wallet] {name} -{amount} ({reason}), now {_balance.Value}.");
            return true;
        }

        /// <summary>Server only. Income: loot sold, a fish landed, a lucky spin.</summary>
        public void ServerAdd(int amount, string reason = "earned")
        {
            if (!IsServerStarted || amount <= 0) return;

            _balance.Value += amount;
            Minted += amount;

            Debug.Log($"[Wallet] {name} +{amount} ({reason}), now {_balance.Value}.");
        }

        /// <summary>
        /// Server only. Money coming back that was spent moments ago - a purchase that did not fit in
        /// the bag, a cancelled trade. Counted against <see cref="Burned"/> rather than into
        /// <see cref="Minted"/>, because a refund is not income and a ledger that called it one would
        /// report the shop as printing money every time somebody's bag was full.
        /// </summary>
        public void ServerRefund(int amount, string reason = "refunded")
        {
            if (!IsServerStarted || amount <= 0) return;

            _balance.Value += amount;
            Burned -= amount;

            Debug.Log($"[Wallet] {name} +{amount} ({reason}), now {_balance.Value}.");
        }

        /// <summary>
        /// Server only. Sets the balance outright, for save loading and for tests. Deliberately not
        /// counted into the ledger: a loaded save is not income, it is the same money again.
        /// </summary>
        public void ServerSetBalance(int amount)
        {
            if (!IsServerStarted) return;
            _balance.Value = Mathf.Max(0, amount);
        }

        // ---------------------------------------------------------------- paying a friend

        /// <summary>
        /// The one money verb a client may ask for: hand some of yours to somebody else.
        ///
        /// It exists because it is a real co-op gesture - chipping in for a revive, paying somebody
        /// back for the boat - and because it is the honest test of the acceptance criterion. A
        /// client names a target and an amount and nothing else; the server reads its own balances.
        /// </summary>
        public void RequestPay(Wallet target, int amount)
        {
            if (!IsOwner || target == null || target == this || amount <= 0) return;

            ServerPay(target.NetworkObject, amount);
        }

        [ServerRpc]
        void ServerPay(NetworkObject targetObject, int amount)
        {
            var target = targetObject != null ? targetObject.GetComponent<Wallet>() : null;
            ServerTransfer(target, amount);
        }

        /// <summary>
        /// Moves money from this wallet to another. Returns how much actually moved.
        ///
        /// Take before give, in one call, with nothing between the two lines - the same rule the
        /// chests use in #44, for the same reason. A give-then-take would create money for a frame,
        /// and a frame is long enough to be a duplication bug.
        /// </summary>
        [Server]
        public int ServerTransfer(Wallet target, int amount)
        {
            if (target == null || target == this || amount <= 0) return 0;
            if (_balance.Value < amount) return 0;

            _balance.Value -= amount;
            target._balance.Value += amount;

            Debug.Log($"[Wallet] {name} paid {target.name} {amount}; "
                      + $"{_balance.Value} left, they now have {target._balance.Value}.");

            return amount;
        }

        /// <summary>One line for the log.</summary>
        public string Describe() => $"{Balance}";

        void OnBalanceChanged(int previous, int next, bool asServer)
        {
            // Fires on the host as well; see Health.OnHealthChanged for why there is no guard.
            Changed?.Invoke(previous, next);
        }
    }
}
