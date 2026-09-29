using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The small cutscenes at the story beats (#197): letterbox bars, a slow camera orbit around
    /// whatever the moment is about, and a title card. Four seconds, any key skips it.
    ///
    /// No timeline and no authored shots: a <see cref="CinemachineCamera"/> above the player rig's
    /// priority, moved by hand, and the brain's default blend does the ease in and out for free.
    /// Local to each peer, like the objective line. Every peer near the moment gets the same beat
    /// and nobody has to agree over the network on when it started.
    ///
    /// Each id plays once per process. A peer farther than <see cref="Reach"/> from the focus skips
    /// it: the camera leaving somebody who is busy on the other side of the island is exactly the
    /// "takes control mid-fight" the issue rules out.
    /// </summary>
    public class StoryBeat : MonoBehaviour
    {
        const float Seconds = 4.5f;
        const float Reach = 60f;
        const float Radius = 11f;
        const float Height = 4.5f;
        const float Sweep = 35f;

        static readonly HashSet<string> _played = new();

        /// <summary>Every beat id this process has played, in order. The playthrough bot reads it.</summary>
        public static readonly List<string> Played = new();

        public static bool Playing => _current != null;

        static StoryBeat _current;

        string _title, _line;
        Transform _focus;
        Vector3 _focusAt;
        float _startAngle, _started;
        CinemachineCamera _camera;

        /// <summary>
        /// Plays the beat <paramref name="id"/> once. Returns false if it already played, or if
        /// this peer is too far from the moment to be interrupted by it.
        /// </summary>
        public static bool Play(string id, string title, string line, Transform focus)
        {
            if (focus == null || _played.Contains(id)) return false;

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
            Vector3 from = main.transform.position - focus.position;
            beat._startAngle = ClearAngle(focus, Mathf.Atan2(from.z, from.x) * Mathf.Rad2Deg);
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
            _camera.Lens.FieldOfView = 50f;
            Place(0f);
        }

        void OnDestroy()
        {
            if (_current == this) _current = null;
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

            Vector3 at = Orbit(_focusAt, _startAngle + Sweep * t);
            transform.SetPositionAndRotation(at, Quaternion.LookRotation(_focusAt + Vector3.up - at));
        }

        /// <summary>
        /// The first heading, from the player's side round, whose whole sweep can see the focus.
        /// The first playthrough shot filmed the inside of a palm trunk: a wreck on the tideline has
        /// jungle on one side and open sea on the other, and only one of them makes a picture.
        /// </summary>
        static float ClearAngle(Transform focus, float preferred)
        {
            for (int i = 0; i < 12; i++)
            {
                float angle = preferred + (i % 2 == 0 ? 1 : -1) * ((i + 1) / 2) * 30f;
                if (Sees(focus, angle) && Sees(focus, angle + Sweep * 0.5f) && Sees(focus, angle + Sweep))
                    return angle;
            }

            return preferred;
        }

        /// <summary>Nothing but the subject itself between the camera and the subject.</summary>
        static bool Sees(Transform focus, float angle)
        {
            Vector3 at = Orbit(focus.position, angle);
            Vector3 look = focus.position + Vector3.up - at;
            foreach (RaycastHit hit in Physics.SphereCastAll(at, 0.4f, look.normalized, look.magnitude, ~0,
                                                             QueryTriggerInteraction.Ignore))
                if (!hit.transform.IsChildOf(focus) && hit.transform.GetComponentInParent<Player.PlayerMotor>() == null)
                    return false;

            return true;
        }

        static Vector3 Orbit(Vector3 focus, float degrees)
        {
            float angle = degrees * Mathf.Deg2Rad;
            Vector3 at = focus + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * Radius;

            // Never under the ground: an orbit on a slope otherwise films the inside of a hill.
            // SampleHeight is from the terrain's own origin, which sits well under the sea: read raw,
            // it put the camera sixty metres up, filming a map (playthrough bot).
            Terrain terrain = Terrain.activeTerrain;
            float ground = terrain != null ? terrain.SampleHeight(at) + terrain.GetPosition().y : at.y;
            at.y = Mathf.Max(focus.y + Height, ground + 2f);
            return at;
        }

        static bool Pressed()
        {
            Keyboard keys = Keyboard.current;
            Mouse mouse = Mouse.current;
            return (keys != null && keys.anyKey.wasPressedThisFrame)
                   || (mouse != null && mouse.leftButton.wasPressedThisFrame);
        }

        GUIStyle _big, _small;

        void OnGUI()
        {
            float t = (Time.time - _started) / Seconds;
            float bars = Mathf.Clamp01(Mathf.Min(t, 1f - t) * 6f);
            float text = Mathf.Clamp01(Mathf.Min(t - 0.12f, 0.95f - t) * 5f);

            float bar = Screen.height * 0.12f * bars;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, bar), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(0, Screen.height - bar, Screen.width, bar), Texture2D.whiteTexture);

            _big ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _small ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Italic };
            _big.fontSize = Mathf.RoundToInt(Screen.height * 0.055f);
            _small.fontSize = Mathf.RoundToInt(Screen.height * 0.026f);

            // Shadow first: white on a sunlit beach was unreadable.
            float y = Screen.height * 0.62f, drop = Mathf.Max(2f, Screen.height * 0.003f);
            for (int pass = 0; pass < 2; pass++)
            {
                float o = pass == 0 ? drop : 0f;
                GUI.color = pass == 0 ? new Color(0f, 0f, 0f, text * 0.8f) : new Color(1f, 0.97f, 0.9f, text);
                GUI.Label(new Rect(o, y + o, Screen.width, Screen.height * 0.08f), _title, _big);
                GUI.Label(new Rect(o, y + Screen.height * 0.075f + o, Screen.width, Screen.height * 0.05f), _line, _small);
            }

            GUI.color = new Color(1f, 1f, 1f, text * 0.5f);
            GUI.Label(new Rect(0, Screen.height - bar, Screen.width - 16f, bar), "any key to skip",
                      new GUIStyle(_small) { alignment = TextAnchor.MiddleRight, fontSize = _small.fontSize / 2 + 4 });
        }
    }
}
