using System;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Vehicles;
using EscapeWithYourFriends.World;
using FishNet.Object;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// A person over the ragdoll (#76, #77, docs/ART-PLAN.md T9).
    ///
    /// The physics skeleton stays exactly what it was - eleven primitives, their joints, their
    /// masses - and stops being drawn. What is drawn is a skinned Quaternius body, which lives two
    /// lives: standing, an <see cref="Animator"/> plays the library's clips on it and the physics
    /// bones are nobody's business; limp, the animator is switched off and each of its big bones is
    /// laid along the physics bone it corresponds to, so the body you see is the body the solver is
    /// throwing. Getting up is a short blend from the last limp pose into whatever the animator
    /// says, so nobody snaps upright.
    ///
    /// The prefab carries several bodies and wears the one its colour slot picks, because four copies
    /// of one body in four headbands is a costume party rather than four people. The colour itself
    /// goes on the headband: the bodies are textured, and tinting a texture turns skin green.
    ///
    /// Nothing here is networked. Every peer already has the position, the ragdoll state and the
    /// weapon events, and derives the same pose from them - the same bargain as
    /// <see cref="BodyAnimator"/>, which still poses the (now invisible) physics bones.
    ///
    /// After the ragdoll and the motor (order 100), so the pose written here is the frame's last word.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class CharacterSkin : MonoBehaviour
    {
        /// <summary>One body, and how it hangs on the physics skeleton. Written by PlayerPrefabBuilder.</summary>
        [Serializable]
        public class Body
        {
            public GameObject Root;
            public Animator Animator;

            /// <summary>The skinned meshes. Not the band, which is the only thing tinted.</summary>
            public Renderer[] Renderers;
            public Renderer Band;

            /// <summary>Per link, parents first: the model bone that follows a physics bone.</summary>
            public Transform[] Bones;

            /// <summary>Per link: the model bone that <see cref="Bones"/> points at. The harness measures along it.</summary>
            public Transform[] To;

            public Transform[] Physics;
            public Transform[] PhysicsTo;

            /// <summary>Per link: the model bone's rotation relative to its physics bone, at rest.</summary>
            public Quaternion[] Offsets;

            public Transform Hips;

            /// <summary>Where the model's hips sit, in the physics hips' space.</summary>
            public Vector3 HipsOffset;
        }

        /// <summary>Vertical speed above which the body counts as off the ground. Same as BodyAnimator's.</summary>
        const float AirborneSpeed = 4f;

        /// <summary>Seconds from lying down to the animator's pose.</summary>
        const float RiseTime = 0.35f;

        static readonly int SpeedId = Animator.StringToHash("Speed");
        static readonly int AirborneId = Animator.StringToHash("Airborne");
        static readonly int SeatedId = Animator.StringToHash("Seated");
        static readonly int PunchId = Animator.StringToHash("Punch");
        static readonly int ArmedId = Animator.StringToHash("Armed");
        static readonly int FireId = Animator.StringToHash("Fire");
        static readonly int SwimmingId = Animator.StringToHash("Swimming");
        static readonly int ReloadId = Animator.StringToHash("Reload");

        /// <summary>
        /// Metres above the feet at which the water counts as having taken the body off the ground.
        /// The same probe as SurvivalStats' warmth, so the body swims exactly when it is cold.
        /// </summary>
        const float SwimProbe = 0.9f;

        /// <summary>The controller's layers over the arms (CharacterArt.BuildController).</summary>
        const int CarryLayer = 1, ArmedLayer = 2;

        [SerializeField] Body[] _bodies = Array.Empty<Body>();

        RagdollController _ragdoll;
        CarrySystem _carry;
        VehicleRider _rider;
        Weapon _weapon;
        PlayerIdentity _identity;
        NetworkObject _network;
        Health _health;
        Inventory _inventory;

        // The selected weapon, in the right hand (see Hold).
        GameObject _held;
        Transform _muzzle;

        /// <summary>The tip of the gun in the hand, where a shot visibly leaves from (#207). Null with no gun.</summary>
        internal Transform Muzzle => _muzzle;

        // The same model again, low and right in front of the owner's camera, where the hand's copy
        // is only a shadow (see ViewModel). Built for every player, shown only in your own first person.
        Transform _viewRoot;
        GameObject _view;
        Transform _viewMuzzle;
        float _kick;

        /// <summary>The first-person model's tip while it is on screen, else null. The shot is drawn from here.</summary>
        internal Transform ViewMuzzle => _viewRoot != null && _viewRoot.gameObject.activeInHierarchy ? _viewMuzzle : null;

        /// <summary>The first-person model, whether or not it is shown. The harness reads it.</summary>
        internal GameObject View => _view;
        GameObject _heldPrefab;
        Transform _heldHand;
        Renderer[] _heldRenderers = Array.Empty<Renderer>();

        /// <summary>What the hand holds, as the controller's Armed parameter: 0 nothing, 1 a gun, 2 a blade.</summary>
        int _armed;

        // Every transform of every body and its bind pose, captured before anything animates them.
        // Restored under the limp pose, so the bones between the linked ones - spine, neck,
        // collarbones, hands - sit as modelled rather than wherever the last clip left them.
        Transform[][] _all;
        Quaternion[][] _rest;

        int _active = -1;
        Vector3 _wasAt;
        float _speed;
        float _carryWeight;
        float _armedWeight;
        float _rise;
        Quaternion[] _last;
        Vector3 _lastHips;
        bool? _hidden;

        internal Body[] Bodies => _bodies;
        internal Body Active => _active >= 0 && _active < _bodies.Length ? _bodies[_active] : null;

        /// <summary>The weapon drawn in the hand, if any. The harness reads it.</summary>
        internal GameObject Held => _held;

        /// <summary>How far the arms are into holding it. The harness reads it.</summary>
        internal float ArmedWeight => _armedWeight;

        /// <summary>Whether the body is following the ragdoll this frame. The harness reads it.</summary>
        internal bool Tracking { get; private set; }

        internal float Speed => _speed;

        /// <summary>Harness overrides: what the body would do while walking, or carrying.</summary>
        internal float? ForceSpeed { get; set; }
        internal bool ForceCarry { get; set; }
        internal bool ForceSwim { get; set; }

        public void Configure(Body[] bodies) => _bodies = bodies;

        /// <summary>
        /// Whether to keep the bodies at all. A headless host draws nothing, and fifteen thousand
        /// skinned triangles and an animator per player are pure cost there - except under
        /// <c>-skinTest</c>, which is the only way this class ever runs in a terminal.
        /// </summary>
        public static bool Wanted(bool headless, bool testing) => !headless || testing;

        void Awake()
        {
            bool headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

            if (!Wanted(headless, CommandLine.HasFlag("-skinTest")) || _bodies.Length == 0)
            {
                foreach (Body body in _bodies)
                    if (body.Root != null) Destroy(body.Root);

                _bodies = Array.Empty<Body>();
                enabled = false;
                return;
            }

            _all = new Transform[_bodies.Length][];
            _rest = new Quaternion[_bodies.Length][];

            for (int i = 0; i < _bodies.Length; i++)
            {
                _all[i] = _bodies[i].Root.GetComponentsInChildren<Transform>(true);
                _rest[i] = Array.ConvertAll(_all[i], t => t.localRotation);

                // Nothing is visible without a screen, and a culled animator does not move bones.
                if (headless) _bodies[i].Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }

            TryGetComponent(out _ragdoll);
            TryGetComponent(out _carry);
            TryGetComponent(out _rider);
            TryGetComponent(out _weapon);
            TryGetComponent(out _identity);
            TryGetComponent(out _network);
            TryGetComponent(out _health);
            TryGetComponent(out _inventory);

            if (_weapon != null) { _weapon.Attacked += OnAttacked; _weapon.Reloading += OnReloading; }
            if (_identity != null) _identity.IdentityChanged += OnIdentity;
            if (_inventory != null) _inventory.Changed += Hold;

            Show(_identity != null ? _identity.ColorIndex % _bodies.Length : 0);
            _wasAt = transform.position;
        }

        void OnDestroy()
        {
            if (_weapon != null) { _weapon.Attacked -= OnAttacked; _weapon.Reloading -= OnReloading; }
            if (_identity != null) _identity.IdentityChanged -= OnIdentity;
            if (_inventory != null) _inventory.Changed -= Hold;
            if (_viewRoot != null) Destroy(_viewRoot.gameObject);
        }

        void OnIdentity(PlayerIdentity identity)
        {
            int wanted = identity.ColorIndex % _bodies.Length;
            if (wanted != _active) Show(wanted);
        }

        void OnAttacked(WeaponDef weapon)
        {
            // Not from a seat: the state machine will not punch there, and a trigger nobody takes
            // stays set, so the swing would play on the way out of the car. Something in the hand is
            // used by the arms; bare fists are the whole body's punch.
            if (Active == null || (_rider != null && _rider.IsSeated)) return;
            _kick = 1f;

            Animator animator = Active.Animator;
            if (_armed > 0 && animator.layerCount > ArmedLayer) animator.SetTrigger(FireId);
            else if (weapon != null && weapon.Kind == WeaponKind.Melee) animator.SetTrigger(PunchId);
        }

        /// <summary>
        /// The reload starts on every peer, off the weapon's existing observers call, so nothing new is
        /// networked. Only a gun in the hand takes it (Armed 1): a trigger nobody takes stays set and
        /// would play the reload the next time a gun is drawn.
        /// </summary>
        internal void OnReloading(WeaponDef weapon, bool started)
        {
            if (!started || Active == null || _armed != 1 || (_rider != null && _rider.IsSeated)) return;

            Animator animator = Active.Animator;
            if (animator.layerCount > ArmedLayer) animator.SetTrigger(ReloadId);
        }

        void Show(int index)
        {
            for (int i = 0; i < _bodies.Length; i++)
                _bodies[i].Root.SetActive(i == index);

            _active = index;
            _last = new Quaternion[_bodies[index].Bones.Length];
            _rise = 0f;
            _hidden = null;
            Hold();
        }

        static Transform Hand(Body body)
            => body.Animator != null && body.Animator.avatar != null && body.Animator.avatar.isHuman
                ? body.Animator.GetBoneTransform(HumanBodyBones.RightHand)
                : null;

        /// <summary>
        /// Draws the selected item in the right hand. Read off the replicated inventory, so every peer
        /// draws everybody's, and nothing new is networked. A weapon wears its view prefab, the one it
        /// also wears on the ground; any other item wears its ground model, shrunk to fit a hand.
        ///
        /// The grip is read off the hand itself, so it is right in any pose the clip has it in: a gun
        /// points from the wrist through the knuckles with its top to the thumb, and a blade stands out
        /// of the fist on the thumb side with its edge the way the knuckles face. The hand closes on
        /// the back of a gun's body, the end of a blade's handle, or the middle of anything else
        /// (<see cref="GripPoint"/>).
        /// </summary>
        void Hold()
        {
            Body body = Active;
            ItemDef item = _inventory != null && _inventory.SlotCount > 0 ? _inventory.Selected.Def : null;
            WeaponDef weapon = item != null && WeaponCatalog.Active != null ? WeaponCatalog.Active.ForItem(item) : null;
            GameObject prefab = weapon != null && weapon.ViewPrefab != null ? weapon.ViewPrefab : item != null ? item.WorldPrefab : null;
            Transform hand = body != null ? Hand(body) : null;
            bool melee = weapon != null && weapon.Kind == WeaponKind.Melee;
            _armed = prefab == null || hand == null || weapon == null ? 0 : melee ? 2 : 1;

            if (prefab == _heldPrefab && hand == _heldHand) return;

            if (_held != null) Destroy(_held);
            if (_view != null) Destroy(_view);
            _held = _view = null;
            _muzzle = _viewMuzzle = null;
            _heldPrefab = prefab;
            _heldHand = hand;
            _heldRenderers = Array.Empty<Renderer>();
            if (prefab == null || hand == null) return;

            string id = weapon != null ? weapon.Id : item.Id;
            _held = Model(prefab, $"Held ({id})");
            if (weapon == null) Shrink(_held.transform, _held, 0.3f);

            Transform knuckle = body.Animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            Transform thumb = body.Animator.GetBoneTransform(HumanBodyBones.RightThumbProximal);
            Vector3 along = knuckle != null ? knuckle.position - hand.position : hand.position - hand.parent.position;
            Vector3 side = Vector3.ProjectOnPlane(thumb != null ? thumb.position - hand.position : transform.up, along);
            Vector3 palm = knuckle != null ? Vector3.Lerp(hand.position, knuckle.position, 0.5f) : hand.position;

            _held.transform.rotation = melee ? Quaternion.LookRotation(side, -along) : Quaternion.LookRotation(along, side);
            _held.transform.position += palm - _held.transform.TransformPoint(GripPoint(_held, melee, weapon == null));
            _held.transform.SetParent(hand, true);
            _heldRenderers = _held.GetComponentsInChildren<Renderer>(true);
            if (weapon != null && !melee) _muzzle = Tip(_held);

            ViewModel(prefab, id, weapon, melee);

            // Now, not at LateUpdate: otherwise the owner sees their own weapon for one frame.
            _hidden = null;
            Visibility(body);
        }

        /// <summary>A copy of an item's model to hold: nothing on it may collide or fall.</summary>
        static GameObject Model(GameObject prefab, string name)
        {
            GameObject model = Instantiate(prefab);
            model.name = name;

            // A collider under the hand would join the player's own and catch every ray aimed past it.
            foreach (Collider collider in model.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
                Destroy(collider);
            }
            foreach (Rigidbody rigidbody in model.GetComponentsInChildren<Rigidbody>(true))
            {
                rigidbody.isKinematic = true;
                Destroy(rigidbody);
            }

            return model;
        }

        /// <summary>Scales a model down until its longest side, measured in <paramref name="space"/>, is at most <paramref name="longest"/>.</summary>
        static void Shrink(Transform space, GameObject model, float longest)
        {
            if (WorldItem.Drawn(space, model) is not Bounds drawn) return;
            float size = Mathf.Max(drawn.size.x, drawn.size.y, drawn.size.z);
            if (size > longest) model.transform.localScale *= longest / size;
        }

        /// <summary>The kit lays a gun along +z, muzzle forward: the tip is the front face, a little high.</summary>
        static Transform Tip(GameObject gun)
        {
            if (WorldItem.Drawn(gun.transform, gun) is not Bounds drawn) return null;

            var tip = new GameObject("Muzzle").transform;
            tip.SetParent(gun.transform, false);
            tip.localPosition = new Vector3(drawn.center.x, drawn.center.y + drawn.extents.y * 0.3f, drawn.max.z);
            return tip;
        }

        /// <summary>
        /// What you see of the thing in your own hand. The hand's copy cannot be it: the camera is
        /// inside the head, the arms swing with the clip, and a gun that points where the clip points
        /// rather than at the crosshair reads as broken. So a second copy hangs off the camera, low and
        /// right, the way every shooter draws it. A gun lies along the view with its back a little
        /// past the near plane, a blade stands up and forward, and anything else is turned three
        /// quarters so it reads as an object.
        /// </summary>
        void ViewModel(GameObject prefab, string id, WeaponDef weapon, bool melee)
        {
            if (_viewRoot == null)
            {
                _viewRoot = new GameObject($"{name} first-person view").transform;
                _viewRoot.gameObject.SetActive(false);
            }

            _view = Model(prefab, $"View ({id})");
            Transform model = _view.transform;
            model.SetParent(_viewRoot, false);
            model.localRotation = weapon == null ? Quaternion.Euler(20f, -35f, 0f)
                                  : melee ? Quaternion.Euler(-50f, -10f, 0f)
                                  : Quaternion.identity;
            Shrink(_viewRoot, _view, weapon == null ? 0.22f : 0.55f);

            if (WorldItem.Drawn(_viewRoot, _view) is Bounds drawn)
            {
                // Its back no nearer than 28 cm: the near plane is 15, and a clipped stock is a hole.
                var at = new Vector3(0.19f, -0.19f, Mathf.Max(0.28f + drawn.extents.z, 0.5f));
                model.localPosition += at - drawn.center;
            }

            foreach (Renderer renderer in _view.GetComponentsInChildren<Renderer>(true))
                renderer.shadowCastingMode = ShadowCastingMode.Off;

            if (weapon != null && !melee) _viewMuzzle = Tip(_view);
        }

        /// <summary>Follows the camera it hangs from, shows only in your own first person, kicks when fired.</summary>
        void PoseView(float dt)
        {
            if (_viewRoot == null) return;

            Camera camera = Camera.main;
            bool carrying = ForceCarry || (_carry != null && _carry.IsCarrying);
            bool shown = _view != null && camera != null && _hidden == true && !carrying
                         && (_ragdoll == null || !_ragdoll.IsRagdolled) && !StoryBeat.Playing;

            if (_viewRoot.gameObject.activeSelf != shown) _viewRoot.gameObject.SetActive(shown);
            if (!shown) return;

            // Parented rather than copied each frame: the camera moves in the brain's LateUpdate, and
            // a copy taken before it would trail a frame behind every turn of the head.
            if (_viewRoot.parent != camera.transform) _viewRoot.SetParent(camera.transform, false);

            _kick = Mathf.MoveTowards(_kick, 0f, dt * 7f);
            float bob = Mathf.Sin(Time.time * 9f) * 0.006f * Mathf.Clamp01(_speed / 4f);
            _viewRoot.localPosition = new Vector3(0f, bob, -0.05f * _kick);
            _viewRoot.localRotation = Quaternion.Euler(-7f * _kick, 0f, 0f);
        }

        /// <summary>Where the hand closes on a model, in its own space. It lies along +z, tip forward (WeaponFactory).</summary>
        static Vector3 GripPoint(GameObject model, bool melee, bool plain)
        {
            if (WorldItem.Drawn(model.transform, model) is not Bounds drawn) return Vector3.zero;
            if (plain) return drawn.center;
            return melee
                ? new Vector3(drawn.center.x, drawn.center.y, drawn.min.z + drawn.size.z * 0.1f)
                : new Vector3(drawn.center.x, drawn.min.y + drawn.size.y * 0.3f, drawn.center.z - drawn.size.z * 0.2f);
        }

        void LateUpdate()
        {
            Body body = Active;
            float dt = Time.deltaTime;
            if (body == null || dt <= 0f) return;

            Vector3 moved = transform.position - _wasAt;
            _wasAt = transform.position;

            if (_ragdoll != null && _ragdoll.IsRagdolled)
            {
                Follow(body);
            }
            else
            {
                // Getting up moves the root to where the hips landed, metres in one frame. That is not
                // a sprint, and a drop to lower ground is not a jump.
                if (_rise >= RiseTime) moved = Vector3.zero;

                Drive(body, moved, dt);
                if (_rise > 0f) Rise(body, dt);
            }

            Visibility(body);
            PoseView(dt);
        }

        /// <summary>Lays the model along the physics skeleton.</summary>
        void Follow(Body body)
        {
            Tracking = true;
            if (body.Animator.enabled) body.Animator.enabled = false;

            Transform[] all = _all[_active];
            Quaternion[] rest = _rest[_active];
            for (int i = 0; i < all.Length; i++) all[i].localRotation = rest[i];

            // Parents first, so each write lands after the one it hangs from.
            for (int i = 0; i < body.Bones.Length; i++)
            {
                body.Bones[i].rotation = body.Physics[i].rotation * body.Offsets[i];
                _last[i] = body.Bones[i].rotation;
            }

            body.Hips.position = body.Physics[0].TransformPoint(body.HipsOffset);
            _lastHips = body.Hips.position;
            _rise = RiseTime;
        }

        void Drive(Body body, Vector3 moved, float dt)
        {
            Tracking = false;
            Animator animator = body.Animator;
            if (!animator.enabled) animator.enabled = true;

            // Measured rather than asked for, like BodyAnimator: a spectator's copy of this body only
            // knows where it is, and that is enough. Capped, so a respawn is not a sprint.
            float measured = Mathf.Min(new Vector2(moved.x, moved.z).magnitude / dt, 12f);
            _speed = Mathf.Lerp(_speed, ForceSpeed ?? measured, 1f - Mathf.Exp(-8f * dt));

            // A seat moves the body at the speed of the car it is in, which is not walking.
            bool seated = _rider != null && _rider.IsSeated;
            animator.SetFloat(SpeedId, seated ? 0f : _speed);
            animator.SetBool(AirborneId, !seated && Mathf.Abs(moved.y / dt) > AirborneSpeed);
            animator.SetBool(SeatedId, seated);

            // Waist-deep is swimming, wherever the player is; a seat is dry and a car floats on its own.
            bool swimming = !seated && (ForceSwim || WaterSurface.IsSubmerged(transform.position + Vector3.up * SwimProbe));
            animator.SetBool(SwimmingId, swimming);

            bool carrying = ForceCarry || (_carry != null && _carry.IsCarrying);
            if (animator.layerCount > CarryLayer)
            {
                _carryWeight = Mathf.MoveTowards(_carryWeight, carrying ? 1f : 0f, dt * 4f);
                animator.SetLayerWeight(CarryLayer, _carryWeight);
            }

            // Both hands on a load, or on a wheel, is not holding a weapon: it is put away, and
            // Armed keeps its last value while the layer fades, so the arms leave the pose they were in.
            if (_held != null && _held.activeSelf == (carrying || seated)) _held.SetActive(!carrying && !seated);
            if (animator.layerCount > ArmedLayer)
            {
                _armedWeight = Mathf.MoveTowards(_armedWeight, _armed > 0 && !carrying && !seated ? 1f : 0f, dt * 6f);
                animator.SetLayerWeight(ArmedLayer, _armedWeight);
                if (_armed > 0) animator.SetInteger(ArmedId, _armed);
            }
        }

        /// <summary>From the last limp pose into the animator's, over <see cref="RiseTime"/>.</summary>
        void Rise(Body body, float dt)
        {
            _rise = Mathf.Max(0f, _rise - dt);
            float t = 1f - _rise / RiseTime;

            for (int i = 0; i < body.Bones.Length; i++)
                body.Bones[i].rotation = Quaternion.Slerp(_last[i], body.Bones[i].rotation, t);

            body.Hips.position = Vector3.Lerp(_lastHips, body.Hips.position, t);
        }

        /// <summary>
        /// Your own body is shadow only while you are alive: the camera is inside its head. Dead, the
        /// camera pulls out to third person and you get to watch it.
        /// </summary>
        void Visibility(Body body)
        {
            bool hide = _network != null && _network.IsOwner
                        && (_health == null || _health.State != LifeState.Dead)
                        && (_rider == null || !_rider.IsSeated); // Seated, the camera is a chase camera.
            if (_hidden == hide) return;

            _hidden = hide;
            ShadowCastingMode mode = hide ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;

            foreach (Renderer renderer in body.Renderers)
                if (renderer != null) renderer.shadowCastingMode = mode;

            if (body.Band != null) body.Band.shadowCastingMode = mode;

            foreach (Renderer renderer in _heldRenderers)
                if (renderer != null) renderer.shadowCastingMode = mode;
        }
    }
}
