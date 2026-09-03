using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// Where to stand when you want to walk between two landmarks.
    ///
    /// A point of interest is a coordinate in the catalog, and that coordinate is usually inside the
    /// building it names - between the shop's hut and its counter, inside the wreck's hull. The
    /// NavMesh is baked around those walls, so the patch under the catalog coordinate is a sealed
    /// room a metre across, and a path to or from it comes back Partial for a reason that has nothing
    /// to do with the island.
    ///
    /// What anything actually wants - an objective marker, a native walking to the village, the test
    /// in <see cref="NavWalk"/> - is the nearest place it can stand outside. So both ends get the
    /// same treatment: the coordinate itself first, then a ring around it, and the first pair that
    /// joins up wins. Both ends matter, which is the mistake this made once: approaching only the
    /// destination works when the walk starts on open ground and fails when it starts in the shop.
    /// </summary>
    public static class NavApproach
    {
        /// <summary>Metres out from a landmark to try when its own coordinate is walled in.</summary>
        public const float Ring = 10f;

        const int RingPoints = 8;

        /// <summary>Nearest point of NavMesh, or false if there is none within <paramref name="search"/>.</summary>
        public static bool OnMesh(Vector3 point, float search, out Vector3 result)
        {
            if (NavMesh.SamplePosition(point, out NavMeshHit hit, search, NavMesh.AllAreas))
            {
                result = hit.position;
                return true;
            }

            result = point;
            return false;
        }

        /// <summary>
        /// A pair of places to stand, one near each end, with a complete path between them. The
        /// offsets are how far short of each landmark they are: zero means the coordinate itself was
        /// fine, and something close to <see cref="Ring"/> means the landmark is walled in.
        /// </summary>
        public static bool Route(Vector3 origin, Vector3 destination, out Vector3 start, out Vector3 end,
                                 out float startOffset, out float endOffset)
        {
            start = origin;
            end = destination;
            startOffset = 0f;
            endOffset = 0f;

            List<Vector3> starts = Candidates(origin);
            List<Vector3> ends = Candidates(destination);
            if (starts.Count == 0 || ends.Count == 0) return false;

            var path = new NavMeshPath();

            // Centres first in both lists, so an unobstructed pair is found on the first try and the
            // ring is only paid for when it is needed.
            foreach (Vector3 from in starts)
            {
                foreach (Vector3 to in ends)
                {
                    if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path)) continue;
                    if (path.status != NavMeshPathStatus.PathComplete) continue;

                    start = from;
                    end = to;
                    startOffset = Vector3.Distance(origin, from);
                    endOffset = Vector3.Distance(destination, to);
                    return true;
                }
            }

            return false;
        }

        /// <summary>The coordinate itself, then a ring around it, each snapped onto the NavMesh.</summary>
        static List<Vector3> Candidates(Vector3 point)
        {
            var found = new List<Vector3>(RingPoints + 1);

            if (OnMesh(point, 12f, out Vector3 centre)) found.Add(centre);

            for (int i = 0; i < RingPoints; i++)
            {
                float angle = i * Mathf.PI * 2f / RingPoints;
                Vector3 around = point + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * Ring;
                if (OnMesh(around, 8f, out Vector3 sampled)) found.Add(sampled);
            }

            return found;
        }

        /// <summary>Length of a path along its corners, which is what an agent will actually walk.</summary>
        public static float Length(NavMeshPath path)
        {
            float total = 0f;
            for (int i = 1; i < path.corners.Length; i++)
                total += Vector3.Distance(path.corners[i - 1], path.corners[i]);

            return total;
        }
    }
}
