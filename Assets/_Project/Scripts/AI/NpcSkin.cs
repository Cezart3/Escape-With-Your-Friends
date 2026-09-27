using System;
using System.Collections.Generic;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Player;
using FishNet.Object;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.AI
{
    /// <summary>
    /// The natives, the castaway and the barman in the players' bodies (#76, docs/ART-PLAN.md T10).
    ///
    /// <see cref="CharacterSkin"/> without the ragdoll: nobody here goes limp, so the animator has the
    /// body the whole time and all this does is tell it how fast the root is going, whether it is in
    /// a seat (the castaway, holding on in the plane), carrying (a native with somebody over its
    /// shoulder) or dead (a native, until it despawns). All of that is read off state every peer
    /// already has - the transform, the castaway's stage, the carry socket, <see cref="Health"/> -
    /// so nothing here is networked.
    ///
    /// Which body is worn is the object id, the same on every peer, so a camp is not four twins.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class NpcSkin : MonoBehaviour
    {
        [Serializable]
        public class Body
        {
            public GameObject Root;
            public Animator Animator;
            public Renderer Band;

            /// <summary>Metres from the feet to the crown at <see cref="Scale"/>.</summary>
            public float Height;

            /// <summary>The root's scale as imported. <see cref="Fit"/> scales from it.</summary>
            public Vector3 Scale;
        }

        static readonly int SpeedId = Animator.StringToHash("Speed");
        static readonly int SeatedId = Animator.StringToHash("Seated");
        static readonly int DeadId = Animator.StringToHash("Dead");

        /// <summary>Every skin that is drawing. The harness reads it.</summary>
        internal static readonly List<NpcSkin> Live = new();

        [SerializeField] Body[] _bodies = Array.Empty<Body>();

        [Tooltip("Where this NPC holds a body, if it ever does. Anything parented here is being carried.")]
        [SerializeField] Transform _carrySocket;

        Health _health;
        NetworkObject _network;
        Castaway _castaway;

        int _active = -1;
        Vector3 _wasAt;
        float _speed;
        float _carryWeight;

        internal Body[] Bodies => _bodies;
        internal Body Active => _active >= 0 && _active < _bodies.Length ? _bodies[_active] : null;

        public void Configure(Body[] bodies, Transform carrySocket)
        {
            _bodies = bodies;
            _carrySocket = carrySocket;
        }

        void Awake()
        {
            bool headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

            if (!CharacterSkin.Wanted(headless, CommandLine.HasFlag("-skinTest")) || _bodies.Length == 0)
            {
                foreach (Body body in _bodies)
                    if (body.Root != null) Destroy(body.Root);

                _bodies = Array.Empty<Body>();
                enabled = false;
                return;
            }

            if (headless)
                foreach (Body body in _bodies)
                    body.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            TryGetComponent(out _health);
            TryGetComponent(out _network);
            TryGetComponent(out _castaway);
        }

        void OnEnable() => Live.Add(this);
        void OnDisable() => Live.Remove(this);

        /// <summary>
        /// A native's role: every body scaled to its height, and its warpaint on the band - the one
        /// thing that tells a spearman from a blowgunner at forty metres.
        /// </summary>
        public void Fit(float height, Color mark)
        {
            var block = new MaterialPropertyBlock();

            foreach (Body body in _bodies)
            {
                if (body.Root == null) continue;

                body.Root.transform.localScale = body.Scale * (height / Mathf.Max(0.1f, body.Height));

                body.Band.GetPropertyBlock(block);
                block.SetColor("_BaseColor", mark);
                block.SetColor("_Color", mark);
                body.Band.SetPropertyBlock(block);
            }
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // Picked once the id is real: before the spawn it is the same placeholder on every object.
            if (_active < 0)
            {
                if (_bodies.Length > 1 && _network != null && !_network.IsSpawned) return;

                _active = _network != null ? _network.ObjectId % _bodies.Length : 0;
                for (int i = 0; i < _bodies.Length; i++)
                    _bodies[i].Root.SetActive(i == _active);

                _wasAt = transform.position;
                return;
            }

            Animator animator = _bodies[_active].Animator;

            Vector3 moved = transform.position - _wasAt;
            _wasAt = transform.position;

            // The castaway rides parented into the plane, but only on the server: the parent is not
            // synchronised. The stage is.
            bool seated = _castaway != null && _castaway.Where == Castaway.Stage.Aboard;
            bool dead = _health != null && _health.State == LifeState.Dead;

            float measured = Mathf.Min(new Vector2(moved.x, moved.z).magnitude / dt, 12f);
            _speed = Mathf.Lerp(_speed, measured, 1f - Mathf.Exp(-8f * dt));

            animator.SetFloat(SpeedId, seated || dead ? 0f : _speed);
            animator.SetBool(SeatedId, seated);
            animator.SetBool(DeadId, dead);

            if (animator.layerCount > 1)
            {
                bool carrying = !dead && _carrySocket != null && _carrySocket.childCount > 0;
                _carryWeight = Mathf.MoveTowards(_carryWeight, carrying ? 1f : 0f, dt * 4f);
                animator.SetLayerWeight(1, _carryWeight);
            }
        }
    }
}
