using UnityEngine;

namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// Shared server-side check on a client-supplied aim direction.
    ///
    /// Every weapon lets the client pick where it is pointing, because the alternative is replicating
    /// a look direction at full rate for four players. What the client does not get is a free choice:
    /// the direction has to roughly agree with where the character is actually facing, or you could
    /// punch and tase people standing behind you.
    ///
    /// Lives here rather than in each weapon so melee and ranged cannot drift apart on the rule.
    /// </summary>
    public static class AimValidation
    {
        /// <summary>
        /// True if <paramref name="direction"/> is within <paramref name="maxDeviation"/> degrees of
        /// where <paramref name="attacker"/> faces.
        ///
        /// Only the horizontal angle is checked. Pitch is deliberately free — looking straight down to
        /// hit a body on the floor is normal play, and the server has no cheap way to know how far a
        /// client's camera is allowed to tilt.
        /// </summary>
        public static bool IsFacing(Transform attacker, Vector3 direction, float maxDeviation)
        {
            if (attacker == null) return false;
            if (direction.sqrMagnitude < 0.001f) return false;

            Vector3 flatAim = Vector3.ProjectOnPlane(direction, Vector3.up);
            Vector3 flatForward = Vector3.ProjectOnPlane(attacker.forward, Vector3.up);

            // Straight up or straight down carries no horizontal information to disagree with.
            if (flatAim.sqrMagnitude < 0.001f || flatForward.sqrMagnitude < 0.001f) return true;

            return Vector3.Angle(flatAim, flatForward) <= maxDeviation;
        }
    }
}
