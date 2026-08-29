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

        void Awake() => _balance.OnChange += OnBalanceChanged;

        void OnDestroy() => _balance.OnChange -= OnBalanceChanged;

        public override void OnStartServer()
        {
            base.OnStartServer();

            // -startingMoney 0 is how the headless test proves the machine refuses a broke player.
            _balance.Value = CommandLine.GetInt("-startingMoney", _startingBalance);
        }

        /// <summary>
        /// Server only. Takes <paramref name="amount"/> if it is there. Returns false and changes
        /// nothing if it is not, so a caller can use this as both the check and the charge and never
        /// end up having half-charged someone.
        /// </summary>
        public bool ServerTrySpend(int amount)
        {
            if (!IsServerStarted) return false;
            if (amount < 0) return false;
            if (_balance.Value < amount) return false;

            _balance.Value -= amount;
            return true;
        }

        /// <summary>Server only. Income: loot sold, a fish landed, a lucky spin.</summary>
        public void ServerAdd(int amount)
        {
            if (!IsServerStarted || amount <= 0) return;
            _balance.Value += amount;
        }

        /// <summary>Server only. Sets the balance outright. For save loading and for tests.</summary>
        public void ServerSetBalance(int amount)
        {
            if (!IsServerStarted) return;
            _balance.Value = Mathf.Max(0, amount);
        }

        void OnBalanceChanged(int previous, int next, bool asServer)
        {
            // Fires on the host as well; see Health.OnHealthChanged for why there is no guard.
            Changed?.Invoke(previous, next);
        }
    }
}
