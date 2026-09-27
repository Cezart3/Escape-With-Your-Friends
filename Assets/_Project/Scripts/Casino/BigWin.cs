using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>
    /// The noise a casino makes when somebody hits: a tier by how many times the stake came back,
    /// a fanfare, a fountain of gold coins, and a banner on the HUD that counts the win up.
    ///
    /// **Local, like every other sound and sight.** The game that paid already told every peer what
    /// it paid - a slot's seed, the wheel's win message - so each peer calls
    /// <see cref="Celebrate"/> from the code that already knows, at the moment its own screen
    /// settles. Nothing here is networked or can reach a wallet.
    ///
    /// The HUD banner pulls <see cref="Latest"/> rather than being told, the way the slot board
    /// reads a cabinet: whoever stands within <see cref="Near"/> of a hit sees it, so a friend's mega
    /// win is everybody's.
    ///
    /// The coins are forty-two pooled octagons with no collider and no shadow, integrated by hand
    /// against the floor they were thrown from: cheap enough for the 760M, and gone in two seconds.
    /// </summary>
    public class BigWin : MonoBehaviour
    {
        /// <summary>Distance from a hit within which the HUD shows its banner.</summary>
        public const float Near = 10f;

        const int PerTier = 14;
        const int Pool = PerTier * 3;
        const float CoinSize = 0.07f;

        /// <summary>The coins' material: the palette's gold, handed over by whichever cabinet woke first.</summary>
        internal static Material Gold;

        /// <summary>Celebrations on this peer, for the harness.</summary>
        internal static int Celebrated { get; private set; }

        /// <summary>The last hit on this peer: where, how much, how big and when. <see cref="Started"/> is negative before the first.</summary>
        public static (Vector3 Where, int Win, int Tier, float Started) Latest { get; private set; } = (Vector3.zero, 0, 0, -100f);

        static BigWin _instance;
        static Mesh _coinMesh;

        readonly Transform[] _coins = new Transform[Pool];
        readonly Vector3[] _velocity = new Vector3[Pool];
        readonly Vector3[] _spin = new Vector3[Pool];
        readonly float[] _floor = new float[Pool];
        readonly float[] _dies = new float[Pool];
        int _next;

        // ---------------------------------------------------------------- the arithmetic

        /// <summary>0 for an ordinary win, then big at 10 times the stake, mega at 25, epic at 50.</summary>
        public static int Tier(int win, int bet)
        {
            if (bet <= 0 || win <= 0) return 0;

            long x = win / bet;
            return x >= 50 ? 3 : x >= 25 ? 2 : x >= 10 ? 1 : 0;
        }

        public static string Name(int tier) => tier switch
        {
            1 => "BIG WIN",
            2 => "MEGA WIN",
            3 => "EPIC WIN",
            _ => "",
        };

        /// <summary>How long the banner counts up. A bigger win takes longer, which is the point.</summary>
        public static float Seconds(int tier) => tier switch
        {
            1 => 2.5f,
            2 => 3.5f,
            3 => 5f,
            _ => 0f,
        };

        /// <summary>The number on the banner <paramref name="elapsed"/> seconds in: fast at first, easing onto the win.</summary>
        public static int Counted(int win, float elapsed, float seconds)
        {
            if (seconds <= 0f || elapsed >= seconds) return win;
            if (elapsed <= 0f) return 0;

            float k = 1f - Mathf.Pow(1f - elapsed / seconds, 3f);
            return Mathf.Min(win, (int)(win * (double)k));
        }

        // ---------------------------------------------------------------- the show

        /// <summary>
        /// A win of <paramref name="win"/> on <paramref name="bet"/> landed at <paramref name="at"/>.
        /// Below the big tier this is the ordinary win chime; above it, the lot. Coins fall back to
        /// <paramref name="floor"/>.
        /// </summary>
        public static void Celebrate(Vector3 at, float floor, int win, int bet)
        {
            if (win <= 0) return;

            int tier = Tier(win, bet);
            if (tier == 0)
            {
                Audio.Sfx.Play(Audio.Sound.Win, at);
                return;
            }

            Celebrated++;
            Latest = (at, win, tier, Time.time);
            Audio.Sfx.Play(Audio.Sound.BigWin, at);

            // A headless peer has nobody to throw coins at.
            if (Gold == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;

            if (_instance == null)
            {
                var go = new GameObject("BigWin");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<BigWin>();
            }

            _instance.Burst(at, floor, tier * PerTier);
        }

        void Burst(Vector3 at, float floor, int count)
        {
            for (int n = 0; n < count; n++)
            {
                int i = _next;
                _next = (_next + 1) % Pool;

                if (_coins[i] == null) _coins[i] = Coin();

                float angle = Random.Range(0f, Mathf.PI * 2f);
                float reach = Random.Range(0.8f, 1.8f);

                _coins[i].gameObject.SetActive(true);
                _coins[i].position = at;
                _coins[i].localScale = Vector3.one * CoinSize;
                _coins[i].rotation = Random.rotation;
                _velocity[i] = new Vector3(Mathf.Cos(angle) * reach, Random.Range(3f, 4.5f), Mathf.Sin(angle) * reach);
                _spin[i] = Random.insideUnitSphere * 720f;
                _floor[i] = floor + 0.004f;
                _dies[i] = Time.time + Random.Range(1.4f, 1.9f);
            }
        }

        void Update()
        {
            float dt = Time.deltaTime;

            for (int i = 0; i < Pool; i++)
            {
                Transform coin = _coins[i];
                if (coin == null || !coin.gameObject.activeSelf) continue;

                float left = _dies[i] - Time.time;
                if (left <= 0f)
                {
                    coin.gameObject.SetActive(false);
                    continue;
                }

                _velocity[i] += Vector3.down * (9.81f * dt);
                Vector3 p = coin.position + _velocity[i] * dt;

                // One cheap bounce off the floor it came from, then a skid.
                if (p.y < _floor[i])
                {
                    p.y = _floor[i];
                    _velocity[i] = new Vector3(_velocity[i].x * 0.6f, -_velocity[i].y * 0.35f, _velocity[i].z * 0.6f);
                    _spin[i] *= 0.5f;
                }

                coin.position = p;
                coin.Rotate(_spin[i] * dt, Space.World);
                coin.localScale = Vector3.one * (CoinSize * Mathf.Clamp01(left / 0.3f));
            }
        }

        Transform Coin()
        {
            var go = new GameObject("Coin");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = _coinMesh ??= CoinMesh();

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = Gold;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go.transform;
        }

        /// <summary>An octagonal prism, unit wide and a sixth as thick: 32 triangles of coin.</summary>
        static Mesh CoinMesh()
        {
            const int sides = 8;
            const float half = 1f / 12f;

            var vertices = new System.Collections.Generic.List<Vector3>();
            var triangles = new System.Collections.Generic.List<int>();

            for (int face = 0; face < 2; face++)
            {
                float y = face == 0 ? half : -half;
                int centre = vertices.Count;
                vertices.Add(new Vector3(0f, y, 0f));

                for (int s = 0; s < sides; s++)
                {
                    float a = s * Mathf.PI * 2f / sides;
                    vertices.Add(new Vector3(Mathf.Cos(a) * 0.5f, y, Mathf.Sin(a) * 0.5f));
                }

                for (int s = 0; s < sides; s++)
                {
                    int a = centre + 1 + s, b = centre + 1 + (s + 1) % sides;
                    if (face == 0) triangles.AddRange(new[] { centre, b, a });
                    else triangles.AddRange(new[] { centre, a, b });
                }
            }

            // The rim, its own vertices so it shades flat.
            for (int s = 0; s < sides; s++)
            {
                float a0 = s * Mathf.PI * 2f / sides, a1 = (s + 1) * Mathf.PI * 2f / sides;
                int v = vertices.Count;
                vertices.Add(new Vector3(Mathf.Cos(a0) * 0.5f, half, Mathf.Sin(a0) * 0.5f));
                vertices.Add(new Vector3(Mathf.Cos(a1) * 0.5f, half, Mathf.Sin(a1) * 0.5f));
                vertices.Add(new Vector3(Mathf.Cos(a1) * 0.5f, -half, Mathf.Sin(a1) * 0.5f));
                vertices.Add(new Vector3(Mathf.Cos(a0) * 0.5f, -half, Mathf.Sin(a0) * 0.5f));
                triangles.AddRange(new[] { v, v + 1, v + 2, v, v + 2, v + 3 });
            }

            var mesh = new Mesh { name = "Coin" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
