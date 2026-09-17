using System;
using System.Collections;
using EscapeWithYourFriends.Core;
using UnityEngine;

namespace EscapeWithYourFriends.Audio
{
    /// <summary>
    /// The acceptance tests for #80 and #81 that a terminal can settle, behind <c>-audioTest</c>.
    /// Run it solo, with or without a session; it touches nothing networked.
    ///
    /// A headless build has no audio device, so "does it sound good" is a playtest question like
    /// every other one. What is checkable is that the arithmetic produces sound at all: that every
    /// <see cref="Sound"/> builds a clip with samples in it rather than a second of silence or a wall
    /// of NaN, that the clips are built once and kept, and - the whole of #81's acceptance - that the
    /// music generator never repeats itself, which is checked by generating two stretches of it and
    /// comparing them.
    /// </summary>
    public class AudioTest : MonoBehaviour
    {
        const int Buffer = 8192;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-audioTest")) return;
            _started = true;

            var go = new GameObject("AudioTest");
            DontDestroyOnLoad(go);
            go.AddComponent<AudioTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            yield return null;

            Effects();
            Soundtrack();

            Debug.Log($"[AudioTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[AudioTest] {_failed} check(s) failed.");
        }

        void Effects()
        {
            foreach (Sound sound in Enum.GetValues(typeof(Sound)))
            {
                AudioClip clip = Sfx.Clip(sound);

                if (clip == null)
                {
                    Check($"{sound} has a sound", false);
                    continue;
                }

                var samples = new float[clip.samples];
                clip.GetData(samples, 0);

                float peak = 0f;
                bool broken = false;

                foreach (float sample in samples)
                {
                    if (float.IsNaN(sample) || Mathf.Abs(sample) > 1f) broken = true;
                    peak = Mathf.Max(peak, Mathf.Abs(sample));
                }

                Check($"{sound} is audible (peak {peak:0.00}, {clip.length:0.00}s)",
                      peak > 0.05f && clip.length > 0.02f && clip.length < 3f);
                Check($"{sound} stays inside the speaker cone", !broken);

                // Built once. A punch that rebuilt its clip on every swing would allocate a hundred
                // kilobytes in the middle of a fight.
                Check($"{sound} is kept once built", ReferenceEquals(clip, Sfx.Clip(sound)));
            }

            int before = Sfx.Played;
            Sfx.Play(Sound.Punch, Vector3.zero);
            Sfx.Play2D(Sound.Click);
            Check("playing a sound with no audio device is harmless", Sfx.Played == before + 2);
        }

        void Soundtrack()
        {
            Music.Playing = Music.Mood.Island;

            var first = new float[Buffer];
            var second = new float[Buffer];

            Music.OnRead(first);
            Music.OnRead(second);

            float peak = 0f;
            bool broken = false;

            foreach (float sample in first)
            {
                if (float.IsNaN(sample) || Mathf.Abs(sample) > 1f) broken = true;
                peak = Mathf.Max(peak, Mathf.Abs(sample));
            }

            Check($"the music generator produces sound (peak {peak:0.00})", peak > 0.05f);
            Check("and stays inside the speaker cone", !broken);

            // #81's acceptance, stated as arithmetic: two consecutive stretches are never the same
            // stretch. A loop point is exactly what this would catch.
            float difference = 0f;
            for (int i = 0; i < Buffer; i++) difference += Mathf.Abs(first[i] - second[i]);

            Check($"and never loops ({difference / Buffer:0.000} mean difference between two "
                  + "consecutive stretches)", difference / Buffer > 0.01f);

            Music.Playing = Music.Mood.Island2;

            var other = new float[Buffer];
            Music.OnRead(other);

            float moodShift = 0f;
            for (int i = 0; i < Buffer; i++) moodShift += Mathf.Abs(other[i] - second[i]);

            Check($"and the second island sounds different ({moodShift / Buffer:0.000})",
                  moodShift / Buffer > 0.01f);

            Music.Playing = Music.Mood.Menu;
        }

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[AudioTest] FAILED: {what}.");
        }
    }
}
