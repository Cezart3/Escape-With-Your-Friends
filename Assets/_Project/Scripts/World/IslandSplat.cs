using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// What the ground is made of, as a function of what the ground is shaped like. Give it a height
    /// and a slope and it hands back the four terrain layer weights for that spot.
    ///
    /// Nobody paints the island. The rules are: sand near sea level, rock where it is steep or high,
    /// dirt in noisy patches inland, grass everywhere that is left. Because it reads the same
    /// <see cref="IslandShape"/> the heightmap came from, the painting cannot drift out of sync with
    /// the terrain - regenerating one regenerates the other.
    ///
    /// Slope is a gradient (rise over run), never an angle in degrees. Converting would mean calling
    /// atan, and the whole point of the shape being hand-rolled is that no library function sits
    /// between a seed and the island.
    /// </summary>
    public class IslandSplat
    {
        public const int LayerCount = 4;

        public const int Sand = 0;
        public const int Grass = 1;
        public const int Rock = 2;
        public const int Dirt = 3;

        /// <summary>Layer names, in index order. The generator uses these for assets and for the log.</summary>
        public static readonly string[] LayerNames = { "Sand", "Grass", "Rock", "Dirt" };

        const int DirtSalt = 424242;
        const int JitterHeightSalt = 8081;
        const int JitterSlopeSalt = 60613;

        readonly IslandShape _shape;
        readonly IslandProfile _profile;

        public IslandSplat(IslandShape shape)
        {
            _shape = shape;
            _profile = shape.Profile;
        }

        /// <summary>
        /// Fills <paramref name="weights"/> with the cover mix at a world position. The weights sum to
        /// 1, which is what Unity expects of an alphamap row.
        /// </summary>
        public void Weights(float height, float slope, float x, float z, float[] weights)
        {
            // Jitter. Without it the shoreline reads as a contour line and the rock line reads as a
            // machine drawing. Two independent fields, so the sand edge and the rock edge do not
            // wobble in step with each other.
            float jitterFreq = 1f / Mathf.Max(1f, _profile.CoverJitterFeatureSize);
            float heightJitter = (IslandShape.Noise(x * jitterFreq, z * jitterFreq, _shape.Salt(JitterHeightSalt)) * 2f - 1f)
                                 * _profile.CoverJitter;
            float slopeJitter = (IslandShape.Noise(x * jitterFreq, z * jitterFreq, _shape.Salt(JitterSlopeSalt)) * 2f - 1f)
                                * _profile.CoverJitter * 0.05f;

            // Rock wins first: it is the only layer that can override anything else, because a cliff
            // is a cliff whether it is at sea level or at the summit.
            float steep = Ramp(slope + slopeJitter, _profile.RockSlope, _profile.RockSlopeBlend);
            float high = Ramp(height + heightJitter, _profile.RockHeight, _profile.RockHeightBlend);
            float rock = Mathf.Max(steep, high);

            // Sand fills everything below the dune line, which includes the whole seabed. Steep
            // underwater ground still comes out as rock, which is what a real drop-off looks like.
            float sand = (1f - Ramp(height + heightJitter, _profile.SandTop, _profile.SandBlend)) * (1f - rock);

            // Whatever is left is vegetated, and some of that is worn down to dirt in patches.
            float remaining = Mathf.Max(0f, 1f - rock - sand);
            float patch = Ramp(_shape.Fbm(x, z, _profile.DirtFeatureSize, 3, 0.5f, 2.09f, _shape.Salt(DirtSalt)),
                               _profile.DirtThreshold, _profile.DirtBlend);
            float dirt = remaining * patch;
            float grass = remaining - dirt;

            weights[Sand] = sand;
            weights[Grass] = grass;
            weights[Rock] = rock;
            weights[Dirt] = dirt;

            Normalise(weights);
        }

        /// <summary>Smooth 0 to 1 ramp centred on an edge. Blend widths are never allowed to be zero.</summary>
        static float Ramp(float value, float edge, float width)
        {
            float w = Mathf.Max(0.0001f, width);
            return IslandShape.Smooth(Mathf.Clamp01((value - edge) / w + 0.5f));
        }

        static void Normalise(float[] weights)
        {
            float sum = 0f;
            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i] < 0f) weights[i] = 0f;
                sum += weights[i];
            }

            if (sum <= 0.0001f)
            {
                // Cannot happen with the rules above, but an alphamap row that sums to zero renders as
                // a black hole in the terrain, so it is worth a floor.
                weights[Grass] = 1f;
                return;
            }

            float scale = 1f / sum;
            for (int i = 0; i < weights.Length; i++) weights[i] *= scale;
        }
    }
}
