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

        /// <summary>
        /// The catalog as it ships. The camp position is not typed in: it is searched for, so that a
        /// different seed puts the camp on that island's beach rather than in that island's sea.
        /// After the first generation it is a number in a text file like everything else, and a human
        /// is free to move it.
        /// </summary>
        static POIEntry[] DefaultEntries(IslandProfile profile)
        {
            // Searched against the island with no pads in it, which is the state the catalog is being
            // written for: the pad this search chooses is the one that will exist afterwards.
            var bare = ScriptableObject.CreateInstance<IslandProfile>();
            EditorUtility.CopySerialized(profile, bare);
            bare.Pois = null;

            var shape = new IslandShape(bare);
            Vector2 camp = FindCampSite(shape, bare);
            Object.DestroyImmediate(bare);

            // Facing inland, so walking out of the machine looks at the island rather than the sea.
            float facing = Mathf.Atan2(-camp.x, -camp.y) * Mathf.Rad2Deg;

            return new[]
            {
                new POIEntry
                {
                    Id = "camp.revive",
                    PrefabPath = ReviveMachinePrefabPath,
                    Position = camp,
                    Yaw = facing,
                    SnapToGround = true,
                    PadRadius = 16f,
                    PadFalloff = 14f,
                    PadRaise = 0.6f,
                    MaxSlope = 0.3f
                }
            };
        }

        /// <summary>
        /// A flat spot near the shore, on the sunny side of the island, found by search rather than
        /// by eye. Scores candidates on how close they are to the height a camp wants and how flat
        /// the ground around them is, because a camp on a slope is a camp everything rolls out of.
        /// </summary>
        public static Vector2 FindCampSite(IslandShape shape, IslandProfile profile)
        {
            const float wantedHeight = 4.5f;
            const int steps = 96;

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

                    float height = shape.HeightAt(x, z);
                    if (height < 1.5f || height > 12f) continue;

                    // Flatness over the whole footprint, not just at the centre: a metre of terrain
                    // noise at the sample point says nothing about the twenty metres around it.
                    float roughness = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        float angle = k * Mathf.PI * 0.5f;
                        float sx = x + Mathf.Cos(angle) * 12f;
                        float sz = z + Mathf.Sin(angle) * 12f;
                        roughness += Mathf.Abs(shape.HeightAt(sx, sz) - height);
                    }

                    float score = -Mathf.Abs(height - wantedHeight) - roughness * 0.6f;

                    // A nudge toward the middle of the map, so the camp is not tucked into a corner
                    // where half the island is a long walk away.
                    score -= new Vector2(x, z).magnitude / profile.Size;

                    if (score <= bestScore) continue;

                    bestScore = score;
                    best = new Vector2(Mathf.Round(x), Mathf.Round(z));
                }
            }

            Debug.Log($"[POIFactory] Camp site found at ({best.x}, {best.y}), "
                      + $"ground {shape.HeightAt(best.x, best.y):F1}m, score {bestScore:F2}.");
            return best;
        }
    }
}
