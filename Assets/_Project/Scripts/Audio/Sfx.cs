using System.Collections.Generic;
using UnityEngine;

namespace EscapeWithYourFriends.Audio
{
    /// <summary>The sounds the game can make. One entry per thing a player does (#80).</summary>
    public enum Sound
    {
        Punch,
        Hurt,
        Down,
        Taser,
        Zap,
        Shot,
        DryFire,
        Reload,
        Pickup,
        Coin,
        Spin,
        Crash,
        Click,
        ReelStop,
        Deal,
        Win,
        BigWin,
    }

    /// <summary>
    /// Plays <see cref="Synth"/> clips at a place in the world, or flat in the player's ears (#80).
    ///
    /// **Called from the code that already knows.** Same rule as the achievements: no event bus and
    /// no audio manager to register with. Every sound is one line inside the observers RPC or the
    /// client-side handler that was already there to make a thing look like it happened - which is
    /// also why nothing here is server-side. The host decides what happened; each machine makes its
    /// own noise about it.
    ///
    /// The pool is twelve sources: a burst of two players punching each other while a buggy lands on
    /// them is about six overlapping sounds, and a seven-reel cabinet stopping its reels under its
    /// own spin is eight. A thirteenth cutting off the oldest is what every game does anyway.
    /// </summary>
    public static class Sfx
    {
        const int Voices = 12;

        static readonly Dictionary<Sound, AudioClip> Clips = new();

        static AudioSource[] _pool;
        static int _next;

        /// <summary>Plays counted for the harness. Never read by the game.</summary>
        internal static int Played { get; private set; }

        /// <summary>The clip for a sound, built the first time anything asks for it.</summary>
        public static AudioClip Clip(Sound sound)
        {
            if (Clips.TryGetValue(sound, out AudioClip cached) && cached != null) return cached;

            AudioClip clip = Build(sound);
            Clips[sound] = clip;
            return clip;
        }

        /// <summary>Plays a sound where it happened. Everything in the world uses this.</summary>
        public static void Play(Sound sound, Vector3 position, float volume = 1f, float pitch = 1f)
            => Fire(sound, position, spatial: true, volume, pitch);

        /// <summary>Plays a sound with no position: menus, your own inventory, your own wallet.</summary>
        public static void Play2D(Sound sound, float volume = 1f, float pitch = 1f)
            => Fire(sound, Vector3.zero, spatial: false, volume, pitch);

        static void Fire(Sound sound, Vector3 position, bool spatial, float volume, float pitch)
        {
            // A headless run has no audio device and no listener. Building the clip is still worth
            // doing - that is what the harness checks - but playing it is not.
            if (!Application.isPlaying) return;

            Played++;

            AudioClip clip = Clip(sound);
            if (clip == null) return;

            AudioSource source = Take();
            if (source == null) return;

            source.transform.position = position;
            source.spatialBlend = spatial ? 1f : 0f;
            source.volume = Mathf.Clamp01(volume);

            // A little variation, because the same punch four times in two seconds stops being funny
            // and starts being a bug report.
            source.pitch = Mathf.Clamp(pitch * Random.Range(0.92f, 1.09f), 0.1f, 3f);
            source.clip = clip;
            source.Play();
        }

        static AudioSource Take()
        {
            if (_pool == null)
            {
                var host = new GameObject("Sfx");
                Object.DontDestroyOnLoad(host);

                _pool = new AudioSource[Voices];
                for (int i = 0; i < Voices; i++)
                {
                    var go = new GameObject($"Voice {i}");
                    go.transform.SetParent(host.transform);

                    AudioSource source = go.AddComponent<AudioSource>();
                    source.playOnAwake = false;
                    source.rolloffMode = AudioRolloffMode.Linear;
                    source.minDistance = 4f;
                    source.maxDistance = 60f;
                    _pool[i] = source;
                }
            }

            AudioSource chosen = _pool[_next];
            _next = (_next + 1) % Voices;
            return chosen;
        }

        // ---------------------------------------------------------------- the sounds themselves

        static readonly float[] WinNotes = { 523f, 659f, 784f, 1047f };
        static readonly float[] HighNotes = { 659f, 784f, 1047f, 1319f };

        /// <summary>Notes one after another, <paramref name="step"/> seconds each, the last one ringing on.</summary>
        static float Arpeggio(float t, float step, float[] notes, float volume)
        {
            int n = Mathf.Min(notes.Length - 1, (int)(t / step));
            float local = t - n * step;
            bool last = n == notes.Length - 1;
            float hz = notes[n];

            return (Synth.Sine(t, hz) * 0.7f + Synth.Sine(t, hz * 2f) * 0.2f) * volume
                   * Synth.Decay(local, last ? 0.3f : step, last ? 3f : 2f);
        }

