using System.Collections.Generic;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using UnityEngine;

namespace EscapeWithYourFriends.AI
{
    /// <summary>
    /// The one thing on the server listening for somebody hitting the floor, so that the natives can
    /// be told about it the instant it happens.
    ///
    /// The hook is <see cref="Health.ServerStateChanged"/> rather than the replicated
    /// <see cref="Health.StateChanged"/>, and that choice is the whole reason this class exists. The
    /// server-side event fires *before* the SyncVar is published and before the ragdoll launches, so
    /// the claim is made on the same frame as the down, from the authoritative side, with no window
    /// in which a client could have seen the body land and the server not yet decided who is coming
    /// for it. What happens after that is unhurried - the native walks over, and everything it does
    /// there re-checks its own state - but *who claimed it* is settled immediately and once.
    ///
    /// Why a watcher and not a subscription per native: a native would have to hook every player and
    /// re-hook on every join, which is N x M subscriptions that all have to be unwound correctly when
    /// either side despawns. One watcher hooks each player once, and the natives are asked by a
    /// single static sweep. It also means a body that goes down with nobody nearby costs one
    /// dictionary lookup rather than a dozen distance checks.
    ///
    /// **Static state, deliberately self-cleaning.** The registry's events are static and this
    /// subscribes to them once for the life of the process; the per-player handlers are keyed by
    /// <see cref="Health"/> and removed when the body leaves, and leaving a session raises
    /// <see cref="NetworkPlayerRegistry.PlayerRemoved"/> for everybody, so the table empties itself
    /// rather than carrying one session's bodies into the next.
    /// </summary>
    public static class AbductionWatch
    {
        static bool _armed;

        /// <summary>
        /// The handler each watched body was hooked with. <see cref="Health.ServerStateChanged"/>
        /// carries no sender, so the closure that knows which body it belongs to has to be kept in
        /// order to unsubscribe it again.
        /// </summary>
        static readonly Dictionary<Health, System.Action<LifeState, LifeState>> _watching = new();

        /// <summary>Bodies claimed since the server started. Read by the harness, and by nothing else.</summary>
        public static int Abductions { get; private set; }

        /// <summary>
        /// Starts listening. Idempotent, and safe to call from every native that spawns - which is
        /// exactly where it is called from, because an island with nobody on it who takes prisoners
        /// has nothing to listen for.
        /// </summary>
        public static void Arm()
        {
            if (_armed) return;
            _armed = true;

            NetworkPlayerRegistry.PlayerAdded += OnPlayerAdded;
            NetworkPlayerRegistry.PlayerRemoved += OnPlayerRemoved;

            // Whoever was already here. The first native usually spawns long after the players do.
            foreach (NetworkPlayerRegistry.PlayerBody body in NetworkPlayerRegistry.Players)
                Watch(body);
        }

        static void OnPlayerAdded(NetworkPlayerRegistry.PlayerBody body) => Watch(body);

        static void OnPlayerRemoved(NetworkPlayerRegistry.PlayerBody body)
        {
            if (!body.IsValid) return;

            var health = body.Object.GetComponent<Health>();
            if (health == null || !_watching.TryGetValue(health, out var handler)) return;

            health.ServerStateChanged -= handler;
            _watching.Remove(health);
        }

        static void Watch(NetworkPlayerRegistry.PlayerBody body)
        {
            if (!body.IsValid) return;

            var health = body.Object.GetComponent<Health>();
            if (health == null || _watching.ContainsKey(health)) return;

            // Only the server decides anything here, and on a pure client the event never fires at
            // all - but a client peer still runs this code, so the guard states the intent.
            if (!health.IsServerStarted) return;

            var carryable = body.Object.GetComponent<Carryable>();
            if (carryable == null) return;

            void Handler(LifeState previous, LifeState next) => OnStateChanged(carryable, health, next);

            health.ServerStateChanged += Handler;
            _watching[health] = Handler;
        }

        /// <summary>
        /// Downed, not dead. A corpse is the Revive Machine's problem and hauling one to a village
        /// would be a punishment with no way out of it; a downed player is on a timer their friends
        /// can still beat, which is what makes the haul a race rather than a sentence.
        ///
        /// A body that dies *while being carried* is not released here - see
        /// <see cref="Native.TickAbduct"/>. That case is #108's, and the answer there is that the
        /// journey continues and the corpse ends up in the village, which is the worst outcome and
        /// the one worth playing to avoid.
        /// </summary>
        static void OnStateChanged(Carryable body, Health health, LifeState next)
        {
            if (next != LifeState.Downed) return;

            Native claimed = Native.ServerOffer(body, health);
            if (claimed == null) return;

            Abductions++;
        }

        /// <summary>
        /// Forgets everybody. Nothing calls this in a running session; it exists so that a headless
        /// harness which starts and stops servers in one process can put the counter back.
        /// </summary>
        public static void Reset()
        {
            foreach (KeyValuePair<Health, System.Action<LifeState, LifeState>> pair in _watching)
                if (pair.Key != null) pair.Key.ServerStateChanged -= pair.Value;

            _watching.Clear();
            Abductions = 0;
        }
    }
}
