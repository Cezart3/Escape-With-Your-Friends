using System.Collections.Generic;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using UnityEngine;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// What the HUD knows about the squad, as data, with no reference to a Canvas anywhere in it.
    ///
    /// The split is not architecture for its own sake. A headless run has no screen, no font and no
    /// graphics device, so a HUD written as one lump of widget code is a HUD that cannot be tested
    /// from a terminal — the only thing an automated run could check is that it did not throw. With
    /// the model separated, <c>-hudTest</c> prints exactly the rows a player would read, on a client
    /// that is not the server, which is where the interesting claim lives: the bleed-out countdown
    /// has to agree on every machine.
    ///
    /// Everything here is read from replicated state that is already on this peer. There is no RPC
    /// behind the HUD and there must never be one.
    /// </summary>
    public static class SquadModel
    {
        /// <summary>
        /// One player's line. A struct rather than a class because this is rebuilt every frame the
        /// HUD draws, and four of these per frame should not be four allocations.
        /// </summary>
        public readonly struct Entry
        {
            public readonly int OwnerId;
            public readonly string Name;
            public readonly Color Color;

            public readonly LifeState State;

            /// <summary>Seconds left before this player dies. Zero unless downed.</summary>
            public readonly float BleedOut;

            /// <summary>1 when just downed, 0 at death. For a radial or a bar.</summary>
            public readonly float BleedOutNormalized;

            /// <summary>Being carried by anything — a friend hauling you, or a native (#107).</summary>
            public readonly bool IsCarried;

            public readonly bool IsBeingRescued;

            /// <summary>Rescue hold progress, 0 to 1. Meaningless unless <see cref="IsBeingRescued"/>.</summary>
            public readonly float RescueProgress;

            public readonly bool IsLocal;

            /// <summary>
            /// Where to draw a world marker. The hip bone, not the body root: once the ragdoll takes
            /// over, the root stops moving and the corpse slides away from it — a marker on the root
            /// would hang in the air where the player went down rather than over where they are.
            /// </summary>
            public readonly Transform Anchor;

            /// <summary>Metres from the local player, or 0 when there is no local body yet.</summary>
            public readonly float Distance;

            public Entry(int ownerId, string name, Color color, LifeState state,
                         float bleedOut, float bleedOutNormalized,
                         bool isCarried, bool isBeingRescued, float rescueProgress,
                         bool isLocal, Transform anchor, float distance)
            {
                OwnerId = ownerId;
                Name = name;
                Color = color;
                State = state;
                BleedOut = bleedOut;
                BleedOutNormalized = bleedOutNormalized;
                IsCarried = isCarried;
                IsBeingRescued = isBeingRescued;
                RescueProgress = rescueProgress;
                IsLocal = isLocal;
                Anchor = anchor;
                Distance = distance;
            }

            /// <summary>True if this row is one the marker layer should draw over the world.</summary>
            public bool NeedsMarker => !IsLocal && State != LifeState.Alive && Anchor != null;
        }

        /// <summary>
        /// Fills <paramref name="into"/> from <see cref="NetworkPlayerRegistry"/>. Cleared first, so
        /// the caller can keep one list alive for the lifetime of the HUD and never allocate again.
        ///
        /// Order is registration order, which is stable for a session — a squad list whose rows swap
        /// places when somebody goes down is a squad list nobody can read in a hurry.
        /// </summary>
        public static void Collect(List<Entry> into)
        {
            into.Clear();

            Transform localAnchor = FindLocalAnchor();

            foreach (NetworkPlayerRegistry.PlayerBody body in NetworkPlayerRegistry.Players)
            {
                if (!body.IsValid) continue;

                var health = body.Object.GetComponent<Health>();
                if (health == null) continue;

                var ragdoll = body.Object.GetComponent<RagdollController>();
                Transform anchor = ragdoll != null && ragdoll.HipBone != null
                    ? ragdoll.HipBone
                    : body.Object.transform;

                var carryable = body.Object.GetComponent<Carryable>();
                var rescuable = body.Object.GetComponent<Rescuable>();

                bool isLocal = body.Object.IsOwner;

                float distance = localAnchor != null && !isLocal
                    ? Vector3.Distance(localAnchor.position, anchor.position)
                    : 0f;

                into.Add(new Entry(
                    body.OwnerId,
                    body.Identity.DisplayName,
                    body.Identity.Color,
                    health.State,
                    health.BleedOutRemaining,
                    health.BleedOutNormalized,
                    carryable != null && carryable.IsCarried,
                    rescuable != null && rescuable.IsBeingRescued,
                    rescuable != null ? rescuable.Progress : 0f,
                    isLocal,
                    anchor,
                    distance));
            }
        }

        /// <summary>
        /// The local player's body, or null before one has spawned. Found through ownership rather
        /// than cached on spawn, because which body is "yours" is not fixed for the whole session —
        /// reconnect adoption (#111) hands it to a different object.
        /// </summary>
        public static Transform FindLocalAnchor()
        {
            foreach (NetworkPlayerRegistry.PlayerBody body in NetworkPlayerRegistry.Players)
            {
                if (!body.IsValid || !body.Object.IsOwner) continue;

                var ragdoll = body.Object.GetComponent<RagdollController>();
                return ragdoll != null && ragdoll.HipBone != null
                    ? ragdoll.HipBone
                    : body.Object.transform;
            }

            return null;
        }

        /// <summary>
        /// The short status a row shows: what happened, and how long you have to care about it.
        /// Shared by the squad list and the world marker so the two can never disagree, which they
        /// would within a week if each formatted its own string.
        /// </summary>
        public static string StatusText(in Entry entry)
        {
            switch (entry.State)
            {
                case LifeState.Downed when entry.IsBeingRescued:
                    return $"HELPED {entry.RescueProgress * 100f:0}%";

                case LifeState.Downed when entry.IsCarried:
                    return $"HAULED {Clock(entry.BleedOut)}";

                case LifeState.Downed:
                    return $"DOWN {Clock(entry.BleedOut)}";

                case LifeState.Dead when entry.IsCarried:
                    return "DEAD - carried";

                case LifeState.Dead:
                    return "DEAD";

                default:
                    return "OK";
            }
        }

        /// <summary>
        /// The colour a row is drawn in. Deliberately not the player's own colour: the player colour
        /// identifies *who*, this says *what happened*, and a red player being dead has to look
        /// different from a red player being fine.
        /// </summary>
        public static Color StatusColor(in Entry entry)
        {
            switch (entry.State)
            {
                case LifeState.Downed when entry.IsBeingRescued:
                    return new Color(0.40f, 0.85f, 0.45f); // green: someone is on it

                case LifeState.Downed when entry.IsCarried:
                    return new Color(0.75f, 0.45f, 0.95f); // purple: being taken somewhere

                case LifeState.Downed:
                    // Amber that runs to red as the timer empties. The colour is the timer for
                    // anyone glancing rather than reading.
                    return Color.Lerp(new Color(0.95f, 0.25f, 0.20f),
                                      new Color(0.98f, 0.72f, 0.20f),
                                      entry.BleedOutNormalized);

                case LifeState.Dead:
                    return new Color(0.55f, 0.55f, 0.58f);

                default:
                    return new Color(0.88f, 0.90f, 0.92f);
            }
        }

        /// <summary>m:ss, floored. A countdown that rounds up would show 0:00 for a whole second.</summary>
        public static string Clock(float seconds)
        {
            if (seconds <= 0f) return "0:00";

            int total = Mathf.FloorToInt(seconds);
            return $"{total / 60}:{total % 60:00}";
        }

        /// <summary>One row as a line of text. The headless test reads these; so does a bug report.</summary>
        public static string Describe(in Entry entry)
        {
            string tag = entry.IsLocal ? "you" : $"{entry.Distance:0}m";
            return $"owner {entry.OwnerId} {entry.Name} [{tag}] {StatusText(entry)}";
        }
    }
}
