using System.Collections.Generic;
using System.IO;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.World;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Turns <see cref="POICatalog"/> into things standing on the island.
    ///
    /// Two jobs, and the order between them is the whole design. The catalog's pads are read by
    /// <see cref="IslandShape"/>, so the catalog has to be attached to the profile *before* a single
    /// height is sampled - flattening the baked heightmap afterwards would leave the splatmap and
    /// fourteen thousand trees believing in the hillside that used to be there. Then, once the
    /// terrain exists, the placements are resolved and baked into the scene's spawner.
    ///
    /// Adding a point of interest is an append to POIs.asset and one regeneration. Nothing is
    /// dragged into a scene, and the diff is seven readable lines.
    /// </summary>
    public static class POIFactory
    {
        public const string CatalogPath = "Assets/_Project/Data/POIs.asset";

        const string ReviveMachinePrefabPath = "Assets/_Project/Prefabs/ReviveMachine.prefab";

        /// <summary>
        /// The catalog, created from the defaults below the first time. Created once and then left
        /// alone, because the entire point is that a human edits it; -rebuildPois starts over.
        /// </summary>
        public static POICatalog EnsureCatalog(IslandProfile profile)
        {
            bool rebuild = CommandLine.HasFlag("-rebuildPois");
            var catalog = AssetDatabase.LoadAssetAtPath<POICatalog>(CatalogPath);

            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<POICatalog>();
                catalog.Entries = DefaultEntries(profile);

                Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath));
                AssetDatabase.CreateAsset(catalog, CatalogPath);
                Debug.Log($"[POIFactory] Generated {CatalogPath} with {catalog.Entries.Length} entries.");
            }
            else if (rebuild)
            {
                catalog.Entries = DefaultEntries(profile);
                EditorUtility.SetDirty(catalog);
                Debug.Log($"[POIFactory] Rebuilt {CatalogPath} from code, hand edits discarded (-rebuildPois).");
            }

            // Attaching it to the profile is what puts the pads into the height function. Done here
            // rather than by hand so a clean clone generates the same island as a working copy.
            if (profile.Pois != catalog)
            {
                profile.Pois = catalog;
                EditorUtility.SetDirty(profile);
                Debug.Log("[POIFactory] Attached the catalog to the island profile; its pads are now part of the shape.");
            }

            return catalog;
        }

        /// <summary>
        /// Resolves every entry against the island and writes the result into the spawner. Entries
        /// whose prefab does not exist yet are reported and skipped rather than failing the build:
        /// the catalog is allowed to describe the shop and the casino before #36 has built them.
        /// </summary>
        public static int Bake(IslandProfile profile, POISpawner spawner)
        {
            POICatalog catalog = profile.Pois;
            if (catalog == null || spawner == null) return 0;

            var shape = new IslandShape(profile);
            var so = new SerializedObject(spawner);
            SerializedProperty placements = so.FindProperty("_placements");

            var resolved = new List<(POIEntry entry, NetworkObject prefab, Vector3 position)>();
            var seen = new HashSet<string>();
            int missing = 0;

            foreach (POIEntry entry in catalog.Entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Id)) continue;

                if (!seen.Add(entry.Id))
                    Debug.LogError($"[POIFactory] Two entries share the id '{entry.Id}'; lookups by id will be a coin flip.");

                float ground = shape.HeightAt(entry.Position.x, entry.Position.y);
                float y = entry.SnapToGround ? ground + entry.YOffset : entry.YOffset;
                var position = new Vector3(entry.Position.x, y, entry.Position.y);

                Validate(entry, shape, ground);

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.PrefabPath);
                NetworkObject networkObject = prefab != null ? prefab.GetComponent<NetworkObject>() : null;

                if (prefab == null)
                {
                    Debug.LogWarning($"[POIFactory] '{entry.Id}' wants {entry.PrefabPath}, which does not exist yet. "
                                     + "Placed nothing; the pad under it is still in the terrain.");
                    missing++;
                    continue;
                }

                if (networkObject == null)
                {
                    Debug.LogError($"[POIFactory] '{entry.Id}' points at {entry.PrefabPath}, which has no "
                                   + "NetworkObject. It cannot be spawned.");
                    missing++;
                    continue;
                }

                resolved.Add((entry, networkObject, position));
            }

            // Rewritten whole rather than appended to: this array is generated output, and a second
            // run must not leave two revive machines standing in the same spot.
            placements.arraySize = resolved.Count;
            for (int i = 0; i < resolved.Count; i++)
            {
                (POIEntry entry, NetworkObject prefab, Vector3 position) = resolved[i];

                SerializedProperty element = placements.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("Id").stringValue = entry.Id;
                element.FindPropertyRelative("Prefab").objectReferenceValue = prefab;
                element.FindPropertyRelative("Position").vector3Value = position;
                element.FindPropertyRelative("Euler").vector3Value = new Vector3(0f, entry.Yaw, 0f);

                Debug.Log($"[POIFactory] {entry.Id} -> {entry.PrefabPath} at "
                          + $"({position.x:F1}, {position.y:F1}, {position.z:F1}), yaw {entry.Yaw:F0}"
                          + (entry.PadRadius > 0f ? $", pad {entry.PadRadius}m" : "") + ".");
            }

            so.FindProperty("_catalog").objectReferenceValue = catalog;
            so.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log($"[POIFactory] Baked {resolved.Count} of {catalog.Entries.Length} catalog entries into the "
                      + $"island spawner" + (missing > 0 ? $"; {missing} have no prefab yet" : "") + ".");

            return resolved.Count;
        }

        /// <summary>
        /// The checks worth making at bake time. None of them refuse to place anything - a POI in a
        /// silly spot is a tuning problem, not a build failure - but all of them are invisible until
        /// somebody walks over there, which is exactly the kind of thing a log line is for.
        /// </summary>
        static void Validate(POIEntry entry, IslandShape shape, float ground)
        {
            if (!entry.AllowUnderwater && ground <= IslandShape.SeaLevel)
            {
                Debug.LogError($"[POIFactory] '{entry.Id}' is at {ground:F1}m, under the sea, and is not "
                               + "marked AllowUnderwater.");
            }

            float slope = shape.SlopeAt(entry.Position.x, entry.Position.y);
            if (slope > entry.MaxSlope)
            {
                Debug.LogWarning($"[POIFactory] '{entry.Id}' stands on a slope of {slope:F2}, over its limit of "
                                 + $"{entry.MaxSlope:F2}. Give it a pad or move it.");
            }

            float half = shape.Profile.Size * 0.5f;
            if (Mathf.Abs(entry.Position.x) > half || Mathf.Abs(entry.Position.y) > half)
                Debug.LogError($"[POIFactory] '{entry.Id}' is outside the island square.");
        }


        // ---------------------------------------------------------------- the defaults

        const string GreyboxDir = "Assets/_Project/Prefabs/World";

        /// <summary>What a landmark wants from the ground it stands on.</summary>
        struct SiteWish
        {
            public float WantedHeight;
            public float MinHeight;
            public float MaxHeight;
            public Vector2 Reference;
            public float MinFromReference;
            public float MaxFromReference;
            public float FlatWeight;
            public float Separation;
            public float FootprintRadius;
        }

        /// <summary>
        /// The catalog as it ships. Nothing here is a typed-in coordinate: every landmark is searched
        /// for against the island it is going to stand on, so a different seed puts the village
        /// inland on *that* island rather than in that island's sea.
        ///
        /// After the first generation they are numbers in a text file like everything else, and a
        /// human is free to drag any of them somewhere better.
        /// </summary>
        static POIEntry[] DefaultEntries(IslandProfile profile)
        {
            // Searched against the island with no pads in it, which is the state the catalog is being
            // written for: the pads these searches choose are the ones that will exist afterwards.
            var bare = ScriptableObject.CreateInstance<IslandProfile>();
            EditorUtility.CopySerialized(profile, bare);
            bare.Pois = null;

            var shape = new IslandShape(bare);
            var taken = new List<Vector2>();

            // Camp first, because everything else is placed relative to it: the shop and the casino
            // are a walk, the village is a hike, and the cave is somewhere in between.
            Vector2 camp = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 4.5f, MinHeight = 1.5f, MaxHeight = 12f,
                MaxFromReference = 0f, FlatWeight = 0.6f, FootprintRadius = 12f
            }, "camp");

            Vector2 shop = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 8f, MinHeight = 2f, MaxHeight = 22f, Reference = camp,
                MinFromReference = 70f, MaxFromReference = 160f,
                FlatWeight = 0.7f, Separation = 40f, FootprintRadius = 8f
            }, "shop");

            Vector2 casino = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 6f, MinHeight = 2f, MaxHeight = 20f, Reference = camp,
                MinFromReference = 90f, MaxFromReference = 210f,
                FlatWeight = 0.7f, Separation = 60f, FootprintRadius = 10f
            }, "casino");

            // Far enough that walking into it is a decision rather than an accident.
            Vector2 village = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 20f, MinHeight = 6f, MaxHeight = 48f, Reference = camp,
                MinFromReference = 240f, MaxFromReference = 600f,
                FlatWeight = 0.9f, Separation = 90f, FootprintRadius = 18f
            }, "village");

            // On the tideline. It is the first thing seen from the water and it is where the boat
            // parts come from, so it belongs half in the sea.
            Vector2 wreck = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 0.3f, MinHeight = -1.5f, MaxHeight = 2f, Reference = camp,
                MinFromReference = 60f, MaxFromReference = 320f,
                FlatWeight = 0.4f, Separation = 50f, FootprintRadius = 10f
            }, "wreck");

            Vector2 cave = Site(shape, bare, taken, new SiteWish
            {
                WantedHeight = 34f, MinHeight = 18f, MaxHeight = 80f, Reference = camp,
                MinFromReference = 120f, MaxFromReference = 500f,
                FlatWeight = 0.5f, Separation = 70f, FootprintRadius = 10f
            }, "cave");

            Object.DestroyImmediate(bare);

            float campFacing = Facing(camp, Vector2.zero);

            return new[]
            {
                Entry("camp.base", GreyboxDir + "/BaseCamp.prefab", camp, campFacing,
                      pad: 22f, falloff: 16f, raise: 0.5f, maxSlope: 0.28f),

                // The machine stands inside the camp's own pad rather than on one of its own: two
                // overlapping pads at different heights make a step in the middle of the camp.
                Entry("camp.revive", ReviveMachinePrefabPath, camp + Offset(campFacing, 9f),
                      campFacing + 180f, pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.3f),

                // #43's starting bench. On the camp's pad for the same reason the machine is, and off
                // to one side of it so the two do not fight over the same square metre. One bench is
                // given rather than crafted because the first recipe a player needs is the one that
                // makes a bench possible somewhere else.
                Entry("camp.bench", StationBuilder.BenchPath,
                      camp + Offset(campFacing + 70f, 8f), campFacing + 250f,
                      pad: 0f, falloff: 0f, raise: 0f, maxSlope: 0.3f),

                Entry("shop", GreyboxDir + "/Shop.prefab", shop, Facing(shop, camp),
                      pad: 12f, falloff: 12f, raise: 0.4f, maxSlope: 0.3f),

                Entry("casino", GreyboxDir + "/Casino.prefab", casino, Facing(casino, camp),
                      pad: 14f, falloff: 12f, raise: 0.4f, maxSlope: 0.3f),

                Entry("village", GreyboxDir + "/NativeVillage.prefab", village, Facing(village, camp),
                      pad: 24f, falloff: 20f, raise: 0.3f, maxSlope: 0.32f),

                Entry("wreck", GreyboxDir + "/Wreck.prefab", wreck, Facing(wreck, camp),
                      pad: 10f, falloff: 14f, raise: 0f, maxSlope: 0.5f, allowUnderwater: true),

                Entry("cave", GreyboxDir + "/Cave.prefab", cave, Facing(cave, camp),
                      pad: 13f, falloff: 16f, raise: 0.2f, maxSlope: 0.45f)
            };
        }

        static POIEntry Entry(string id, string prefab, Vector2 position, float yaw, float pad,
                              float falloff, float raise, float maxSlope, bool allowUnderwater = false)
            => new POIEntry
            {
                Id = id,
                PrefabPath = prefab,
                Position = new Vector2(Mathf.Round(position.x), Mathf.Round(position.y)),
                Yaw = Mathf.Round(yaw),
                SnapToGround = true,
                PadRadius = pad,
                PadFalloff = falloff,
                PadRaise = raise,
                MaxSlope = maxSlope,
                AllowUnderwater = allowUnderwater
            };

        /// <summary>Degrees that turn <paramref name="from"/> to look at <paramref name="at"/>.</summary>
        static float Facing(Vector2 from, Vector2 at)
        {
            Vector2 delta = at - from;
            return delta.sqrMagnitude < 0.001f ? 0f : Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg;
        }

        static Vector2 Offset(float yaw, float distance)
        {
            float radians = yaw * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians)) * distance;
        }

        /// <summary>
        /// The best ground on the island for one landmark, by search rather than by eye. Scored on how
        /// close the height is to what the place wants and how flat the ground is across its whole
        /// footprint, because a metre of noise at the sample point says nothing about the twenty
        /// metres the building will actually sit on.
        /// </summary>
        static Vector2 Site(IslandShape shape, IslandProfile profile, List<Vector2> taken,
                            SiteWish wish, string label)
        {
            const int steps = 110;

            float half = profile.Size * 0.5f;
            float step = profile.Size / steps;

            Vector2 best = Vector2.zero;
            float bestScore = float.MinValue;

            for (int j = 1; j < steps; j++)
            {
                float z = -half + j * step;
                for (int i = 1; i < steps; i++)
                {
                    float x = -half + i * step;
                    var candidate = new Vector2(x, z);

                    float height = shape.HeightAt(x, z);
                    if (height < wish.MinHeight || height > wish.MaxHeight) continue;

                    if (wish.MaxFromReference > 0f)
                    {
                        float distance = Vector2.Distance(candidate, wish.Reference);
                        if (distance < wish.MinFromReference || distance > wish.MaxFromReference) continue;
                    }

                    bool crowded = false;
                    foreach (Vector2 other in taken)
                    {
                        if (Vector2.Distance(candidate, other) >= wish.Separation) continue;
                        crowded = true;
                        break;
                    }

                    if (crowded) continue;

                    float footprint = Mathf.Max(4f, wish.FootprintRadius);
                    float roughness = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        float angle = k * Mathf.PI * 0.5f;
                        roughness += Mathf.Abs(shape.HeightAt(x + Mathf.Cos(angle) * footprint,
                                                              z + Mathf.Sin(angle) * footprint) - height);
                    }

                    float score = -Mathf.Abs(height - wish.WantedHeight) - roughness * wish.FlatWeight;

                    // A nudge inland, so nothing ends up tucked into a corner of the square with half
                    // the island a long walk away.
                    score -= candidate.magnitude / profile.Size;

                    if (score <= bestScore) continue;

                    bestScore = score;
                    best = new Vector2(Mathf.Round(x), Mathf.Round(z));
                }
            }

            taken.Add(best);
            Debug.Log($"[POIFactory] {label} site ({best.x}, {best.y}), ground "
                      + $"{shape.HeightAt(best.x, best.y):F1}m, score {bestScore:F2}.");
            return best;
        }

        // ---------------------------------------------------------------- reachability

        /// <summary>
        /// Whether you can walk from the camp to each of the others, and how far it is.
        ///
        /// The acceptance criterion for #36 is that all six landmarks are reachable on foot, and that
        /// is a claim about the terrain rather than about the prefabs, so it is checked against the
        /// shape: a flood fill with a cost over an eight-metre grid of cells that are above water and
        /// no steeper than a character controller can climb. It is not a NavMesh - that is #37 - but
        /// a NavMesh cannot invent a route the terrain does not have.
        /// </summary>
        public static void ReportReachability(IslandProfile profile)
        {
            POICatalog catalog = profile.Pois;
            if (catalog == null || catalog.Entries.Length == 0) return;

            const float cell = 8f;
            const float maxSlope = 0.8f;      // about 39 degrees, past which a character controller stalls
            const float minHeight = 0.25f;

            var shape = new IslandShape(profile);
            int side = Mathf.Max(8, Mathf.RoundToInt(profile.Size / cell));
            float half = profile.Size * 0.5f;

            var walkable = new bool[side, side];
            int walkableCells = 0;

            for (int j = 0; j < side; j++)
            {
                float z = -half + (j + 0.5f) * cell;
                for (int i = 0; i < side; i++)
                {
                    float x = -half + (i + 0.5f) * cell;
                    walkable[j, i] = shape.HeightAt(x, z) > minHeight && shape.SlopeAt(x, z, 4f) <= maxSlope;
                    if (walkable[j, i]) walkableCells++;
                }
            }

            POIEntry start = catalog.Find("camp.base") ?? catalog.Entries[0];
            if (!Cell(start.Position, half, cell, side, out int startX, out int startZ))
            {
                Debug.LogError("[POIFactory] The camp is off the reachability grid; nothing was checked.");
                return;
            }

            var distance = new float[side, side];
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                    distance[j, i] = float.MaxValue;

            // Breadth-first with a cost, which on a uniform grid with only two edge lengths comes out
            // close enough to Dijkstra to report a walking distance anyone would recognise.
            var queue = new Queue<Vector2Int>();
            distance[startZ, startX] = 0f;
            queue.Enqueue(new Vector2Int(startX, startZ));

            int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] dz = { 0, 0, 1, -1, 1, -1, 1, -1 };

            while (queue.Count > 0)
            {
                Vector2Int at = queue.Dequeue();
                for (int k = 0; k < 8; k++)
                {
                    int nx = at.x + dx[k];
                    int nz = at.y + dz[k];
                    if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                    if (!walkable[nz, nx]) continue;

                    float cost = distance[at.y, at.x] + (k < 4 ? cell : cell * 1.41421f);
                    if (cost >= distance[nz, nx]) continue;

                    distance[nz, nx] = cost;
                    queue.Enqueue(new Vector2Int(nx, nz));
                }
            }

            int reached = 0;
            int total = 0;

            foreach (POIEntry entry in catalog.Entries)
            {
                if (entry == null) continue;
                total++;

                if (!Cell(entry.Position, half, cell, side, out int ex, out int ez))
                {
                    Debug.LogError($"[POIFactory] {entry.Id} is off the reachability grid entirely.");
                    continue;
                }

                // A landmark on the tideline sits on a cell that is under water by a few centimetres,
                // so the search widens until it finds walkable ground. It starts at the centre cell
                // and grows one ring at a time rather than taking the best of a wide neighbourhood:
                // a fixed three-cell window quietly shaves up to thirty metres off every distance it
                // reports, which turns a measurement into a flattering guess.
                float best = float.MaxValue;
                int slack = 0;

                for (; slack <= 3 && best >= float.MaxValue; slack++)
                    best = Nearest(distance, ex, ez, side, slack);

                if (best >= float.MaxValue)
                {
                    Debug.LogError($"[POIFactory] {entry.Id} cannot be walked to from the camp: "
                                   + "water or a cliff is in the way.");
                    continue;
                }

                reached++;
                int rings = slack - 1;
                Debug.Log($"[POIFactory] {entry.Id} is {best:F0}m of walking from the camp"
                          + (rings > 0 ? $", landing {rings * cell:F0}m short of its centre - the "
                                       + "ground under it is not walkable" : "") + ".");
            }

            Debug.Log($"[POIFactory] {reached} of {total} landmarks reachable on foot across "
                      + $"{walkableCells} walkable cells of {cell}m at up to {maxSlope:F1} gradient.");
        }

        static bool Cell(Vector2 position, float half, float cell, int side, out int x, out int z)
        {
            x = Mathf.FloorToInt((position.x + half) / cell);
            z = Mathf.FloorToInt((position.y + half) / cell);
            return x >= 0 && z >= 0 && x < side && z < side;
        }

        /// <summary>Shortest distance in any cell within <paramref name="reach"/> cells of this one.</summary>
        static float Nearest(float[,] distance, int x, int z, int side, int reach)
        {
            float best = float.MaxValue;

            for (int j = -reach; j <= reach; j++)
            {
                for (int i = -reach; i <= reach; i++)
                {
                    int nx = x + i;
                    int nz = z + j;
                    if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                    best = Mathf.Min(best, distance[nz, nx]);
                }
            }

            return best;
        }
    }
}
