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
        static readonly int SkyTopId = Shader.PropertyToID("_SkyTop");
        static readonly int SkyHorizonId = Shader.PropertyToID("_SkyHorizon");
        static readonly int SkyGroundId = Shader.PropertyToID("_SkyGround");
        static readonly int SunColourId = Shader.PropertyToID("_SunColour");
        static readonly int SunDirId = Shader.PropertyToID("_SunDir");
        static readonly int MoonColourId = Shader.PropertyToID("_MoonColour");
        static readonly int CloudLitId = Shader.PropertyToID("_CloudLit");
        static readonly int CloudShadeId = Shader.PropertyToID("_CloudShade");
        static readonly int CloudCoverId = Shader.PropertyToID("_CloudCover");
        static readonly int StarsId = Shader.PropertyToID("_Stars");
        static readonly int HeightFogId = Shader.PropertyToID("_HeightFog");

        [Tooltip("Where every colour and every number comes from.")]
        public DayNightProfile Profile;

        [Tooltip("The one directional light. It is the sun by day and the moon by night.")]
        public Light Sun;

        [Tooltip("Skybox material to drive. A runtime copy is made, so the asset on disk is never touched.")]
        public Material Sky;

        [Tooltip("How much light the ground bounces back at night, as a fraction of the day value.")]
        [Range(0f, 1f)] public float NightGroundBounce = 0.4f;

        /// <summary>
        /// Multiplier on the fog the profile asks for. One island, one climate: the sky profile is
        /// shared, and this is the whole of what #68 means by "worse weather" on the second island -
        /// the same day turned murkier, so you see a headhunter at the distance it sees you.
        /// </summary>
        public float FogScale = 1f;

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

            // RenderSettings belong to the active scene, and the network scene loads make another one
            // active after this has written the sky. A moving clock rewrites it within a second; a
            // frozen one (-timeOfDay) never would, so a lost sky forces it.
            Apply(WorldClock.Normalized, Sky != null && RenderSettings.skybox != _skyInstance);
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
                Quaternion rotation = Profile.LightRotation(timeOfDay);

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
            RenderSettings.fogDensity =
                Mathf.Max(0f, Profile.FogDensity.Evaluate(timeOfDay) * Mathf.Max(0.01f, FogScale));

            Shader.SetGlobalVector(HeightFogId, new Vector4(
                Mathf.Max(0f, Profile.HeightFogDensity.Evaluate(timeOfDay) * Mathf.Max(0.01f, FogScale)),
                IslandShape.SeaLevel, Mathf.Max(0.001f, Profile.HeightFogFalloff), 0f));

            ApplySky(timeOfDay, sunlight, moonlight);
        }

        void ApplySky(float timeOfDay, float sunlight, float moonlight)
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

            // The horizon is the fog, so the fogged world and the sky meet without a seam. Below it is
            // the fog too: the far sea is fully fogged, and anything else drew the edge of the water
            // plane as a slab against a darker band (the first #243 overlook shots).
            Color fog = Profile.FogColour.Evaluate(timeOfDay);
            Color sun = Profile.SunColour.Evaluate(timeOfDay);
            Color ambient = Profile.AmbientSky.Evaluate(timeOfDay);
            float night = 1f - Mathf.Clamp01(sunlight / Handover);

            _skyInstance.SetColor(SkyTopId, Profile.SkyTint.Evaluate(timeOfDay));
            _skyInstance.SetColor(SkyHorizonId, fog);
            _skyInstance.SetColor(SkyGroundId, fog);
            _skyInstance.SetColor(SunColourId, sun * Mathf.Clamp01(sunlight * 1.5f));
            _skyInstance.SetVector(SunDirId, -(Profile.SunRotation(timeOfDay) * Vector3.forward));
            _skyInstance.SetColor(MoonColourId, Profile.MoonColour * (0.6f + moonlight * 3f));
            _skyInstance.SetFloat(StarsId, night);
            _skyInstance.SetFloat(CloudCoverId, Profile.CloudCover);

            // Clouds are lit by the sun on top and by the sky underneath, so they go gold at dusk and
            // slate at night without a colour table of their own.
            _skyInstance.SetColor(CloudLitId, sun * Mathf.Min(1.1f, sunlight * 0.85f) + ambient * 1.2f + fog * 0.25f);
            _skyInstance.SetColor(CloudShadeId, ambient * 1.1f + fog * 0.45f);

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
