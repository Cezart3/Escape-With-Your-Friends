using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The casino's lighting, from #65. Cycles a handful of coloured lamps through a slow chase and
    /// breathes their brightness, out of step with each other.
    ///
    /// **The point is that it is bad.** A casino built by four people stranded on an island has
    /// whatever lights washed up, wired by somebody who was not an electrician, and the thing that
    /// sells that is not the colour of the boxes - it is a room that will not sit still. Rooms in
    /// this game are lit and left alone; this one is the exception, and it is the whole reason the
    /// building reads as a casino rather than as another shack.
    ///
    /// Nothing here is networked and nothing here is timed against anything. Every peer runs its own
    /// chase off its own clock, because two players seeing slightly different shades of magenta is
    /// not a bug anybody can have.
    /// </summary>
    public class TackyLights : MonoBehaviour
    {
        [SerializeField] Light[] _lights = new Light[0];

        [Tooltip("Seconds for a lamp to work its way round the colour wheel.")]
        [Min(1f)] [SerializeField] float _cycle = 14f;

        [Tooltip("How far the brightness swings either side of the lamp's own. 0 is a still room.")]
        [Range(0f, 1f)] [SerializeField] float _flicker = 0.35f;

        float[] _base;
        float[] _offset;

        void Awake()
        {
            // No graphics device, no lights worth animating. A headless host runs this scene too.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                enabled = false;
                return;
            }

            _base = new float[_lights.Length];
            _offset = new float[_lights.Length];

            for (int i = 0; i < _lights.Length; i++)
            {
                if (_lights[i] == null) continue;

                _base[i] = _lights[i].intensity;

                // Spread the lamps round the cycle rather than randomising: a random offset would
                // occasionally put them all in phase, which reads as one deliberate colour instead
                // of four arguing.
                _offset[i] = (float)i / Mathf.Max(1, _lights.Length);
            }
        }

        void Update()
        {
            if (_lights == null) return;

            for (int i = 0; i < _lights.Length; i++)
            {
                Light lamp = _lights[i];
                if (lamp == null) continue;

                float t = Mathf.Repeat(Time.time / _cycle + _offset[i], 1f);
                lamp.color = Color.HSVToRGB(t, 0.75f, 1f);

                float pulse = Mathf.Sin((Time.time + _offset[i] * 7f) * 3.1f);
                lamp.intensity = _base[i] * (1f + pulse * _flicker);
            }
        }

        /// <summary>Editor-time setup. See <c>GreyboxBuilder</c>.</summary>
        public void Configure(Light[] lights) => _lights = lights ?? new Light[0];
    }
}
