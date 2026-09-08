using System.Collections.Generic;
using EscapeWithYourFriends.Data;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// Draws the line a bullet took. Purely cosmetic, and deliberately so.
    ///
    /// The server has already decided what was hit by the time this runs - <c>Weapon.Fired</c> carries
    /// the origin and the end of every ray *after* the raycast, so a tracer can only ever draw
    /// something that has already happened. That is the whole design: **where the tracer is drawn is a
    /// lie the client tells, where the damage landed is the server's raycast**, and keeping the two
    /// apart is what stops a modified client from turning a visual effect into a hit.
    ///
    /// It runs on every peer, not just the shooter, because a shot you did not fire is the one you
    /// most need to see. One line renderer per pellet, pooled, because a shotgun makes eight at once
    /// and a submachine gun makes thirteen a second.
    /// </summary>
    public class TracerEffect : MonoBehaviour
    {
        [SerializeField] Weapon _weapon;

        [Tooltip("Unlit material for the line. Created by PlayerPrefabBuilder.")]
        [SerializeField] Material _material;

        [Tooltip("Seconds a tracer stays visible. Long enough to read, short enough not to clutter.")]
        [SerializeField] float _lifetime = 0.07f;

        [SerializeField] float _width = 0.02f;

        [Tooltip("Colour at the muzzle. Fades to transparent along the line and over its lifetime.")]
        [SerializeField] Color _colour = new(1f, 0.85f, 0.45f, 0.9f);

        readonly List<LineRenderer> _pool = new();
        readonly List<float> _expiry = new();

        Transform _root;

        void Awake()
        {
            if (_weapon == null) _weapon = GetComponent<Weapon>();

            // Parented to the scene rather than to the player: a tracer is a mark left in the world,
            // and one that slid sideways because the shooter kept running would read as a laser sight.
            _root = new GameObject($"{name} tracers").transform;
        }

        void OnEnable()
        {
            if (_weapon != null) _weapon.Fired += OnFired;
        }

        void OnDisable()
        {
            if (_weapon != null) _weapon.Fired -= OnFired;
        }

        void OnDestroy()
        {
            if (_root != null) Destroy(_root.gameObject);
        }

        void Update()
        {
            for (int i = 0; i < _pool.Count; i++)
                if (_pool[i] != null && _pool[i].enabled && Time.time >= _expiry[i])
                    _pool[i].enabled = false;
        }

        void OnFired(Vector3 origin, Vector3[] ends)
        {
            // A headless build has no renderers worth feeding, and a dedicated server would otherwise
            // spend a shotgun blast building line meshes nobody will ever look at.
            if (ends == null || Application.isBatchMode) return;

            WeaponDef weapon = _weapon != null ? _weapon.Equipped : null;
            float width = weapon != null && weapon.Pellets > 1 ? _width * 0.6f : _width;

            foreach (Vector3 end in ends)
            {
                if (end == Vector3.zero) continue;

                LineRenderer line = Take();
                if (line == null) return;

                line.startWidth = width;
                line.endWidth = width * 0.4f;
                line.SetPosition(0, origin);
                line.SetPosition(1, end);
                line.enabled = true;
            }
        }

        LineRenderer Take()
        {
            for (int i = 0; i < _pool.Count; i++)
            {
                if (_pool[i] == null || _pool[i].enabled) continue;

                _expiry[i] = Time.time + _lifetime;
                return _pool[i];
            }

            var go = new GameObject("Tracer");
            go.transform.SetParent(_root, worldPositionStays: false);

            var created = go.AddComponent<LineRenderer>();
            created.useWorldSpace = true;
            created.positionCount = 2;
            created.numCapVertices = 0;
            created.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            created.receiveShadows = false;
            created.sharedMaterial = _material;
            created.startColor = _colour;
            created.endColor = new Color(_colour.r, _colour.g, _colour.b, 0f);
            created.enabled = false;

            _pool.Add(created);
            _expiry.Add(Time.time + _lifetime);

            return created;
        }

        /// <summary>Bake time only.</summary>
        public void Configure(Weapon weapon, Material material)
        {
            _weapon = weapon;
            _material = material;
        }
    }
}
