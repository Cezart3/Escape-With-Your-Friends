using System.Collections.Generic;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.World;
using UnityEngine;

namespace EscapeWithYourFriends.Audio
{
    /// <summary>
    /// Footsteps for every body on the island, driven from outside the bodies (#80).
    ///
    /// **Not a component on the player prefab**, for two reasons. Prefabs in this project are built
    /// by editor scripts and never hand-edited, so adding a component to one is a code change plus a
    /// bake plus a commit of a binary; and the movement itself runs inside a predicted tick that
    /// FishNet replays on a correction, which would fire a step several times for one stride.
    ///
    /// So this watches instead: one object, every frame, how far each body has moved since its last
    /// step. Distance rather than a timer, so a sprinting player steps faster than a crouching one
    /// without anybody writing a gait.
    ///
    /// The step is a recorded one (Kenney's Impact Sounds, CC0, in Resources/Footsteps) picked by
    /// what is underfoot: grass, sand (the kit's snow, which is the same crunch), rock, or wood on
    /// anything that is not the terrain. It used to be a synthesised burst of white noise, which a
    /// playtest heard as a hiss on every stride and called the most annoying thing in the game.
    /// Each body has its own source, so steps never steal a voice from a gunshot.
    /// </summary>
    public class Footsteps : MonoBehaviour
    {
        /// <summary>Metres of walking between steps. A stride, roughly, at this scale.</summary>
        const float Stride = 2.2f;

        /// <summary>Above this, the body is falling or being thrown, and feet are not involved.</summary>
        const float Airborne = 4f;

        /// <summary>Your own feet are right under the listener; everyone else's are a little louder.</summary>
        const float OwnVolume = 0.22f;
        const float OtherVolume = 0.35f;

        enum Surface { Grass, Sand, Rock, Wood }

        static readonly string[] SurfaceFiles = { "grass", "snow", "concrete", "wood" };

        static Footsteps _instance;

        readonly Dictionary<int, Vector3> _wasAt = new();
        readonly Dictionary<int, float> _walked = new();
        readonly Dictionary<int, AudioSource> _sources = new();
        readonly RaycastHit[] _hits = new RaycastHit[8];

        AudioClip[][] _clips;
        int _lastClip;

        /// <summary>Steps played this run. The harness reads it; nothing else does.</summary>
        internal static int Steps { get; private set; }

        internal static void Begin()
        {
            if (_instance != null) return;

            var go = new GameObject("Footsteps");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<Footsteps>();
        }

        void Awake()
        {
            AudioClip[] all = Resources.LoadAll<AudioClip>("Footsteps");
            _clips = new AudioClip[SurfaceFiles.Length][];

            for (int s = 0; s < SurfaceFiles.Length; s++)
                _clips[s] = System.Array.FindAll(all, c => c.name.Contains(SurfaceFiles[s]));
        }

        void Update()
        {
            foreach (NetworkPlayerRegistry.PlayerBody body in NetworkPlayerRegistry.Players)
            {
                if (!body.IsValid) continue;

                int id = body.Object.ObjectId;
                Vector3 now = body.Object.transform.position;

                if (!_wasAt.TryGetValue(id, out Vector3 was))
                {
                    _wasAt[id] = now;
                    continue;
                }

                _wasAt[id] = now;

                Vector3 moved = now - was;
                if (Mathf.Abs(moved.y / Mathf.Max(Time.deltaTime, 0.001f)) > Airborne) continue;

                moved.y = 0f;
                float walked = _walked.TryGetValue(id, out float sofar) ? sofar + moved.magnitude : moved.magnitude;

                if (walked < Stride)
                {
                    _walked[id] = walked;
                    continue;
                }

                _walked[id] = 0f;
                Steps++;
                Step(id, body.Object.transform, body.Object.IsOwner);
            }
        }

        void Step(int id, Transform body, bool own)
        {
            AudioClip[] clips = _clips[(int)Under(body)];
            if (clips.Length == 0) return;

            if (!_sources.TryGetValue(id, out AudioSource source) || source == null)
            {
                var go = new GameObject($"Feet {id}");
                go.transform.SetParent(transform);
                source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.minDistance = 2f;
                source.maxDistance = 25f;
                _sources[id] = source;
            }

            // Never the same take twice running: five takes in a row is a stride, one take five
            // times is a metronome.
            int pick = Random.Range(0, clips.Length);
            if (pick == _lastClip && clips.Length > 1) pick = (pick + 1) % clips.Length;
            _lastClip = pick;

            source.transform.position = body.position;
            source.pitch = Random.Range(0.92f, 1.06f);
            source.PlayOneShot(clips[pick], own ? OwnVolume : OtherVolume);
        }

        /// <summary>What the body is standing on: the terrain's strongest layer there, or wood.</summary>
        Surface Under(Transform body)
        {
            int count = Physics.RaycastNonAlloc(body.position + Vector3.up * 0.5f, Vector3.down, _hits, 1.5f,
                                                ~0, QueryTriggerInteraction.Ignore);
            RaycastHit? ground = null;

            for (int i = 0; i < count; i++)
            {
                if (_hits[i].transform.IsChildOf(body)) continue;
                if (ground == null || _hits[i].distance < ground.Value.distance) ground = _hits[i];
            }

            if (ground == null) return Surface.Grass;
            if (ground.Value.collider is not TerrainCollider) return Surface.Wood;

            Terrain terrain = ground.Value.collider.GetComponent<Terrain>();
            TerrainData data = terrain != null ? terrain.terrainData : null;
            if (data == null) return Surface.Grass;

            Vector3 local = ground.Value.point - terrain.transform.position;
            int x = Mathf.Clamp((int)(local.x / data.size.x * data.alphamapWidth), 0, data.alphamapWidth - 1);
            int z = Mathf.Clamp((int)(local.z / data.size.z * data.alphamapHeight), 0, data.alphamapHeight - 1);

            float[,,] weights = data.GetAlphamaps(x, z, 1, 1);
            int best = 0;
            for (int layer = 1; layer < weights.GetLength(2); layer++)
                if (weights[0, 0, layer] > weights[0, 0, best]) best = layer;

            return best switch
            {
                IslandSplat.Sand => Surface.Sand,
                IslandSplat.Rock => Surface.Rock,
                _ => Surface.Grass,
            };
        }
    }
}
