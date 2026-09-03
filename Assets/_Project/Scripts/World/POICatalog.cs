using System;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// One thing standing somewhere on the island: the revive machine, the shop, the casino, the
    /// native village, a wreck on the sand.
    ///
    /// The prefab is named by path rather than referenced directly, because the point of this whole
    /// arrangement is that adding a point of interest is an edit to a text file from a terminal, and
    /// a Unity object reference in YAML is a GUID nobody can type. The editor resolves the path once,
    /// at bake time, and complains loudly if it does not exist.
    /// </summary>
    [Serializable]
    public class POIEntry
    {
        [Tooltip("What this is, for logs and for errors. Must be unique.")]
        public string Id = "poi";

        [Tooltip("Asset path of the prefab. It must carry a NetworkObject and be in the spawnable prefabs list.")]
        public string PrefabPath = "";

        [Tooltip("Where it stands, in world XZ. Height comes from the island unless SnapToGround is off.")]
        public Vector2 Position;

        [Tooltip("Which way it faces, in degrees.")]
        public float Yaw;

        [Tooltip("Metres above the ground. Use it to sink a foundation or float a buoy.")]
        public float YOffset;

        [Tooltip("Off only for something deliberately not on the ground, like a raft or a platform.")]
        public bool SnapToGround = true;

        [Tooltip("Metres of ground flattened to a level pad around this. Zero leaves the terrain alone.")]
        public float PadRadius;

        [Tooltip("Metres over which the pad blends back into the hillside. Too small and the pad has a cliff around it.")]
        public float PadFalloff = 10f;

        [Tooltip("Metres the pad sits above the ground it replaced. A little lift keeps a camp out of the tideline.")]
        public float PadRaise;

        [Tooltip("Steepest ground this is allowed to stand on, as a gradient. Checked at bake time, not enforced.")]
        public float MaxSlope = 0.35f;

        [Tooltip("Whether it is allowed to end up below sea level. On for wrecks and docks, off for everything else.")]
        public bool AllowUnderwater;
    }

    /// <summary>
    /// Every point of interest on the island, in one text asset.
    ///
    /// The acceptance criterion for #35 is that a new POI is one edit from a terminal, and this is
    /// what makes that true: append seven lines of YAML here, regenerate the island, and the thing
    /// is standing on the beach with the ground flattened under it. No scene is opened, nothing is
    /// dragged, and the diff is readable.
    ///
    /// The catalog is read in two very different places, which is the reason it is a runtime asset
    /// rather than an editor one:
    ///   - <see cref="IslandShape"/> reads the pads, because flattening the ground under a camp has
    ///     to happen inside the height function or the splatmap and the trees will disagree with the
    ///     terrain they are painted on;
    ///   - <see cref="POISpawner"/> reads the placements at run time on the server.
    /// </summary>
    [CreateAssetMenu(fileName = "POIs", menuName = "EWYF/POI Catalog")]
    public class POICatalog : ScriptableObject
    {
        [Tooltip("Everything that stands on the island. Order is not meaningful except where pads overlap.")]
        public POIEntry[] Entries = Array.Empty<POIEntry>();

        /// <summary>The entry with this id, or null. Ids are expected to be unique; the first wins.</summary>
        public POIEntry Find(string id)
        {
            foreach (POIEntry entry in Entries)
                if (entry != null && entry.Id == id) return entry;

            return null;
        }
    }
}
