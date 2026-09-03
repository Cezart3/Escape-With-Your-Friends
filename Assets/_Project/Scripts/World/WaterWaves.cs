using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The shape of the sea, as three numbers times three.
    ///
    /// This exists as its own class, and as constants rather than as a tunable asset, because the
    /// same wave has to be computed in two completely different places: the vertex shader that draws
    /// the water, and the C# that will float a boat on it. If those two ever disagree the boat sits
    /// in a trough it cannot see, or hovers a metre over the crest, and nobody can tell which half is
    /// wrong. One definition, copied into the material by the factory and read back here, means the
    /// disagreement is impossible rather than unlikely.
    ///
    /// Three directional sine waves summed vertically. Not Gerstner: Gerstner displaces sideways as
    /// well, which looks better up close and turns "how high is the water at x,z" into a fixed-point
    /// solve. Vertical-only stays a plain function, and at this amplitude - the whole sea moves about
    /// 60cm - the difference is invisible against a shore break that is faked anyway.
    ///
    /// Wavelengths are bounded below by the mesh: the near patch has a vertex every
    /// <see cref="IslandProfile.WaterCellSize"/> metres, and a wave shorter than four cells turns
    /// into aliased noise that crawls when the camera moves. Everything finer than that is the
    /// normal map's job.
    /// </summary>
    public static class WaterWaves
    {
        public const int Count = 3;

        /// <summary>Direction of travel, unit length. x and z; the sea does not care about y.</summary>
        public static readonly Vector2[] Directions =
        {
            new Vector2(0.8600f, 0.5103f),
            new Vector2(-0.4191f, 0.9079f),
            new Vector2(0.6000f, -0.8000f)
        };

        /// <summary>Crest-to-mean height in metres. They sum to 0.62m, which is a calm day.</summary>
        public static readonly float[] Amplitudes = { 0.34f, 0.19f, 0.09f };

        /// <summary>Crest to crest in metres.</summary>
        public static readonly float[] Wavelengths = { 41f, 23f, 17f };

        /// <summary>Metres per second the crest travels along its direction.</summary>
        public static readonly float[] Speeds = { 3.1f, 2.4f, 1.7f };

        /// <summary>
        /// Packed the way the shader wants it: xy is the direction, z the amplitude, w the wave
        /// number. Speed rides along separately because it is the only per-wave value that is not
        /// needed for the derivative.
        /// </summary>
        public static Vector4 Packed(int wave)
        {
            return new Vector4(Directions[wave].x, Directions[wave].y,
                               Amplitudes[wave], WaveNumber(wave));
        }

        /// <summary>Radians per metre along the direction of travel.</summary>
        public static float WaveNumber(int wave) => 2f * Mathf.PI / Wavelengths[wave];

        /// <summary>Radians per second, tied to the wave number so the crest moves at Speeds[wave].</summary>
        public static float AngularSpeed(int wave) => Speeds[wave] * WaveNumber(wave);

        /// <summary>Displacement from mean sea level at a world position, in metres.</summary>
        public static float Height(float x, float z, float time)
        {
            float sum = 0f;
            for (int i = 0; i < Count; i++)
            {
                float phase = (Directions[i].x * x + Directions[i].y * z) * WaveNumber(i)
                              + time * AngularSpeed(i);
                sum += Amplitudes[i] * Mathf.Sin(phase);
            }

            return sum;
        }

        /// <summary>
        /// Surface normal, from the analytic derivative rather than from sampling neighbours. The
        /// same expression the vertex shader uses, so a boat tilts the way the water looks.
        /// </summary>
        public static Vector3 Normal(float x, float z, float time)
        {
            float dx = 0f;
            float dz = 0f;

            for (int i = 0; i < Count; i++)
            {
                float k = WaveNumber(i);
                float phase = (Directions[i].x * x + Directions[i].y * z) * k + time * AngularSpeed(i);
                float slope = Amplitudes[i] * k * Mathf.Cos(phase);
                dx += slope * Directions[i].x;
                dz += slope * Directions[i].y;
            }

            return new Vector3(-dx, 1f, -dz).normalized;
        }
    }
}
