using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The ocean. Two meshes and a clock.
    ///
    /// The near mesh is a few hundred metres of grid that follows the camera and carries the waves;
    /// the far mesh is a flat ring out to the horizon that carries only colour. That split is the
    /// entire performance story: waves need vertices, and vertices spent on water four kilometres
    /// away are vertices spent on nothing. The near patch snaps to whole cells when it moves, so the
    /// grid never slides underneath the wave - the wave is a function of world position, and a patch
    /// that moved by half a cell would make every crest shimmer.
    ///
    /// The rest of the game asks this class where the water is. <see cref="HeightAt"/> is the same
    /// arithmetic the vertex shader runs, so buoyancy, drowning and the boat all agree with what is
    /// on screen. They are static because "where is the sea" has exactly one answer per scene and
    /// threading a reference through every rigidbody would buy nothing.
    ///
    /// <see cref="Clock"/> is deliberately not Time.time. Waves have to run on the same clock on
    /// every machine or a boat bobs out of sync with the boat everyone else can see; when the
    /// vehicles land, the host's network tick is assigned here and the sea becomes shared state
    /// instead of four independent oceans. Until then it follows local time and nothing notices.
    /// </summary>
    [ExecuteAlways]
    public class WaterSurface : MonoBehaviour
    {
        static readonly int WaterTimeId = Shader.PropertyToID("_WaterTime");

        [Tooltip("How far the near patch may drift from the camera before it snaps, in metres. Must match the mesh cell size.")]
        public float SnapStep = 4f;

        [Tooltip("Follow the camera. Off pins the patch to the origin, which is what a headless host wants.")]
        public bool FollowCamera = true;

        /// <summary>Mean sea level in world space. The island is built around it being zero.</summary>
        public const float SeaLevel = IslandShape.SeaLevel;

        /// <summary>
        /// Seconds fed to the waves. Set <see cref="ExternalClock"/> and write to this to drive the
        /// sea from a network tick instead of from local time.
        /// </summary>
        public static float Clock { get; set; }

        /// <summary>When true, nothing here touches <see cref="Clock"/> - the owner does.</summary>
        public static bool ExternalClock { get; set; }

        /// <summary>Water height at a world position, in metres. Mean level plus the wave.</summary>
        public static float HeightAt(float x, float z) => SeaLevel + WaterWaves.Height(x, z, Clock);

        /// <summary>Water height under a point.</summary>
        public static float HeightAt(Vector3 position) => HeightAt(position.x, position.z);

        /// <summary>Surface normal at a world position.</summary>
        public static Vector3 NormalAt(float x, float z) => WaterWaves.Normal(x, z, Clock);

        /// <summary>Metres a point is below the surface. Negative in air, which is what buoyancy wants.</summary>
        public static float SubmersionAt(Vector3 position) => HeightAt(position.x, position.z) - position.y;

        /// <summary>True when a point is under water at this instant.</summary>
        public static bool IsSubmerged(Vector3 position) => SubmersionAt(position) > 0f;

        void OnEnable()
        {
            // The clock is static and the scene may have been reloaded under it. Starting from the
            // current time rather than from zero means the sea does not jump on a scene change.
            if (!ExternalClock) Clock = Application.isPlaying ? Time.time : 0f;
            Push();
        }

        void LateUpdate()
        {
            if (!ExternalClock && Application.isPlaying) Clock = Time.time;
            Push();
            Follow();
        }

        /// <summary>
        /// One global float instead of a per-material set: the shader and this class have to be
        /// reading the same instant, and a global is the only way that stays true when a second
        /// water material shows up.
        /// </summary>
        void Push() => Shader.SetGlobalFloat(WaterTimeId, Clock);

        void Follow()
        {
            if (!FollowCamera) return;

            Camera camera = Camera.main;
            if (camera == null) return;

            float step = Mathf.Max(0.01f, SnapStep);
            Vector3 eye = camera.transform.position;

            transform.position = new Vector3(Mathf.Round(eye.x / step) * step,
                                             SeaLevel,
                                             Mathf.Round(eye.z / step) * step);
        }
    }
}
