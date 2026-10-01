using EscapeWithYourFriends.Core;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The one wind every swaying material reads (#244): a direction that wanders, a strength, and
    /// gusts on top, pushed to the shaders as globals each frame. Nothing is networked: each machine
    /// blows its own, and nobody can tell two palms on two screens apart.
    ///
    /// <see cref="Strength"/> is the knob a storm turns. The shader does the rest - see ApplyWind in
    /// Stylized.shader - by height above each model's pivot, so a material opts in with its _WIND
    /// keyword and nothing else.
    /// </summary>
    public class Wind : MonoBehaviour
    {
        static readonly int Params = Shader.PropertyToID("_WindParams");
        static readonly int Detail = Shader.PropertyToID("_WindDetail");

        /// <summary>0 still, 1 a fresh trade wind, 3 a storm.</summary>
        public static float Strength = 1f;

        /// <summary>Gust right now, 0-1. Public for ambient effects that want to drift with it.</summary>
        public static float Gust { get; private set; }

        public static Vector2 Direction { get; private set; } = new(0.8f, 0.6f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

            var go = new GameObject("Wind");
            DontDestroyOnLoad(go);
            go.AddComponent<Wind>();

            VideoSettings.Changed += ApplyDetail;
            ApplyDetail();
        }

        /// <summary>Leaf flutter is off on the lowest grass tier; trunks sway on every tier.</summary>
        static void ApplyDetail() => Shader.SetGlobalFloat(Detail, VideoSettings.Current.Grass > 0 ? 1f : 0f);

        void Update()
        {
            float t = Time.time;

            // The trade wind wanders thirty degrees either side of the north-east over minutes.
            float angle = Mathf.Deg2Rad * (37f + 30f * (Mathf.PerlinNoise(t * 0.004f, 3.1f) * 2f - 1f));
            Direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

            // Gusts: slow noise, squared so most of the time is calm and a gust is an event.
            float n = Mathf.PerlinNoise(t * 0.12f, 7.7f);
            Gust = Mathf.Clamp01((n - 0.35f) / 0.65f);
            Gust *= Gust;

            Shader.SetGlobalVector(Params, new Vector4(Direction.x, Direction.y, Strength, Gust));
        }
    }
}
