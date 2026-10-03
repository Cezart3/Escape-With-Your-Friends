using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Audio;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The cutscenes (#197, remade for #287): the story told in-engine, by the game's own bodies
    /// acting from the animation library, cut like a film, with subtitles, under 40 s each and
    /// skippable. The scenes themselves are in StoryScenes.cs; this half is the crew.
    ///
    /// **No timeline.** A scene is a coroutine: put actors down, call a shot, wait, give a line. A
    /// <see cref="CinemachineCamera"/> above the rig's priority is the camera, and every shot is a
    /// smoothstepped dolly from one pose to another (or a cut, which is a dolly of no length). The
    /// actors are <see cref="Puppet"/>s, not the players: the real bodies are hidden for the length
    /// of the scene and come back where they were.
    ///
    /// **Two kinds of scene.** The island scenes (waking up, finding Bogdan, the plane) play where
    /// the moment is, framed round it by <see cref="Frame"/>, which turns the whole blocking until
    /// every camera position is clear of the jungle. The others (the attic, the storm, the
    /// epilogue) build their set far from the island: <see cref="Attic"/> hangs in the sky and the
    /// storm is out at sea, where the ocean already is.
    ///
    /// Local to each peer, as before: every peer near the moment plays the same scene, and nobody
    /// agrees over the network on when it started. A scene asked for while another plays waits its
    /// turn. Each id plays once per process. A peer farther than <see cref="Reach"/> from an island
    /// scene skips it: the camera leaving somebody mid-fight on the other side of the island is the
    /// thing #197 ruled out.
    /// </summary>
    [DefaultExecutionOrder(-100)] // The camera pose is written before the brain reads it in LateUpdate.
    public partial class StoryBeat : MonoBehaviour
    {
        const float Reach = 60f;

        /// <summary>Letterbox height as a share of the screen: 2.39:1 inside 16:9.</summary>
        const float Bar = 0.128f;

        /// <summary>Scenes with their own set, played wherever the player is.</summary>
        static readonly HashSet<string> Elsewhere = new() { "prologue", "ending", "radio" };

        static readonly HashSet<string> _played = new();

        /// <summary>Every beat id this process has played, in order. The playthrough bot reads it.</summary>
        public static readonly List<string> Played = new();

        static readonly Queue<(string Id, string Title, string Line, Transform Focus)> _queue = new();

        public static bool Playing => _current != null;

        static StoryBeat _current;

        /// <summary>Plays the scenes headless too, for <c>-cutsceneTest</c>; normally a headless peer only keeps the books.</summary>
        internal static bool Rehearse;

        internal static StoryBeat Current => _current;

        internal string Id => _id;

        /// <summary>Lets a played id play again: the test replays every scene.</summary>
        internal static void Forget(string id) => _played.Remove(id);

        string _id, _title, _line;
        Transform _focus;
        CutsceneCast _cast;
        Transform _lens;
        CinemachineCamera _camera;

        /// <summary>Puppets, sets and lights: children of the beat, so ending it clears them.</summary>
        internal readonly List<Puppet> Actors = new();
        readonly List<Renderer> _hidden = new();

        internal float Started { get; private set; }
        internal int Shots { get; private set; }

        /// <summary>Every place the lens stood this scene, for the test.</summary>
        internal readonly List<Vector3> Stood = new();

        /// <summary>
        /// Plays the beat <paramref name="id"/> once. Returns false if it already played, or if this
        /// peer is too far from the moment to be interrupted by it.
        /// </summary>
        public static bool Play(string id, string title, string line, Transform focus)
        {
            // The look route flies its camera past every beat's focus; a scene in a perf frame is noise.
            if (_played.Contains(id) || LookRoute.Running) return false;

            Camera main = Camera.main;
            if (!Elsewhere.Contains(id))
            {
                if (focus == null) return false;
                if (main != null && !Rehearse && Vector3.Distance(main.transform.position, focus.position) > Reach) return false;
            }

            _played.Add(id);
            Played.Add(id);
            Debug.Log($"[StoryBeat] {id}: \"{title}\" - {line}");

            // Headless has no camera to move and no screen to draw on; the bookkeeping above is all
            // a harness can see, unless it asked for a rehearsal.
            bool screen = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && main != null;
            if (!screen && !Rehearse) return true;

            if (_current != null) _queue.Enqueue((id, title, line, focus));
            else Begin(id, title, line, focus);
            return true;
        }

        /// <summary>Ends whatever is playing; the next one waiting, if any, starts. The brain blends back on its own.</summary>
        public static void Skip()
        {
            if (_current != null) _current.Finish();
        }

        static void Begin(string id, string title, string line, Transform focus)
        {
            var beat = new GameObject($"StoryBeat ({id})").AddComponent<StoryBeat>();
            beat._id = id;
            beat._title = title;
            beat._line = line;
            beat._focus = focus;
            _current = beat;
        }

        void Start()
        {
            Started = Time.time;
            _cast = CutsceneCast.Load();

            _lens = new GameObject("Lens").transform;
            _lens.SetParent(transform, false);
            _camera = _lens.gameObject.AddComponent<CinemachineCamera>();
            _camera.Priority.Value = 30; // Over the rig's 10 and the death camera's 20.
            BuildLook();
            HideWorld();

            if (_cast == null)
            {
                Debug.LogError("[StoryBeat] No CutsceneCast in Resources: run CutsceneCastBuilder.Build.");
                Finish();
                return;
            }

            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            IEnumerator scene = _id switch
            {
                "prologue" => Prologue(),
                "arrive:island" => Wake(),
                "arrive:island2" => Landing(),
                "castaway" => Rescue(),
                "plane" => Airworthy(),
                "ending" => Epilogue(),
                "radio" => Radio(),
                _ => Glimpse(),
            };
            yield return scene;
            Finish();
        }

        bool _finishing;

        void Finish()
        {
            if (_finishing) return;
            _finishing = true;
            StopAllCoroutines();
            StartCoroutine(Out());
        }

        /// <summary>
        /// Back to the player. From the attic or the storm that is a cut: the brain's usual blend
        /// would fly the camera two kilometres across the sky to get home.
        /// </summary>
        IEnumerator Out()
        {
            Camera main = Camera.main;
            CinemachineBrain brain = main != null ? main.GetComponent<CinemachineBrain>() : null;
            if (brain != null && _lens != null && Vector3.Distance(_lens.position, Here()) > 40f)
            {
                CinemachineBlendDefinition was = brain.DefaultBlend;
                brain.DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.Cut, 0f);
                _camera.enabled = false;
                yield return null;
                yield return null;
                brain.DefaultBlend = was;
            }

            Destroy(gameObject);
        }

        void OnDestroy()
        {
            foreach (Renderer r in _hidden)
                if (r != null) r.forceRenderingOff = false;
            if (_profile != null) Destroy(_profile);

            if (_current != this) return;
            _current = null;
            if (_queue.Count > 0 && gameObject.scene.isLoaded)
            {
                (string id, string title, string line, Transform focus) = _queue.Dequeue();
                Begin(id, title, line, focus);
            }
        }

        /// <summary>The players and Bogdan step out of shot; puppets stand in for them.</summary>
        void HideWorld()
        {
            var bodies = new List<Component>();
            bodies.AddRange(FindObjectsByType<Player.PlayerMotor>(FindObjectsSortMode.None));
            bodies.AddRange(FindObjectsByType<AI.Castaway>(FindObjectsSortMode.None));
            foreach (Component body in bodies)
            foreach (Renderer r in body.GetComponentsInChildren<Renderer>())
                if (!r.forceRenderingOff)
                {
                    r.forceRenderingOff = true;
                    _hidden.Add(r);
                }
        }

        void Update()
        {
            if (Time.time - Started > 0.6f && Pressed()) { Skip(); return; }

            _white = Mathf.MoveTowards(_white, 0f, Time.deltaTime * 1.5f);
            _flash = Mathf.MoveTowards(_flash, 0f, Time.deltaTime * 4f);
            _shake = Mathf.MoveTowards(_shake, 0f, Time.deltaTime * 0.8f);
            if (_grade != null) _grade.postExposure.Override(_exposure + _flash * 2.6f);
        }

        // ------------------------------------------------------------------------- the camera

        Vector3 _from0, _from1, _aim0, _aim1;
        float _fov0, _fov1, _shotAt, _shotLength;
        Transform _ride, _track;
        Vector3 _trackOffset;
        float _shake, _handheld;

        /// <summary>
        /// A shot: the lens at <paramref name="from"/> looking at <paramref name="aim"/>, easing to the
        /// <c>End</c> pose over <paramref name="seconds"/> if one is given. With <paramref name="ride"/>
        /// both points are in that transform's space and the lens moves with it (the Marisol's deck).
        /// </summary>
        internal void Shot(Vector3 from, Vector3 aim, float fov, float seconds = 0f, Vector3? fromEnd = null,
                           Vector3? aimEnd = null, float fovEnd = -1f, Transform ride = null, float handheld = 0.4f)
        {
            _from0 = from;
            _aim0 = aim;
            _from1 = fromEnd ?? from;
            _aim1 = aimEnd ?? aim;
            _fov0 = fov;
            _fov1 = fovEnd > 0f ? fovEnd : fov;
            _shotAt = Time.time;
            _shotLength = seconds;
            _ride = ride;
            _track = null;
            _handheld = handheld;
            Camera main = Camera.main;
            if (Shots == 0 && main != null && Vector3.Distance(main.transform.position, from) > 40f) StartCoroutine(CutIn(main));
            Shots++;
            Pose();
        }

        /// <summary>Into the attic or the storm, a cut: a blend would show the sky on the way there.</summary>
        static IEnumerator CutIn(Camera main)
        {
            CinemachineBrain brain = main.GetComponent<CinemachineBrain>();
            if (brain == null) yield break;
            CinemachineBlendDefinition was = brain.DefaultBlend;
            brain.DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.Cut, 0f);
            yield return null;
            yield return null;
            brain.DefaultBlend = was;
        }

        /// <summary>Keeps the lens on a moving thing, as an operator would, for the rest of the shot.</summary>
        internal void Track(Transform target, Vector3 offset)
        {
            _track = target;
            _trackOffset = offset;
        }

        internal void Shake(float amount) => _shake = Mathf.Max(_shake, amount);

        void LateUpdate() => Pose();

        void Pose()
        {
            if (_camera == null) return;

            float k = _shotLength <= 0f ? 1f : Mathf.SmoothStep(0f, 1f, (Time.time - _shotAt) / _shotLength);
            Vector3 from = Vector3.Lerp(_from0, _from1, k);
            Vector3 aim = Vector3.Lerp(_aim0, _aim1, k);
            if (_ride != null)
            {
                from = _ride.TransformPoint(from);
                aim = _ride.TransformPoint(aim);
            }

            if (_track != null) aim = _track.position + _trackOffset;

            // A person holding a camera: a slow drift of a fraction of a degree, more when it shakes.
            float t = Time.time;
            float wobble = _handheld + _shake * 6f;
            Quaternion look = Quaternion.LookRotation(aim - from)
                              * Quaternion.Euler((Mathf.PerlinNoise(t * 0.4f, 1.3f) - 0.5f) * wobble,
                                                 (Mathf.PerlinNoise(2.1f, t * 0.35f) - 0.5f) * wobble,
                                                 (Mathf.PerlinNoise(t * 0.3f, 7.7f) - 0.5f) * wobble * 0.6f);
            if (_shake > 0f) from += Random.insideUnitSphere * (_shake * 0.08f);

            _lens.SetPositionAndRotation(from, look);
            _camera.Lens.FieldOfView = Mathf.Lerp(_fov0, _fov1, k);
            if (Stood.Count == 0 || (Stood[^1] - from).sqrMagnitude > 0.25f) Stood.Add(from);

            if (_focusPull != null)
            {
                float distance = Vector3.Distance(from, aim);
                _focusPull.gaussianStart.Override(distance + 1.2f);
                _focusPull.gaussianEnd.Override(distance + 10f);
            }
        }

        // ------------------------------------------------------------------------- the look

        Volume _volume;
        VolumeProfile _profile;
        DepthOfField _focusPull;
        ColorAdjustments _grade;
        float _exposure, _flash;

        /// <summary>
        /// The film look for the length of the scene: vignette, grain, a Gaussian focus pull on whatever
        /// the lens aims at (the cheap one, for the integrated-GPU reason in <see cref="Player.DrunkVision"/>),
        /// and a grade each scene sets for itself.
        /// </summary>
        void BuildLook()
        {
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();

            _focusPull = _profile.Add<DepthOfField>(true);
            _focusPull.mode.Override(DepthOfFieldMode.Gaussian);
            _focusPull.gaussianMaxRadius.Override(1.2f);
            _focusPull.highQualitySampling.Override(true);

            Vignette vignette = _profile.Add<Vignette>(true);
            vignette.intensity.Override(0.38f);
            vignette.smoothness.Override(0.5f);

            FilmGrain grain = _profile.Add<FilmGrain>(true);
            grain.type.Override(FilmGrainLookup.Thin1);
            grain.intensity.Override(0.28f);

            _grade = _profile.Add<ColorAdjustments>(true);

            _volume = gameObject.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 50;
            _volume.sharedProfile = _profile;
        }

        internal void Grade(float exposure, Color filter, float saturation = 0f, float contrast = 0f)
        {
            _exposure = exposure;
            _grade.colorFilter.Override(filter);
            _grade.saturation.Override(saturation);
            _grade.contrast.Override(contrast);
        }

        /// <summary>Lightning: two quick flashes and the thunder a breath later.</summary>
        internal IEnumerator Lightning(float distance = 0.6f)
        {
            _flash = 1f;
            yield return new WaitForSeconds(0.07f);
            _flash = 0f;
            yield return new WaitForSeconds(0.06f);
            _flash = 0.8f;
            yield return new WaitForSeconds(distance);
            Sfx.Play2D(Sound.Thunder, 0.9f);
        }

        // ------------------------------------------------------------------------- the props

        internal Puppet Actor(int who, Vector3 at, float yaw, Transform parent = null, bool grounded = true)
        {
            GameObject body = who < 4 ? _cast.Friends[who] : _cast.Bogdan;
            Puppet puppet = Puppet.Spawn(body, _cast, at, yaw, parent != null ? parent : transform);
            if (grounded) puppet.Ground = GroundAt;
            Actors.Add(puppet);
            return puppet;
        }

        internal Transform Prop(string set, Vector3 at, Quaternion rotation, Transform parent = null)
        {
            var go = new GameObject("Set_" + set);
            go.transform.SetParent(parent != null ? parent : transform, false);
            go.transform.SetLocalPositionAndRotation(at, rotation);
            go.AddComponent<MeshFilter>().sharedMesh = _cast.Set(set);
            go.AddComponent<MeshRenderer>().sharedMaterial = _cast.Atlas;
            return go.transform;
        }

        internal Light Lamp(Vector3 at, Color colour, float intensity, float range, Transform parent = null)
        {
            var go = new GameObject("Lamp");
            go.transform.SetParent(parent != null ? parent : transform, false);
            go.transform.localPosition = at;
            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = colour;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
            return light;
        }

        internal Light Beam(Vector3 at, Vector3 towards, Color colour, float intensity, float range, float angle)
        {
            Light light = Lamp(at, colour, intensity, range);
            light.type = LightType.Spot;
            light.spotAngle = angle;
            light.transform.rotation = Quaternion.LookRotation(towards - at);
            return light;
        }

        AudioSource _ambience;

        internal void Ambience(Sound sound, float volume)
        {
            _ambience ??= gameObject.AddComponent<AudioSource>();
            _ambience.clip = Sfx.Clip(sound);
            _ambience.loop = true;
            _ambience.spatialBlend = 0f;
            _ambience.volume = volume;
            if (Application.isPlaying && _ambience.clip != null) _ambience.Play();
        }

        // ------------------------------------------------------------------------- the ground

        /// <summary>
        /// The terrain under a point. SampleHeight is from the terrain's own origin, which sits well under
        /// the sea; read raw, it put the camera sixty metres up (playthrough bot, #197).
        /// </summary>
        internal static float GroundAt(Vector3 at)
        {
            Terrain terrain = Terrain.activeTerrain;
            return terrain != null ? terrain.SampleHeight(at) + terrain.GetPosition().y : at.y;
        }

        /// <summary>
        /// The blocking of an island scene, turned to <paramref name="yaw"/> about <paramref name="origin"/>:
        /// local x right, z forward, and y for a camera the height over the ground (or the sea) there.
        /// </summary>
        internal static Vector3 Lens(Vector3 origin, float yaw, Vector3 local)
        {
            Vector3 at = origin + Quaternion.Euler(0f, yaw, 0f) * new Vector3(local.x, 0f, local.z);
            at.y = Mathf.Max(GroundAt(at), WaterSurface.SeaLevel) + local.y;
            return at;
        }

        internal static Vector3 Spot(Vector3 origin, float yaw, float x, float z)
        {
            Vector3 at = origin + Quaternion.Euler(0f, yaw, 0f) * new Vector3(x, 0f, z);
            at.y = GroundAt(at);
            return at;
        }

        /// <summary>
        /// The heading, from <paramref name="preferred"/> round in 30 degree steps, at which every
        /// camera position is out of the scenery and can see the middle of the scene, and every actor
        /// stands on ground near the scene's own height. The first playthrough of the old beat filmed
        /// the inside of a palm trunk; a camp has jungle on one side and open beach on the other, and
        /// only one of them makes a picture. The best partial fit if nothing passes.
        /// </summary>
        internal float Frame(Vector3 origin, float preferred, Vector3[] lenses, Vector2[] spots)
        {
            float best = preferred;
            int bestScore = -1;
            Vector3 middle = origin + Vector3.up * 1.2f;

            for (int i = 0; i < 12; i++)
            {
                float yaw = preferred + (i % 2 == 0 ? 1 : -1) * ((i + 1) / 2) * 30f;
                int score = 0;
                foreach (Vector3 local in lenses)
                {
                    Vector3 at = Lens(origin, yaw, local);
                    if (!Physics.CheckSphere(at, 0.35f, ~0, QueryTriggerInteraction.Ignore) && Clear(at, middle)) score += 2;
                }

                foreach (Vector2 spot in spots)
                    if (Mathf.Abs(GroundAt(Spot(origin, yaw, spot.x, spot.y)) - origin.y) < 1.2f) score++;

                if (score == lenses.Length * 2 + spots.Length) return yaw;
                if (score > bestScore) { bestScore = score; best = yaw; }
            }

            return best;
        }

        /// <summary>
        /// Nothing but players, Bogdan and the scene's own subject between a lens and its aim. The
        /// subject only in the far half: the wreck's camp is its child too, and a shelter pole a metre
        /// from the lens filled a third of the frame.
        /// </summary>
        internal bool Clear(Vector3 at, Vector3 target)
        {
            Vector3 look = target - at;
            foreach (RaycastHit hit in Physics.SphereCastAll(at, 0.25f, look.normalized, look.magnitude, ~0,
                                                             QueryTriggerInteraction.Ignore))
                if ((_focus == null || !hit.transform.IsChildOf(_focus) || hit.distance < look.magnitude * 0.5f)
                    && hit.transform.GetComponentInParent<Player.PlayerMotor>() == null
                    && hit.transform.GetComponentInParent<AI.Castaway>() == null)
                    return false;

            // A camp's poles have no colliders; their boxes will do, near the lens. Big boxes (a roof, a
            // palm) say nothing about a line under them.
            _near ??= FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None)
                .Where(r => (r.bounds.center - at).sqrMagnitude < 900f && r.bounds.size.magnitude < 8f).ToArray();
            // Across the frame, not just down its middle: a pole a third of the way in is as bad.
            Vector3 side = Vector3.Cross(Vector3.up, look).normalized * look.magnitude * 0.4f;
            foreach (MeshRenderer r in _near)
            for (int i = -1; i <= 1; i++)
                if (r != null && r.bounds.IntersectRay(new Ray(at, look + side * i), out float d) && d < look.magnitude * 0.5f)
                    return false;

            return true;
        }

        MeshRenderer[] _near;

        /// <summary>The local player's feet, or failing that the camera's, or the focus.</summary>
        internal Vector3 Here()
        {
            foreach (Player.PlayerMotor motor in FindObjectsByType<Player.PlayerMotor>(FindObjectsSortMode.None))
                if (motor != null && motor.IsOwner) return motor.transform.position;
            Camera main = Camera.main;
            if (main != null) return main.transform.position - Vector3.up * 1.6f;
            return _focus != null ? _focus.position : Vector3.zero;
        }

        static float Heading(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            return d.x * d.x + d.z * d.z < 0.01f ? 0f : Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        // ------------------------------------------------------------------------- the words

        struct Words
        {
            public string Who, Line;
            public float From, Until;

            public float Alpha => Mathf.Clamp01(Mathf.Min(Time.time - From, Until - Time.time) * 4f);
        }

        Words _say, _caption, _card;
        float _black = 1f, _white, _bars;
        bool _rain;

        /// <summary>A subtitle: who, in their colour, and the line.</summary>
        internal void Say(string who, string line, float seconds)
            => _say = new Words { Who = who, Line = line, From = Time.time, Until = Time.time + seconds };

        /// <summary>Small italics in the lower left: a place, a time.</summary>
        internal void Caption(string line, float seconds)
            => _caption = new Words { Line = line, From = Time.time, Until = Time.time + seconds };

        /// <summary>The title card: tracked-out capitals and a line under them, lower left.</summary>
        internal void Card(string title, string line, float seconds)
            => _card = new Words { Who = Tracked(title), Line = line, From = Time.time, Until = Time.time + seconds };

        internal IEnumerator Fade(float black, float seconds)
        {
            float from = _black;
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                _black = Mathf.Lerp(from, black, t / seconds);
                yield return null;
            }

            _black = black;
        }

        internal void Black(float black) => _black = black;
        internal void White(float white) => _white = white;
        internal void Rain(bool on) => _rain = on;

        static WaitForSeconds Wait(float seconds) => new(seconds);

        static Color Voice(string who) => who switch
        {
            "GUS" => new Color(1f, 0.62f, 0.25f),
            "KIKI" => new Color(1f, 0.55f, 0.72f),
            "MO" => new Color(0.6f, 0.9f, 0.35f),
            "REX" => new Color(0.5f, 0.78f, 1f),
            "BOGDAN" => new Color(0.95f, 0.85f, 0.6f),
            _ => new Color(1f, 0.75f, 0.35f),
        };

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

        GUIStyle _big, _small, _sub;

        void OnGUI()
        {
            float w = Screen.width, h = Screen.height;
            _bars = Mathf.MoveTowards(_bars, 1f, Time.deltaTime * 2f);

            if (_rain) DrawRain(w, h);

            if (_black > 0f) Fill(new Rect(0, 0, w, h), new Color(0f, 0f, 0f, _black));
            if (_white > 0f) Fill(new Rect(0, 0, w, h), new Color(1f, 1f, 1f, Mathf.Clamp01(_white)));

            float bar = h * Bar * _bars;
            Fill(new Rect(0, 0, w, bar), Color.black);
            Fill(new Rect(0, h - bar, w, bar), Color.black);

            _big ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerLeft, fontStyle = FontStyle.Bold };
            _small ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, fontStyle = FontStyle.Italic };
            _sub ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerCenter, richText = true, wordWrap = true };
            _big.fontSize = Mathf.RoundToInt(h * 0.042f);
            _small.fontSize = Mathf.RoundToInt(h * 0.024f);
            _sub.fontSize = Mathf.RoundToInt(h * 0.03f);

            float left = w * 0.08f;
            float baseline = h - h * Bar - h * 0.07f;

            float card = _card.Alpha;
            if (card > 0f)
            {
                float rise = (1f - card) * h * 0.01f;
                Shadowed(new Rect(left, baseline - h * 0.06f + rise, w, h * 0.06f), _card.Who, _big,
                         new Color(1f, 0.96f, 0.88f, card));
                Shadowed(new Rect(left, baseline + h * 0.008f + rise, w, h * 0.05f), _card.Line, _small,
                         new Color(1f, 0.96f, 0.88f, card * 0.85f));
            }

            float caption = _caption.Alpha;
            if (caption > 0f)
                Shadowed(new Rect(left, h * Bar + h * 0.03f, w * 0.8f, h * 0.05f), _caption.Line, _small,
                         new Color(1f, 0.96f, 0.88f, caption));

            float say = _say.Alpha;
            if (say > 0f)
            {
                Color voice = Voice(_say.Who);
                string text = string.IsNullOrEmpty(_say.Who)
                    ? _say.Line
                    : $"<b><color=#{ColorUtility.ToHtmlStringRGB(voice)}>{_say.Who}</color></b>   {_say.Line}";
                Shadowed(new Rect(w * 0.15f, h - bar - h * 0.13f, w * 0.7f, h * 0.11f), text, _sub, new Color(1f, 1f, 1f, say));
            }

            GUI.color = new Color(1f, 1f, 1f, _bars * 0.4f);
            GUI.Label(new Rect(0, h - bar, w - 16f, bar), "any key to skip",
                      new GUIStyle(_small) { alignment = TextAnchor.MiddleRight, fontSize = _small.fontSize / 2 + 4 });
            GUI.color = Color.white;
        }

        static void Fill(Rect rect, Color colour)
        {
            GUI.color = colour;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
        }

        /// <summary>White on a sunlit beach was unreadable: a dark copy a couple of pixels down first.</summary>
        static void Shadowed(Rect rect, string text, GUIStyle style, Color colour)
        {
            float drop = Mathf.Max(2f, Screen.height * 0.002f);
            GUI.color = new Color(0f, 0f, 0f, colour.a * 0.75f);
            GUI.Label(new Rect(rect.x + drop, rect.y + drop, rect.width, rect.height), text, style);
            GUI.color = colour;
            GUI.Label(rect, text, style);
        }

        /// <summary>Rain on the lens: thin slanted streaks falling at three speeds. No particles, no material.</summary>
        static void DrawRain(float w, float h)
        {
            Matrix4x4 was = GUI.matrix;
            GUIUtility.RotateAroundPivot(14f, new Vector2(w * 0.5f, h * 0.5f));
            GUI.color = new Color(0.75f, 0.82f, 0.95f, 0.22f);
            float t = Time.time;
            for (int i = 0; i < 140; i++)
            {
                float speed = 1.4f + (i % 3) * 0.5f;
                float x = Mathf.Repeat(i * 0.6180339f, 1f) * w * 1.3f - w * 0.15f;
                float y = Mathf.Repeat(i * 0.3713f + t * speed, 1f) * h * 1.3f - h * 0.15f;
                GUI.DrawTexture(new Rect(x, y, 1.6f, h * (0.04f + (i % 4) * 0.012f)), Texture2D.whiteTexture);
            }

            GUI.matrix = was;
            GUI.color = Color.white;
        }
    }
}
