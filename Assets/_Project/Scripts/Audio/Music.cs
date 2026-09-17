using UnityEngine;

namespace EscapeWithYourFriends.Audio
{
    /// <summary>
    /// The soundtrack, generated as it plays (#81).
    ///
    /// The acceptance is *"music never loops noticeably within a normal session"*, and the cheapest
    /// way to never loop is to never repeat: this writes samples into the audio buffer as the engine
    /// asks for them, so what comes out is a different arrangement every time. No files, no loop
    /// points, no fifteen-megabyte ogg somebody has to license.
    ///
    /// It is two voices. A pad holds a slow chord that drifts between the notes of one scale; over it
    /// a pluck picks a note from the same scale every couple of seconds, with rests. The mood only
    /// changes the scale, the tempo and how loud the pad is, which is enough to tell the beach from
    /// the island that shoots back.
    ///
    /// **The callback runs on the audio thread.** Nothing in it may touch the Unity API - no
    /// Time.time, no transforms, no logging. It is arithmetic and one <see cref="System.Random"/>.
    /// </summary>
    public static class Music
    {
        public enum Mood
        {
            /// <summary>The menu: sparse, friendly, nothing happening yet.</summary>
            Menu,

            /// <summary>The first island. Warm, lazy, holiday-that-went-wrong.</summary>
            Island,

            /// <summary>The second island. Same idea, lower and more suspicious.</summary>
            Island2,

            /// <summary>The casino. Faster, louder, a bad decision with a beat.</summary>
            Casino,
        }

        const int Rate = Synth.Rate;

        // Semitone offsets from the root. Major pentatonic for the nice places, minor for the rest:
        // the only thing separating a beach from a horror island is which five notes you are allowed.
        static readonly int[] Major = { 0, 2, 4, 7, 9, 12, 14 };
        static readonly int[] Minor = { 0, 3, 5, 7, 10, 12, 15 };

        static readonly System.Random Pick = new();

        static AudioSource _source;
        static AudioClip _clip;

        static Mood _mood = Mood.Menu;

        // Audio-thread state. Written only inside OnRead.
        static double _t;
        static double _padPhaseA, _padPhaseB;
        static double _pluckPhase;
        static double _pluckUntil;
        static float _pluckHz;
        static double _nextPluckAt;

        /// <summary>What is playing. Setting it takes effect on the next note, not with a cut.</summary>
        public static Mood Playing
        {
            get => _mood;
            set => _mood = value;
        }

        /// <summary>Starts the soundtrack if it is not already running. Safe to call repeatedly.</summary>
        public static void Begin()
        {
            if (_source != null) return;

            // No audio device, no music. A headless run still builds the clip in the harness, which
            // is how the generator is checked at all.
            if (!Application.isPlaying) return;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

            var go = new GameObject("Music");
            Object.DontDestroyOnLoad(go);

            _source = go.AddComponent<AudioSource>();
            _source.spatialBlend = 0f;
            _source.loop = true;
            _source.volume = 0.35f;
            _source.playOnAwake = false;

            _clip = Stream();
            _source.clip = _clip;
            _source.Play();
        }

        /// <summary>Stops it and lets go of the clip. The ending screen and the harness use this.</summary>
        public static void Stop()
        {
            if (_source == null) return;

            Object.Destroy(_source.gameObject);
            _source = null;
            _clip = null;
        }

        /// <summary>The streaming clip, exposed so the harness can pull samples without a device.</summary>
        public static AudioClip Stream()
            => AudioClip.Create("Music", Rate * 10, 1, Rate, stream: true, OnRead);

        /// <summary>Picks the mood that matches a scene name. Called on every scene change.</summary>
        public static void ForScene(string scene)
        {
            if (string.IsNullOrEmpty(scene)) { Playing = Mood.Menu; return; }

            Playing = scene.ToLowerInvariant() switch
            {
                "island2" => Mood.Island2,
                "island" => Mood.Island,
                _ => Mood.Menu,
            };
        }

        // ---------------------------------------------------------------- the audio thread

        internal static void OnRead(float[] data)
        {
            Mood mood = _mood;

            (int[] scale, float root, double beat, float pad) = mood switch
            {
                Mood.Island2 => (Minor, 98f, 2.4, 0.5f),
                Mood.Casino => (Major, 165f, 0.5, 0.25f),
                Mood.Menu => (Major, 131f, 2.0, 0.35f),
                _ => (Major, 147f, 1.6, 0.4f),
            };

            for (int i = 0; i < data.Length; i++)
            {
                _t += 1.0 / Rate;

                // The pad: two sines a hair apart, which beat against each other and stop it sounding
                // like a test tone. The fifth drifts in and out on a very slow sine.
                double drift = 0.5 + 0.5 * System.Math.Sin(_t * 0.07);
                _padPhaseA += 2.0 * System.Math.PI * root / Rate;
                _padPhaseB += 2.0 * System.Math.PI * (root * 1.5 * (0.999 + 0.002 * drift)) / Rate;

                var sample = (float)((System.Math.Sin(_padPhaseA) + System.Math.Sin(_padPhaseB) * 0.6)
                                     * pad * 0.28);

                // The pluck: a note from the scale, every beat or two, with rests so it breathes.
                if (_t >= _nextPluckAt)
                {
                    int step = scale[Pick.Next(scale.Length)];
                    int octave = Pick.Next(4) == 0 ? 2 : 1;

                    _pluckHz = root * octave * Mathf.Pow(2f, step / 12f);
                    _pluckUntil = _t + beat * 0.8;
                    _pluckPhase = 0.0;

                    // One beat, sometimes three. A rest is a note nobody has to write.
                    _nextPluckAt = _t + beat * (Pick.Next(3) == 0 ? 3 : 1);
                }

                if (_t < _pluckUntil)
                {
                    _pluckPhase += 2.0 * System.Math.PI * _pluckHz / Rate;

                    var age = (float)(_pluckUntil - _t);
                    float envelope = Mathf.Clamp01(age / (float)(beat * 0.8));

                    sample += (float)System.Math.Sin(_pluckPhase) * 0.22f * envelope * envelope;
                }

                data[i] = Mathf.Clamp(sample, -1f, 1f);
            }
        }
    }
}
