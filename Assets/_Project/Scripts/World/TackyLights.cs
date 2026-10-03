using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The casino's lighting, from #65. Each lamp drifts a little round its own colour and breathes its
    /// brightness, out of step with the others.
    ///
    /// #65 cycled every lamp round the whole colour wheel: a room that would not sit still, which sold
    /// "wired by castaways". #252 gave the room neon, gold and a polished floor, and a lamp that turns
    /// green under a magenta sign fights it. So each lamp keeps the colour GreyboxBuilder gave it, the
    /// colour of the neon nearest it, and only wanders a few degrees of hue either side. The room still
    /// moves; it no longer argues with itself.
    ///
    /// Nothing here is networked and nothing here is timed against anything. Every peer runs its own
    /// drift off its own clock.
    /// </summary>
    public class TackyLights : MonoBehaviour
    {
        [SerializeField] Light[] _lights = new Light[0];

        [Tooltip("Seconds for a lamp to drift there and back.")]
        [Min(1f)] [SerializeField] float _cycle = 14f;

        [Tooltip("How far round the colour wheel a lamp wanders either side of its own colour.")]
        [Range(0f, 0.5f)] [SerializeField] float _drift = 0.05f;

        [Tooltip("How far the brightness swings either side of the lamp's own. 0 is a still room.")]
        [Range(0f, 1f)] [SerializeField] float _flicker = 0.15f;

        float[] _base, _offset, _hue, _saturation;

        void Awake()
        {
            // No graphics device, no lights worth animating. A headless host runs this scene too.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                enabled = false;
                return;
            }

            int n = _lights.Length;
            (_base, _offset, _hue, _saturation) = (new float[n], new float[n], new float[n], new float[n]);

            for (int i = 0; i < n; i++)
            {
                if (_lights[i] == null) continue;

                _base[i] = _lights[i].intensity;
                Color.RGBToHSV(_lights[i].color, out _hue[i], out _saturation[i], out _);

                // Spread round the cycle rather than randomised, so they never all move together.
                _offset[i] = (float)i / Mathf.Max(1, n);
            }
        }

        void Update()
        {
            for (int i = 0; i < _lights.Length; i++)
            {
                Light lamp = _lights[i];
                if (lamp == null) continue;

                float phase = (Time.time / _cycle + _offset[i]) * Mathf.PI * 2f;
                lamp.color = Color.HSVToRGB(Mathf.Repeat(_hue[i] + Mathf.Sin(phase) * _drift, 1f), _saturation[i], 1f);

                float pulse = Mathf.Sin((Time.time + _offset[i] * 7f) * 3.1f);
                lamp.intensity = _base[i] * (1f + pulse * _flicker);
            }
        }

        /// <summary>Editor-time setup. See <c>GreyboxBuilder</c>.</summary>
        public void Configure(Light[] lights) => _lights = lights ?? new Light[0];
    }
}
