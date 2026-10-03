using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The casino's two runtime touches (#252). Its reflection probe is drawn once, a moment after the
    /// building arrives, so the polished floor holds the lamps and the neon; the probe is never drawn
    /// again, which is the whole of its cost. And while the camera stands inside, the bloom is turned
    /// up, so the neon halos the way a room lit by neon does.
    /// </summary>
    public class CasinoLook : MonoBehaviour
    {
        [SerializeField] ReflectionProbe _probe;
        [SerializeField] Vector3 _min, _max;

        bool _inside;

        IEnumerator Start()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                enabled = false;
                yield break;
            }

            // Low and Medium have realtime probes off: the floor stays matte there, and costs nothing.
            if (!QualitySettings.realtimeReflectionProbes) yield break;

            // Long enough for the games and the lamps to be placed and lit.
            yield return new WaitForSeconds(2f);
            Transform floor = transform.Find("Model.Floor");
            if (_probe == null || floor == null)
            {
                Debug.LogWarning($"[CasinoLook] {name}: no {(_probe == null ? "probe" : "Model.Floor")}, the floor stays matte.");
                yield break;
            }

            int id = _probe.RenderProbe();
            while (!_probe.IsFinishedRendering(id)) yield return null;

            // Handed to the floor directly: URP's probe atlas would not give it this probe.
            var block = new MaterialPropertyBlock();
            block.SetTexture("_RoomCube", _probe.realtimeTexture);
            floor.GetComponent<Renderer>().SetPropertyBlock(block);
            Debug.Log($"[CasinoLook] {name}: the floor reflects the room ({_probe.realtimeTexture.width} px probe).");
        }

        void Update()
        {
            Camera camera = Camera.main;
            bool inside = false;
            if (camera != null)
            {
                Vector3 p = transform.InverseTransformPoint(camera.transform.position);
                inside = p.x > _min.x && p.x < _max.x && p.y > _min.y && p.y < _max.y && p.z > _min.z && p.z < _max.z;
            }

            if (inside == _inside) return;
            _inside = inside;
            PostProcess.Indoors(inside);
        }

        void OnDisable()
        {
            if (!_inside) return;
            _inside = false;
            PostProcess.Indoors(false);
        }

        /// <summary>Editor-time setup. See <c>GreyboxBuilder</c>.</summary>
        public void Configure(ReflectionProbe probe, Vector3 min, Vector3 max) => (_probe, _min, _max) = (probe, min, max);
    }
}
