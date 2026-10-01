using EscapeWithYourFriends.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// What drifts through the air round the camera (#244): leaves falling under the canopy, sand
    /// blown off the beach in the gusts, fireflies in the trees at night. Three particle systems
    /// for the whole game, each kept in a box round the camera, so the cost does not grow with the
    /// island.
    ///
    /// How much canopy is overhead is read off the terrain's own tree list, once per scene, into a
    /// coarse grid: terrain trees have no colliders of their own to ask.
    ///
    /// The amount follows the grass option: a quarter of it on Low, 60% on Medium, all of it above.
    /// Not on a headless build, which has no camera.
    ///
    /// Not done: butterflies (they want a mesh and a flight path, not a particle) and gulls, which the
    /// shore already has as animals.
    /// </summary>
    public class AmbientLife : MonoBehaviour
    {
        const float Cell = 10f;
        static readonly float[] TierAmount = { 0.25f, 0.6f, 1f, 1f };

        ParticleSystem _leaves;
        ParticleSystem _sand;
        ParticleSystem _fireflies;

        int[,] _canopy;
        Texture2D _dot;
        Vector3 _origin;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

            var go = new GameObject("AmbientLife");
            DontDestroyOnLoad(go);
            go.AddComponent<AmbientLife>();
        }

        void Awake()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) { enabled = false; return; }
            _dot = Dot();

            _leaves = Make("Leaves", shader, additive: false, size: 0.09f, life: 8f, colour: new Color(0.45f, 0.55f, 0.18f, 1f));
            _sand = Make("Sand", shader, additive: false, size: 0.035f, life: 2.5f, colour: new Color(0.93f, 0.84f, 0.66f, 0.55f));
            _fireflies = Make("Fireflies", shader, additive: true, size: 0.12f, life: 6f, colour: new Color(0.75f, 1f, 0.35f, 1f));

            // Over-bright, so bloom puts a halo on each one; a particle's own colour stops at 1.
            _fireflies.GetComponent<ParticleSystemRenderer>().sharedMaterial.SetColor("_BaseColor", new Color(3f, 4f, 1.4f, 1f));

            // Leaves tumble and drift; fireflies wander. Sand only rides the wind.
            var spin = _leaves.rotationOverLifetime;
            spin.enabled = true;
            spin.z = new ParticleSystem.MinMaxCurve(-3f, 3f);

            var wander = _fireflies.noise;
            wander.enabled = true;
            wander.strength = 0.6f;
            wander.frequency = 0.4f;

            var glow = _fireflies.colorOverLifetime;
            glow.enabled = true;
            var pulse = new Gradient();
            pulse.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                          new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.3f), new GradientAlphaKey(0.2f, 0.6f),
                                  new GradientAlphaKey(1f, 0.8f), new GradientAlphaKey(0f, 1f) });
            glow.color = pulse;

            SceneManager.sceneLoaded += (_, _) => _canopy = null;
        }

        ParticleSystem Make(string name, Shader shader, bool additive, float size, float life, Color colour)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var system = go.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = system.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = life;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.6f, size * 1.4f);
            main.startColor = colour;
            main.maxParticles = 400;
            main.playOnAwake = false;

            var emission = system.emission;
            emission.rateOverTime = 0f;

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Box;

            var velocity = system.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;

            // Same blend as the tracer material, so the build already carries this shader variant.
            var material = new Material(shader) { name = name };
            material.SetTexture("_BaseMap", _dot);
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)(additive ? UnityEngine.Rendering.BlendMode.One
                                                             : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            system.Play();
            return system;
        }

        /// <summary>A soft round dot. Without a texture the particle shader draws hard white squares.</summary>
        static Texture2D Dot()
        {
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "AmbientDot", wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(size / 2f, size / 2f)) / (size / 2f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(1f - d) * Mathf.Clamp01(1f - d)));
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        void LateUpdate()
        {
            Camera camera = Camera.main;
            Terrain terrain = Terrain.activeTerrain;
            if (camera == null || terrain == null)
            {
                Rate(_leaves, 0f); Rate(_sand, 0f); Rate(_fireflies, 0f);
                return;
            }

            if (_canopy == null) MapCanopy(terrain);

            Vector3 eye = camera.transform.position;
            float ground = terrain.SampleHeight(eye) + terrain.transform.position.y;
            float trees = Mathf.Clamp01(Canopy(eye) / 6f);
            float amount = TierAmount[VideoSettings.Current.Grass];
            Vector2 wind = Wind.Direction * (Wind.Strength * (0.4f + Wind.Gust * 2f));

            Place(_leaves, new Vector3(eye.x, ground + 8f, eye.z), new Vector3(24f, 4f, 24f), new Vector3(wind.x, -0.6f, wind.y));
            Rate(_leaves, 8f * trees * amount);

            // The beach: dry sand within two metres of the tide line, and only when a gust lifts it.
            bool beach = ground < IslandShape.SeaLevel + 2f && ground > IslandShape.SeaLevel - 0.2f;
            Place(_sand, new Vector3(eye.x, ground + 0.15f, eye.z), new Vector3(20f, 0.3f, 20f), new Vector3(wind.x * 3f, 0.05f, wind.y * 3f));
            Rate(_sand, beach ? 60f * Mathf.Clamp01((Wind.Gust - 0.2f) * 2f) * amount : 0f);

            Place(_fireflies, new Vector3(eye.x, ground + 1.2f, eye.z), new Vector3(18f, 2f, 18f), Vector3.zero);
            Rate(_fireflies, 14f * trees * WorldClock.Night01 * amount);
        }

        static void Place(ParticleSystem system, Vector3 centre, Vector3 box, Vector3 drift)
        {
            system.transform.position = centre;
            var shape = system.shape;
            shape.scale = box;

            var velocity = system.velocityOverLifetime;
            velocity.x = drift.x;
            velocity.y = drift.y;
            velocity.z = drift.z;
        }

        static void Rate(ParticleSystem system, float perSecond)
        {
            var emission = system.emission;
            emission.rateOverTime = perSecond;
        }

        /// <summary>Trees per 10 m cell, from the terrain's instance list. Once per scene.</summary>
        void MapCanopy(Terrain terrain)
        {
            TerrainData data = terrain.terrainData;
            _origin = terrain.transform.position;
            int w = Mathf.CeilToInt(data.size.x / Cell), d = Mathf.CeilToInt(data.size.z / Cell);
            _canopy = new int[w, d];

            foreach (TreeInstance tree in data.treeInstances)
            {
                int x = Mathf.Clamp((int)(tree.position.x * data.size.x / Cell), 0, w - 1);
                int z = Mathf.Clamp((int)(tree.position.z * data.size.z / Cell), 0, d - 1);
                _canopy[x, z]++;
            }
        }

        /// <summary>Trees in the 3x3 cells round a point.</summary>
        int Canopy(Vector3 at)
        {
            int cx = (int)((at.x - _origin.x) / Cell), cz = (int)((at.z - _origin.z) / Cell);
            int count = 0;
            for (int x = cx - 1; x <= cx + 1; x++)
            for (int z = cz - 1; z <= cz + 1; z++)
                if (x >= 0 && z >= 0 && x < _canopy.GetLength(0) && z < _canopy.GetLength(1)) count += _canopy[x, z];
            return count;
        }
    }
}
