using System.Collections;
using EscapeWithYourFriends.Core;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The acceptance test for the crater (#245), behind <c>-volcanoTest</c> with <c>-scene island</c>
    /// or <c>island2</c>, whose summit is a spire and must get none. Otherwise: one crater, on the
    /// highest point, standing clear of the summit with its pit floor above the ground and its skirt
    /// buried, solid, within its triangle budget, and its lava brighter at night. Geometry only; how
    /// it looks is the beauty shots' job.
    /// </summary>
    public class VolcanoTest : MonoBehaviour
    {
        const int TriangleCap = 1500;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-volcanoTest")) return;

            _started = true;

            var go = new GameObject("VolcanoTest");
            DontDestroyOnLoad(go);
            go.AddComponent<VolcanoTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            Terrain island = null;
            for (float deadline = Time.time + 30f; Time.time < deadline && island == null;)
            {
                island = Terrain.activeTerrain;
                if (island == null) yield return new WaitForSeconds(0.5f);
            }
            Check("the scene has an island", island != null);
            if (island == null) { Report(); yield break; }

            // A summit too steep for the cap gets none, and that is a pass, not a missing crater.
            Vector3 top = Volcano.Peak(island);
            if (!Volcano.Fits(island, top))
            {
                yield return new WaitForSeconds(2f);
                Check($"a spire of a summit gets no crater (its skirt would hang {-Volcano.SkirtClearance(island, top):F0} m in the air)",
                      FindFirstObjectByType<Volcano>() == null);
                Report();
                yield break;
            }

            Volcano volcano = null;
            for (float deadline = Time.time + 10f; Time.time < deadline && volcano == null;)
            {
                volcano = FindFirstObjectByType<Volcano>();
                if (volcano == null) yield return new WaitForSeconds(0.5f);
            }

            Check("the island has a crater", volcano != null);
            if (volcano == null) { Report(); yield break; }

            Check("and only one", FindObjectsByType<Volcano>(FindObjectsSortMode.None).Length == 1);

            Terrain terrain = volcano.GetComponentInParent<Terrain>();
            Check("it belongs to the island's terrain", terrain != null);
            if (terrain == null) { Report(); yield break; }

            Vector3 at = volcano.transform.position;
            Vector3 peak = Volcano.Peak(terrain);
            float off = Vector2.Distance(new Vector2(at.x, at.z), new Vector2(peak.x, peak.z));
            Check($"it stands on the summit ({off:F1} m from the highest point, {at.y:F1} m up)", off < 2f && Mathf.Abs(at.y - peak.y) < 0.5f);

            // The pool is 1.25 m over the crater's origin. Nowhere inside the pit may the ground rise
            // through it, or the lava has a hill in it.
            float worst = float.MinValue;
            for (int i = 0; i < 24; i++)
            for (float r = 0f; r <= 7.5f; r += 2.5f)
            {
                float a = i * Mathf.PI * 2f / 24f;
                var p = new Vector3(at.x + Mathf.Cos(a) * r, 0f, at.z + Mathf.Sin(a) * r);
                worst = Mathf.Max(worst, terrain.SampleHeight(p) + terrain.transform.position.y - at.y);
            }
            Check($"the ground stays under the lava ({worst:F2} m against the pool at 1.25 m)", worst < 1.1f);

            float edge = Volcano.SkirtClearance(terrain, at);
            Check($"the skirt's foot is buried all round ({edge:F1} m under the ground at its shallowest)", edge > 0f);

            MeshFilter filter = volcano.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            Check("it has its mesh", mesh != null);
            if (mesh != null)
            {
                int tris = mesh.triangles.Length / 3;
                Check($"within its budget ({tris} triangles, cap {TriangleCap})", tris <= TriangleCap);
                Check($"rock and lava are two submeshes ({mesh.subMeshCount})", mesh.subMeshCount == 2);
            }

            Collider solid = volcano.GetComponent<Collider>();
            Check("its rim is solid", solid != null && solid.enabled);
            if (solid != null)
            {
                // Dropped from above the crest, outside the pit: the rim has to stop it above the ground.
                var from = at + new Vector3(10.5f, 30f, 0f);
                bool hit = solid.Raycast(new Ray(from, Vector3.down), out RaycastHit h, 60f);
                float ground = terrain.SampleHeight(from) + terrain.transform.position.y;
                Check($"the rim stands clear of the slope ({(hit ? h.point.y - ground : 0f):F1} m above it)", hit && h.point.y > ground + 2f);
            }

            float day = Volcano.Glow(0f).maxColorComponent, night = Volcano.Glow(1f).maxColorComponent;
            Check($"the lava glows harder at night ({day:F2} by day, {night:F2} at night)", night > day * 2f && day > 1f);

            Report();
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                Debug.Log($"[VolcanoTest] ok: {what}.");
                return;
            }

            _failed++;
            Debug.LogError($"[VolcanoTest] FAILED: {what}.");
        }

        void Report()
        {
            string line = $"[VolcanoTest] {_passed} passed, {_failed} failed.";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }
    }
}
