using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// Frame times, measured rather than guessed at.
    ///
    /// #38 asks for sixty frames a second at 1080p on a Radeon 760M. Nothing in this repository can
    /// answer that question - the machine that writes the code is not the machine that has to run it,
    /// and a headless build has no frames at all. So the deliverable is the instrument: whoever has
    /// the integrated GPU runs the game, and the log says what happened in numbers that can be pasted
    /// back.
    ///
    /// Average frame rate on its own is close to useless for a game. A run that averages 62 fps and
    /// stutters to 14 twice a second feels far worse than a flat 45, and the average hides it
    /// completely. So the number that matters here is the **1% low**: the mean of the worst one
    /// percent of frames in the window. That is the one to quote.
    /// </summary>
    public class PerfProbe : MonoBehaviour
    {
        /// <summary>The probe for this process, or null when it was never asked for.</summary>
        public static PerfProbe Instance { get; private set; }

        const int Capacity = 4096;

        readonly float[] _frames = new float[Capacity];
        int _count;
        int _write;

        float _reportEvery;
        float _nextReport;
        float _smoothed;

        // Kept across reports so the summary describes the session and not just the last window.
        int _sessionFrames;
        double _sessionMillis;
        float _sessionWorst;

        /// <summary>Smoothed frames per second, for anything that puts a number on the screen.</summary>
        public float Fps => _smoothed > 0f ? 1000f / _smoothed : 0f;

        /// <summary>Milliseconds for the slowest 1% of the current window. The number that matters.</summary>
        public float OnePercentLow { get; private set; }

        internal static void Start(float reportEvery)
        {
            if (Instance != null) return;

            var go = new GameObject("PerfProbe");
            DontDestroyOnLoad(go);

            Instance = go.AddComponent<PerfProbe>();
            Instance._reportEvery = reportEvery;
            Instance._nextReport = Time.realtimeSinceStartup + reportEvery;

            Debug.Log($"[PerfProbe] {Describe()}");
        }

        /// <summary>What the numbers below were measured on. Useless without it.</summary>
        public static string Describe()
        {
            string device = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
                ? "no GPU (headless)"
                : $"{SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize}MB)";

            return $"{device}, {Screen.width}x{Screen.height}"
                   + (Screen.fullScreen ? " fullscreen" : " windowed")
                   + $", quality '{QualitySettings.names[QualitySettings.GetQualityLevel()]}'"
                   + $", vsync {QualitySettings.vSyncCount}, {SystemInfo.processorType}";
        }

        void Update()
        {
            float millis = Time.unscaledDeltaTime * 1000f;

            // A ring rather than a cap: a window that fills up should describe its last few
            // thousand frames, not silently stop looking after the first few thousand.
            _frames[_write] = millis;
            _write = (_write + 1) % Capacity;
            if (_count < Capacity) _count++;

            _sessionFrames++;
            _sessionMillis += millis;
            if (millis > _sessionWorst) _sessionWorst = millis;

            // A short exponential average for the screen, because a counter that jitters by twenty is
            // unreadable, and a long one lies about the stutter you just felt.
            _smoothed = _smoothed <= 0f ? millis : Mathf.Lerp(_smoothed, millis, 0.1f);

            if (_reportEvery <= 0f || Time.realtimeSinceStartup < _nextReport) return;

            _nextReport = Time.realtimeSinceStartup + _reportEvery;
            Report();
        }

        void Report()
        {
            if (_count == 0) return;

            var window = new float[_count];
            Array.Copy(_frames, window, _count);
            Array.Sort(window);

            double sum = 0d;
            foreach (float frame in window) sum += frame;

            float average = (float)(sum / _count);
            float median = window[_count / 2];

            // Mean of the worst one percent, never fewer than one frame.
            int worstCount = Mathf.Max(1, _count / 100);
            double worstSum = 0d;
            for (int i = _count - worstCount; i < _count; i++) worstSum += window[i];
            OnePercentLow = (float)(worstSum / worstCount);

            Debug.Log($"[PerfProbe] {_count} frames: {1000f / average:F0} fps average "
                      + $"({average:F1}ms), {1000f / median:F0} median, "
                      + $"1% low {1000f / OnePercentLow:F0} fps ({OnePercentLow:F1}ms), "
                      + $"worst {window[_count - 1]:F1}ms.");

            _count = 0;
            _write = 0;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_sessionFrames == 0) return;

            float average = (float)(_sessionMillis / _sessionFrames);
            Debug.Log($"[PerfProbe] Session: {_sessionFrames} frames, {1000f / average:F0} fps average "
                      + $"({average:F1}ms), worst single frame {_sessionWorst:F0}ms. {Describe()}");
        }
    }
}
