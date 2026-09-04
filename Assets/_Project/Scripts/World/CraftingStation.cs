using EscapeWithYourFriends.Data;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// A bench, a campfire, a water filter: a place that unlocks recipes while you stand near it.
    ///
    /// **Proximity, not interaction.** Walking up to a bench does not press a button - the bench simply
    /// exists, and <c>Crafting</c> asks whether one is within range when a Bench recipe is requested.
    /// That is deliberate: the alternative is an Interact prompt that opens a UI which does not exist
    /// until #46, and a prompt that promises something unimplemented is worse than no prompt. It also
    /// means four players can use the same bench at once without queueing for it, which is the correct
    /// behaviour for a co-op game and would have taken extra work to get out of an interaction model.
    ///
    /// Stations are registered in a static list rather than found by physics: there are single digits
    /// of them in a session, they never move, and a sphere cast per craft attempt to find something
    /// that could be answered by a distance check is work for nothing.
    /// </summary>
    public class CraftingStation : NetworkBehaviour
    {
        static readonly System.Collections.Generic.List<CraftingStation> Live = new();

        [Tooltip("Which recipes this unlocks while you are near it.")]
        [SerializeField] CraftStation _kind = CraftStation.Bench;

        [Tooltip("Metres. Generous, because being told 'too far' while standing at a bench is maddening.")]
        [Min(1f)]
        [SerializeField] float _radius = 4.5f;

        public CraftStation Kind => _kind;
        public float Radius => Mathf.Max(1f, _radius);

        void OnEnable() => Live.Add(this);
        void OnDisable() => Live.Remove(this);

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Evidence for the harness that a structure somebody else built actually arrived here.
            // Costs a flag check on spawn and says nothing in a real session.
            if (IsServerStarted || !Core.CommandLine.HasFlag("-craftTest")) return;

            Debug.Log($"[CraftingStation] client sees a {_kind} at "
                      + $"{transform.position.ToString("F1")}, radius {Radius:F1}m.");
        }

        /// <summary>
        /// Whether a station of this kind is close enough to <paramref name="position"/>. Hand recipes
        /// need no station and are answered true without looking, which is what makes the field set
        /// work anywhere.
        /// </summary>
        public static bool InRange(CraftStation kind, Vector3 position)
        {
            if (kind == CraftStation.Hand) return true;

            for (int i = 0; i < Live.Count; i++)
            {
                CraftingStation station = Live[i];
                if (station == null || station.Kind != kind) continue;

                float radius = station.Radius;
                if ((station.transform.position - position).sqrMagnitude <= radius * radius)
                    return true;
            }

            return false;
        }

        /// <summary>The nearest station of a kind, or null. For the HUD in #46.</summary>
        public static CraftingStation Nearest(CraftStation kind, Vector3 position)
        {
            CraftingStation best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < Live.Count; i++)
            {
                CraftingStation station = Live[i];
                if (station == null || station.Kind != kind) continue;

                float distance = (station.transform.position - position).sqrMagnitude;
                if (distance >= bestDistance) continue;

                best = station;
                bestDistance = distance;
            }

            return best;
        }

        /// <summary>How many stations exist, by kind. For the crafting test's log line.</summary>
        public static int CountOf(CraftStation kind)
        {
            int count = 0;
            for (int i = 0; i < Live.Count; i++)
                if (Live[i] != null && Live[i].Kind == kind) count++;

            return count;
        }

        /// <summary>Bake time only.</summary>
        public void Configure(CraftStation kind, float radius)
        {
            _kind = kind;
            _radius = radius;
        }
    }
}
