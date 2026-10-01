using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// Scales the terrain's draw distances to the quality level.
    ///
    /// These are the numbers that decide whether an integrated GPU can run this island. #32 set them
    /// once - trees out to 320 m, grass to 85 m - and those are good numbers for a machine with a
    /// discrete card. On a Radeon 760M the grass alone is most of a frame: it is thousands of
    /// alpha-tested quads, drawn back to front, covering the whole screen when you look down a slope.
    ///
    /// Halving the grass distance quarters its area. That is the single largest lever available
    /// without changing what the island is, which is why it is here and not in a shader.
    ///
    /// The multipliers rather than absolute numbers: the profile stays the one place the island's
    /// look is defined, and this only says how much of it a given machine gets.
    /// </summary>
    public class TerrainQuality : MonoBehaviour
    {
        [Tooltip("The terrain to scale. Found in this scene when left empty.")]
        [SerializeField] Terrain _terrain;

        [Header("What the profile asked for")]
        [Tooltip("Baked from IslandProfile so the multipliers have something to multiply after a scene load.")]
        [SerializeField] float _treeDistance = 320f;
        [SerializeField] float _detailDistance = 85f;
        [SerializeField] float _detailDensity = 0.8f;
        [SerializeField] float _billboardDistance = 90f;

        void Start() => Apply();

        /// <summary>Called again when the player changes the grass or view distance option (#241).</summary>
        public void Apply()
        {
            if (_terrain == null) _terrain = FindAnyObjectByType<Terrain>();
            if (_terrain == null) return;

            // The pixel error is absolute, not a multiplier: it is already a tolerance.
            var (grass, density, trees, pixelError) = Core.VideoSettings.Terrain;

            _terrain.treeDistance = _treeDistance * trees;
            _terrain.detailObjectDistance = _detailDistance * grass;
            _terrain.detailObjectDensity = Mathf.Min(1f, _detailDensity * density);
            _terrain.heightmapPixelError = pixelError;

            // Billboards past the mesh distance are the cheap half of the tree budget, so they keep
            // their share: pulling them in with everything else would make the island look bald from
            // a hilltop for almost no saving.
            // Off the stored value, not the current one: taking a minimum against whatever is
            // already there would walk the billboard distance down a little further every time the
            // player changed quality.
            _terrain.treeBillboardDistance = Mathf.Min(_billboardDistance, _terrain.treeDistance * 0.35f);

            Debug.Log($"[TerrainQuality] trees {_terrain.treeDistance:F0}m, "
                          + $"grass {_terrain.detailObjectDistance:F0}m at {_terrain.detailObjectDensity:F2} "
                          + $"density, terrain error {_terrain.heightmapPixelError:F0}px.");
        }

        /// <summary>Bake time. The profile's numbers are copied in so nothing has to load it later.</summary>
        public void Configure(Terrain terrain, float trees, float billboards, float grass, float density)
        {
            _terrain = terrain;
            _treeDistance = trees;
            _billboardDistance = billboards;
            _detailDistance = grass;
            _detailDensity = density;
        }
    }
}
