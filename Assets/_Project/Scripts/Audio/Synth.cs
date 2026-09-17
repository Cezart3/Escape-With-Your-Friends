using System;
using UnityEngine;

namespace EscapeWithYourFriends.Audio
{
    /// <summary>
    /// Every sound in the game, as arithmetic (#80).
    ///
    /// **Why not wav files.** The rest of the project refuses to keep things that only exist as a
    /// binary: the terrain is a seed, the arena is an editor script, the HUD is code. Audio is the
    /// last place that would have smuggled in a folder of files nobody can review in a diff, licensed
    /// from somewhere nobody can re-download. A punch is a noise burst with a fast decay, and that is
    /// one line to read and one line to tune. When a real sound designer replaces these, the only
    /// file that changes is this one.
    ///
    /// The sounds are deliberately cheap and slightly wrong, which is the same joke the art is
    /// making. Mono, 22.05kHz, a few hundred milliseconds each; they cost nothing to hold in memory.
    /// </summary>
    public static class Synth
    {
        public const int Rate = 22050;

        /// <summary>One deterministic noise source, so a build sounds the same as the last one.</summary>
        static readonly System.Random Noise = new(20260917);

        /// <summary>White noise in -1..1.</summary>
        public static float White() => (float)(Noise.NextDouble() * 2.0 - 1.0);

        /// <summary>Exponential decay, 1 at the start of the clip and near 0 at the end.</summary>
        public static float Decay(float t, float length, float sharpness = 5f)
            => Mathf.Exp(-sharpness * t / Mathf.Max(0.0001f, length));

        /// <summary>A sine at <paramref name="hz"/>, in -1..1.</summary>
        public static float Sine(float t, float hz) => Mathf.Sin(2f * Mathf.PI * hz * t);

        /// <summary>A square, which is what cheap sounds cheap.</summary>
        public static float Square(float t, float hz) => Sine(t, hz) >= 0f ? 1f : -1f;

        /// <summary>
        /// Builds a clip from a function of time. <paramref name="shape"/> gets seconds since the
        /// start and returns -1..1; anything outside is clamped, because a clip that clips is a
        /// crackle on somebody's headphones.
        /// </summary>
        public static AudioClip Clip(string name, float seconds, Func<float, float> shape)
        {
            int samples = Mathf.Max(1, Mathf.RoundToInt(seconds * Rate));
            var data = new float[samples];

            for (int i = 0; i < samples; i++)
            {
                float value = shape(i / (float)Rate);
                data[i] = float.IsNaN(value) ? 0f : Mathf.Clamp(value, -1f, 1f);
            }

            // A hard edge at either end is a click, and a click is what a cheap sound engine sounds
            // like. Four milliseconds is enough and is shorter than anything here.
            int fade = Mathf.Min(samples / 4, Rate / 250);
            for (int i = 0; i < fade; i++)
            {
                float k = i / (float)fade;
                data[i] *= k;
                data[samples - 1 - i] *= k;
            }

            AudioClip clip = AudioClip.Create(name, samples, 1, Rate, stream: false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
