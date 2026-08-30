using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The island, as a function. Ask it for a height at a world position and it answers the same
    /// thing every time, on every machine, forever.
    ///
    /// Why the noise is written out by hand instead of calling Mathf.PerlinNoise: Unity documents
    /// its Perlin implementation as unspecified and free to change between versions. The acceptance
    /// criterion for the island is that a seed reproduces it byte for byte, and "byte for byte until
    /// we upgrade the editor" is not that. Everything below is integer hashing and float lerps, so
    /// the only thing it depends on is IEEE 754.
    ///
    /// Why it is a plain class rather than an editor script: the terrain asset is baked once, but
    /// spawn points, POI placement and the boat dock all need to ask "how high is the ground over
    /// there" without raycasting a scene that may not be loaded yet. Same function, same answer.
    ///
    /// The shape is built in five layers, in this order:
    ///   1. domain warp    - drag the sampling position around, so hills bend instead of blobbing
    ///   2. fBm relief     - the base hills, centred on a water line rather than on zero
    ///   3. coast mask     - a radial falloff with a noisy radius, giving bays and headlands
    ///   4. mountain       - a ridged dome added inland, the landmark you steer by
    ///   5. beach flatten  - squash whatever ends up near sea level into walkable sand
    /// </summary>
    public class IslandShape
    {
        /// <summary>Eight fixed gradient directions. No trig anywhere, so no library drift.</summary>
        static readonly Vector2[] Gradients =
        {
            new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f),
            new Vector2(0.70710678f, 0.70710678f), new Vector2(0.70710678f, -0.70710678f),
            new Vector2(-0.70710678f, 0.70710678f), new Vector2(-0.70710678f, -0.70710678f)
        };

        // Salts, so the layers do not all read the same noise field under different names.
        const int HillSalt = 0;
        const int WarpXSalt = 7717;
        const int WarpZSalt = 31337;
        const int CoastSalt = 90210;
        const int RidgeSalt = 51966;

        readonly IslandProfile _profile;
        readonly float _half;

        public IslandShape(IslandProfile profile)
        {
            _profile = profile;
            _half = profile.Size * 0.5f;
        }

        public IslandProfile Profile => _profile;

        /// <summary>Sea level, in world space. Fixed at the origin so buoyancy never needs a lookup.</summary>
        public const float SeaLevel = 0f;

        /// <summary>
        /// Ground height in metres above sea level at a world position, clamped into the vertical
        /// range of the terrain. Negative is seabed.
        /// </summary>
        public float HeightAt(float x, float z)
        {
            // 1. Domain warp. Two independent noise fields, one per axis, sampled at the unwarped
            //    position: cheap, and enough to break up the radial symmetry of everything below.
            float warpFreq = 1f / Mathf.Max(1f, _profile.WarpFeatureSize);
            float wx = x + (Noise(x * warpFreq, z * warpFreq, Salt(WarpXSalt)) * 2f - 1f) * _profile.WarpStrength;
            float wz = z + (Noise(x * warpFreq, z * warpFreq, Salt(WarpZSalt)) * 2f - 1f) * _profile.WarpStrength;

            // 2. Base relief. Shifting by the water line before scaling is what decides how much of
            //    the island is dry: the fBm sits around 0.5, so a lower line lifts everything.
            float hills = (Fbm(wx, wz, _profile.HillFeatureSize, _profile.HillOctaves,
                               _profile.HillGain, _profile.HillLacunarity, Salt(HillSalt))
                           - _profile.HillWaterLine) * _profile.HillHeight;

            // 4. Mountain, added before the mask so the coast can still cut it off if it is placed
            //    near the shore. Sampled warped, so its ridges follow the same flow as the hills.
            float mountain = Mountain(wx, wz);

            // 3. Coast mask. Land is what survives it; the rest sinks. The seabed pull is squared
            //    rather than linear because a linear one drowns the whole middle band of the mask:
            //    forty metres of sea beats five metres of hill everywhere except the last tenth of
            //    the falloff, which leaves a small island in a big square of water. Squaring keeps
            //    the shoreline out where the mask actually fades, and gives a shallow shelf to
            //    swim over on the way in.
            float mask = CoastMask(x, z);
            float shelf = 1f - mask;
            float height = (hills + mountain) * mask - shelf * shelf * _profile.SeabedDepth;

            // 5. Beaches. Anything within a few metres of sea level gets its slope cut, which widens
            //    the shoreline into something you can drag a boat onto instead of a wall.
            float band = Mathf.Max(0.01f, _profile.BeachBand);
            float away = Smooth(Mathf.Clamp01(Mathf.Abs(height) / band));
            height *= Mathf.Lerp(_profile.BeachFlatten, 1f, away);

            return Mathf.Clamp(height, -_profile.SeabedDepth, _profile.PeakHeight);
        }

        /// <summary>True when the ground here is dry. The one question most systems ask.</summary>
        public bool IsLand(float x, float z) => HeightAt(x, z) > SeaLevel;

        /// <summary>
        /// 1 well inside the island, 0 out at sea, with a noisy transition. The noise is added to the
        /// radius rather than to the mask so the coastline moves in and out; adding it to the mask
        /// would instead punch holes in the middle of otherwise solid ground.
        /// </summary>
        float CoastMask(float x, float z)
        {
            float radius = Mathf.Sqrt(x * x + z * z) / _half;

            float ragged = (Fbm(x, z, _profile.CoastFeatureSize, 3, 0.5f, 2.07f, Salt(CoastSalt)) - 0.5f)
                           * _profile.CoastRaggedness;
            radius += ragged;

            float inner = _profile.CoastInnerRadius;
            float outer = Mathf.Max(inner + 0.01f, _profile.CoastOuterRadius);
            return 1f - Smooth(Mathf.Clamp01((radius - inner) / (outer - inner)));
        }

        /// <summary>
        /// The landmark. A dome falling off to nothing at its radius, then chewed by ridged noise so
        /// it reads as rock rather than as a scoop of ice cream.
        /// </summary>
        float Mountain(float x, float z)
        {
            float radius = _profile.MountainRadius * _half;
            if (radius <= 0f || _profile.MountainHeight <= 0f) return 0f;

            float cx = _profile.MountainCentre.x * _half;
            float cz = _profile.MountainCentre.y * _half;
            float distance = Mathf.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz)) / radius;
            if (distance >= 1f) return 0f;

            float dome = Mathf.Pow(1f - distance, Mathf.Max(0.01f, _profile.MountainSharpness));

            // Ridged noise: fold the field at its midpoint so the maxima become creases. Three
            // octaves is plenty here, the dome supplies the large shape.
            float ridge = 1f - Mathf.Abs(Fbm(x, z, _profile.MountainRidgeFeatureSize, 3, 0.5f, 2.11f,
                                             Salt(RidgeSalt)) * 2f - 1f);
            float carve = Mathf.Lerp(1f, ridge, Mathf.Clamp01(_profile.MountainRidge));

            return dome * carve * _profile.MountainHeight;
        }

        /// <summary>Stacked octaves of gradient noise, normalised back into 0..1.</summary>
        float Fbm(float x, float z, float featureSize, int octaves, float gain, float lacunarity, int seed)
        {
            float frequency = 1f / Mathf.Max(1f, featureSize);
            float amplitude = 1f;
            float sum = 0f;
            float total = 0f;

            int count = Mathf.Max(1, octaves);
            for (int i = 0; i < count; i++)
            {
                sum += Noise(x * frequency, z * frequency, seed + i * 1013) * amplitude;
                total += amplitude;
                amplitude *= gain;
                frequency *= lacunarity;
            }

            return total > 0f ? sum / total : 0f;
        }

        /// <summary>Gradient noise on the unit grid, remapped from -1..1 to 0..1.</summary>
        static float Noise(float x, float z, int seed)
        {
            int xi = Mathf.FloorToInt(x);
            int zi = Mathf.FloorToInt(z);
            float xf = x - xi;
            float zf = z - zi;

            float u = Smooth(xf);
            float v = Smooth(zf);

            float a = Dot(xi, zi, xf, zf, seed);
            float b = Dot(xi + 1, zi, xf - 1f, zf, seed);
            float c = Dot(xi, zi + 1, xf, zf - 1f, seed);
            float d = Dot(xi + 1, zi + 1, xf - 1f, zf - 1f, seed);

            float value = Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);

            // Gradient noise on eight directions peaks near 0.707; the scale pulls it back to
            // roughly the full range before the clamp, which only catches the rare overshoot.
            return Mathf.Clamp01(value * 0.7071f + 0.5f);
        }

        static float Dot(int gx, int gz, float dx, float dz, int seed)
        {
            Vector2 gradient = Gradients[Hash(gx, gz, seed) & 7];
            return gradient.x * dx + gradient.y * dz;
        }

        /// <summary>Quintic smoothstep. Second derivative is zero at the ends, so octaves do not crease.</summary>
        static float Smooth(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

        /// <summary>
        /// Integer hash. FNV-1a over the two coordinates and the seed, finished with an avalanche so
        /// that neighbouring cells do not land on neighbouring gradients.
        /// </summary>
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

        /// <summary>Mixes the profile seed with a per-layer salt, so one seed drives every layer.</summary>
        int Salt(int salt)
        {
            unchecked { return _profile.Seed * 1103515245 + salt; }
        }
    }
}
