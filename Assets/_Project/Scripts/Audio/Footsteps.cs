using System.Collections.Generic;
using EscapeWithYourFriends.Net;
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
    /// </summary>
    public class Footsteps : MonoBehaviour
    {
        /// <summary>Metres of walking between steps. A stride, roughly, at this scale.</summary>
        const float Stride = 2.2f;

        /// <summary>Above this, the body is falling or being thrown, and feet are not involved.</summary>
        const float Airborne = 4f;

        static Footsteps _instance;

        readonly Dictionary<int, Vector3> _wasAt = new();
        readonly Dictionary<int, float> _walked = new();

        /// <summary>Steps played this run. The harness reads it; nothing else does.</summary>
        internal static int Steps { get; private set; }

        internal static void Begin()
        {
            if (_instance != null) return;

            var go = new GameObject("Footsteps");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<Footsteps>();
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
                Sfx.Play(Sound.Step, now, 0.5f);
            }
        }
    }
}
