using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>What a button in front of a blackjack seat does.</summary>
    public enum BlackjackPress
    {
        /// <summary>Sits down, or adds a chunk to the bet, while the table takes bets.</summary>
        Bet,

        Hit,
        Stand,
        Double,
        Split,
    }

    /// <summary>
    /// One button in front of one seat of a <see cref="BlackjackTable"/>. Its own nested
    /// <see cref="NetworkObject"/> for <see cref="BetSpot"/>'s reason. Every seat has all five, and a
    /// button only answers the player sitting there, on their turn.
    /// </summary>
    public class BlackjackButton : NetworkBehaviour, IInteractable
    {
        [SerializeField] int _seat;
        [SerializeField] BlackjackPress _press;

        BlackjackTable _table;

        public int Seat => _seat;
        public BlackjackPress Press => _press;

        public BlackjackTable Table => _table != null ? _table : _table = GetComponentInParent<BlackjackTable>();

        static BlackjackAction Action(BlackjackPress press) => press switch
        {
            BlackjackPress.Hit => BlackjackAction.Hit,
            BlackjackPress.Double => BlackjackAction.Double,
            BlackjackPress.Split => BlackjackAction.Split,
            _ => BlackjackAction.Stand,
        };

        public string Prompt
        {
            get
            {
                NetworkObject local = ClientManager != null && ClientManager.Connection != null
                    ? ClientManager.Connection.FirstObject
                    : null;
                Wallet wallet = local != null ? local.GetComponent<Wallet>() : null;
                if (!CasinoDays.IsOpen(CasinoGame.Blackjack))
                    return _press == BlackjackPress.Bet ? CasinoDays.Closed("Blackjack", CasinoGame.Blackjack) : string.Empty;
                if (Table == null || wallet == null || !Allows(local.ObjectId, wallet.Chips)) return string.Empty;

                switch (_press)
                {
                    case BlackjackPress.Bet:
                        int on = Table.BetOn(_seat * 2);
                        return on > 0 ? $"Bet {BlackjackTable.Chunk} more ({on} on seat {_seat + 1})"
                                      : $"Sit at seat {_seat + 1} and bet {BlackjackTable.Chunk}";
                    case BlackjackPress.Double:
                        return $"Double for {Table.BetOn(Table.Turn)} more";
                    case BlackjackPress.Split:
                        return $"Split for {Table.BetOn(Table.Turn)} more";
                    default:
                        return _press.ToString();
                }
            }
        }

        bool Allows(int objectId, int chips)
            => _press == BlackjackPress.Bet
                ? Table.CanBet(objectId, _seat, chips)
                : Table.CanAct(objectId, _seat, Action(_press), chips);

        public bool ServerCanInteract(NetworkObject actor)
        {
            Wallet wallet = actor != null ? actor.GetComponent<Wallet>() : null;
            return Table != null && wallet != null && CasinoDays.IsOpen(CasinoGame.Blackjack)
                   && Allows(actor.ObjectId, wallet.Chips);
        }

        public void ServerInteract(NetworkObject actor)
        {
            if (!IsServerStarted || Table == null) return;

            if (_press == BlackjackPress.Bet) Table.ServerBet(actor, _seat);
            else Table.ServerAct(actor, _seat, Action(_press));
        }

        /// <summary>Editor-time setup. See <c>BlackjackFactory</c>.</summary>
        public void Configure(int seat, BlackjackPress press)
        {
            _seat = seat;
            _press = press;
        }
    }
}
