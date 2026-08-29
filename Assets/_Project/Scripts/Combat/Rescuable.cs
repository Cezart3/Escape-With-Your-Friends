using EscapeWithYourFriends.Core;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// The downed half of the rescue. Makes a player on the floor something a teammate can walk up
    /// to and hold Interact on, and publishes how far along that hold is so the HUD (#106) can draw
    /// it over the right body.
    ///
    /// This is the *thing*; <see cref="RescueSystem"/> is the *doer*, the same split as
    /// <see cref="Carryable"/> and <see cref="CarrySystem"/>. The reason it is worth splitting is
    /// that both bodies need to see the progress bar and only one of them is the rescuer: putting
    /// the replicated progress on the victim means the HUD reads it off the marker it is already
    /// drawing, instead of hunting for whoever happens to be kneeling nearby.
    ///
    /// **Only downed, never dead.** A corpse is the Revive Machine's problem and costs money (#25).
    /// The whole point of the downed state is that there is a cheap way out of it if your friends
    /// are quick, and an expensive one if they are not.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class Rescuable : NetworkBehaviour, IInteractable
    {
        [SerializeField] Health _health;

        /// <summary>
        /// Who is currently helping this body up, or null. Replicated so a third player can see that
        /// someone already has it covered and go do something more useful.
        /// </summary>
        readonly SyncVar<NetworkObject> _rescuer = new();

        /// <summary>
        /// Hold progress, 0 to 1. Sent at 10Hz rather than every tick — this drives a bar that is
        /// full in three and a half seconds, and nobody can see the difference between ten and
        /// thirty updates a second on that.
        /// </summary>
        readonly SyncVar<float> _progress = new(new SyncTypeSettings(0.1f));

        public NetworkObject Rescuer => _rescuer.Value;
        public bool IsBeingRescued => _rescuer.Value != null;
        public float Progress => _progress.Value;
        public Health Health => _health;

        /// <summary>
        /// Empty unless this body is actually down. <see cref="PlayerInteractor"/> treats an empty
        /// prompt as "not a target right now" and falls through to carrying, which is what keeps a
        /// corpse pickup-able with the same key that helps a downed friend up.
        ///
        /// This does not contradict the interface's rule that a prompt is not a permission check.
        /// The distinction is what the client can answer for free: life state is a SyncVar sitting
        /// in memory, so "is this even a rescue target" costs nothing and is never stale in a way
        /// that matters. "Can this particular actor afford it" would need the server, and still does.
        /// </summary>
        public string Prompt
        {
            get
            {
                if (_health == null || !_health.IsDowned) return string.Empty;
                return IsBeingRescued ? "Being helped up" : "Hold to help up";
            }
        }

        void Awake()
        {
            if (_health == null) _health = GetComponent<Health>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            if (_health != null) _health.ServerStateChanged += OnServerStateChanged;
        }

        public override void OnStopServer()
        {
            if (_health != null) _health.ServerStateChanged -= OnServerStateChanged;
            base.OnStopServer();
        }

        /// <summary>Server only. True if <paramref name="actor"/> could start helping right now.</summary>
        public bool ServerCanInteract(NetworkObject actor)
        {
            if (!IsServerStarted || actor == null || actor == NetworkObject) return false;
            if (_health == null || !_health.IsDowned) return false;

            // First hold wins. Two people crouching over the same body is not more efficient, and
            // letting the second one restart the timer would make it slower.
            if (IsBeingRescued && _rescuer.Value != actor) return false;

            var system = actor.GetComponent<RescueSystem>();
            return system != null && system.ServerCanRescue();
        }

        /// <summary>
        /// Server only. Starts the hold on the actor's own <see cref="RescueSystem"/>. The timer
        /// does not live here: it belongs to the player doing the work, because that is whose
        /// health, stun and distance decide whether it keeps running.
        /// </summary>
        public void ServerInteract(NetworkObject actor)
        {
            if (!ServerCanInteract(actor)) return;
            actor.GetComponent<RescueSystem>().ServerBegin(this);
        }

        /// <summary>Server only. Called by the rescuer's system every frame it is still holding.</summary>
        public void ServerSetRescue(NetworkObject rescuer, float progress)
        {
            if (!IsServerStarted) return;

            _rescuer.Value = rescuer;
            _progress.Value = rescuer != null ? Mathf.Clamp01(progress) : 0f;
        }

        void OnServerStateChanged(LifeState previous, LifeState next)
        {
            // Leaving Downed ends the rescue whichever way it went — helped up, bled out, or killed
            // outright by someone who did not care. The rescuer's system polls this every frame, but
            // clearing it here as well means the replicated bar never survives the body it belonged to.
            if (next == LifeState.Downed) return;

            if (_rescuer.Value != null)
            {
                var system = _rescuer.Value.GetComponent<RescueSystem>();
                if (system != null) system.ServerCancel();
            }

            ServerSetRescue(null, 0f);
        }
    }
}
