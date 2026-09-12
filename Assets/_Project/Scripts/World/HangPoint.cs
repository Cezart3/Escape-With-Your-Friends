using System.Collections.Generic;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// A frame in the native village with a hook on it, and the place #107's haul was always walking
    /// towards. One body at a time, hung upside down by the ankles, in full view of the camp that put
    /// it there.
    ///
    /// **It holds a body the same way everything else does.** This is an <see cref="ICarryHolder"/>
    /// with a socket, exactly like a player's shoulder, a native's shoulder and the Revive Machine's
    /// intake - so there is no fourth attach path, no special case in <see cref="Carryable"/>, and a
    /// body hung here is in the same replicated state as a body on somebody's back. That the socket
    /// is rotated 180 degrees is the entire difference between being carried and being strung up.
    ///
    /// **The timer does not stop here either.** The whole point of #107 was that being carried off
    /// is not a reprieve, and arriving is not one either: a hung player is still Downed, still
    /// bleeding out, and still rescuable by a friend who can reach them. What the village has bought
    /// itself is the walk - the distance between where you went down and where you are now, with a
    /// camp in between.
    ///
    /// **Cutting somebody down does not fix them.** The interaction frees the body, which falls, and
    /// is still downed and still on the clock. It then has to be carried out, by somebody who is now
    /// moving at carry speed through a village that is fully awake. That is the fight: the rescue is
    /// the getaway, not the cut.
    ///
    /// **If the clock runs out while they are up here, they die up here.** The corpse stays on the
    /// hook - nothing releases it - so the rescue becomes a body-recovery run and then a Revive
    /// Machine bill, and the difference between arriving in time and arriving late is a thing you
    /// can see from across the village.
    /// </summary>
    public class HangPoint : NetworkBehaviour, IInteractable, ICarryHolder
    {
        [Tooltip("Where a hung body's hips are parented. Under the beam, upside down.")]
        [SerializeField] Transform _socket;

        [Tooltip("How far a native will look from its delivery point to find a free hook.")]
        [SerializeField] float _claimRadius = 14f;

        /// <summary>
        /// Who is on the hook, or null. A SyncVar rather than an event because a late joiner walking
        /// into the village has to see the body hanging there, and because the prompt is answered
        /// client-side out of this.
        /// </summary>
        readonly SyncVar<NetworkObject> _occupant = new();

        /// <summary>Every hook that currently exists. Server-side; used to find a free one.</summary>
        static readonly List<HangPoint> _live = new();

        Health _health;
        Carryable _body;

        public bool IsOccupied => _occupant.Value != null;
        public NetworkObject Occupant => _occupant.Value;
        public float ClaimRadius => Mathf.Max(0f, _claimRadius);

        /// <summary>Bodies hung here since the server started. Read by the harness, and nothing else.</summary>
        public static int Hangings { get; private set; }

        /// <inheritdoc />
        public Transform CarrySocket => _socket != null ? _socket : transform;

        /// <summary>
        /// Empty while the hook is free, because Interact is a shared key and a component that always
        /// answers swallows every other gesture within reach of it - see <see cref="IInteractable"/>.
        /// Occupancy is a SyncVar, so a client can answer this for nothing.
        /// </summary>
        public string Prompt => IsOccupied ? "Cut down" : string.Empty;

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();
            _occupant.OnChange += OnOccupantChanged;
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();
            _occupant.OnChange -= OnOccupantChanged;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _live.Add(this);
        }

        public override void OnStopServer()
        {
            // A despawning hook must not leave a body welded to an object that is going away. Same
            // reasoning as the Revive Machine's shutdown and the native's despawn.
            if (IsOccupied) ServerCutDown(null, "the hook despawned");

            _live.Remove(this);
            base.OnStopServer();
        }

        // ---------------------------------------------------------------- hanging

        /// <summary>
        /// Server only. The nearest free hook to <paramref name="where"/>, or null. Called by a
        /// native that has walked a body home; a null answer means it puts them down on the ground,
        /// which is what happens in a camp with no prison in it and is deliberately not an error.
        /// </summary>
        public static HangPoint ServerFree(Vector3 where)
        {
            HangPoint best = null;
            float bestDistance = float.MaxValue;

            foreach (HangPoint point in _live)
            {
                if (point == null || !point.IsServerStarted || point.IsOccupied) continue;

                float distance = Vector3.Distance(point.transform.position, where);
                if (distance > point.ClaimRadius || distance >= bestDistance) continue;

                best = point;
                bestDistance = distance;
            }

            return best;
        }

        /// <summary>
        /// Server only. Whether anybody is strung up within <paramref name="radius"/> of
        /// <paramref name="where"/>. This is what a native asks to decide whether its camp is
        /// guarding something - see <see cref="AI.Native.Guarding"/>.
        /// </summary>
        public static bool ServerAnyOccupiedNear(Vector3 where, float radius)
        {
            foreach (HangPoint point in _live)
            {
                if (point == null || !point.IsOccupied) continue;
                if (Vector3.Distance(point.transform.position, where) <= radius) return true;
            }

            return false;
        }

        /// <summary>
        /// Server only. Puts a body on the hook. The body must already be free of whoever brought it
        /// - a native calls its own release first - because <see cref="Carryable.ServerAttach"/>
        /// refuses a body somebody else is holding, and quietly stealing it behind the carrier's back
        /// is how the Revive Machine learned not to do that.
        /// </summary>
        public bool ServerHang(Carryable body)
        {
            if (!IsServerStarted || body == null || IsOccupied) return false;
            if (!body.ServerCanBeCarriedBy(NetworkObject)) return false;

            var health = body.GetComponent<Health>();
            if (health == null) return false;

            body.ServerAttach(NetworkObject);
            if (body.Carrier != NetworkObject) return false;

            _body = body;
            _health = health;
            _health.ServerStateChanged += OnOccupantStateChanged;

            _occupant.Value = body.NetworkObject;
            Hangings++;

            Debug.Log($"[HangPoint] {name} strung up owner {health.OwnerId} with "
                      + $"{health.BleedOutRemaining:F0}s left on the timer.");

            return true;
        }

        /// <summary>
        /// Server only. Lets go. The body falls where it is standing, still in whatever life state it
        /// was in a moment ago - this is not a rescue, it is the end of being luggage.
        /// </summary>
        public bool ServerCutDown(NetworkObject actor, string why)
        {
            if (!IsServerStarted || !IsOccupied) return false;

            Health health = _health;
            Carryable body = _body;

            if (_health != null) _health.ServerStateChanged -= OnOccupantStateChanged;

            _health = null;
            _body = null;
            _occupant.Value = null;

            if (body != null && body.Carrier == NetworkObject) body.ServerDetach();

            string state = health == null ? "gone"
                         : health.IsDead ? "dead"
                         : health.IsDowned ? $"still down, {health.BleedOutRemaining:F0}s left"
                         : "on their feet";

            Debug.Log($"[HangPoint] {name} let go of owner {(health != null ? health.OwnerId : -1)} "
                      + $"({why}); {state}.");

            return true;
        }

        /// <summary>
        /// The clock ran out while they were up here. Nothing is released: the corpse stays on the
        /// hook, which is the worst outcome in the game and the one the haul is worth racing. Somebody
        /// still has to come and cut it down, and then pay for it (#25).
        /// </summary>
        void OnOccupantStateChanged(LifeState previous, LifeState next)
        {
            if (next != LifeState.Dead) return;

            Debug.Log($"[HangPoint] {name}: owner {(_health != null ? _health.OwnerId : -1)} bled out "
                      + "on the hook. The body stays where it is.");
        }

        // ---------------------------------------------------------------- the interaction

        /// <inheritdoc />
        public bool ServerCanInteract(NetworkObject actor)
        {
            if (!IsServerStarted || actor == null) return false;
            if (!IsOccupied || actor == _occupant.Value) return false;

            // A native could reach this too, and letting one cut down the prisoner it just delivered
            // would be a comedy. Only somebody with a Health and a CarrySystem - a player - can.
            return actor.GetComponent<CarrySystem>() != null;
        }

        /// <inheritdoc />
        public void ServerInteract(NetworkObject actor)
        {
            if (!ServerCanInteract(actor)) return;

            ServerCutDown(actor, $"cut down by owner {actor.OwnerId}");
        }

        void OnOccupantChanged(NetworkObject previous, NetworkObject next, bool asServer)
        {
            // Nothing visual yet; the body is parented by Carryable on every peer, which is the only
            // thing a client needs to see. This exists so the prompt updates without polling.
        }

        /// <summary>
        /// Forgets the counter. Headless harnesses only, and deliberately not called <c>Reset</c>:
        /// Unity reserves that name as an editor message on a MonoBehaviour and warns about a static
        /// one every time the component is added.
        /// </summary>
        public static void ResetCounter() => Hangings = 0;
    }
}
