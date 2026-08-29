using System.Collections.Generic;
using EscapeWithYourFriends.Net;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The greybox arena: the map M1 is validated in.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.ArenaBuilder.BuildArena
    ///
    /// Everything here is a grey primitive placed from code, for the same reason the island will be
    /// generated rather than sculpted: a map that only exists as a binary somebody assembled once by
    /// hand cannot be reviewed in a diff, cannot be rebuilt from a terminal, and cannot be tweaked by
    /// changing a number. The whole layout is the constants at the top of this file.
    ///
    /// What it has to contain is set by what M1 has to prove. Punching, tasering, carrying and
    /// throwing are only interesting next to a drop — a punch on flat ground is a stumble, and the
    /// same punch on a catwalk is a four-second fall into a pit somebody has to climb into to get
    /// you out of. So the arena is a stack of heights with a hole in it, and every height is reachable
    /// on foot so the fight can move between them:
    ///
    ///   * a three-tier tower on -X, ground to 3m to 6m, joined by walkable ramps;
    ///   * a catwalk at 6m running the length of the map from the top of the tower, ending in an
    ///     overhang above the pit, which is the single best place in the arena to be shoved from;
    ///   * a pit 4m deep with its own ramp out, so falling in costs time rather than the whole match;
    ///   * loose blocks at 1m to 2.5m for cover and for tripping over;
    ///   * a plank through a gap in the perimeter wall that ends over nothing, because a game about
    ///     throwing your friends needs one place to throw them where the fall guard has to catch them.
    ///
    /// The floor is four slabs rather than one box because of that pit: a hole means the floor cannot
    /// be a single primitive. Each slab is two metres thick for the reason <see cref="SceneBootstrap"/>
    /// documents — nothing in this game moves two metres in one 50Hz physics step, so nothing tunnels
    /// through. The top surface stays at y = 0, which every spawn height in the project assumes.
    /// </summary>
    public static class ArenaBuilder
    {
        const string BootstrapPath = "Assets/_Project/Scenes/Bootstrap.unity";

        /// <summary>Root the whole arena hangs off, so rebuilding it is one DestroyImmediate.</summary>
        const string RootName = "Arena";

        /// <summary>The name the floor used to have when it was the entire arena.</summary>
        const string LegacyFloorName = "Floor";

        const float FloorSize = 60f;
        const float FloorThickness = 2f;
        const float WallHeight = 3f;
        const float WallThickness = 1f;

        /// <summary>Width of the gap in the south wall the plank runs through.</summary>
        const float WallGap = 6f;

        /// <summary>Half-extent of the square hole in the floor, and how far below y = 0 it bottoms out.</summary>
        const float PitHalf = 4f;
        const float PitDepth = 4f;

        /// <summary>Centre of the pit. Off to +X so it is nowhere near the spawn ring.</summary>
        static readonly Vector3 PitCentre = new(10f, 0f, 18f);

        const float RampThickness = 0.5f;

        /// <summary>Four spawn points on a ring, all facing the middle.</summary>
        const int SpawnCount = 4;
        const float SpawnRadius = 6f;

        /// <summary>Matches the spawner's own fallback ring height: clear of the floor, not floating.</summary>
        const float SpawnHeight = 1.2f;

        /// <summary>
        /// Rebuilds the arena in the existing Bootstrap scene and re-points the spawner at its new
        /// spawn transforms. Safe to run repeatedly: it removes what it built last time first.
        /// </summary>
        public static void BuildArena()
        {
            Scene scene = EditorSceneManager.OpenScene(BootstrapPath, OpenSceneMode.Single);

            Build();
            WireSpawnPoints();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, BootstrapPath);

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Builds the geometry into the open scene, replacing any previous arena. Called directly by
        /// <see cref="SceneBootstrap.CreateBootstrapScene"/>, which has no scene to open.
        /// </summary>
        public static void Build()
        {
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
                if (root.name == RootName || root.name == LegacyFloorName)
                    Object.DestroyImmediate(root);

            var arena = new GameObject(RootName);
            Transform t = arena.transform;

            BuildFloor(t);
            BuildPit(t);
            BuildTower(t);
            BuildBlocks(t);
            BuildPerimeter(t);
            BuildSpawnPoints(t);

            Debug.Log($"[ArenaBuilder] Arena rebuilt: {FloorSize}m plate, a {PitHalf * 2f}m pit "
                      + $"{PitDepth}m deep, a catwalk at 6m, and {SpawnCount} spawn points. "
                      + $"{t.childCount} objects.");
        }

        /// <summary>
        /// The plate, as four slabs around the pit rather than one box, because a hole cannot be cut
        /// out of a primitive. The seams are exact: the slabs share edges with the pit's footprint,
        /// so there is no lip to trip a character controller on and no crack to fall through.
        /// </summary>
        static void BuildFloor(Transform parent)
        {
            const float half = FloorSize * 0.5f;
            float y = -FloorThickness * 0.5f;

            float pitMinX = PitCentre.x - PitHalf, pitMaxX = PitCentre.x + PitHalf;
            float pitMinZ = PitCentre.z - PitHalf, pitMaxZ = PitCentre.z + PitHalf;

            // South of the pit, full width; then north of it; then the two strips either side.
            Slab(parent, "Floor.South", -half, half, -half, pitMinZ, y);
            Slab(parent, "Floor.North", -half, half, pitMaxZ, half, y);
            Slab(parent, "Floor.West", -half, pitMinX, pitMinZ, pitMaxZ, y);
            Slab(parent, "Floor.East", pitMaxX, half, pitMinZ, pitMaxZ, y);
        }

        /// <summary>
        /// The hole. A bottom, four walls that hang below the floor slabs rather than poking through
        /// them, and a ramp out.
        ///
        /// The ramp is the design decision worth arguing about. A pit you cannot climb out of removes
        /// a player from the match until somebody kills them, which is a longer punishment than any
        /// other in the game — being carried to the Revive Machine is at least something happening.
        /// Four metres with a 30-degree way out is a detour, not a removal, and the fall guard never
        /// has to hear about it.
        /// </summary>
        static void BuildPit(Transform parent)
        {
            float minX = PitCentre.x - PitHalf, maxX = PitCentre.x + PitHalf;
            float minZ = PitCentre.z - PitHalf, maxZ = PitCentre.z + PitHalf;

            Box(parent, "Pit.Floor",
                new Vector3(PitCentre.x, -PitDepth - 0.5f, PitCentre.z),
                new Vector3(PitHalf * 2f + 2f, 1f, PitHalf * 2f + 2f));

            // Outside the hole's footprint on purpose: a wall centred on the edge would eat half a
            // metre of the hole and leave the floor slabs overhanging nothing.
            var side = new Vector3(1f, PitDepth, PitHalf * 2f);
            var end = new Vector3(PitHalf * 2f + 2f, PitDepth, 1f);
            float wallY = -PitDepth * 0.5f;

            Box(parent, "Pit.Wall.West", new Vector3(minX - 0.5f, wallY, PitCentre.z), side);
            Box(parent, "Pit.Wall.East", new Vector3(maxX + 0.5f, wallY, PitCentre.z), side);
            Box(parent, "Pit.Wall.South", new Vector3(PitCentre.x, wallY, minZ - 0.5f), end);
            Box(parent, "Pit.Wall.North", new Vector3(PitCentre.x, wallY, maxZ + 0.5f), end);

            // The top lands on the lip exactly, not short of it. Half a metre short is not a step a
            // character controller climbs, it is a half-metre gap back into the pit — and unlike the
            // tower's ramps, which end *inside* the platform they serve, a ramp out of a hole has no
            // geometry to overlap into. Its far end is the hole's own edge.
            Ramp(parent, "Pit.Ramp",
                 new Vector3(PitCentre.x, -PitDepth, maxZ - 0.5f),
                 new Vector3(PitCentre.x, 0f, minZ), 3f);
        }

        /// <summary>
        /// Ground to 3m to 6m, then out along a catwalk that ends over the pit.
        ///
        /// Both ramps land flush against the south edge of the platform above them, so the route up
        /// is continuous and a player being chased never has to stop and aim a jump. The catwalk is
        /// deliberately narrow: two and a half metres is wide enough to run along and narrow enough
        /// that a taser at the wrong moment is a fall.
        /// </summary>
        static void BuildTower(Transform parent)
        {
            const float x = -14f;

            // Low platform: 12 x 12, top at 3m, spanning z from -4 to 8.
            Box(parent, "Tower.Platform.Low", new Vector3(x, 2.5f, 2f), new Vector3(12f, 1f, 12f));
            Ramp(parent, "Tower.Ramp.Low", new Vector3(x, 0f, -12f), new Vector3(x, 3f, -3.5f), 5f);

            // High platform: 10 x 8, top at 6m, spanning z from 14 to 22.
            Box(parent, "Tower.Platform.High", new Vector3(x, 5.5f, 18f), new Vector3(10f, 1f, 8f));
            Ramp(parent, "Tower.Ramp.High", new Vector3(x, 3f, 8f), new Vector3(x, 6f, 14.5f), 4f);

            // The overhang. Runs from the high platform's east edge to a metre past the pit's centre,
            // so walking off the end is a six-metre drop into a four-metre hole.
            const float from = -9f;
            float to = PitCentre.x + 3f;
            Box(parent, "Tower.Catwalk",
                new Vector3((from + to) * 0.5f, 5.75f, 18f),
                new Vector3(to - from, 0.5f, 2.5f));
        }

        /// <summary>
        /// Cover. Low enough to vault or be thrown over, high enough that a body behind one is out of
        /// sight, and scattered rather than symmetrical so no two fights look the same.
        /// </summary>
        static void BuildBlocks(Transform parent)
        {
            Box(parent, "Block.A", new Vector3(8f, 0.75f, -8f), new Vector3(6f, 1.5f, 6f));
            Box(parent, "Block.B", new Vector3(-4f, 1.25f, -16f), new Vector3(5f, 2.5f, 5f));
            Box(parent, "Block.C", new Vector3(16f, 0.5f, -2f), new Vector3(4f, 1f, 4f));
            Box(parent, "Block.D", new Vector3(-2f, 1f, 22f), new Vector3(4f, 2f, 7f));
        }

        /// <summary>
        /// A wall on every side but one, and a plank through the gap that ends over nothing.
        ///
        /// The wall exists because a 60m plate with open edges turns every fight into someone
        /// reversing off the map by accident, which is not the same joke as being thrown off it. The
        /// gap and the plank are how the joke stays available — and they keep <c>FallGuard</c> under
        /// test, since a net nobody ever sees catch anything is not a net anyone should trust.
        /// </summary>
        static void BuildPerimeter(Transform parent)
        {
            const float half = FloorSize * 0.5f;
            float edge = half + WallThickness * 0.5f;
            float y = WallHeight * 0.5f;
            float span = FloorSize + WallThickness * 2f;

            Box(parent, "Wall.North", new Vector3(0f, y, edge), new Vector3(span, WallHeight, WallThickness));
            Box(parent, "Wall.East", new Vector3(edge, y, 0f), new Vector3(WallThickness, WallHeight, span));
            Box(parent, "Wall.West", new Vector3(-edge, y, 0f), new Vector3(WallThickness, WallHeight, span));

            // South wall in two pieces, with the gap centred on x = 0 where the plank runs out.
            float piece = (span - WallGap) * 0.5f;
            float offset = (WallGap + piece) * 0.5f;
            Box(parent, "Wall.South.Left", new Vector3(-offset, y, -edge),
                new Vector3(piece, WallHeight, WallThickness));
            Box(parent, "Wall.South.Right", new Vector3(offset, y, -edge),
                new Vector3(piece, WallHeight, WallThickness));

            Box(parent, "Plank", new Vector3(0f, -0.25f, -half - 5f), new Vector3(3f, 0.5f, 10f));
        }

        /// <summary>
        /// A ring of empties facing the middle, so four players start able to see each other. The
        /// spawner falls back to a generated ring when this list is empty, so these exist to be moved
        /// later rather than to make spawning work at all.
        /// </summary>
        static void BuildSpawnPoints(Transform parent)
        {
            for (int i = 0; i < SpawnCount; i++)
            {
                float angle = i * Mathf.PI * 2f / SpawnCount;
                var position = new Vector3(
                    Mathf.Sin(angle) * SpawnRadius, SpawnHeight, Mathf.Cos(angle) * SpawnRadius);

                var go = new GameObject($"SpawnPoint.{i}");
                go.transform.SetParent(parent, false);
                // Flattened before it is used as a facing: the height is part of the position, not
                // part of where the player is looking, and a tilted spawn rotation would pitch the
                // camera into the floor on the first frame.
                var facing = new Vector3(-position.x, 0f, -position.z).normalized;
                go.transform.SetPositionAndRotation(position, Quaternion.LookRotation(facing, Vector3.up));
            }
        }

        /// <summary>
        /// Points the spawner at the arena's spawn transforms. Separate from <see cref="Build"/>
        /// because the spawner lives on the NetworkManager, which <see cref="SceneBootstrap"/> builds
        /// after the geometry.
        /// </summary>
        public static void WireSpawnPoints()
        {
            var spawner = Object.FindFirstObjectByType<PlayerSpawner>();
            if (spawner == null)
            {
                Debug.LogWarning("[ArenaBuilder] No PlayerSpawner in the scene; spawn points not wired. "
                                 + "The spawner falls back to a generated ring, so this is survivable.");
                return;
            }

            var points = new List<Transform>();
            foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name != RootName) continue;

                foreach (Transform child in root.transform)
                    if (child.name.StartsWith("SpawnPoint.")) points.Add(child);
            }

            points.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            var so = new SerializedObject(spawner);
            SerializedProperty list = so.FindProperty("_spawnPoints");
            list.arraySize = points.Count;
            for (int i = 0; i < points.Count; i++)
                list.GetArrayElementAtIndex(i).objectReferenceValue = points[i];
            so.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log($"[ArenaBuilder] PlayerSpawner points at {points.Count} spawn transforms.");
        }

        /// <summary>An axis-aligned grey box, static so it can be culled and light-mapped.</summary>
        static GameObject Box(Transform parent, string name, Vector3 centre, Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = centre;
            go.transform.localScale = size;
            go.isStatic = true;
            return go;
        }

        /// <summary>A floor slab given as its extents, which is how the pit's seams are reasoned about.</summary>
        static void Slab(Transform parent, string name, float minX, float maxX, float minZ, float maxZ, float y)
            => Box(parent, name,
                   new Vector3((minX + maxX) * 0.5f, y, (minZ + maxZ) * 0.5f),
                   new Vector3(maxX - minX, FloorThickness, maxZ - minZ));

        /// <summary>
        /// A slope whose <em>top surface</em> runs from one point to the other, which is what the
        /// caller actually means: both ends are walkable positions, not box centres. The box is
        /// pushed half its thickness down its own local up so the surface lands on the line rather
        /// than the middle of the slab does, and no ramp ends in a step.
        /// </summary>
        static void Ramp(Transform parent, string name, Vector3 bottom, Vector3 top, float width)
        {
            Vector3 delta = top - bottom;
            Quaternion rotation = Quaternion.LookRotation(delta.normalized, Vector3.up);

            var go = Box(parent, name, Vector3.zero, new Vector3(width, RampThickness, delta.magnitude));
            go.transform.SetPositionAndRotation(
                (bottom + top) * 0.5f - rotation * Vector3.up * (RampThickness * 0.5f), rotation);
        }
    }
}
