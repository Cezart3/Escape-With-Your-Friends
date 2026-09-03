using System.IO;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.World;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Generates the sky: the procedural skybox material, and the day/night profile that drives it.
    ///
    /// The gradients are written here rather than typed into an inspector because a Unity
    /// <see cref="Gradient"/> serialises as a block of packed key data that is unreadable in YAML and
    /// unmergeable in git. Written from code they are eight lines anybody can argue with, and the
    /// asset they produce is still a plain text file that can be hand-tuned afterwards.
    ///
    /// Created once and then left alone, like every other look asset here: the whole point of the
    /// profile is that a human tunes it. Pass -rebuildSky to throw the tuning away and start again
    /// from these numbers.
    /// </summary>
    public static class SkyFactory
    {
        public const string SkyFolder = "Assets/_Project/Art/Sky";
        public const string SkyMaterialPath = SkyFolder + "/Sky.mat";
        public const string ProfilePath = "Assets/_Project/Data/DayNight.asset";

        /// <summary>The profile, created from the numbers below if it is not on disk yet.</summary>
        public static DayNightProfile EnsureProfile()
        {
            bool rebuild = CommandLine.HasFlag("-rebuildSky");
            var profile = AssetDatabase.LoadAssetAtPath<DayNightProfile>(ProfilePath);

            if (profile != null && !rebuild) return profile;

            bool fresh = profile == null;
            if (fresh) profile = ScriptableObject.CreateInstance<DayNightProfile>();

            profile.CycleMinutes = 20f;
            profile.StartOfDay = 0.28f;
            profile.SunAzimuth = -30f;
            profile.SunTilt = 18f;

            // Sunlight. Deep orange on the horizon, white at noon. The keys are clustered around
            // sunrise and sunset because that is where all the change happens; the middle of the day
            // and the middle of the night are both flat.
            profile.SunColour = Gradient(
                (0.00f, new Color(0.16f, 0.20f, 0.35f)),
                (0.23f, new Color(0.35f, 0.28f, 0.34f)),
                (0.27f, new Color(1.00f, 0.55f, 0.28f)),
                (0.34f, new Color(1.00f, 0.86f, 0.70f)),
                (0.50f, new Color(1.00f, 0.97f, 0.90f)),
                (0.68f, new Color(1.00f, 0.83f, 0.62f)),
                (0.75f, new Color(1.00f, 0.46f, 0.22f)),
                (1.00f, new Color(0.16f, 0.20f, 0.35f)));

            profile.SunIntensity = Curve(
                (0.00f, 0f), (0.23f, 0f), (0.27f, 0.35f), (0.34f, 1.00f),
                (0.50f, 1.25f), (0.66f, 1.00f), (0.73f, 0.35f), (0.77f, 0f), (1.00f, 0f));

            profile.MoonColour = new Color(0.52f, 0.64f, 0.95f);

            // Night has to be dark enough that a flashlight is worth carrying, and no darker: a
            // black screen is not tense, it is a bug report. 0.14 is enough to make out a treeline.
            profile.MoonIntensity = 0.14f;
            profile.MoonShadowStrength = 0.35f;

            // Ambient is the real lever on how dark night feels. Direct light only touches what it
            // hits; ambient is what fills every shadow, and a night with bright ambient looks like an
            // overcast afternoon with a blue filter no matter what the sun is doing.
            profile.AmbientSky = Gradient(
                (0.00f, new Color(0.020f, 0.026f, 0.048f)),
                (0.22f, new Color(0.045f, 0.055f, 0.090f)),
                (0.28f, new Color(0.32f, 0.30f, 0.34f)),
                (0.50f, new Color(0.46f, 0.60f, 0.80f)),
                (0.70f, new Color(0.42f, 0.46f, 0.60f)),
                (0.76f, new Color(0.30f, 0.24f, 0.28f)),
                (0.83f, new Color(0.045f, 0.055f, 0.090f)),
                (1.00f, new Color(0.020f, 0.026f, 0.048f)));

            profile.AmbientEquator = Gradient(
                (0.00f, new Color(0.016f, 0.020f, 0.036f)),
                (0.26f, new Color(0.26f, 0.20f, 0.20f)),
                (0.50f, new Color(0.52f, 0.54f, 0.52f)),
                (0.74f, new Color(0.30f, 0.20f, 0.18f)),
                (1.00f, new Color(0.016f, 0.020f, 0.036f)));

            profile.AmbientGround = Gradient(
                (0.00f, new Color(0.010f, 0.012f, 0.018f)),
                (0.30f, new Color(0.16f, 0.15f, 0.12f)),
                (0.50f, new Color(0.26f, 0.24f, 0.19f)),
                (0.72f, new Color(0.16f, 0.14f, 0.11f)),
                (1.00f, new Color(0.010f, 0.012f, 0.018f)));

            // Fog is matched to the sky at every hour, because the horizon is where the two meet and
            // a mismatch there draws a hard line across the sea.
            profile.FogColour = Gradient(
                (0.00f, new Color(0.035f, 0.045f, 0.075f)),
                (0.24f, new Color(0.30f, 0.26f, 0.32f)),
                (0.30f, new Color(0.72f, 0.64f, 0.58f)),
                (0.50f, new Color(0.68f, 0.78f, 0.88f)),
                (0.70f, new Color(0.72f, 0.66f, 0.60f)),
                (0.78f, new Color(0.26f, 0.20f, 0.26f)),
                (1.00f, new Color(0.035f, 0.045f, 0.075f)));

            // Thicker at the edges of the day and at night. It hides the draw distance, it is free,
            // and it is the oldest trick in the book for making a small island feel large.
            profile.FogDensity = Curve(
                (0.00f, 0.0060f), (0.25f, 0.0075f), (0.40f, 0.0035f),
                (0.60f, 0.0035f), (0.76f, 0.0075f), (1.00f, 0.0060f));

            profile.SkyTint = Gradient(
                (0.00f, new Color(0.06f, 0.08f, 0.16f)),
                (0.25f, new Color(0.42f, 0.30f, 0.34f)),
                (0.34f, new Color(0.52f, 0.58f, 0.72f)),
                (0.50f, new Color(0.54f, 0.66f, 0.86f)),
                (0.70f, new Color(0.52f, 0.56f, 0.70f)),
                (0.77f, new Color(0.44f, 0.26f, 0.28f)),
                (1.00f, new Color(0.06f, 0.08f, 0.16f)));

            // Exposure is what actually makes night dark rather than merely blue. The procedural sky
            // is lit by the sun's elevation, so at midnight it is already dim; this takes it the rest
            // of the way without touching the day.
            profile.SkyExposure = Curve(
                (0.00f, 0.16f), (0.22f, 0.22f), (0.30f, 1.05f),
                (0.50f, 1.30f), (0.70f, 1.05f), (0.80f, 0.24f), (1.00f, 0.16f));

            // Thick air at the horizons is what scatters sunrise red. Thin at night so the sky goes
            // properly black instead of navy.
            profile.AtmosphereThickness = Curve(
                (0.00f, 0.55f), (0.24f, 1.85f), (0.30f, 1.30f),
                (0.50f, 0.95f), (0.72f, 1.35f), (0.78f, 1.85f), (1.00f, 0.55f));

            if (fresh)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath));
                AssetDatabase.CreateAsset(profile, ProfilePath);
                Debug.Log($"[SkyFactory] Generated {ProfilePath}.");
            }
            else
            {
                EditorUtility.SetDirty(profile);
                Debug.Log($"[SkyFactory] Rebuilt {ProfilePath} from code, tuning discarded (-rebuildSky).");
            }

            return profile;
        }

        /// <summary>
        /// The skybox material. Unity's own procedural sky: it takes a tint, an exposure and an
        /// atmosphere thickness, works out the rest from where the sun is pointing, and costs one
        /// full-screen pass of arithmetic with no cubemap to load. Nothing hand-painted here would
        /// look better on an integrated GPU.
        /// </summary>
        public static Material EnsureSkyMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(SkyMaterialPath);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Skybox/Procedural");
            if (shader == null)
            {
                Debug.LogError("[SkyFactory] Shader 'Skybox/Procedural' not found; the scene will keep the default sky.");
                return null;
            }

            Directory.CreateDirectory(SkyFolder);

            var material = new Material(shader) { name = "Sky" };
            material.SetFloat("_SunDisk", 2f);          // high quality: a disk with a bloom around it
            material.SetFloat("_SunSize", 0.035f);
            material.SetFloat("_SunSizeConvergence", 6f);
            material.SetColor("_SkyTint", new Color(0.54f, 0.66f, 0.86f));
            material.SetColor("_GroundColor", new Color(0.26f, 0.24f, 0.19f));
            material.SetFloat("_AtmosphereThickness", 0.95f);
            material.SetFloat("_Exposure", 1.3f);

            AssetDatabase.CreateAsset(material, SkyMaterialPath);
            Debug.Log($"[SkyFactory] Generated {SkyMaterialPath}.");
            return material;
        }

        /// <summary>
        /// What the sky does across a whole day, printed. This is the acceptance test for #34 in a
        /// build with no screen: the sun has to rise, set and come back, and the night has to be
        /// measurably darker than the day rather than merely a different colour.
        /// </summary>
        public static void Report(DayNightCycle cycle)
        {
            if (cycle == null || cycle.Profile == null) return;

            float[] samples = { 0f, 0.125f, 0.24f, 0.28f, 0.35f, 0.5f, 0.7f, 0.76f, 0.82f, 0.875f };
            float night = 0f;
            float day = float.MaxValue;

            foreach (float sample in samples)
            {
                WorldClock.Freeze(sample);
                cycle.Apply(sample, true);

                float lit = Lit(cycle);

                // Twilight is neither, and including it in either number turns a real measurement
                // into a meaningless one: dusk is dark and it is also not night.
                float height = cycle.Profile.SunHeight(sample);
                if (height > 0.3f) day = Mathf.Min(day, lit);
                else if (height < -0.3f) night = Mathf.Max(night, lit);

                Debug.Log($"[SkyFactory] {cycle.Describe()}");
            }

            WorldClock.Freeze(-1f);
            cycle.Apply(cycle.Profile.StartOfDay, true);

            Debug.Log($"[SkyFactory] Full daylight is {day / Mathf.Max(0.0001f, night):F1}x the brightest "
                      + $"real night ({day:F3} vs {night:F3}, sun plus ambient). "
                      + $"Cycle {cycle.Profile.CycleMinutes} minutes, starting at {cycle.Profile.StartOfDay:F2}, "
                      + $"replicated by tick.");

            if (night >= day * 0.25f)
            {
                Debug.LogError($"[SkyFactory] Night ({night:F3}) is not much darker than day ({day:F3}). "
                               + "A flashlight would be pointless.");
            }
        }

        /// <summary>Everything lighting the scene right now: the one directional light plus ambient.</summary>
        static float Lit(DayNightCycle cycle)
        {
            float ambient = RenderSettings.ambientSkyColor.grayscale;
            return ambient + (cycle.Sun != null ? cycle.Sun.intensity : 0f);
        }

        static Gradient Gradient(params (float time, Color colour)[] keys)
        {
            var gradient = new Gradient();
            var colours = new GradientColorKey[keys.Length];
            for (int i = 0; i < keys.Length; i++)
                colours[i] = new GradientColorKey(keys[i].colour, keys[i].time);

            gradient.SetKeys(colours, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }

        static AnimationCurve Curve(params (float time, float value)[] keys)
        {
            var curve = new AnimationCurve();
            foreach ((float time, float value) in keys) curve.AddKey(time, value);

            for (int i = 0; i < curve.length; i++)
                curve.SmoothTangents(i, 0f);

            return curve;
        }
    }
}
