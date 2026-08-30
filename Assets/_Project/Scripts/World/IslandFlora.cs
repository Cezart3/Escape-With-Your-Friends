using System.Collections.Generic;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>One placed plant, in world space. The caller converts to whatever it needs.</summary>
    public struct FloraInstance
    {
        public int Prototype;
        public Vector3 Position;
        public float Rotation;
        public float Height;
        public float Width;
    }

    /// <summary>
    /// Where the plants go, as a function of the seed. Same contract as <see cref="IslandShape"/>:
    /// ask twice, get the same forest, on any machine.
    ///
    /// The placement is a jittered grid rather than a random spray. A spray clumps and gaps for
    /// free, because that is what uniform random does, and those gaps read as bald patches you
    /// cannot tune away by adding more trees. One candidate per cell, offset inside its cell by the
    /// same hash that decides whether it lives, gives even coverage with no visible lattice, and it
    /// costs one hash per cell instead of a rejection loop.
    ///
    /// Clumping is then put back deliberately, per species, as a low-frequency grove mask: palms
    /// bunch along one stretch of beach and not another, and the highland pines thin out before the
    /// rock rather than stopping dead on a contour line.
    /// </summary>
    public class IslandFlora
    {
        public const int Palm = 0;
        public const int JungleTree = 1;
        public const int HighlandTree = 2;
        public const int Bush = 3;
        public const int SpeciesCount = 4;

        public static readonly string[] SpeciesNames = { "Palm", "JungleTree", "HighlandTree", "Bush" };

        /// <summary>Which pass places each species. Bushes get the finer grid, to fill in under the canopy.</summary>
        public static readonly bool[] IsUndergrowth = { false, false, false, true };

        const int PlacementSalt = 771177;
        const int GroveSalt = 313131;

        readonly IslandShape _shape;
        readonly IslandSplat _splat;
        readonly IslandProfile _profile;

        public IslandFlora(IslandShape shape)
        {
            _shape = shape;
            _splat = new IslandSplat(shape);
            _profile = shape.Profile;
        }

        /// <summary>
        /// Every plant on the island, in a fixed order. Two passes over two grids: canopy first,
        /// undergrowth second on a finer cell, so bushes are not competing with trees for the same
        /// slot and the ground under a canopy is not bare.
        /// </summary>
        public List<FloraInstance> Scatter()
        {
            var placed = new List<FloraInstance>(16384);
            Pass(placed, _profile.CanopyCellSize, false);
            Pass(placed, _profile.UndergrowthCellSize, true);
            return placed;
        }

        void Pass(List<FloraInstance> placed, float cellSize, bool undergrowth)
        {
            float cell = Mathf.Max(1f, cellSize);
            float half = _profile.Size * 0.5f;

            // Inset by one cell: a tree centred on the very edge of the terrain has half its canopy
            // hanging over nothing, and the coast mask has made that border seabed anyway.
            int cells = Mathf.Max(1, Mathf.FloorToInt((_profile.Size - cell * 2f) / cell));
            float origin = -half + cell;

            var weights = new float[IslandSplat.LayerCount];

            for (int j = 0; j < cells; j++)
            {
                for (int i = 0; i < cells; i++)
                {
                    uint hash = Hash(i, j, _shape.Salt(PlacementSalt + (undergrowth ? 1 : 0)));

                    // Three independent streams out of one hash: two for the jitter, one for the roll.
                    float jx = ((hash & 0x3FF) / 1023f - 0.5f) * _profile.FloraJitter;
                    float jz = (((hash >> 10) & 0x3FF) / 1023f - 0.5f) * _profile.FloraJitter;
                    float roll = ((hash >> 20) & 0xFFF) / 4095f;

                    float x = origin + (i + 0.5f + jx) * cell;
                    float z = origin + (j + 0.5f + jz) * cell;

                    float height = _shape.HeightAt(x, z);
                    if (height <= _profile.FloraMinHeight) continue;

                    float slope = _shape.SlopeAt(x, z);
                    _splat.Weights(height, slope, x, z, weights);

                    int species = Choose(x, z, height, slope, weights, roll, undergrowth);
                    if (species < 0) continue;

                    // A second hash for the look, so tuning a density rule does not reshuffle the
                    // size and rotation of every tree that was going to be placed anyway.
                    uint look = Hash(i, j, _shape.Salt(PlacementSalt + 977 + species));
                    float sizeRoll = (look & 0xFFFF) / 65535f;
                    float widthRoll = ((look >> 16) & 0xFF) / 255f;

                    float scale = Mathf.Lerp(_profile.FloraMinScale, _profile.FloraMaxScale, sizeRoll);

                    placed.Add(new FloraInstance
                    {
                        Prototype = species,
                        Position = new Vector3(x, height, z),
                        Rotation = ((look >> 24) & 0xFF) / 255f * Mathf.PI * 2f,
                        Height = scale,
                        // Width varies less than height. A tree twice as wide as it is tall reads as
                        // a bug; a tree slightly fatter than its neighbour reads as a different tree.
                        Width = scale * Mathf.Lerp(0.88f, 1.12f, widthRoll)
                    });
                }
            }
        }

        /// <summary>
        /// The species that wins this cell, or -1 for bare ground. Candidates are tried in order and
        /// the roll is spent as it goes, so the ordering is a priority: a palm-suitable cell down by
        /// the water is a palm before it is anything else.
        /// </summary>
        int Choose(float x, float z, float height, float slope, float[] weights, float roll, bool undergrowth)
        {
            for (int species = 0; species < SpeciesCount; species++)
            {
                if (IsUndergrowth[species] != undergrowth) continue;

                float chance = Suitability(species, height, slope, weights) * Grove(species, x, z);
                if (roll < chance) return species;
                roll -= chance;
            }

            return -1;
        }

        /// <summary>
        /// How well a species fits this ground, 0..1, before the grove mask. Every rule is a band on
        /// height, a ceiling on slope and a demand on the cover already painted there, so the forest
        /// agrees with the splatmap instead of contradicting it: no jungle on bare rock, no palms up
        /// the mountain.
        /// </summary>
        float Suitability(int species, float height, float slope, float[] weights)
        {
            float sand = weights[IslandSplat.Sand];
            float grass = weights[IslandSplat.Grass];
            float dirt = weights[IslandSplat.Dirt];
            float rock = weights[IslandSplat.Rock];

            switch (species)
            {
                case Palm:
                    // The beach band and only the beach band. Palms are the silhouette that says
                    // "island" from out at sea, so they get the shoreline to themselves.
                    return _profile.PalmDensity
                           * Band(height, 0.6f, 7f, 1.5f)
                           * Ceiling(slope, 0.45f, 0.15f)
                           * Mathf.Clamp01(sand * 1.3f + grass * 0.35f);

                case JungleTree:
                    return _profile.JungleDensity
                           * Band(height, 2.5f, 58f, 8f)
                           * Ceiling(slope, 0.62f, 0.18f)
                           * Mathf.Clamp01(grass + dirt * 0.55f);

                case HighlandTree:
                    return _profile.HighlandDensity
                           * Band(height, 28f, 110f, 12f)
                           * Ceiling(slope, 0.7f, 0.2f)
                           * Mathf.Clamp01(grass + dirt * 0.8f + rock * 0.25f);

                case Bush:
                    return _profile.BushDensity
                           * Band(height, 1f, 92f, 3f)
                           * Ceiling(slope, 0.75f, 0.2f)
                           * Mathf.Clamp01(grass * 0.9f + dirt + sand * 0.25f);
            }

            return 0f;
        }

        /// <summary>
        /// The clumping. One low-frequency noise field per species, remapped so most of the island
        /// stays at full density and only the tails thin out. Multiplying by raw noise instead would
        /// halve the forest everywhere, which does not look like clumping, it looks like fewer trees.
        ///
        /// The span matters more than it looks. Averaged gradient noise piles up around 0.5 and
        /// almost never reaches the ends, so dividing by the whole range above the floor - the
        /// obvious thing to write - multiplies the entire island by about a quarter and reads as a
        /// thin forest with no clearings anywhere. A narrow span puts the noise's own middle at full
        /// density, and only the genuine dips become open ground.
        /// </summary>
        float Grove(int species, float x, float z)
        {
            float value = _shape.Fbm(x, z, _profile.GroveFeatureSize, 2, 0.5f, 2.03f,
                                     _shape.Salt(GroveSalt + species * 131));
            return Mathf.Clamp01((value - _profile.GroveFloor) / Mathf.Max(0.01f, _profile.GroveSpan));
        }

        /// <summary>1 inside the band, feathered to 0 outside it over <paramref name="feather"/> metres.</summary>
        static float Band(float value, float low, float high, float feather)
        {
            float f = Mathf.Max(0.01f, feather);
            float rising = Mathf.Clamp01((value - low) / f);
            float falling = Mathf.Clamp01((high - value) / f);
            return IslandShape.Smooth(Mathf.Min(rising, falling));
        }

        /// <summary>1 below the limit, 0 above it, feathered. Slope is a gradient here, never degrees.</summary>
        static float Ceiling(float value, float limit, float feather)
        {
            return IslandShape.Smooth(Mathf.Clamp01((limit - value) / Mathf.Max(0.01f, feather)));
        }

        /// <summary>Same FNV-1a and avalanche as the noise, over grid indices instead of cell corners.</summary>
        static uint Hash(int x, int z, int seed)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = (h ^ (uint)seed) * 16777619u;
                h = (h ^ (uint)x) * 16777619u;
                h = (h ^ (uint)z) * 16777619u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                h *= 3266489917u;
                h ^= h >> 16;
                return h;
            }
        }
    }
}
