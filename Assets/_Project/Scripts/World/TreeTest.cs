using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Player;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The acceptance test for #199, behind <c>-treeTest</c> with <c>-scene island</c>: walks the
    /// player into trees and checks each one stops it at its trunk, is not walked through, and lets
    /// it back out.
    ///
    /// One isolated tree per prototype that has a collider (palms, pines, broadleaves, rocks), so a
    /// neighbour cannot be what stopped the walk. The body starts a couple of metres out on a gentle
    /// slope and walks straight at the tree's spot for two seconds, then backs off for one. A capsule
    /// that sits where the trunk is not shows up as a walk that ends at the wrong distance.
    /// </summary>
    public class TreeTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const int Wanted = 12;
        const float StartOut = 2.6f, Alone = 4f, MaxSlope = 20f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-treeTest")) return;

            _started = true;

            var go = new GameObject("TreeTest");
            DontDestroyOnLoad(go);
            go.AddComponent<TreeTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            PlayerMotor motor = null;
            for (float deadline = Time.time + WaitForPlayer; Time.time < deadline && motor == null;)
            {
                motor = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None).FirstOrDefault(m => m != null && m.IsOwner);
                if (motor == null) yield return new WaitForSeconds(0.5f);
            }

            Terrain terrain = Terrain.activeTerrain;
            Check("a player spawned", motor != null);
            Check("there is a terrain with trees", terrain != null && terrain.terrainData.treeInstanceCount > 0);
            if (motor == null || terrain == null)
            {
                Report();
                yield break;
            }

            // Let the spawn settle before the body is moved around.
            yield return new WaitForSeconds(2f);

            // The trunk is where the collider is: the bake measured it off the mesh after centring.
            foreach (TreePrototype prototype in terrain.terrainData.treePrototypes)
            {
                GameObject flora = prototype.prefab;
                if (flora == null || !flora.TryGetComponent(out CapsuleCollider _)) continue;
                Vector2 off = flora.TryGetComponent(out ArtVisual art) ? art.Trunk : new Vector2(float.NaN, 0f);
                Check($"{flora.name}'s trunk stands on its collider ({off.magnitude:F2} m off; bake flora again if not)",
                      off.magnitude < 0.05f);
            }

            var input = motor.GetComponent<PlayerInputReader>();
            var controller = motor.GetComponent<CharacterController>();
            input.BotDriven = true;

            TerrainData data = terrain.terrainData;
            TreeInstance[] trees = data.treeInstances;
            Vector3[] at = trees.Select(t => terrain.GetPosition() + Vector3.Scale(t.position, data.size)).ToArray();

            var tried = new HashSet<int>();
            var random = new System.Random(199);
            int walked = 0;

            foreach (int i in Enumerable.Range(0, trees.Length).OrderBy(_ => random.Next()))
            {
                if (walked >= Wanted) break;

                TreeInstance tree = trees[i];
                GameObject prefab = data.treePrototypes[tree.prototypeIndex].prefab;
                var capsule = prefab != null ? prefab.GetComponent<CapsuleCollider>() : null;
                if (capsule == null || tried.Contains(tree.prototypeIndex) && tried.Count < data.treePrototypes.Length) continue;

                Vector3 trunk = at[i];
                if (!Clear(terrain, at, i, trunk, out Vector3 start)) continue;

                tried.Add(tree.prototypeIndex);
                walked++;

                float radius = capsule.radius * tree.widthScale;
                Vector3 toward = Flat(trunk - start).normalized;
                float yaw = Mathf.Atan2(toward.x, toward.z) * Mathf.Rad2Deg;

                motor.ServerTeleport(start, yaw);
                input.BotLook(yaw, 0f);
                yield return new WaitForSeconds(0.4f);

                // In: two seconds straight at the tree's spot.
                float furthest = float.NegativeInfinity;
                input.BotMove = Vector2.up;
                for (float t = 0f; t < 2f; t += Time.deltaTime)
                {
                    input.BotLook(yaw, 0f);
                    furthest = Mathf.Max(furthest, Vector3.Dot(Flat(motor.transform.position - start), toward));
                    yield return null;
                }
                input.BotMove = Vector2.zero;

                Vector3 stopped = motor.transform.position;
                float gap = Flat(stopped - trunk).magnitude;
                float contact = radius + controller.radius;
                string name = $"{prefab.name} at {trunk.x:F0},{trunk.z:F0}";

                Check($"{name}: not walked through (reached {furthest:F2} of {StartOut:F2} m)", furthest < StartOut);
                Check($"{name}: stopped at its trunk ({gap:F2} m out, contact at {contact:F2})",
                      gap > contact - 0.12f && gap < contact + 0.35f);

                // Out: a second backwards, which a body wedged in a collider cannot do.
                input.BotMove = Vector2.down;
                for (float t = 0f; t < 1f; t += Time.deltaTime)
                {
                    input.BotLook(yaw, 0f);
                    yield return null;
                }
                input.BotMove = Vector2.zero;

                float backed = Flat(motor.transform.position - stopped).magnitude;
                Check($"{name}: backs out freely ({backed:F2} m)", backed > 0.8f);
            }

            input.BotDriven = false;
            Check($"walked into {walked} trees of {tried.Count} kinds", walked >= 6);
            Report();
        }

        /// <summary>
        /// A spot <see cref="StartOut"/> metres from tree <paramref name="index"/> with nothing else near,
        /// gentle ground between, and nothing but the terrain where the body will stand.
        /// </summary>
        static bool Clear(Terrain terrain, Vector3[] trees, int index, Vector3 trunk, out Vector3 start)
        {
            start = default;
            for (int other = 0; other < trees.Length; other++)
                if (other != index && Flat(trees[other] - trunk).sqrMagnitude < Alone * Alone) return false;

            Vector3 away = Quaternion.Euler(0f, index * 137f % 360f, 0f) * Vector3.forward;
            start = trunk + away * StartOut;
            start.y = terrain.SampleHeight(start) + terrain.GetPosition().y + 0.1f;
            if (start.y < 1f) return false; // The sea.

            Vector3 normal = terrain.terrainData.GetInterpolatedNormal(
                (start.x - terrain.GetPosition().x) / terrain.terrainData.size.x,
                (start.z - terrain.GetPosition().z) / terrain.terrainData.size.z);
            if (Vector3.Angle(normal, Vector3.up) > MaxSlope) return false;

            foreach (Collider hit in Physics.OverlapCapsule(start + Vector3.up * 0.6f, start + Vector3.up * 1.6f, 0.5f,
                                                            ~0, QueryTriggerInteraction.Ignore))
                if (hit is not TerrainCollider && hit.attachedRigidbody == null) return false;

            return true;
        }

        static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[TreeTest] FAILED: {what}.");
        }

        void Report()
        {
            string line = $"[TreeTest] {_passed} passed, {_failed} failed.";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }
    }
}
