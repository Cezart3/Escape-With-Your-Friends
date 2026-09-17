using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>
    /// One painted square on the roulette baize, from #64. Aim at it, press the key, and a stack of
    /// chips goes on that bet.
    ///
    /// **Ten small objects instead of one screen.** The alternative is a betting UI, which is #65's
    /// job and needs the casino interior to sit in; until then, the lazy version of "multiple bet
    /// types" is the thing the table already is - a board with the bets written on it - and the
    /// player chooses by looking at one. It is the same trick as the two cage windows in #63, for
    /// the same reason: aiming is a verb the player already has.
    ///
    /// Each spot is its own nested <see cref="NetworkObject"/>, which is the part that actually
    /// matters. The client sends the object it aimed at and the server resolves the component with
    /// <c>GetComponentInChildren&lt;IInteractable&gt;()</c>; ten spots parented to one networked
    /// root would all resolve to whichever one happened to be first, and every bet in the game would
    /// land on red.
    /// </summary>
    public class BetSpot : NetworkBehaviour, IInteractable
    {
        [SerializeField] BetKind _kind = BetKind.Red;

        [Tooltip("Which number, for a straight bet. Ignored by every other kind.")]
        [Range(0, 36)]
        [SerializeField] int _number = 7;

        [Tooltip("Chips per press, or everything that is left if that is less.")]
        [Min(1)]
        [SerializeField] int _chunk = 100;

        RouletteWheel _table;

        public BetKind Kind => _kind;
        public int Number => _number;
        public int Chunk => _chunk;

        public RouletteWheel Table => _table != null ? _table : _table = GetComponentInParent<RouletteWheel>();

        public string Label => RouletteWheel.Describe(_kind, _number);

        public string Prompt
        {
            get
            {
                if (Table == null || !Table.TakingBets) return string.Empty;

                Wallet mine = LocalWallet();
                if (mine == null) return string.Empty;

                int stake = Mathf.Min(_chunk, mine.Chips);
                if (stake <= 0) return string.Empty;

                return $"Put {stake} on {Label}";
            }
        }

        public bool ServerCanInteract(NetworkObject actor)
        {
            if (Table == null || !Table.TakingBets) return false;

            Wallet wallet = actor != null ? actor.GetComponent<Wallet>() : null;
            return wallet != null && wallet.Chips > 0;
        }

        public void ServerInteract(NetworkObject actor)
        {
            if (!IsServerStarted || Table == null) return;

            Wallet wallet = actor != null ? actor.GetComponent<Wallet>() : null;
            if (wallet == null) return;

            int stake = Mathf.Min(_chunk, wallet.Chips);
            if (stake <= 0) return;

            Table.ServerPlaceBet(actor, _kind, _number, stake);

            // What the run gambled, counted where it is staked rather than where it is won or
            // lost: the ending asks how much went on the table, not how it went. #74.
            World.RunSummary.ServerStaked(stake);
        }

        Wallet LocalWallet()
        {
            NetworkObject local = ClientManager != null && ClientManager.Connection != null
                ? ClientManager.Connection.FirstObject
                : null;

            return local != null ? local.GetComponent<Wallet>() : null;
        }

        /// <summary>Editor-time setup. See <c>CasinoFactory</c>.</summary>
        public void Configure(BetKind kind, int number, int chunk)
        {
            _kind = kind;
            _number = Mathf.Clamp(number, 0, RouletteWheel.Pockets - 1);
            _chunk = Mathf.Max(1, chunk);
        }
    }
}
