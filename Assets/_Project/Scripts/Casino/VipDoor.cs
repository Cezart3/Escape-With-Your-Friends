using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Player;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>
    /// The door between the casino floor and the VIP room. Its front (+z) faces the floor.
    ///
    /// **It is a press, not a doorway.** A physical door that opened for one player would let in
    /// whoever stood behind them, so the door never opens: pressing it from the floor with at least
    /// <see cref="Minimum"/> chips teleports you through, and pressing it from inside always lets you
    /// out. A player below the minimum still gets a prompt. That prompt, and the games visible through
    /// the glass beside the door, are the reason to grind the floor's small tables first.
    ///
    /// Holding the chips is checked only at the door. Losing them inside does not throw anybody out.
    /// </summary>
    public class VipDoor : NetworkBehaviour, IInteractable
    {
        /// <summary>The minimum on day 1, in chips. It grows with the casino's stakes (<see cref="CasinoDays.Scaled"/>).</summary>
        public const int BaseMinimum = 5000;

        /// <summary>How far past the door a player lands, either way.</summary>
        const float Step = 1.4f;

        public static int Minimum => CasinoDays.AllOpen ? 0 : CasinoDays.Scaled(BaseMinimum);

        /// <summary>True for a point on the VIP side.</summary>
        public bool Inside(Vector3 point) => Vector3.Dot(point - transform.position, transform.forward) < 0f;

        NetworkObject Local => ClientManager != null && ClientManager.Connection != null
            ? ClientManager.Connection.FirstObject
            : null;

        // #252: the neon arch round the door is lit while this player holds enough to go in, dark
        // while they do not. Checked twice a second; a property block, so the material stays shared.
        Renderer _neon;
        MaterialPropertyBlock _block;
        float _next;
        bool? _lit;

        void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 0.5f;

            if (_neon == null)
            {
                Transform t = transform.Find("Model.Neon");
                if (t == null) { enabled = false; return; }
                _neon = t.GetComponent<Renderer>();
                _block = new MaterialPropertyBlock();
            }

            NetworkObject local = Local;
            Wallet wallet = local != null ? local.GetComponent<Wallet>() : null;
            bool lit = wallet != null && wallet.Chips >= Minimum;
            if (lit == _lit) return;

            _lit = lit;
            if (lit) _neon.SetPropertyBlock(null);
            else
            {
                _block.SetColor("_EmissionColor", Color.black);
                _neon.SetPropertyBlock(_block);
            }
        }

        public string Prompt
        {
            get
            {
                NetworkObject local = Local;
                Wallet wallet = local != null ? local.GetComponent<Wallet>() : null;
                if (wallet == null) return string.Empty;

                if (Inside(local.transform.position)) return "Leave the VIP room";
                return wallet.Chips >= Minimum
                    ? "Enter the VIP room"
                    : $"VIP: hold {Minimum:N0} chips to enter (you have {wallet.Chips:N0})";
            }
        }

        public bool ServerCanInteract(NetworkObject actor)
        {
            if (actor == null || actor.GetComponent<PlayerMotor>() == null) return false;
            if (Inside(actor.transform.position)) return true;

            Wallet wallet = actor.GetComponent<Wallet>();
            return wallet != null && wallet.Chips >= Minimum;
        }

        public void ServerInteract(NetworkObject actor)
        {
            if (!IsServerStarted || !ServerCanInteract(actor)) return;

            Vector3 way = Inside(actor.transform.position) ? transform.forward : -transform.forward;
            actor.GetComponent<PlayerMotor>().ServerTeleport(
                transform.position + way * Step + Vector3.up * 0.1f, Quaternion.LookRotation(way).eulerAngles.y);
        }
    }
}
