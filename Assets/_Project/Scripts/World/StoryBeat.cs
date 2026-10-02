using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The small cutscenes at the story beats (#197): letterbox bars, two shots of whatever the moment
    /// is about, and a title card. Six seconds, any key skips it.
    ///
    /// No timeline and no authored shots: a <see cref="CinemachineCamera"/> above the player rig's
    /// priority, moved by hand, and the brain's default blend does the ease in and back out for free.
    /// Local to each peer, like the objective line. Every peer near the moment gets the same beat
    /// and nobody has to agree over the network on when it started.
    ///
    /// **Two shots, cut, not one orbit.** The first version circled the subject from four and a half
    /// metres up, which is a security camera, not a film. Now a low wide establishes where it is, a
    /// dip to black cuts, and an eye-level two-shot of the player and the subject plays the title,
    /// with the background thrown out of focus. With nobody to pair the subject with, the second
    /// shot is a close-up of the subject alone.
    ///
    /// Each id plays once per process. A peer farther than <see cref="Reach"/> from the focus skips
    /// it: the camera leaving somebody who is busy on the other side of the island is exactly the
    /// "takes control mid-fight" the issue rules out.
    /// </summary>
    public class StoryBeat : MonoBehaviour
    {
        const float Seconds = 6f;
        const float Reach = 60f;

        /// <summary>Where the cut falls, as a share of the beat, and how long the dip to black lasts.</summary>
        const float CutAt = 0.36f, Dip = 0.035f;

        /// <summary>The wide: distance, height over the subject, degrees of drift, lens.</summary>
        const float WideRadius = 13f, WideHeight = 2.2f, WideSweep = 10f, WideFov = 42f;

        /// <summary>The close shot: camera height over the subject's feet, how far it creeps in, lens.</summary>
        const float EyeHeight = 1.55f, Creep = 0.12f, CloseFov = 34f;

        /// <summary>Where on a standing body the lens aims: the chest, not the feet.</summary>
        const float AimHeight = 1.25f;

        /// <summary>Half-width, in metres, up to which the subject is a person and gets a two-shot.</summary>
        const float PersonSize = 1.5f;

        static readonly HashSet<string> _played = new();

        /// <summary>Every beat id this process has played, in order. The playthrough bot reads it.</summary>
        public static readonly List<string> Played = new();

        public static bool Playing => _current != null;

        static StoryBeat _current;

        string _title, _line;
        Transform _focus;
        Vector3 _focusAt, _player;
        float _wideAngle, _started;
        bool _framed;

        // How big the subject is. The first cut put the "Day one" close-up five metres from the
        // wreck's pivot, which is inside the hull: every distance below scales with this.
        Bounds _bounds;
        Vector3 _aimOffset;
        float _size, _wideRadius, _wideHeight;
        CinemachineCamera _camera;

        // The close shot, worked out once at the cut so it does not wander as the subject moves.
        Vector3 _closeFrom, _closeAim;

        Volume _volume;
        VolumeProfile _profile;
        DepthOfField _focusPull;

        /// <summary>
        /// Plays the beat <paramref name="id"/> once. Returns false if it already played, or if
        /// this peer is too far from the moment to be interrupted by it.
        /// </summary>
        public static bool Play(string id, string title, string line, Transform focus)
        {
            // The look route flies its camera past every beat's focus; a title card in a perf frame or
            // a beauty shot is noise.
            if (focus == null || _played.Contains(id) || LookRoute.Running) return false;

            Camera main = Camera.main;
            if (main != null && Vector3.Distance(main.transform.position, focus.position) > Reach) return false;

            _played.Add(id);
            Played.Add(id);
            Debug.Log($"[StoryBeat] {id}: \"{title}\" - {line}");

            // Headless has no camera to move and no screen to draw on; the bookkeeping above is all
            // a harness can see.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || main == null) return true;

            Skip();
            var beat = new GameObject($"StoryBeat ({id})").AddComponent<StoryBeat>();
            beat._title = title;
            beat._line = line;
            beat._focus = focus;
            beat._focusAt = focus.position;

            // The player's feet, near enough: the first-person camera is at their eyes.
            beat._player = main.transform.position - Vector3.up * EyeHeight;
            beat.Measure();
            Vector3 from = main.transform.position - focus.position;
            beat._wideAngle = beat.ClearAngle(Mathf.Atan2(from.z, from.x) * Mathf.Rad2Deg);
            _current = beat;
            return true;
        }

        /// <summary>Ends whatever is playing. The brain blends back to the player on its own.</summary>
        public static void Skip()
        {
            if (_current != null) Destroy(_current.gameObject);
            _current = null;
        }

        void Start()
        {
            _started = Time.time;
            _camera = gameObject.AddComponent<CinemachineCamera>();
            _camera.Priority.Value = 30; // Over the rig's 10 and the death camera's 20.
            BuildLook();
            Place(0f);
        }

        void OnDestroy()
        {
            if (_current == this) _current = null;
            if (_profile != null) Destroy(_profile);
        }

        void Update()
        {
            float t = (Time.time - _started) / Seconds;
            if (t >= 1f || Pressed()) { Skip(); return; }

            Place(t);
        }

        void Place(float t)
        {
            if (_focus != null) _focusAt = _focus.position;

            float bars = Mathf.Clamp01(Mathf.Min(t, 1f - t) * 6f);
            if (_volume != null) _volume.weight = bars;

            if (t < CutAt)
            {
                // The wide: low, slow, drifting a little sideways. Smoothstepped so it starts and
                // stops like a dolly, not a turntable.
                float u = Mathf.SmoothStep(0f, 1f, t / CutAt);
                Vector3 at = Orbit(_focusAt, _wideAngle + WideSweep * u, _wideRadius, _wideHeight);
                Aim(at, _focusAt + _aimOffset, WideFov);
                if (_focusPull != null) _focusPull.active = false;
                return;
            }

            if (!_framed) FrameClose();

            float v = Mathf.SmoothStep(0f, 1f, (t - CutAt) / (1f - CutAt));
            Vector3 from = Vector3.Lerp(_closeFrom, _closeAim, Creep * v);
            Aim(from, _closeAim, CloseFov);

            if (_focusPull != null)
            {
                float distance = Vector3.Distance(from, _closeAim);
                _focusPull.active = true;
                _focusPull.gaussianStart.Override(distance + 2f);
                _focusPull.gaussianEnd.Override(distance + 18f);
            }
        }

        void Aim(Vector3 at, Vector3 target, float fov)
        {
            transform.SetPositionAndRotation(at, Quaternion.LookRotation(target - at));
            _camera.Lens.FieldOfView = fov;
        }

        /// <summary>
        /// The second shot. A two-shot side-on to the line between the player and the subject, at eye
        /// height, the subject on the near third; or, with the player too close or too far to share a
        /// frame, a close-up of the subject from the clear side.
        /// </summary>
        void FrameClose()
        {
            Vector3 between = _focusAt - _player;
            between.y = 0f;
            float apart = between.magnitude;

            if (_size < PersonSize && apart > 1.5f && apart < 20f)
            {
                Vector3 along = between / apart;
                Vector3 side = Vector3.Cross(Vector3.up, along);
                Vector3 middle = Vector3.Lerp(_player, _focusAt, 0.6f);
                _closeAim = middle + Vector3.up * AimHeight;
                float back = Mathf.Max(4.5f, apart * 0.9f + 2.5f);

                foreach (float sign in new[] { 1f, -1f })
                {
                    Vector3 at = Grounded(middle + side * (sign * back), middle.y + EyeHeight);
                    if (Clear(at, _closeAim))
                    {
                        _closeFrom = at;
                        _framed = true;
                        return;
                    }
                }
            }

            // Alone: three-quarters on from the side the wide found clear, or the nearest clear heading
            // round from there, at a distance that fits the subject in the frame.
            _closeAim = _focusAt + _aimOffset;
            float distance = Mathf.Max(5f, _size * 1.8f + 3f);
            float height = Mathf.Max(EyeHeight, _aimOffset.y);
            _closeFrom = Orbit(_focusAt, _wideAngle + WideSweep + 35f, distance, height);
            for (int i = 0; i < 12; i++)
            {
                Vector3 at = Orbit(_focusAt, _wideAngle + WideSweep + 35f + (i % 2 == 0 ? 1 : -1) * ((i + 1) / 2) * 30f,
                                   distance, height);
                if (Clear(at, _closeAim)) { _closeFrom = at; break; }
            }
            _framed = true;
        }

        /// <summary>
        /// The film look for the length of the beat: heavier vignette, a touch of grain, and in the
        /// close shot a Gaussian focus pull that softens everything behind the subject. Gaussian, the
        /// cheap one, for the same integrated-GPU reason as <see cref="Player.DrunkVision"/>.
        /// </summary>
        void BuildLook()
        {
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();

            _focusPull = _profile.Add<DepthOfField>(true);
            _focusPull.mode.Override(DepthOfFieldMode.Gaussian);
            _focusPull.gaussianMaxRadius.Override(1.4f);
            _focusPull.highQualitySampling.Override(true);
            _focusPull.active = false;

            Vignette vignette = _profile.Add<Vignette>(true);
            vignette.intensity.Override(0.42f);
            vignette.smoothness.Override(0.5f);

            FilmGrain grain = _profile.Add<FilmGrain>(true);
            grain.type.Override(FilmGrainLookup.Thin1);
            grain.intensity.Override(0.3f);

            _volume = gameObject.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 50;
            _volume.sharedProfile = _profile;
            _volume.weight = 0f;
        }

        /// <summary>
        /// The first heading, from the player's side round, whose whole drift can see the subject.
        /// The first playthrough shot filmed the inside of a palm trunk: a wreck on the tideline has
        /// jungle on one side and open sea on the other, and only one of them makes a picture.
        /// </summary>
        float ClearAngle(float preferred)
        {
            Vector3 aim = _focusAt + _aimOffset;
            for (int i = 0; i < 12; i++)
            {
                float angle = preferred + (i % 2 == 0 ? 1 : -1) * ((i + 1) / 2) * 30f;
                if (Clear(Orbit(_focusAt, angle, _wideRadius, _wideHeight), aim)
                    && Clear(Orbit(_focusAt, angle + WideSweep, _wideRadius, _wideHeight), aim))
                    return angle;
            }

            return preferred;
        }

        /// <summary>
        /// The subject's size from what it draws, and the shot distances that follow from it. A
        /// person aims at the chest; anything bigger at the middle of what it draws.
        /// </summary>
        void Measure()
        {
            _bounds = new Bounds(_focusAt, Vector3.one);
            bool any = false;
            foreach (Renderer r in _focus.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer || !r.enabled) continue;
                if (any) _bounds.Encapsulate(r.bounds);
                else { _bounds = r.bounds; any = true; }
            }

            _size = Mathf.Max(_bounds.extents.x, _bounds.extents.z);
            _aimOffset = _size < PersonSize ? Vector3.up * AimHeight : _bounds.center - _focusAt;
            _wideRadius = Mathf.Max(WideRadius, _size * 2.5f + 6f);
            _wideHeight = Mathf.Max(WideHeight, _bounds.extents.y);
        }

        /// <summary>
        /// Outside the subject, and nothing but the subject or a player between the camera and what
        /// it aims at. The subject's own colliders do not count as in the way, which is exactly how
        /// the first cut ended up filming the wreck from inside its hull.
        /// </summary>
        bool Clear(Vector3 at, Vector3 target)
        {
            Bounds keepOut = _bounds;
            keepOut.Expand(1f);
            if (keepOut.Contains(at)) return false;

            Vector3 look = target - at;
            foreach (RaycastHit hit in Physics.SphereCastAll(at, 0.4f, look.normalized, look.magnitude, ~0,
                                                             QueryTriggerInteraction.Ignore))
                if ((_focus == null || !hit.transform.IsChildOf(_focus))
                    && hit.transform.GetComponentInParent<Player.PlayerMotor>() == null)
                    return false;

            return true;
        }

        static Vector3 Orbit(Vector3 focus, float degrees, float radius, float height)
        {
            float angle = degrees * Mathf.Deg2Rad;
            return Grounded(focus + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius, focus.y + height);
        }

        /// <summary>
        /// At <paramref name="height"/>, but never under the ground: a shot on a slope otherwise films
        /// the inside of a hill. SampleHeight is from the terrain's own origin, which sits well under
        /// the sea: read raw, it put the camera sixty metres up, filming a map (playthrough bot).
        /// </summary>
        static Vector3 Grounded(Vector3 at, float height)
        {
            Terrain terrain = Terrain.activeTerrain;
            float ground = terrain != null ? terrain.SampleHeight(at) + terrain.GetPosition().y : at.y;
            at.y = Mathf.Max(height, ground + 1.2f);
            return at;
        }

        static bool Pressed()
        {
            Keyboard keys = Keyboard.current;
            Mouse mouse = Mouse.current;
            return (keys != null && keys.anyKey.wasPressedThisFrame)
                   || (mouse != null && mouse.leftButton.wasPressedThisFrame);
        }

        /// <summary>"Found them" as "F O U N D   T H E M": tracked-out capitals, the title-card look.</summary>
        static string Tracked(string text)
            => string.Join(" ", text.ToUpperInvariant().ToCharArray()).Replace("   ", "     ");

        GUIStyle _big, _small;
        string _tracked;

        void OnGUI()
        {
            float t = (Time.time - _started) / Seconds;
            float bars = Mathf.Clamp01(Mathf.Min(t, 1f - t) * 6f);

            // The title belongs to the close shot: it comes up after the cut and is gone before the end.
            float close = (t - CutAt) / (1f - CutAt);
            float text = Mathf.Clamp01(Mathf.Min(close - 0.12f, 0.92f - close) * 5f);

            // 2.39:1 inside a 16:9 frame.
            float bar = Screen.height * 0.128f * bars;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, bar), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(0, Screen.height - bar, Screen.width, bar), Texture2D.whiteTexture);

            // The cut: a few frames through black, so the jump reads as an edit and not a glitch.
            float dip = 1f - Mathf.Clamp01(Mathf.Abs(t - CutAt) / Dip);
            if (dip > 0f)
            {
                GUI.color = new Color(0f, 0f, 0f, dip);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            }

            _big ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerLeft, fontStyle = FontStyle.Bold };
            _small ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, fontStyle = FontStyle.Italic };
            _big.fontSize = Mathf.RoundToInt(Screen.height * 0.042f);
            _small.fontSize = Mathf.RoundToInt(Screen.height * 0.024f);
            _tracked ??= Tracked(_title);

            // Lower left, just above the bar, the way a film puts a place or a name: it leaves the
            // middle of the frame to the picture. Shadow first: white on a sunlit beach was unreadable.
            float left = Screen.width * 0.08f, rise = (1f - text) * Screen.height * 0.01f;
            float baseline = Screen.height - Screen.height * 0.128f - Screen.height * 0.07f + rise;
            float drop = Mathf.Max(2f, Screen.height * 0.002f);
            for (int pass = 0; pass < 2; pass++)
            {
                float o = pass == 0 ? drop : 0f;
                GUI.color = pass == 0 ? new Color(0f, 0f, 0f, text * 0.7f) : new Color(1f, 0.96f, 0.88f, text);
                GUI.Label(new Rect(left + o, baseline - Screen.height * 0.06f + o, Screen.width, Screen.height * 0.06f), _tracked, _big);
                GUI.color = pass == 0 ? new Color(0f, 0f, 0f, text * 0.7f) : new Color(1f, 0.96f, 0.88f, text * 0.85f);
                GUI.Label(new Rect(left + o, baseline + Screen.height * 0.008f + o, Screen.width, Screen.height * 0.05f), _line, _small);
            }

            GUI.color = new Color(1f, 1f, 1f, bars * 0.4f);
            GUI.Label(new Rect(0, Screen.height - bar, Screen.width - 16f, bar), "any key to skip",
                      new GUIStyle(_small) { alignment = TextAnchor.MiddleRight, fontSize = _small.fontSize / 2 + 4 });
        }
    }
}
