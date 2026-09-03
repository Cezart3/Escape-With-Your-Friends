using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// What a place on the island is for, attached to the place itself.
    ///
    /// The acceptance criterion for #36 is that each landmark has "a clear purpose", and a comment in
    /// a builder script is not that: the purpose has to survive into the running game, where the HUD
    /// can name what you are looking at and the objective system can point at it. So it is a
    /// component with a string, not a convention.
    ///
    /// It is not a NetworkBehaviour. Every field here is baked into the prefab and never changes, so
    /// replicating it would be sending four clients the same constant they already have on disk.
    /// </summary>
    public class Landmark : MonoBehaviour
    {
        [Tooltip("Stable id, matching the POI catalog entry. Code keys off this; players never see it.")]
        public string Id = "landmark";

        [Tooltip("What it is called on screen.")]
        public string DisplayName = "Landmark";

        [Tooltip("Why a player would walk here. One line, shown when the place is discovered.")]
        [TextArea] public string Purpose = "";

        [Tooltip("Roughly how big the place is, in metres. Used for discovery range and map markers.")]
        public float Radius = 12f;

        [Tooltip("Whether walking in here is expected to be dangerous. The village is; the shop is not.")]
        public bool Hostile;

        /// <summary>Everything currently in the world, so the HUD does not have to search the scene.</summary>
        public static readonly System.Collections.Generic.List<Landmark> All = new();

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        /// <summary>The nearest landmark to a point, or null if the world has none yet.</summary>
        public static Landmark Nearest(Vector3 position)
        {
            Landmark best = null;
            float bestDistance = float.MaxValue;

            foreach (Landmark landmark in All)
            {
                float distance = (landmark.transform.position - position).sqrMagnitude;
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = landmark;
            }

            return best;
        }
    }
}
