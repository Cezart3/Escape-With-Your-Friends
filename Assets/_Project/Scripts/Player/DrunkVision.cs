using FishNet.Object;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// What a drink looks like from behind your own eyes (#66). Reads <see cref="BuffState.Haze"/>
    /// and drives a URP volume off it: depth of field that will not settle, colour fringing at the
    /// edges, grain, and a barrel distortion that makes the middle of the screen bulge.
    ///
    /// **The handicap has to be real or the buff is free.** Drunk gives you a quarter off incoming
    /// damage, which is genuinely worth having in a fight; the price is that you cannot see the
    /// fight and cannot shoot straight (<c>BuffDef.AimWobble</c>, rolled on the server). The blur is
    /// the half the player feels, the wobble is the half they lose to. Neither one alone is a trade.
    ///
    /// Owner only, and only where there is a screen. A headless host has no camera to blur and a
    /// spectator watching somebody else drink should see their own sober picture: the haze belongs
    /// to a pair of eyes, not to a body, so nothing about it is networked. Every peer computes it
    /// from a buff list it already has.
    ///
    /// The profile is built here rather than authored as an asset because it is four overrides whose
    /// values are all derived from one number. An asset would be a file to keep in sync with this
    /// file, which is worse than no asset.
    /// </summary>
    public class DrunkVision : NetworkBehaviour
    {
        [SerializeField] BuffState _buffs;

        [Tooltip("Seconds for the blur to catch up with the drink. Slow, because the drink is slow.")]
        [Min(0.05f)]
        [SerializeField] float _response = 1.5f;

        /// <summary>
        /// The volume's weight at full haze. Not 1: at 1 the depth of field is a wall of soup and
        /// the player stops being able to find the door, which is annoying rather than funny.
        /// </summary>
        public const float MaxWeight = 0.85f;

        Volume _volume;
        float _weight;

        public float Weight => _weight;

        public override void OnStartClient()
        {
            base.OnStartClient();

            // Spectators and the headless host run none of this. `enabled = false` rather than a
            // guard in Update, because the whole component is for one pair of eyes.
            if (!IsOwner || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                enabled = false;
                return;
            }

            if (_buffs == null) _buffs = GetComponent<BuffState>();

            Build();
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            if (_volume != null) Destroy(_volume.gameObject);
        }

        void Update()
        {
            if (_volume == null) return;

            float target = _buffs != null ? Mathf.Clamp01(_buffs.Haze) * MaxWeight : 0f;

            _weight = Mathf.MoveTowards(_weight, target, Time.deltaTime / _response);
            _volume.weight = _weight;
        }

        void Build()
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();

            // Gaussian rather than Bokeh: the cheap one, and this game has to run on an integrated
            // GPU. The near blur is what sells drunk anyway - the world close to you swimming.
            DepthOfField dof = profile.Add<DepthOfField>(true);
            dof.mode.Override(DepthOfFieldMode.Gaussian);
            dof.gaussianStart.Override(1.5f);
            dof.gaussianEnd.Override(14f);
            dof.gaussianMaxRadius.Override(1.2f);

            ChromaticAberration fringing = profile.Add<ChromaticAberration>(true);
            fringing.intensity.Override(0.65f);

            FilmGrain grain = profile.Add<FilmGrain>(true);
            grain.type.Override(FilmGrainLookup.Medium2);
            grain.intensity.Override(0.55f);

            // The bulge. Small, because a big one reads as a fisheye lens rather than as a problem
            // with the person wearing it.
            LensDistortion bulge = profile.Add<LensDistortion>(true);
            bulge.intensity.Override(-0.25f);
            bulge.scale.Override(1.05f);

            var go = new GameObject($"DrunkVision (owner {OwnerId})") { hideFlags = HideFlags.DontSave };
            _volume = go.AddComponent<Volume>();
            _volume.isGlobal = true;

            // Above anything the world sets, because this is a state of the viewer rather than a
            // property of where they are standing.
            _volume.priority = 100f;
            _volume.profile = profile;
            _volume.weight = 0f;

            EnablePostProcessing();
        }

        /// <summary>
        /// URP ignores every volume in the scene unless the camera asks for them, and a camera built
        /// in code does not. Nothing else in the project needed post-processing, so this is where it
        /// gets switched on - one flag, on the one camera this peer looks through.
        /// </summary>
        static void EnablePostProcessing()
        {
            Camera camera = Camera.main;
            if (camera == null) return;

            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            if (data != null) data.renderPostProcessing = true;
        }

        // ---------------------------------------------------------------- the pure part

        /// <summary>
        /// How far the camera leans, in degrees, at this haze and this moment. Pitch, yaw, roll.
        ///
        /// Three sines at frequencies that do not divide into each other, so the lean never settles
        /// into a rhythm a player can steer against - which is the difference between a handicap and
        /// a bit of motion. Roll is the big one: a drunk person's horizon tips, it does not shake.
        ///
        /// Static and pure so the harness can hold it to a number without a screen, and so the
        /// camera rig can add it without owning any of it.
        /// </summary>
        public static Vector3 Sway(float haze, float time)
        {
            haze = Mathf.Clamp01(haze);
            if (haze <= 0f) return Vector3.zero;

            float roll = Mathf.Sin(time * 0.7f) * 4.4f + Mathf.Sin(time * 1.31f) * 2.2f;
            float pitch = Mathf.Sin(time * 0.53f) * 1.8f;
            float yaw = Mathf.Sin(time * 0.41f + 1.7f) * 2.6f;

            return new Vector3(pitch, yaw, roll) * haze;
        }

        /// <summary>The widest the lean can ever get, per axis. What the harness checks it against.</summary>
        public static Vector3 MaxSway(float haze) => new Vector3(1.8f, 2.6f, 6.6f) * Mathf.Clamp01(haze);
    }
}
