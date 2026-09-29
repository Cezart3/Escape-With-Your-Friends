using System.Collections.Generic;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Player;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// Everything a shot looks like: the tracer, the flash at the muzzle, what flies off where it
    /// lands, and the hitmarker on the shooter's screen (#207). Purely cosmetic, and deliberately so.
    ///
    /// The server has already decided what was hit by the time this runs - <c>Weapon.Fired</c> carries
    /// the origin and the end of every ray *after* the raycast, so a tracer can only ever draw
    /// something that has already happened. That is the whole design: **where the tracer is drawn is a
    /// lie the client tells, where the damage landed is the server's raycast**, and keeping the two
    /// apart is what stops a modified client from turning a visual effect into a hit.
    ///
    /// Only the start is a lie. The server's rays leave from the eye, and a tracer from the eye is a
    /// dot in the middle of the shooter's screen and a line out of everybody else's forehead, which
    /// is most of why hits did not read as hits. It is drawn from the muzzle instead: the gun in the
    /// hand for everybody else, a point low and right of the camera for the shooter, whose own gun is
    /// hidden. The end is the server's, to the centimetre (GunTest checks it).
    ///
    /// The body hits arrive first: <c>Weapon.ApplyHit</c> sends its observers RPC before the shot's,
    /// so by the time the ends come in, the ones that drew blood are known. Every other end that
    /// touches something throws dust; one that stopped in the air at the gun's range throws nothing.
    ///
    /// It runs on every peer, not just the shooter, because a shot you did not fire is the one you
    /// most need to see. Lines and bits are pooled, because a shotgun makes eight at once and a
    /// submachine gun makes thirteen a second.
    /// </summary>
    public class TracerEffect : MonoBehaviour
    {
        [SerializeField] Weapon _weapon;

        [Tooltip("Unlit material for the line. Created by PlayerPrefabBuilder.")]
        [SerializeField] Material _material;

        [Tooltip("Seconds a tracer stays visible. Long enough to read, short enough not to clutter.")]
        [SerializeField] float _lifetime = 0.09f;

        [SerializeField] float _width = 0.025f;

        [Tooltip("Colour at the muzzle. Fades to transparent along the line and over its lifetime.")]
        [SerializeField] Color _colour = new(1f, 0.85f, 0.45f, 0.9f);

        const float FlashSeconds = 0.05f, BitSeconds = 0.55f, MarkerSeconds = 0.18f;
        static readonly Color Blood = new(0.45f, 0.03f, 0.03f), Dust = new(0.62f, 0.55f, 0.42f);

        readonly List<LineRenderer> _pool = new();
        readonly List<float> _expiry = new();
        readonly List<Vector3> _bodyHits = new();

        struct Bit
        {
            public Transform Transform;
            public Vector3 Velocity;
            public float Size, Born;
        }

        readonly List<Bit> _bits = new();
        Material _bloodMaterial, _dustMaterial;

        Transform _root, _flash;
        Light _flashLight;
        float _flashUntil, _markerUntil;
        CharacterSkin _skin;

        void Awake()
        {
            if (_weapon == null) _weapon = GetComponent<Weapon>();
            _skin = GetComponent<CharacterSkin>();

            // Parented to the scene rather than to the player: a tracer is a mark left in the world,
            // and one that slid sideways because the shooter kept running would read as a laser sight.
            _root = new GameObject($"{name} tracers").transform;
        }

        void OnEnable()
        {
            if (_weapon == null) return;
            _weapon.Fired += OnFired;
            _weapon.HitLanded += OnHitLanded;
        }

        void OnDisable()
        {
            if (_weapon == null) return;
            _weapon.Fired -= OnFired;
            _weapon.HitLanded -= OnHitLanded;
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

            if (_flash != null && _flash.gameObject.activeSelf && Time.time >= _flashUntil)
                _flash.gameObject.SetActive(false);

            // Bits fall, tumble a little and shrink away. No rigidbodies: forty of them a burst would
            // be forty bodies for half a second each, for a thing nobody could tell from this.
            for (int i = 0; i < _bits.Count; i++)
            {
                Bit bit = _bits[i];
                if (!bit.Transform.gameObject.activeSelf) continue;

                float age = (Time.time - bit.Born) / BitSeconds;
                if (age >= 1f)
                {
                    bit.Transform.gameObject.SetActive(false);
                    continue;
                }

                bit.Velocity += Physics.gravity * Time.deltaTime;
                bit.Transform.position += bit.Velocity * Time.deltaTime;
                bit.Transform.localScale = Vector3.one * bit.Size * (1f - age * age);
                _bits[i] = bit;
            }
        }

        void OnHitLanded(Vector3 contact)
        {
            if (Application.isBatchMode) return;

            _bodyHits.Add(contact);
            WeaponDef weapon = _weapon.Equipped;
            if (weapon == null || weapon.Kind != WeaponKind.Hitscan) return;

            Burst(contact, Random.onUnitSphere + Vector3.up, Blood, 8, 0.035f, 2.2f);
            if (_weapon.IsOwner)
            {
                _markerUntil = Time.time + MarkerSeconds;
                Audio.Sfx.Play2D(Audio.Sound.Click, 0.7f, 1.9f);
            }
        }

        void OnFired(Vector3 origin, Vector3[] ends)
        {
            // A headless build has no renderers worth feeding, and a dedicated server would otherwise
            // spend a shotgun blast building line meshes nobody will ever look at.
            if (ends == null || Application.isBatchMode) return;

            WeaponDef weapon = _weapon != null ? _weapon.Equipped : null;
            float width = weapon != null && weapon.Pellets > 1 ? _width * 0.6f : _width;
            // Half a metre from the shooter's eye, a bullet's width is a plank's.
            if (_weapon.IsOwner) width *= 0.3f;
            Vector3 muzzle = Muzzle(origin);
            Flash(muzzle);

            foreach (Vector3 end in ends)
            {
                if (end == Vector3.zero) continue;

                LineRenderer line = Take();
                if (line == null) break;

                line.startWidth = width;
                line.endWidth = width * 0.4f;
                line.SetPosition(0, muzzle);
                line.SetPosition(1, end);
                line.enabled = true;

                bool bled = _bodyHits.Exists(c => (c - end).sqrMagnitude < 0.0001f);
                bool struck = weapon != null && (end - origin).sqrMagnitude < weapon.Range * weapon.Range * 0.99f;
                if (!bled && struck) Burst(end, (origin - end).normalized + Vector3.up * 0.6f, Dust, 6, 0.05f, 1.6f);
            }

            _bodyHits.Clear();
        }

        /// <summary>Where the shot is seen to leave from. See the class comment.</summary>
        Vector3 Muzzle(Vector3 eye)
        {
            if (_weapon.IsOwner && Camera.main != null)
            {
                Transform view = Camera.main.transform;
                return view.position + view.right * 0.14f - view.up * 0.11f + view.forward * 0.6f;
            }

            return _skin != null && _skin.Muzzle != null ? _skin.Muzzle.position : eye;
        }

        void Flash(Vector3 at)
        {
            if (_flash == null)
            {
                GameObject flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(flash.GetComponent<Collider>());
                flash.name = "Muzzle flash";
                flash.transform.SetParent(_root, false);
                flash.transform.localScale = new Vector3(0.035f, 0.035f, 0.07f);
                var renderer = flash.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = new Material(_material) { color = new Color(0.9f, 0.6f, 0.25f) };
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                _flashLight = flash.AddComponent<Light>();
                _flashLight.type = LightType.Point;
                _flashLight.range = 5f;
                _flashLight.intensity = 4f;
                _flashLight.color = new Color(1f, 0.8f, 0.5f);
                _flash = flash.transform;
            }

            _flash.position = at;
            if (Camera.main != null) _flash.rotation = Camera.main.transform.rotation;
            _flash.gameObject.SetActive(true);
            _flashUntil = Time.time + FlashSeconds;
        }

        /// <summary>A handful of small cubes thrown from a point: dust off sand and wood, blood off a body.</summary>
        void Burst(Vector3 at, Vector3 away, Color colour, int count, float size, float speed)
        {
            Material material = colour == Blood ? _bloodMaterial ??= Lit(Blood) : _dustMaterial ??= Lit(Dust);

            for (int n = 0; n < count; n++)
            {
                int i = _bits.FindIndex(b => !b.Transform.gameObject.activeSelf);
                if (i < 0)
                {
                    if (_bits.Count >= 96) return;
                    GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Destroy(cube.GetComponent<Collider>());
                    cube.name = "Impact bit";
                    cube.transform.SetParent(_root, false);
                    cube.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    _bits.Add(new Bit { Transform = cube.transform });
                    i = _bits.Count - 1;
                }

                Bit bit = _bits[i];
                bit.Transform.GetComponent<MeshRenderer>().sharedMaterial = material;
                bit.Transform.SetPositionAndRotation(at, Random.rotation);
                bit.Size = size * Random.Range(0.5f, 1.2f);
                bit.Transform.localScale = Vector3.one * bit.Size;
                bit.Velocity = (away.normalized + Random.insideUnitSphere * 0.8f) * speed * Random.Range(0.5f, 1.2f);
                bit.Born = Time.time;
                bit.Transform.gameObject.SetActive(true);
                _bits[i] = bit;
            }
        }

        /// <summary>The island's own shader, which ships because every art material wears it (see WorldItem.Greybox).</summary>
        static Material Lit(Color colour)
        {
            Shader shader = Shader.Find("EWYF/Stylized");
            return new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Unlit")) { color = colour };
        }

        /// <summary>A cross that opens round the crosshair when your shot lands. Sound goes with it.</summary>
        void OnGUI()
        {
            if (Time.time >= _markerUntil || !_weapon.IsOwner) return;

            float t = 1f - (_markerUntil - Time.time) / MarkerSeconds;
            var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            float gap = 7f + 5f * t, length = 9f;
            Color before = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 1f - t * 0.6f);

            for (int corner = 0; corner < 4; corner++)
            {
                float angle = 45f + corner * 90f;
                Matrix4x4 matrix = GUI.matrix;
                GUIUtility.RotateAroundPivot(angle, centre);
                GUI.DrawTexture(new Rect(centre.x + gap, centre.y - 1f, length, 2f), Texture2D.whiteTexture);
                GUI.matrix = matrix;
            }

            GUI.color = before;
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
