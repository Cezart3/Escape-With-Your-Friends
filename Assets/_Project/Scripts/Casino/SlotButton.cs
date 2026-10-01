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

        /// <summary>Buys the free spins outright, Volcano and Reef only.</summary>
        Buy,

        /// <summary>Turns the ante on or off, Volcano only.</summary>
        Ante,

        /// <summary>Starts, raises or stops the autoplay, Lagoon only.</summary>
        Auto,
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
                if (Machine == null) return string.Empty;

                NetworkObject me = ClientManager != null && ClientManager.Connection != null
                    ? ClientManager.Connection.FirstObject
                    : null;
                bool mine = me != null && me.ObjectId == Machine.AutoOwnerId;

                // An autoplay holds the cabinet: its owner sees how to change it, everybody else whose it is.
                if (Machine.AutoLeft > 0 && (_action == SlotAction.Auto || _action == SlotAction.Spin))
                {
                    if (!mine) return $"{Machine.AutoOwnerName} is autoplaying: {Machine.AutoLeft} spins left";
                    if (_action == SlotAction.Spin) return string.Empty;
                    return Machine.NextAuto > 0
                        ? $"Autoplay to {Machine.NextAuto} spins ({Machine.AutoLeft} left)"
                        : $"Stop autoplay ({Machine.AutoLeft} left)";
                }

                if (Machine.Busy) return string.Empty;
                if (!Machine.Open)
                    return _action == SlotAction.Spin ? CasinoDays.Closed(Machine.Title, CasinoDays.Of(Machine.Kind)) : string.Empty;

                NetworkObject local = ClientManager != null && ClientManager.Connection != null
                    ? ClientManager.Connection.FirstObject
                    : null;
                Wallet wallet = local != null ? local.GetComponent<Wallet>() : null;
                if (wallet == null) return string.Empty;

                switch (_action)
                {
                    case SlotAction.Spin:
                        return wallet.Chips >= Machine.Stake ? $"Spin {Machine.Title} for {Machine.Stake} chips" : string.Empty;
                    case SlotAction.Bet:
                        return $"Bet {Machine.NextBet} (now {Machine.Bet})";
                    case SlotAction.Buy:
                        return Machine.BuyCost > 0 && wallet.Chips >= Machine.BuyCost
                            ? $"Buy free spins for {Machine.BuyCost} chips"
                            : string.Empty;
                    case SlotAction.Auto:
                        return wallet.Chips >= Machine.Stake
                            ? $"Autoplay {SlotMachine.AutoSpins[0]} spins at {Machine.Stake} chips each"
                            : string.Empty;
                    case SlotAction.Ante:
                        return Machine.Ante
                            ? $"Ante off (spins back to {Machine.Bet})"
                            : $"Ante on: spins cost {SlotMath.AnteStake(Machine.Bet)}, free spins twice as likely, no buying";
                    default:
                        if (Machine.Gamble <= 0 || local.ObjectId != Machine.GamblerId) return string.Empty;
                        return $"Double {Machine.Gamble} on {(_action == SlotAction.Red ? "red" : "black")}";
                }
            }
        }

        public bool ServerCanInteract(NetworkObject actor)
        {
            if (Machine == null || actor == null || !Machine.Open) return false;

            if (_action == SlotAction.Auto && SlotMath.HasAutoplay(Machine.Kind))
            {
                if (Machine.AutoLeft > 0) return actor.ObjectId == Machine.AutoOwnerId;
                Wallet player = actor.GetComponent<Wallet>();
                return !Machine.Busy && player != null && player.Chips >= Machine.Stake;
            }

            if (Machine.Busy) return false;

            switch (_action)
            {
                case SlotAction.Spin:
                    Wallet wallet = actor.GetComponent<Wallet>();
                    return wallet != null && wallet.Chips >= Machine.Stake;
                case SlotAction.Bet:
                    return true;
                case SlotAction.Ante:
                    return SlotMath.HasAnte(Machine.Kind);
                case SlotAction.Buy:
                    Wallet buyer = actor.GetComponent<Wallet>();
                    return Machine.BuyCost > 0 && buyer != null && buyer.Chips >= Machine.BuyCost;
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
                case SlotAction.Buy: Machine.ServerSpin(actor, buy: true); break;
                case SlotAction.Ante: Machine.ServerToggleAnte(); break;
                case SlotAction.Auto: Machine.ServerAutoplay(actor); break;
            }
        }

        /// <summary>Editor-time setup. See <c>SlotFactory</c>.</summary>
        public void Configure(SlotAction action) => _action = action;
    }
}
