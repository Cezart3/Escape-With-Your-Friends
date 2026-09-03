using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// What the player is currently supposed to be doing, and where it is.
    ///
    /// Static and not networked, deliberately. An objective is a statement about the run, and every
    /// peer can work out the same statement from state FishNet has already replicated - a networked
    /// objective would be four clients waiting a round trip to be told something they could each have
    /// concluded locally. When an objective genuinely depends on server-only knowledge, the server
    /// replicates that knowledge and everyone derives the same line from it.
    ///
    /// The HUD reads this every frame. Nothing subscribes, because a poll of two fields is cheaper
    /// than an event, and the HUD is already polling everything else.
    /// </summary>
    public static class Objective
    {
        /// <summary>One line, imperative, second person. "Search the wreck", not "The wreck can be searched".</summary>
        public static string Text = "";

        /// <summary>Where it is, for the distance readout. Null for an objective with no place.</summary>
        public static Transform Target;

        /// <summary>Whether there is anything to show at all.</summary>
        public static bool Active => !string.IsNullOrEmpty(Text);

        public static void Set(string text, Transform target = null)
        {
            Text = text;
            Target = target;
            Debug.Log($"[Objective] {text}" + (target != null ? $" -> {target.name}" : "") + ".");
        }

        public static void Clear()
        {
            Text = "";
            Target = null;
        }

        /// <summary>
        /// How far the local player is from the objective, in metres, or -1 if that question has no
        /// answer yet - no target, or no body to measure from.
        /// </summary>
        public static float DistanceFrom(Vector3 position)
        {
            if (Target == null) return -1f;

            Vector3 delta = Target.position - position;
            delta.y = 0f;
            return delta.magnitude;
        }
    }
}