        static AudioClip Build(Sound sound)
        {
            switch (sound)
            {
                // A body hit: low thump plus a slap of noise. Comedy lives in the pitch drop.
                case Sound.Punch:
                    return Synth.Clip("punch", 0.22f, t =>
                        (Synth.Sine(t, 150f - 90f * t / 0.22f) * 0.8f + Synth.White() * 0.5f)
                        * Synth.Decay(t, 0.22f, 9f));

                case Sound.Hurt:
                    return Synth.Clip("hurt", 0.30f, t =>
                        Synth.Square(t, 230f + 120f * Mathf.Sin(t * 30f)) * 0.35f
                        * Synth.Decay(t, 0.30f, 6f));

                // Going down: a descending honk, the noise a cartoon makes falling over.
                case Sound.Down:
                    return Synth.Clip("down", 0.75f, t =>
                        Synth.Square(t, 300f - 230f * t / 0.75f) * 0.32f
                        * Synth.Decay(t, 0.75f, 2.2f));

                case Sound.Taser:
                    return Synth.Clip("taser", 0.45f, t =>
                        Synth.Square(t, 70f) * Synth.Square(t, 640f) * 0.30f
                        * (0.6f + 0.4f * Synth.Sine(t, 13f)));

                case Sound.Zap:
                    return Synth.Clip("zap", 0.35f, t =>
                        (Synth.White() * 0.6f + Synth.Square(t, 820f) * 0.4f)
                        * Synth.Decay(t, 0.35f, 7f));

                case Sound.Shot:
                    return Synth.Clip("shot", 0.40f, t =>
                        (Synth.White() * 0.9f + Synth.Sine(t, 90f - 50f * t / 0.40f) * 0.6f)
                        * Synth.Decay(t, 0.40f, 11f));

                case Sound.DryFire:
                    return Synth.Clip("dry", 0.08f, t =>
                        Synth.White() * 0.5f * Synth.Decay(t, 0.08f, 20f));

                case Sound.Reload:
                    return Synth.Clip("reload", 0.30f, t =>
                        Synth.White() * 0.45f
                        * (Synth.Decay(t, 0.12f, 16f) + 0.8f * Synth.Decay(Mathf.Max(0f, t - 0.16f), 0.12f, 16f)));

                case Sound.Pickup:
                    return Synth.Clip("pickup", 0.14f, t =>
                        Synth.Sine(t, 520f + 700f * t / 0.14f) * 0.35f * Synth.Decay(t, 0.14f, 8f));

                // Money: two clean tones, which is every arcade machine ever built.
                case Sound.Coin:
                    return Synth.Clip("coin", 0.22f, t =>
                        (t < 0.06f ? Synth.Sine(t, 880f) : Synth.Sine(t, 1320f)) * 0.3f
                        * Synth.Decay(t, 0.22f, 5f));

                case Sound.Spin:
                    return Synth.Clip("spin", 1.20f, t =>
                        Synth.Square(t, 40f + 150f * Mathf.Exp(-2f * t)) * 0.18f
                        * (1f - t / 1.25f));

                case Sound.Crash:
                    return Synth.Clip("crash", 0.55f, t =>
                        (Synth.White() * 0.8f + Synth.Sine(t, 70f) * 0.7f)
                        * Synth.Decay(t, 0.55f, 5f));

                case Sound.Click:
                    return Synth.Clip("click", 0.06f, t =>
                        Synth.Sine(t, 1200f) * 0.25f * Synth.Decay(t, 0.06f, 18f));

                // A reel landing: a low thunk with a click on its front edge.
                case Sound.ReelStop:
                    return Synth.Clip("reelstop", 0.12f, t =>
                        (Synth.Sine(t, 120f - 50f * t / 0.12f) * 0.7f + Synth.White() * 0.35f * Synth.Decay(t, 0.03f, 6f))
                        * Synth.Decay(t, 0.12f, 7f));

                // A card snapped onto felt.
                case Sound.Deal:
                    return Synth.Clip("deal", 0.09f, t =>
                        (Synth.White() * 0.5f + Synth.Sine(t, 2400f) * 0.15f) * Synth.Decay(t, 0.09f, 14f));

                // Any win: a major arpeggio up, the last note left to ring.
                case Sound.Win:
                    return Synth.Clip("win", 0.60f, t => Arpeggio(t, 0.09f, WinNotes, 0.32f));

                // A big win: the arpeggio twice, a step higher the second time, then a held chord that
                // shimmers, with sparkles over the top. Under two seconds, so the banner outlasts it.
                case Sound.BigWin:
                    return Synth.Clip("bigwin", 1.80f, t =>
                    {
                        if (t < 0.32f) return Arpeggio(t, 0.08f, WinNotes, 0.3f) * Synth.Decay(t, 0.32f, 0.5f);
                        if (t < 0.64f) return Arpeggio(t - 0.32f, 0.08f, HighNotes, 0.3f) * Synth.Decay(t - 0.32f, 0.32f, 0.5f);

                        float held = t - 0.64f;
                        float shimmer = 0.75f + 0.25f * Synth.Sine(held, 9f);
                        float chord = (Synth.Sine(t, 1047f) + Synth.Sine(t, 1319f) + Synth.Sine(t, 1568f)) * 0.22f;
                        float sparkle = Synth.Sine(t, 3000f + 1000f * Mathf.Sin(t * 37f)) * 0.06f * (Synth.Sine(t, 11f) > 0.6f ? 1f : 0f);
                        return (chord * shimmer + sparkle) * Synth.Decay(held, 1.16f, 2.5f);
                    });

                default:
                    return null;
            }
        }
    }
}
