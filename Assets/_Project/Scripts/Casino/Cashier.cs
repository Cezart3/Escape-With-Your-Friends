using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>Which way a cage window converts. One window, one direction, no ambiguity.</summary>
    public enum CageDirection
    {
        /// <summary>Money in, chips out.</summary>
        Buy,

        /// <summary>Chips in, money out.</summary>
        CashOut,
    }

    /// <summary>
    /// A window in the casino cage, from #63. Press the interact key and your money becomes chips,
    /// or your chips become money, one for one.
    ///
    /// **There are two windows rather than one with a modifier.** A single booth that has to mean
    /// two opposite things from one key needs either a rule about which - "it cashes you out unless
    /// you are broke" - or a second key nobody would find. Two booths cost one extra prefab and one
    /// extra line in the POI list, and the player picks by aiming, which is a thing they already
    /// know how to do. It also sidesteps the trap #62 ran into: the server resolves an interaction
    /// with <c>GetComponentInChildren&lt;IInteractable&gt;()</c>, so two interactables on one
    /// <see cref="NetworkObject"/> would quietly be one.
    ///
    /// The exchange itself is not here. Both directions are one call into <see cref="Wallet"/>,
    /// which is where #63's acceptance lives: the two doors between money and chips are methods on
    /// the wallet and there is no third one. This class decides *when* a door opens, never what
    /// comes through it.
    ///
    /// The window trades in fixed chunks so it needs no screen. #65 brings the casino UI, and when
    /// it does it calls the same two wallet methods with a number the player typed.
    /// </summary>
    public class Cashier : NetworkBehaviour, IInteractable
    {
        [SerializeField] CageDirection _direction = CageDirection.Buy;

        [Tooltip("Money per press, or chips per press the other way. A stack big enough to bet with "
                 + "and small enough that a bad night is several decisions rather than one.")]
        [Min(1)]
        [SerializeField] int _chunk = 100;

        public CageDirection Direction => _direction;
        public int Chunk => _chunk;

        /// <summary>
        /// What the crosshair says, read off the local player's own wallet. An empty prompt means
        /// the window has nothing to offer - the interactor skips those, so a broke player walks
        /// past the buy window instead of pressing a key that would do nothing.
        /// </summary>
        public string Prompt
        {
            get
            {
                Wallet mine = LocalWallet();
                if (mine == null) return string.Empty;

                int moving = Moving(mine);
                if (moving <= 0) return string.Empty;

                return _direction == CageDirection.Buy
                    ? $"Buy {moving} chips"
                    : $"Cash in {moving} chips";
            }
        }

        public bool ServerCanInteract(NetworkObject actor)
        {
            Wallet wallet = actor != null ? actor.GetComponent<Wallet>() : null;
            return wallet != null && Moving(wallet) > 0;
        }

        public void ServerInteract(NetworkObject actor)
        {
            if (!IsServerStarted) return;

            Wallet wallet = actor != null ? actor.GetComponent<Wallet>() : null;
            if (wallet == null) return;

            int moving = Moving(wallet);
            if (moving <= 0) return;

            int moved = _direction == CageDirection.Buy
                ? wallet.ServerBuyChips(moving)
                : wallet.ServerCashOut(moving);

            if (moved <= 0) return;

            Debug.Log($"[Cashier] {actor.name} at the {_direction} window: {moved}. "
                      + $"Now {wallet.Describe()}.");
        }

        /// <summary>
        /// How much this press would move: a whole chunk, or everything that is left if that is
        /// less. The remainder matters - a player with 40 chips left has to be able to get their 40
        /// back, or the cage has quietly eaten them, which is the one thing #63 must never do.
        /// </summary>
        int Moving(Wallet wallet)
        {
            int available = _direction == CageDirection.Buy ? wallet.Balance : wallet.Chips;
            return Mathf.Min(_chunk, available);
        }

        /// <summary>Same shape as the vehicle prompt's; see <c>Items.Storage</c> for the original.</summary>
        Wallet LocalWallet()
        {
            NetworkObject local = ClientManager != null && ClientManager.Connection != null
                ? ClientManager.Connection.FirstObject
                : null;

            return local != null ? local.GetComponent<Wallet>() : null;
        }

        /// <summary>Editor-time setup. See <c>CasinoFactory</c>.</summary>
        public void Configure(CageDirection direction, int chunk)
        {
            _direction = direction;
            _chunk = Mathf.Max(1, chunk);
        }
    }
}
