using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// An actor in a cutscene (#287): one of the game's own bodies, driven straight from the clip
    /// library through a two-input Playables mixer, so any clip crossfades into any other without an
    /// animator controller that would have to know every scene's moves in advance.
    ///
    /// No collider, no navmesh, no network: a puppet exists on one screen for forty seconds. It walks
    /// in a straight line at the pace its clip was animated for (the clip's own average speed, so the
    /// feet do not skate), turned to face where it is going, kept on whatever <see cref="Ground"/>
    /// says is underfoot. Spawned on something that moves (the Marisol's deck), it is given no ground.
    /// </summary>
    public class Puppet : MonoBehaviour
    {
        /// <summary>Height of the floor at a point. Null keeps the puppet's own height.</summary>
        public System.Func<Vector3, float> Ground;

        public Animator Animator { get; private set; }
        public string Clip { get; private set; }

        /// <summary>A clip that does not loop has reached its end (or, played backwards, its start).</summary>
        public bool Finished { get; private set; }

        public bool Walking => _walking;

        CutsceneCast _cast;
        PlayableGraph _graph;
        AnimationMixerPlayable _mixer;
        AnimationClipPlayable _now, _was;
        float _fade, _faded, _length, _speed;
        bool _loops;

        Vector3 _to;
        float _pace;
        bool _walking;
        string _then;
        Vector3? _faceAfter;
        float _turnTo, _turnRate;

        Transform _held;
        bool _heldRight;

        public static Puppet Spawn(GameObject body, CutsceneCast cast, Vector3 at, float yaw, Transform parent = null)
        {
            var root = new GameObject($"Puppet ({body.name})");
            root.transform.SetParent(parent, false);
            root.transform.SetLocalPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));

            GameObject model = Instantiate(body, root.transform, false);
            model.name = body.name;

            var puppet = root.AddComponent<Puppet>();
            puppet._cast = cast;
            puppet._turnTo = yaw;
            puppet.Animator = model.GetComponent<Animator>();
            puppet.Animator.applyRootMotion = false;
            puppet.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            puppet.Animator.runtimeAnimatorController = null;

            // Same test as CharacterArt.Put: a rig whose toes point back down -z is turned round.
            Transform foot = puppet.Animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform toes = puppet.Animator.GetBoneTransform(HumanBodyBones.LeftToes);
            if (foot != null && toes != null
                && root.transform.InverseTransformPoint(toes.position).z < root.transform.InverseTransformPoint(foot.position).z)
                model.transform.localRotation = Quaternion.AngleAxis(180f, Vector3.up);

            puppet._graph = PlayableGraph.Create(root.name);
            puppet._graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            puppet._mixer = AnimationMixerPlayable.Create(puppet._graph, 2);
            AnimationPlayableOutput.Create(puppet._graph, "Body", puppet.Animator).SetSourcePlayable(puppet._mixer);
            puppet._graph.Play();
            puppet.Play("Idle_Loop", 0f);
            return puppet;
        }

        /// <summary>
        /// Crossfades into <paramref name="clip"/> over <paramref name="fade"/> seconds. Backwards from
        /// its end with <paramref name="reverse"/>: Death01 played backwards is somebody getting up.
        /// </summary>
        public Puppet Play(string clip, float fade = 0.3f, float speed = 1f, bool reverse = false)
        {
            AnimationClip found = _cast.Clip(clip);
            if (found == null)
            {
                Debug.LogWarning($"[Puppet] {name}: no clip called {clip}.");
                return this;
            }

            if (_was.IsValid())
            {
                _graph.Disconnect(_mixer, 1);
                _was.Destroy();
            }

            if (_now.IsValid())
            {
                _graph.Disconnect(_mixer, 0);
                _was = _now;
                _graph.Connect(_was, 0, _mixer, 1);
            }

            _now = AnimationClipPlayable.Create(_graph, found);
            _now.SetApplyFootIK(true);
            _speed = reverse ? -speed : speed;
            _now.SetSpeed(_speed);
            _length = found.length;
            _loops = found.isLooping;
            if (reverse) _now.SetTime(_length);
            _graph.Connect(_now, 0, _mixer, 0);

            _fade = fade;
            _faded = 0f;
            Clip = clip;
            Finished = false;
            Blend();
            return this;
        }

        /// <summary>Holds a clip's last frame (or first, <paramref name="atEnd"/> false) without playing it.</summary>
        public Puppet Pose(string clip, bool atEnd = true)
        {
            Play(clip, 0f, 0f);
            if (atEnd && _now.IsValid()) _now.SetTime(_length);
            return this;
        }

        /// <summary>Walks in a straight line to <paramref name="to"/>, then plays <paramref name="then"/>.</summary>
        public Puppet Walk(Vector3 to, string clip = "Walk_Loop", float speed = 1f, string then = "Idle_Loop",
                           Vector3? face = null)
        {
            _faceAfter = face;
            AnimationClip found = _cast.Clip(clip);
            float natural = found != null ? found.averageSpeed.magnitude : 0f;
            _pace = (natural > 0.2f ? natural : 1.4f) * speed;
            _to = to;
            _then = then;
            _walking = true;
            Play(clip, 0.25f, speed);
            Face(to);
            return this;
        }

        /// <summary>Turns, at a person's pace, to face <paramref name="point"/>.</summary>
        public Puppet Face(Vector3 point, float degreesPerSecond = 220f)
        {
            Vector3 flat = point - transform.position;
            if (transform.parent != null) flat = transform.parent.InverseTransformDirection(flat);
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.0001f) _turnTo = Quaternion.LookRotation(flat).eulerAngles.y;
            _turnRate = degreesPerSecond;
            return this;
        }

        /// <summary>Puts <paramref name="prop"/> in a hand until <see cref="Drop"/>.</summary>
        public void Hold(Transform prop, bool right = true)
        {
            _held = prop;
            _heldRight = right;
        }

        public void Drop() => _held = null;

        public Transform Bone(HumanBodyBones bone) => Animator.GetBoneTransform(bone);

        /// <summary>The point between the eyes, near enough: what a close-up aims at.</summary>
        public Vector3 Eyes
        {
            get
            {
                Transform head = Bone(HumanBodyBones.Head);
                return head != null ? head.position + Vector3.up * 0.08f : transform.position + Vector3.up * 1.6f;
            }
        }

        void Update()
        {
            float dt = Time.deltaTime;
            _faded += dt;
            Blend();

            if (_now.IsValid() && !_loops && _speed != 0f)
            {
                double time = _now.GetTime();
                if (time >= _length || time <= 0.0)
                {
                    _now.SetTime(Mathf.Clamp((float)time, 0f, _length));
                    _now.SetSpeed(0f);
                    _speed = 0f;
                    Finished = true;
                }
            }

            if (_walking)
            {
                Vector3 to = _to;
                to.y = transform.position.y;
                transform.position = Vector3.MoveTowards(transform.position, to, _pace * dt);
                if ((transform.position - to).sqrMagnitude < 0.0025f)
                {
                    _walking = false;
                    if (!string.IsNullOrEmpty(_then)) Play(_then, 0.3f);
                    if (_faceAfter.HasValue) Face(_faceAfter.Value);
                }
            }

            // In the parent's space: on the Marisol's deck that is the deck, pitching.
            transform.localRotation = Quaternion.Euler(0f, Mathf.MoveTowardsAngle(transform.localEulerAngles.y, _turnTo, _turnRate * dt), 0f);

            if (Ground != null)
            {
                Vector3 at = transform.position;
                at.y = Ground(at);
                transform.position = at;
            }
        }

        void LateUpdate()
        {
            if (_held == null) return;

            // After the animation: in the palm, level, square to the body.
            Transform hand = Bone(_heldRight ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
            if (hand == null) return;
            _held.SetPositionAndRotation(hand.position - Vector3.up * 0.06f + transform.forward * 0.04f, transform.rotation);
        }

        void Blend()
        {
            float w = _fade <= 0f ? 1f : Mathf.Clamp01(_faded / _fade);
            _mixer.SetInputWeight(0, w);
            _mixer.SetInputWeight(1, _was.IsValid() ? 1f - w : 0f);
        }

        void OnDestroy()
        {
            if (_graph.IsValid()) _graph.Destroy();
        }
    }
}
