using UnityEngine;
using UnityEngine.SceneManagement;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The crater on the summit (#245). Every island terrain whose highest point stands more than
    /// <see cref="MinPeak"/> above its base, and is a dome the cap's skirt can bury itself in, gets one, put on that point when its scene loads: the
    /// summit moves with the island's seed, so no scene can hold the crater where it belongs.
    ///
    /// The cap (VolcanoFactory, tools/art/volcano.py) sits on the peak, its pit floor above the
    /// ground, its skirt diving into the slopes. It carries a collider, so nobody walks through its
    /// rim, on the host and on every client alike.
    ///
    /// The lava is unlit and its colour climbs from <see cref="Glow"/>(0) by day to <see cref="Glow"/>(1)
    /// at night: over one, bloom puts a halo round the summit you can steer by in the dark.
    /// </summary>
    public class Volcano : MonoBehaviour
    {
        public const string ResourceName = "Volcano";

        /// <summary>Metres of peak below which a terrain is not a mountain: the arenas, the test plates.</summary>
        const float MinPeak = 60f;
        const int Grid = 128;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly Color Lava = new(1f, 0.72f, 0.5f, 1f);

        Material _lava;
        float _shown = -1f;

        /// <summary>The lava's colour: a little over white by day, three and a half times it at night.</summary>
        public static Color Glow(float night01) => Lava * Mathf.Lerp(1.3f, 3.5f, night01);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot() => SceneManager.sceneLoaded += (_, _) => PlaceAll();

        static void PlaceAll()
        {
            foreach (Terrain terrain in Terrain.activeTerrains)
                if (terrain.GetComponentInChildren<Volcano>() == null) Place(terrain);
        }

        /// <summary>The terrain's highest point in world space: a coarse grid, then a fine one round its best cell.</summary>
        public static Vector3 Peak(Terrain terrain)
        {
            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            Vector3 best = origin;
            float step = size.x / Grid;
            Vector2 centre = new(origin.x + size.x / 2f, origin.z + size.z / 2f);
            float span = size.x / 2f;

            for (int pass = 0; pass < 2; pass++)
            {
                best.y = float.MinValue;
                for (float x = centre.x - span; x <= centre.x + span; x += step)
                for (float z = centre.y - span; z <= centre.y + span; z += step)
                {
                    float y = terrain.SampleHeight(new Vector3(x, 0f, z)) + origin.y;
                    if (y > best.y) best = new Vector3(x, y, z);
                }
                centre = new Vector2(best.x, best.z);
                span = step;
                step = 1f;
            }
            return best;
        }

        /// <summary>
        /// How deep the skirt's lowest ring (volcano.py's PROFILE: 80 m out, 90 m down, give or take
        /// the 7% it wobbles) is buried at its shallowest. Below zero, its edge hangs in the air
        /// somewhere: the summit drops faster than the skirt does.
        /// </summary>
        public static float SkirtClearance(Terrain terrain, Vector3 at)
        {
            float worst = float.MaxValue;
            for (int i = 0; i < 32; i++)
            {
                float a = i * Mathf.PI * 2f / 32f;
                foreach (float r in new[] { 80f * 0.93f, 80f * 1.07f })
                {
                    var p = new Vector3(at.x + Mathf.Cos(a) * r, 0f, at.z + Mathf.Sin(a) * r);
                    float ground = terrain.SampleHeight(p) + terrain.transform.position.y;
                    worst = Mathf.Min(worst, ground - (at.y - 90f));
                }
            }
            return worst;
        }

        public static bool Fits(Terrain terrain, Vector3 peak) => SkirtClearance(terrain, peak) > 0f;

        static void Place(Terrain terrain)
        {
            Vector3 peak = Peak(terrain);
            if (peak.y - terrain.transform.position.y < MinPeak) return;

            // A spire (island 2's summit is a sea cliff's top) falls away faster than the skirt and
            // would leave it hanging in the air: no crater there.
            if (!Fits(terrain, peak)) { Debug.Log($"[Volcano] the summit at {peak} is too steep for a crater."); return; }

            var prefab = Resources.Load<GameObject>(ResourceName);
            if (prefab == null) { Debug.LogError("[Volcano] no Resources/Volcano prefab: run VolcanoFactory.Build."); return; }

            Instantiate(prefab, peak, Quaternion.identity, terrain.transform).name = ResourceName;
            Debug.Log($"[Volcano] crater on the summit at {peak}, skirt buried {SkirtClearance(terrain, peak):F1} m at its shallowest.");
        }

        void Awake()
        {
            var renderer = GetComponent<MeshRenderer>();
            if (renderer != null && renderer.sharedMaterials.Length > 1) _lava = renderer.materials[1];
        }

        void Update()
        {
            if (_lava == null) return;
            float night = WorldClock.Night01;
            if (Mathf.Abs(night - _shown) < 0.01f) return;
            _shown = night;
            _lava.SetColor(BaseColorId, Glow(night));
        }

        void OnDestroy()
        {
            if (_lava != null) Destroy(_lava);
        }
    }
}
