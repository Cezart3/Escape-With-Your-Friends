using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// Drives the sun, the ambient light, the fog and the sky from <see cref="WorldClock"/>.
    ///
    /// One directional light does both jobs. When the sun goes under, the same light flips to face
    /// the other way, takes the moon colour and drops to a fraction of the intensity. Two lights
    /// would mean URP picking a main light every frame and shadows switching between them; one light
    /// that turns around is the standard trick and it costs nothing.
    ///
    /// The whole thing is a pure function of the time of day, which is a pure function of the
    /// FishNet tick. Nothing about the sky is replicated, and four players still watch the same
    /// sunset - see <see cref="WorldClock"/> for why that is not luck.
    /// </summary>
    [ExecuteAlways]
    public class DayNightCycle : MonoBehaviour
    {
        static readonly int SkyTintId = Shader.PropertyToID("_SkyTint");
        static readonly int GroundColorId = Shader.PropertyToID("_GroundColor");
        static readonly int ExposureId = Shader.PropertyToID("_Exposure");
        static readonly int AtmosphereId = Shader.PropertyToID("_AtmosphereThickness");

        [Tooltip("Where every colour and every number comes from.")]
        public DayNightProfile Profile;

        [Tooltip("The one directional light. It is the sun by day and the moon by night.")]
        public Light Sun;

        [Tooltip("Skybox material to drive. A runtime copy is made, so the asset on disk is never touched.")]
        public Material Sky;

        [Tooltip("How much light the ground bounces back at night, as a fraction of the day value.")]
        [Range(0f, 1f)] public float NightGroundBounce = 0.4f;

        /// <summary>Sunlight level at which the moon has faded to nothing. Purely a crossfade width.</summary>
        const float Handover = 0.3f;

        Material _skyInstance;
        float _lastApplied = -1f;

        /// <summary>What the cycle is currently showing, for the HUD and for tests.</summary>
        public float TimeOfDay { get; private set; }

        void OnEnable()
        {
            PushProfile();
            Apply(WorldClock.Normalized, true);
        }

        void OnDisable()
        {
            if (_skyInstance == null) return;

            if (RenderSettings.skybox == _skyInstance) RenderSettings.skybox = Sky;
            if (Application.isPlaying) Destroy(_skyInstance);
            else DestroyImmediate(_skyInstance);
            _skyInstance = null;
        }

        void LateUpdate()
        {
            PushProfile();
            Apply(WorldClock.Normalized, false);
        }

        void PushProfile()
        {
            if (Profile == null) return;
            WorldClock.CycleSeconds = Profile.CycleSeconds;
            WorldClock.StartOfDay = Profile.StartOfDay;
        }

        /// <summary>
        /// Everything the sky does, at one instant. Public because the only way to test lighting in a
        /// build with no screen is to ask it what it would look like at a given hour.
        /// </summary>
        public void Apply(float timeOfDay, bool force)
        {
            if (Profile == null) return;

            // A whole day is twenty minutes and the sun crosses 360 degrees in that time, so a
            // thousandth of a cycle is about a degree. Below that there is nothing to see and no
            // reason to touch RenderSettings, which is not free.
            if (!force && Mathf.Abs(Mathf.DeltaAngle(timeOfDay * 360f, _lastApplied * 360f)) < 0.35f) return;

            _lastApplied = timeOfDay;
            TimeOfDay = timeOfDay;

            float sunlight = Mathf.Max(0f, Profile.SunIntensity.Evaluate(timeOfDay));

            // The moon fades out as the sun comes up rather than switching off, so the handover
            // happens at the moment the two are equally bright. Swapping on the horizon crossing
            // instead - the obvious rule - swings every shadow in the world through 180 degrees in
            // one frame, at sunrise, in full view.
            float moonlight = Profile.MoonIntensity * (1f - Mathf.Clamp01(sunlight / Handover));
            bool day = sunlight >= moonlight;

            if (Sun != null)
            {
                Quaternion rotation = Profile.SunRotation(timeOfDay);

                // Under the horizon the light is turned around to come from where the moon would be.
                // It is the same object, so nothing has to hand over shadow cascades mid-frame.
                Sun.transform.rotation = day ? rotation : rotation * Quaternion.Euler(180f, 0f, 0f);

                Sun.color = day ? Profile.SunColour.Evaluate(timeOfDay) : Profile.MoonColour;
                Sun.intensity = day ? sunlight : moonlight;
                Sun.shadowStrength = day ? 1f : Profile.MoonShadowStrength;

                // A light with no intensity still costs a shadow pass. Twilight is where this saves
                // the most, because that is also when the shadows are longest and most expensive.
                Sun.shadows = Sun.intensity > 0.02f ? LightShadows.Soft : LightShadows.None;
            }

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Profile.AmbientSky.Evaluate(timeOfDay);
            RenderSettings.ambientEquatorColor = Profile.AmbientEquator.Evaluate(timeOfDay);

            Color ground = Profile.AmbientGround.Evaluate(timeOfDay);
            RenderSettings.ambientGroundColor = day ? ground : ground * NightGroundBounce;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = Profile.FogColour.Evaluate(timeOfDay);
            RenderSettings.fogDensity = Mathf.Max(0f, Profile.FogDensity.Evaluate(timeOfDay));

            ApplySky(timeOfDay);
        }

        void ApplySky(float timeOfDay)
        {
            if (Sky == null) return;

            // The material on disk is a versioned asset. Writing tint and exposure into it every
            // frame would leave the repo permanently dirty at whatever time of day the editor was
            // last closed, so the running game gets a copy and the asset is never written to.
            if (_skyInstance == null)
            {
                _skyInstance = new Material(Sky) { name = Sky.name + " (runtime)" };
                _skyInstance.hideFlags = HideFlags.HideAndDontSave;
            }

            Color tint = Profile.SkyTint.Evaluate(timeOfDay);
            if (_skyInstance.HasProperty(SkyTintId)) _skyInstance.SetColor(SkyTintId, tint);
            if (_skyInstance.HasProperty(GroundColorId))
                _skyInstance.SetColor(GroundColorId, Profile.AmbientGround.Evaluate(timeOfDay));
            if (_skyInstance.HasProperty(ExposureId))
                _skyInstance.SetFloat(ExposureId, Mathf.Max(0f, Profile.SkyExposure.Evaluate(timeOfDay)));
            if (_skyInstance.HasProperty(AtmosphereId))
                _skyInstance.SetFloat(AtmosphereId, Mathf.Clamp(Profile.AtmosphereThickness.Evaluate(timeOfDay), 0f, 5f));

            RenderSettings.skybox = _skyInstance;
            RenderSettings.sun = Sun;
        }

        /// <summary>
        /// One line describing the sky right now. Called from the batchmode verification and from
        /// the smoke test, because "is it dark at night" has to be answerable without a screen.
        /// </summary>
        public string Describe()
        {
            if (Profile == null) return "no profile";

            float ambient = RenderSettings.ambientSkyColor.grayscale;
            float sunlight = Mathf.Max(0f, Profile.SunIntensity.Evaluate(TimeOfDay));
            string body = sunlight >= Profile.MoonIntensity * (1f - Mathf.Clamp01(sunlight / Handover))
                ? "sun" : "moon";
            Vector3 direction = Sun != null ? Sun.transform.forward : Vector3.zero;

            return $"{WorldClock.Clock24} (t={TimeOfDay:F3}) {body} "
                   + $"intensity {(Sun != null ? Sun.intensity : 0f):F3}, "
                   + $"elevation {(Sun != null ? -Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg : 0f):F1}deg, "
                   + $"ambient {ambient:F3}, fog {RenderSettings.fogDensity:F4}";
        }
    }
}
