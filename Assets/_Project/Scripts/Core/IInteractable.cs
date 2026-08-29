using FishNet.Object;

namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// Something a player can walk up to, look at, and press Interact on.
    ///
    /// This exists because <see cref="Player.PlayerCombatInput"/> had a note in it saying it would
    /// have to: Interact was hard-wired to "pick up a body", and the Revive Machine (#25) is the
    /// first thing in the game that has to win that key against a corpse lying at your feet. The
    /// shop counter, the roulette table and the boat all arrive with the same problem, and the
    /// alternative to a contract is a growing chain of type checks in the input component.
    ///
    /// **The interface is server-side on purpose.** A client finds the target and asks; the server
    /// decides. Both halves of that are needed — the client because only it knows where the camera
    /// is pointing, the server because it is the only machine whose answer counts. So the two
    /// decision methods take the actor's <see cref="NetworkObject"/> and are only ever called on the
    /// server, and the one client-facing member is a string for the HUD.
    ///
    /// <see cref="Prompt"/> is what the crosshair says. It is deliberately not "can I do this right
    /// now" — that answer lives on the server and asking for it would cost a round trip per frame.
    /// A prompt that lies until you press the key is the correct trade here: the refusal is instant
    /// and free, and the machine says why.
    /// </summary>
    public interface IInteractable
    {
        /// <summary>
        /// What the crosshair shows when this is aimed at. Read on clients.
        ///
        /// **Empty or null means "not offering anything right now", and the interactor skips it.**
        /// That is not the same as a permission check: it covers only what a client can answer for
        /// free out of replicated state, such as a <see cref="Combat.Rescuable"/> on a player who is
        /// standing up. Anything that needs the server — can this actor afford it, is the machine
        /// busy — still belongs in <see cref="ServerCanInteract"/>, and the prompt is still allowed
        /// to lie about it.
        ///
        /// The skip matters because Interact is a shared key. Without it, a component that is always
        /// present on every body would swallow the press even when it has nothing to do, and the
        /// gestures behind it — picking a corpse up, most of all — would become unreachable.
        /// </summary>
        string Prompt { get; }

        /// <summary>
        /// Server only. Whether <paramref name="actor"/> may interact right now. Called before
        /// <see cref="ServerInteract"/>, and separately by anything that wants to grey out a prompt.
        /// </summary>
        bool ServerCanInteract(NetworkObject actor);

        /// <summary>Server only. Do the thing. Re-validates; never trust the caller's check.</summary>
        void ServerInteract(NetworkObject actor);
    }
}
