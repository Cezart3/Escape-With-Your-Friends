using System.Collections.Generic;
using System.IO;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.World;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Bakes the walkable surface of the island, so that anything with a brain can get from one end
    /// of it to the other.
    ///
    /// Three decisions are worth the words:
    ///
    /// **The bake is bounded by a volume whose floor is the waterline.** Recast rasterises triangles
    /// into the build bounds and clips whatever falls outside them, so putting the floor at
    /// NavWaterline means the seabed is never voxelised at all. That is both the correct result - the
    /// sea is not walkable - and a large saving, because the seabed is roughly half the square.
    /// Bounding it also stops the surface measuring itself against every source in the scene, which
    /// would include the water's four-kilometre horizon ring and give a bake volume sixteen times the
    /// island.
    ///
    /// **The POIs are instantiated for the bake and thrown away afterwards.** They are spawned by the
    /// server at run time, so at bake time the scene is bare terrain and a NavMesh built from it would
    /// send agents straight through the shop. The alternative - a carving NavMeshObstacle on every
    /// solid piece - costs a re-voxelisation per obstacle at spawn and buys nothing, because the
    /// buildings never move. Baking them in is free at run time and exact.
    ///
    /// **Terrain trees are not in the NavMesh.** Unity collects a terrain as one heightmap source;
    /// its fourteen thousand trees are not colliders it can see. Agents will clip palm trunks. Fixing
    /// it properly means fourteen thousand obstacles or a hand-built modifier per grove, and neither
    /// is worth it before there is an agent to be annoyed by it.
    /// </summary>
    internal static class NavFactory
    {
        const string DataPath = "Assets/_Project/Data/IslandNavMesh.asset";

        // The humanoid agent from ProjectSettings/NavMeshAreas.asset: radius 0.5, height 2, slope 45,
        // climb 0.75. Natives and animals are people-sized for now, so one agent type is enough; a
        // second one costs a second bake of the whole island.
        const int HumanoidAgent = 0;

        /// <summary>
        /// Adds the surface to the island scene and bakes it. Called with the scene already built, so
        /// the terrain and its collider exist; called before the scene is saved, so the surface and
        /// its data reference go into the file.
        /// </summary>
        internal static void Bake(IslandProfile profile, POISpawner spawner)
        {
            var root = new GameObject("NavMesh");
            var surface = root.AddComponent<NavMeshSurface>();

            float floor = IslandShape.SeaLevel + profile.NavWaterline;
            float ceiling = profile.PeakHeight + 10f;

            surface.agentTypeID = HumanoidAgent;
            surface.collectObjects = CollectObjects.Volume;
            surface.center = new Vector3(0f, (floor + ceiling) * 0.5f, 0f);
            surface.size = new Vector3(profile.Size, ceiling - floor, profile.Size);

            // Colliders rather than render meshes: the water has renderers and no collider, which is
            // exactly the distinction wanted here, and the greybox pieces that were built without a
            // collider on purpose are the ones nothing should path around.
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = ~0;

            surface.overrideVoxelSize = true;
            surface.voxelSize = Mathf.Max(0.05f, profile.NavVoxelSize);
            surface.overrideTileSize = true;
            surface.tileSize = Mathf.Clamp(profile.NavTileSize, 16, 1024);
            surface.minRegionArea = Mathf.Max(0f, profile.NavMinRegionArea);

            if (CommandLine.HasFlag("-skipNav"))
            {
                Debug.LogWarning("[NavFactory] -skipNav: the surface is in the scene but was not baked. "
                                 + "Nothing will be able to path until the island is generated again without it.");
                return;
            }

            var bounds = new Bounds(surface.center, surface.size);
            List<GameObject> standIns = PlaceStandIns(spawner);
            List<NavMeshBuildSource> sources = Collect(bounds, standIns);

            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(HumanoidAgent);
            settings.overrideVoxelSize = true;
            settings.voxelSize = surface.voxelSize;
            settings.overrideTileSize = true;
            settings.tileSize = surface.tileSize;
            settings.minRegionArea = surface.minRegionArea;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            NavMeshData data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero,
                                                              Quaternion.identity);
            watch.Stop();

            foreach (GameObject standIn in standIns) Object.DestroyImmediate(standIn);

            if (data == null)
            {
                Debug.LogError("[NavFactory] The bake produced no data. Nothing on this island can path.");
                return;
            }

            data.name = "Island";
            surface.navMeshData = data;
            surface.AddData();

            Save(surface);
            Report(profile, spawner, surface, watch.Elapsed.TotalSeconds);
        }

        /// <summary>
        /// The geometry to bake, named rather than searched for.
        ///
        /// The terrain is added by hand instead of letting the surface find it. A NavMeshSurface set
        /// to collect a volume asks the scene which colliders overlap that box, and a TerrainCollider
        /// created seconds earlier in an unsaved scene answers that question with nothing - which is
        /// how the first version of this file produced a NavMesh containing seven roofs and no
        /// island. Nothing here depends on the collider's bookkeeping being up to date: a terrain
        /// source is its TerrainData and the position of the terrain object, both of which are
        /// certain.
        /// </summary>
        static List<NavMeshBuildSource> Collect(Bounds bounds, List<GameObject> standIns)
        {
            var sources = new List<NavMeshBuildSource>();

            Terrain terrain = Object.FindFirstObjectByType<Terrain>();
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogError("[NavFactory] No terrain in the scene. There is nothing to walk on.");
                return sources;
            }

            // The heights were written into this TerrainData minutes ago and live in a GPU-side
            // texture until something asks for them back. Recast reads the CPU copy, so without this
            // it rasterises a terrain that is still flat - which is a NavMesh of nothing at all, and
            // took a while to recognise as that rather than as a collection failure.
            TerrainData terrainData = terrain.terrainData;
            terrainData.SyncHeightmap();
            terrain.Flush();

            sources.Add(new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Terrain,
                sourceObject = terrainData,
                transform = Matrix4x4.TRS(terrain.transform.position, Quaternion.identity, Vector3.one),
                area = 0,
            });

            foreach (GameObject standIn in standIns) AddColliders(standIn, sources);

            Debug.Log($"[NavFactory] {sources.Count} sources over {bounds.size.x:F0}x{bounds.size.z:F0}m, "
                      + $"from y {bounds.min.y:F1} to {bounds.max.y:F1}: one terrain "
                      + $"({terrainData.heightmapResolution}^2, {terrainData.size.y:F0}m tall, "
                      + $"mid-island height {terrainData.GetInterpolatedHeight(0.5f, 0.5f):F1}m) and "
                      + $"{sources.Count - 1} pieces of building.");

            return sources;
        }

        /// <summary>
        /// One build source per collider on a building, written out by hand.
        ///
        /// Unity's own collector is the obvious thing to call here and it came back with five sources
        /// for seven buildings, which is not a number worth debugging when the conversion is this
        /// short. A NavMeshBuildSource carries no scale, so every size is multiplied out here and
        /// every offset is put through the transform rather than added to it.
        /// </summary>
        static void AddColliders(GameObject root, List<NavMeshBuildSource> sources)
        {
            foreach (Collider collider in root.GetComponentsInChildren<Collider>())
            {
                Transform t = collider.transform;
                Vector3 scale = t.lossyScale;
                float round = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));

                switch (collider)
                {
                    case BoxCollider box:
                        sources.Add(new NavMeshBuildSource
                        {
                            shape = NavMeshBuildSourceShape.Box,
                            transform = Matrix4x4.TRS(t.TransformPoint(box.center), t.rotation, Vector3.one),
                            size = Vector3.Scale(box.size, scale),
                            area = 0,
                        });
                        break;

                    case CapsuleCollider capsule:
                        sources.Add(new NavMeshBuildSource
                        {
                            shape = NavMeshBuildSourceShape.Capsule,
                            transform = Matrix4x4.TRS(t.TransformPoint(capsule.center), t.rotation, Vector3.one),
                            size = new Vector3(capsule.radius * round, capsule.height * Mathf.Abs(scale.y), 0f),
                            area = 0,
                        });
                        break;

                    case SphereCollider sphere:
                        sources.Add(new NavMeshBuildSource
                        {
                            shape = NavMeshBuildSourceShape.Sphere,
                            transform = Matrix4x4.TRS(t.TransformPoint(sphere.center), t.rotation, Vector3.one),
                            size = new Vector3(sphere.radius * Mathf.Max(round, Mathf.Abs(scale.y)), 0f, 0f),
                            area = 0,
                        });
                        break;

                    case MeshCollider mesh when mesh.sharedMesh != null:
                        sources.Add(new NavMeshBuildSource
                        {
                            shape = NavMeshBuildSourceShape.Mesh,
                            sourceObject = mesh.sharedMesh,
                            transform = t.localToWorldMatrix,
                            area = 0,
                        });
                        break;
                }
            }
        }

        /// <summary>
        /// The buildings, as plain copies with no prefab link and no network identity, standing where
        /// the spawner will put the real ones. Instantiated rather than linked because they exist for
        /// one bake and are destroyed before the scene is saved; a prefab instance would leave a
        /// reference behind in the file if anything went wrong halfway.
        /// </summary>
        static List<GameObject> PlaceStandIns(POISpawner spawner)
        {
            var made = new List<GameObject>();
            if (spawner == null) return made;

            foreach (POISpawner.Placement placement in spawner.Placements)
            {
                if (placement == null || placement.Prefab == null) continue;

                GameObject copy = Object.Instantiate(placement.Prefab.gameObject, placement.Position,
                                                    Quaternion.Euler(placement.Euler));
                copy.name = "NavStandIn." + placement.Id;
                made.Add(copy);
            }

            if (made.Count > 0)
                Debug.Log($"[NavFactory] {made.Count} buildings standing in for the bake; they are thrown "
                          + "away before the scene is saved.");

            return made;
        }

        /// <summary>
        /// Writes the data over the existing asset rather than replacing it, so the GUID survives and
        /// the scene reference written next to it does not change every time the island is rebuilt.
        /// </summary>
        static void Save(NavMeshSurface surface)
        {
            NavMeshData baked = surface.navMeshData;
            var existing = AssetDatabase.LoadAssetAtPath<NavMeshData>(DataPath);

            if (existing == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DataPath));
                AssetDatabase.CreateAsset(baked, DataPath);
                Debug.Log($"[NavFactory] Created {DataPath}.");
                return;
            }

            Bounds wanted = baked.sourceBounds;

            EditorUtility.CopySerialized(baked, existing);
            surface.RemoveData();
            surface.navMeshData = existing;
            surface.AddData();
            EditorUtility.SetDirty(existing);

            // CopySerialized on a native object is the kind of thing that either works or silently
            // does nothing, and a NavMesh that silently did nothing looks exactly like one that
            // worked until an agent tries to walk on it.
            Bounds got = existing.sourceBounds;
            if ((got.center - wanted.center).sqrMagnitude > 0.01f || (got.size - wanted.size).sqrMagnitude > 0.01f)
                Debug.LogError($"[NavFactory] {DataPath} did not take the bake: bounds are {got} and should "
                               + $"be {wanted}. Delete the asset and generate the island again.");
            else
                Debug.Log($"[NavFactory] Wrote {DataPath} in place; its GUID and the scene reference are unchanged.");
        }

        /// <summary>
        /// What came out: how big the walkable surface is against how big it could have been, and
        /// whether an agent can actually get from each landmark to the camp. The second half is the
        /// acceptance criterion for #37, checked here rather than left for somebody to notice.
        /// </summary>
        static void Report(IslandProfile profile, POISpawner spawner, NavMeshSurface surface, double seconds)
        {
            NavMeshTriangulation mesh = NavMesh.CalculateTriangulation();

            double area = 0d;
            for (int i = 0; i + 2 < mesh.indices.Length; i += 3)
            {
                Vector3 a = mesh.vertices[mesh.indices[i]];
                Vector3 b = mesh.vertices[mesh.indices[i + 1]];
                Vector3 c = mesh.vertices[mesh.indices[i + 2]];
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5d;
            }

            float possible = WalkableGround(profile);

            Debug.Log($"[NavFactory] Baked in {seconds:F1}s at {surface.voxelSize}m voxels, "
                      + $"{surface.tileSize}-voxel tiles: {mesh.vertices.Length} vertices, "
                      + $"{mesh.indices.Length / 3} triangles, {area / 10000d:F1} hectares walkable "
                      + $"out of {possible / 10000f:F1} the terrain offers ({area / Mathf.Max(1f, possible) * 100d:F0}%).");

            if (spawner == null) return;

            Vector3 camp = spawner.PositionOf("camp.base");
            foreach (POISpawner.Placement placement in spawner.Placements)
            {
                if (placement == null || placement.Id == "camp.base") continue;
                PathTo(placement.Id, placement.Position, camp);
            }
        }

        /// <summary>
        /// One landmark, reported the way somebody debugging it would want it: whether a walk to the
        /// camp exists at all, how far it is, and how much further that is than flying. A ratio near
        /// 1 means open ground; a big one means the agent is walking round a bay, which is either
        /// correct or the first sign the NavMesh has a hole in it.
        /// </summary>
        static void PathTo(string id, Vector3 from, Vector3 to)
        {
            if (!NavApproach.Route(from, to, out Vector3 start, out Vector3 end,
                                   out float startOffset, out float endOffset))
            {
                Debug.LogError($"[NavFactory] Nothing can walk from '{id}' to the camp, even approaching both "
                               + $"ends from {NavApproach.Ring}m out. It is on its own piece of NavMesh.");
                return;
            }

            var path = new NavMeshPath();
            NavMesh.CalculatePath(start, end, NavMesh.AllAreas, path);

            float length = NavApproach.Length(path);
            float straight = Vector3.Distance(start, end);

            Debug.Log($"[NavFactory] {id} -> camp.base: {path.status}, {length:F0}m over "
                      + $"{path.corners.Length} corners, {length / Mathf.Max(1f, straight):F2}x the straight line"
                      + (startOffset > 1f ? $", setting off {startOffset:F0}m from the marker" : "")
                      + (startOffset >= NavApproach.Ring * 0.8f ? " because the marker is inside the building" : "")
                      + $", arriving {endOffset:F0}m from the camp fire.");
        }

        /// <summary>
        /// A coarse estimate of how much of the island a human could stand on, from the shape rather
        /// than from the bake. It is the number the baked area is worth comparing against: if the
        /// NavMesh comes out at a fraction of this, the bake lost the island rather than trimming it.
        /// </summary>
        static float WalkableGround(IslandProfile profile)
        {
            const float step = 4f;

            var shape = new IslandShape(profile);
            int side = Mathf.Max(2, Mathf.RoundToInt(profile.Size / step));
            float half = profile.Size * 0.5f;
            int walkable = 0;

            // tan(45) = 1, and SlopeAt is a gradient, so the agent's 45 degree limit is a straight
            // comparison against 1 with no trigonometry anywhere.
            float limit = Mathf.Tan(45f * Mathf.Deg2Rad);

            for (int j = 0; j < side; j++)
            {
                float z = -half + (j + 0.5f) * step;
                for (int i = 0; i < side; i++)
                {
                    float x = -half + (i + 0.5f) * step;
                    if (shape.HeightAt(x, z) <= IslandShape.SeaLevel + profile.NavWaterline) continue;
                    if (shape.SlopeAt(x, z, 2f) > limit) continue;
                    walkable++;
                }
            }

            return walkable * step * step;
        }
    }
}
