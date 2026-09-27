using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>What a button on a slot cabinet does.</summary>
    public enum SlotAction
    {
        Spin,

        /// <summary>Next stake up, wrapping round to the smallest.</summary>
        Bet,

        /// <summary>Double-up card, Sevens only.</summary>
        Red,
        Black,
    }

    /// <summary>
    /// One button on a <see cref="SlotMachine"/>. Aim, press the interact key.
    ///
    /// Each button is its own nested <see cref="NetworkObject"/> for the reason every
    /// <see cref="BetSpot"/> is: the client names the object it aimed at, and the server takes the
    /// first interactable under it that will have the actor. One object with four buttons would be
    /// one button.
    /// </summary>
    public class SlotButton : NetworkBehaviour, IInteractable
    {
        [SerializeField] SlotAction _action;

        SlotMachine _machine;

        public SlotAction Action => _action;

        public SlotMachine Machine => _machine != null ? _machine : _machine = GetComponentInParent<SlotMachine>();

        public string Prompt
        {
            get
            {
                if (Machine == null || Machine.Busy) return string.Empty;

                NetworkObject local = ClientManager != null && ClientManager.Connection != null
                    ? ClientManager.Connection.FirstObject
                    : null;
                Wallet wallet = local != null ? local.GetComponent<Wallet>() : null;
                if (wallet == null) return string.Empty;

                switch (_action)
                {
                    case SlotAction.Spin:
                        return wallet.Chips >= Machine.Bet ? $"Spin {Machine.Title} for {Machine.Bet} chips" : string.Empty;
                    case SlotAction.Bet:
                        return $"Bet {Machine.NextBet} (now {Machine.Bet})";
                    default:
                        if (Machine.Gamble <= 0 || local.ObjectId != Machine.GamblerId) return string.Empty;
                        return $"Double {Machine.Gamble} on {(_action == SlotAction.Red ? "red" : "black")}";
                }
            }
        }

        public bool ServerCanInteract(NetworkObject actor)
        {
            if (Machine == null || Machine.Busy || actor == null) return false;

            switch (_action)
            {
                case SlotAction.Spin:
                    Wallet wallet = actor.GetComponent<Wallet>();
                    return wallet != null && wallet.Chips >= Machine.Bet;
                case SlotAction.Bet:
                    return true;
                default:
                    return Machine.CanGamble(actor);
            }
        }

        public void ServerInteract(NetworkObject actor)
        {
            if (!IsServerStarted || Machine == null) return;

            switch (_action)
            {
                case SlotAction.Spin: Machine.ServerSpin(actor); break;
                case SlotAction.Bet: Machine.ServerNextBet(); break;
                case SlotAction.Red: Machine.ServerGamble(actor, red: true); break;
                case SlotAction.Black: Machine.ServerGamble(actor, red: false); break;
            }
        }

        /// <summary>Editor-time setup. See <c>SlotFactory</c>.</summary>
        public void Configure(SlotAction action) => _action = action;
    }
}
