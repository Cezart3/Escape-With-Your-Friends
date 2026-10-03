using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using EscapeWithYourFriends.AI;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Bakes the island into a TerrainData asset, from the terminal, from a seed.
    ///
    /// Nothing here is sculpted. The whole island is <see cref="IslandShape"/> evaluated on a grid,
    /// which means the island is a number in a text asset and regenerating it is one command:
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath . \
    ///     -executeMethod EscapeWithYourFriends.EditorTools.TerrainGenerator.GenerateIsland \
    ///     -islandSeed 20260830 -logFile island.log
    ///
    /// The log is the evidence. It prints an FNV-1a hash of the heightmap and of the saved asset
    /// bytes, so two runs can be compared without opening the editor, plus a coarse ASCII map and
    /// the land fraction, so a bad island is obvious before anyone loads a scene.
    ///
    /// The generated scene is deliberately kept out of build settings: it holds terrain and a sun
    /// and nothing networked, so shipping it now would only slow the headless tests down. Wiring it
    /// into the game scene is #39.
    /// </summary>
    public static class TerrainGenerator
    {
        /// <summary>
        /// Which island this run bakes. <c>-island 2</c> points every path below at the second set
        /// of assets; with no switch it is the first island and byte-for-byte what it always was.
        ///
        /// ponytail: a static, because one batchmode process bakes one island and threading an id
        /// through eight factories would be a bigger diff than the feature. Everything downstream
        /// of the profile reads <c>IslandProfile.Id</c> instead, which is threaded already.
        /// </summary>
        static string _id = FirstIsland;

        const string FirstIsland = "Island";
        const string SecondIsland = "Island2";

        static string ProfilePath => $"Assets/_Project/Data/{_id}.asset";
        static string TerrainDataPath => $"Assets/_Project/Data/{_id}Terrain.asset";
        static string TerrainMaterialPath => $"Assets/_Project/Data/{_id}Terrain.mat";
        static string ScenePath => $"Assets/_Project/Scenes/{_id}.unity";
        static string TerrainObjectName => _id;
        const string TerrainArtFolder = "Assets/_Project/Art/Terrain";

        // Fixed salt for the placeholder textures: their look must not change when the island seed does.
        const int TextureSalt = 606060;

        // The ASCII map in the log. Wide and short, because terminal characters are about twice as
        // tall as they are wide and a square map printed square looks stretched.
        const int MapWidth = 72;
        const int MapHeight = 30;

        [MenuItem("EWYF/Generate Island Terrain")]
        public static void GenerateIsland()
        {
            _id = CommandLine.GetInt("-island", 1) == 2 ? SecondIsland : FirstIsland;

            IslandProfile profile = LoadOrCreateProfile();
            ApplyCommandLine(profile);

            int resolution = ValidResolution(profile.Resolution);
            if (resolution != profile.Resolution)
            {
                Debug.LogWarning($"[TerrainGenerator] Heightmap resolution {profile.Resolution} is not "
                                 + $"2^n+1; using {resolution} instead.");
                profile.Resolution = resolution;
            }

            EditorUtility.SetDirty(profile);

            // Before a single height is sampled. The catalog carries the flattened pads under the
            // camps, and those live inside the height function - levelling the baked heightmap
            // afterwards would leave the splatmap and every tree standing on the hill that was there.
            POIFactory.EnsureCatalog(profile);

            Debug.Log($"[TerrainGenerator] Seed {profile.Seed}, {profile.Size}m square, {resolution}^2 "
                      + $"samples, heights {-profile.SeabedDepth}m to {profile.PeakHeight}m.");

            var stopwatch = Stopwatch.StartNew();
            float[,] heights = Sample(profile, resolution);
            FillPuddles(profile, heights, resolution);
            stopwatch.Stop();

            uint heightmapHash = HashHeights(heights);
            Debug.Log($"[TerrainGenerator] Sampled in {stopwatch.ElapsedMilliseconds}ms. "
                      + $"Heightmap hash {heightmapHash:X8}.");

            ReportShape(profile, heights, resolution);

            TerrainData data = WriteTerrainData(profile, heights, resolution);

            int splatResolution = ValidSplatResolution(profile.SplatResolution);
            if (splatResolution != profile.SplatResolution)
            {
                Debug.LogWarning($"[TerrainGenerator] Splat resolution {profile.SplatResolution} is not a "
                                 + $"power of two; using {splatResolution} instead.");
                profile.SplatResolution = splatResolution;
            }

            stopwatch.Restart();
            float[,,] splat = SampleSplat(profile, splatResolution, out bool[,] land);
            stopwatch.Stop();

            uint splatHash = HashSplat(splat);
            Debug.Log($"[TerrainGenerator] Painted {splatResolution}^2 splat cells in "
                      + $"{stopwatch.ElapsedMilliseconds}ms. Splatmap hash {splatHash:X8}.");

            ReportCover(splat, land, splatResolution);
            WriteSplat(profile, data, splat, splatResolution);

            WriteFlora(profile, data);
            WriteDetail(profile, data);

            WriteScene(profile, data);

            // Read back what actually landed on disk. The hash above proves the maths repeats; this
            // one proves the asset does, which is what the issue actually asks for.
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            uint assetHash = HashFile(TerrainDataPath);

            var written = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
            Debug.Log($"[TerrainGenerator] Wrote {TerrainDataPath} (asset hash {assetHash:X8}) and {ScenePath}. "
                      + $"Heightmap {written.heightmapResolution}^2, alphamap {written.alphamapResolution}^2, "
                      + $"{written.terrainLayers.Length} layers.");
        }

        /// <summary>
        /// The profile asset, created with its defaults the first time. Generating an island must
        /// work on a clean clone with no manual step in the editor.
        /// </summary>
        static IslandProfile LoadOrCreateProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<IslandProfile>(ProfilePath);
            if (profile != null)
            {
                // An island baked before the id existed has an empty one, and every factory
                // downstream would then name its assets after nothing at all.
                if (string.IsNullOrEmpty(profile.Id)) profile.Id = _id;
                return profile;
            }

            profile = ScriptableObject.CreateInstance<IslandProfile>();
            profile.Id = _id;
            if (_id == SecondIsland) Harden(profile);

            Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath));
            AssetDatabase.CreateAsset(profile, ProfilePath);
            Debug.Log($"[TerrainGenerator] Created {ProfilePath} with "
                      + (_id == SecondIsland ? "the second island's parameters." : "default parameters."));
            return profile;
        }

        /// <summary>
        /// The second island, as a set of numbers rather than as a second generator.
        ///
        /// Every value here is one the first island already had; none of them is new machinery. The
        /// brief is that a player standing on the beach knows within half a minute that this is the
        /// worse place, so the changes are the ones that read at a glance: half the size so the
        /// danger is not diluted, almost no beach because the land meets the sea as rock, a coast
        /// cut into headlands, a higher and sharper mountain, the rock line dropped to 40m so most
        /// of what is visible is bare, and the forest thinned to a few stands of highland trees
        /// with no palms in them at all.
        ///
        /// It only runs when the asset is created. After that the YAML is the truth and this code
        /// has no say, which is what makes the island tunable with sed - delete the asset to get
        /// these numbers back.
        /// </summary>
        static void Harden(IslandProfile p)
        {
            p.Seed = 20260916;

            // Half the island, and the sample grids halved with it: the same metres per sample the
            // first island has, so nothing about the detail changes except how much of it there is.
            p.Size = 512f;
            p.Resolution = 513;
            p.SplatResolution = 256;
            p.DetailResolution = 256;
            p.WaterDepthResolution = 256;

            // Taller and steeper out of less ground. The relief is choppier (smaller features, more
            // height) and the warp is pulled in to match, or the ridges bend further than they run.
            p.SeabedDepth = 45f;
            p.PeakHeight = 190f;
            p.HillFeatureSize = 220f;
            p.HillHeight = 58f;
            p.HillWaterLine = 0.44f;
            p.WarpStrength = 80f;
            p.WarpFeatureSize = 300f;

            // A coast of headlands and inlets rather than a ring of sand, and a shore that stays at
            // its natural slope. Between them this is what makes a landing a decision.
            p.CoastInnerRadius = 0.34f;
            p.CoastOuterRadius = 0.80f;
            p.CoastRaggedness = 0.22f;
            p.CoastFeatureSize = 140f;
            p.BeachBand = 1.6f;
            p.BeachFlatten = 0.75f;

            p.MountainCentre = new Vector2(0.06f, -0.10f);
            p.MountainRadius = 0.40f;
            p.MountainHeight = 150f;
            p.MountainSharpness = 3.0f;
            p.MountainRidge = 0.55f;
            p.MountainRidgeFeatureSize = 90f;

            // Grey. Sand only where the water actually reaches, rock from 26 degrees and from 40m up.
            p.SandTop = 1.2f;
            p.SandBlend = 1.2f;
            p.RockSlope = 0.45f;
            p.RockSlopeBlend = 0.2f;
            p.RockHeight = 40f;
            p.RockHeightBlend = 18f;
            p.DirtThreshold = 0.50f;

            // No palms at all - a palm reads as holiday, and this island is not one. What is left is
            // highland trees in thin stands, which is also a great deal less cover to run through.
            p.PalmDensity = 0f;
            p.JungleDensity = 0.5f;
            p.HighlandDensity = 0.95f;
            p.BushDensity = 0.35f;
            p.GroveFloor = 0.45f;
            p.GrassThreshold = 0.55f;

            // Half the island means half the draw distance buys the same view, and this one has to
            // run on the same integrated GPU with worse weather sitting in front of it (#38).
            p.TreeDistance = 260f;
            p.DetailDistance = 70f;

            // Twice the fog the shared sky profile asks for. It is the cheapest hostile thing on the
            // list and the one that lands first: the mountain is a shape rather than a place, and a
            // headhunter with 75m of vision now sees you at about the distance you see it.
            p.FogScale = 2f;
        }

        /// <summary>
        /// Command line beats the asset. Overrides are written back into the profile, so the asset
        /// always describes the island that was last baked rather than the one somebody meant to bake.
        /// </summary>
        static void ApplyCommandLine(IslandProfile profile)
        {
            profile.Seed = CommandLine.GetInt("-islandSeed", profile.Seed);
            profile.Size = CommandLine.GetFloat("-islandSize", profile.Size);
            profile.Resolution = CommandLine.GetInt("-islandRes", profile.Resolution);
        }

        /// <summary>Nearest 2^n+1 at or below the request, clamped to what Unity accepts.</summary>
        static int ValidResolution(int requested)
        {
            int clamped = Mathf.Clamp(requested, 33, 4097);
            int power = 32;
            while (power * 2 + 1 <= clamped) power *= 2;
            return power + 1;
        }

        /// <summary>
        /// Evaluates the shape over the grid. Unity indexes heights as [z, x] and stores them
        /// normalised into 0..1 over the vertical size of the terrain, so sea level ends up at
        /// SeabedDepth / TotalHeight rather than at 0.
        /// </summary>
        static float[,] Sample(IslandProfile profile, int resolution)
        {
            var shape = new IslandShape(profile);
            var heights = new float[resolution, resolution];

            float half = profile.Size * 0.5f;
            float step = profile.Size / (resolution - 1);
            float total = profile.TotalHeight;

            for (int z = 0; z < resolution; z++)
            {
                float worldZ = -half + z * step;
                for (int x = 0; x < resolution; x++)
                {
                    float worldX = -half + x * step;
                    heights[z, x] = Mathf.Clamp01((shape.HeightAt(worldX, worldZ) + profile.SeabedDepth) / total);
                }
            }

            return heights;
        }

        /// <summary>
        /// Ground below sea level that the sea cannot reach. The sea is one flat plane under the whole
        /// island, so every inland dip below zero showed it as a puddle of ocean in a field. A flood
        /// fill from the map's edge finds the real sea; everything below it that the fill never
        /// reached is raised to just above the water. Heightmap only: <c>IslandShape</c> still calls
        /// those spots water, so nothing gets planted in them, which is the right bare patch anyway.
        /// </summary>
        static void FillPuddles(IslandProfile profile, float[,] heights, int resolution)
        {
            float total = profile.TotalHeight;
            float sea = (IslandShape.SeaLevel + profile.SeabedDepth) / total;
            float dry = (IslandShape.SeaLevel + 0.3f + profile.SeabedDepth) / total;

            var reached = new bool[resolution, resolution];
            var open = new System.Collections.Generic.Stack<(int z, int x)>();
            for (int i = 0; i < resolution; i++)
            {
                open.Push((0, i));
                open.Push((resolution - 1, i));
                open.Push((i, 0));
                open.Push((i, resolution - 1));
            }

            while (open.Count > 0)
            {
                var (z, x) = open.Pop();
                if (z < 0 || x < 0 || z >= resolution || x >= resolution) continue;
                if (reached[z, x] || heights[z, x] > sea) continue;
                reached[z, x] = true;
                open.Push((z + 1, x));
                open.Push((z - 1, x));
                open.Push((z, x + 1));
                open.Push((z, x - 1));
            }

            int filled = 0;
            for (int z = 0; z < resolution; z++)
            for (int x = 0; x < resolution; x++)
            {
                if (reached[z, x] || heights[z, x] > sea) continue;
                heights[z, x] = dry;
                filled++;
            }

            Debug.Log($"[TerrainGenerator] {filled} inland samples below the sea raised out of it.");
        }

        /// <summary>
        /// Creates or updates the terrain asset. The existing asset is reused when there is one, so
        /// its GUID survives and every scene reference to it keeps working across a regeneration.
        /// </summary>
        static TerrainData WriteTerrainData(IslandProfile profile, float[,] heights, int resolution)
        {
            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
            bool fresh = data == null;
            if (fresh) data = new TerrainData();

            // Resolution first: setting it resets the size, so the order here is not cosmetic.
            data.heightmapResolution = resolution;
            data.size = new Vector3(profile.Size, profile.TotalHeight, profile.Size);
            data.SetHeights(0, 0, heights);

            if (fresh)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TerrainDataPath));
                AssetDatabase.CreateAsset(data, TerrainDataPath);
            }

            EditorUtility.SetDirty(data);
            return data;
        }

        /// <summary>
        /// A scene holding the terrain and a sun. Rebuilt from scratch every time rather than
        /// patched, because it contains nothing a human is allowed to have edited.
        /// </summary>
        static void WriteScene(IslandProfile profile, TerrainData data)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // NewScene unloads unused assets, and on a first bake after an art import that includes
            // the POI catalog: profile.Pois reads null from here on. Everything below hangs off it -
            // the landmarks, the herds, the native camps and the spawn ring - and each fell back to
            // the origin, so the players woke up inside the village. Reattached once, for all of them.
            if (profile.Pois == null)
                profile.Pois = AssetDatabase.LoadAssetAtPath<POICatalog>(POIFactory.CatalogPathFor(profile));

            var island = new GameObject(TerrainObjectName);
            island.transform.position = profile.TerrainOrigin;

            var terrain = island.AddComponent<Terrain>();
            terrain.terrainData = data;

            // The terrain gets its material spelled out rather than left null. A null template falls
            // back to the built-in Nature/Terrain/Standard shader, which does not exist under URP -
            // and a terrain with no shader is a kilometre of magenta.
            terrain.materialTemplate = EnsureTerrainMaterial(out bool stylized);

            // EWYF/StylizedTerrain has no basemap and no instanced path (see the shader), so the
            // terrain is told to need neither: drawn with the full shader to the horizon, which is
            // five texture fetches, cheaper than URP's own terrain shader up close.
            terrain.drawInstanced = !stylized;
            terrain.heightmapPixelError = 5f;
            terrain.basemapDistance = stylized ? 20000f : 400f;

            // The vegetation budget, all of it in one place. Trees are meshes out to
            // TreeBillboardDistance and cheap impostors from there to TreeDistance; grass stops
            // dead at DetailDistance, which is where an integrated GPU spends most of its frame.
            terrain.treeDistance = profile.TreeDistance;
            terrain.treeBillboardDistance = profile.TreeBillboardDistance;
            terrain.treeCrossFadeLength = profile.TreeCrossFade;
            terrain.treeMaximumFullLODCount = profile.TreeMaximumFullLOD;
            terrain.detailObjectDistance = profile.DetailDistance;
            terrain.detailObjectDensity = profile.DetailDensity;

            var collider = island.AddComponent<TerrainCollider>();
            collider.terrainData = data;

            // Those distances are what an integrated GPU can least afford, so they are not fixed:
            // TerrainQuality scales them to whatever quality level the machine ended up on.
            var quality = island.AddComponent<TerrainQuality>();
            quality.Configure(terrain, profile.TreeDistance, profile.TreeBillboardDistance,
                              profile.DetailDistance, profile.DetailDensity);

            // One directional light for the whole island, driven by the clock rather than posed.
            // It is the sun while the sun is up and the moon after that - see DayNightCycle for why
            // that is one light turning around rather than two lights handing over.
            var sun = new GameObject("Sun");
            Light light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.shadowBias = 0.05f;

            DayNightProfile sky = SkyFactory.EnsureProfile();
            Material skyMaterial = SkyFactory.EnsureSkyMaterial();

            var cycle = sun.AddComponent<DayNightCycle>();
            cycle.Profile = sky;
            cycle.FogScale = profile.FogScale;
            cycle.Sun = light;
            cycle.Sky = skyMaterial;

            SkyFactory.Report(cycle);

            // The cycle runs on a hidden copy of the skybox so it never writes to the asset. That
            // copy must not be what the scene file points at, or the saved scene references an
            // object that does not exist the next time it is opened.
            RenderSettings.skybox = skyMaterial;

            // The sea, as a prefab instance rather than as loose objects, so a shader or mesh
            // change reaches the scene without regenerating the island. It sits at the world origin
            // and moves itself to follow the camera at runtime.
            GameObject waterPrefab = WaterFactory.EnsureWater(profile);
            if (waterPrefab != null)
            {
                var water = (GameObject)PrefabUtility.InstantiatePrefab(waterPrefab, scene);
                water.transform.position = new Vector3(0f, IslandShape.SeaLevel, 0f);
            }

            // The points of interest, resolved against the terrain that now exists and baked into a
            // spawner that lives in this scene rather than in Bootstrap: they belong to the island,
            // and the arena has its own props.
            var pois = new GameObject("POIs");
            var spawner = pois.AddComponent<POISpawner>();
            POIFactory.Bake(profile, spawner);
            POIFactory.ReportReachability(profile);

            // The walkable surface, baked last of the world-building steps because it wants the
            // terrain, its collider and the resolved POI positions all to exist first. It puts the
            // buildings in temporarily, bakes around them and takes them out again.
            NavFactory.Bake(profile, spawner);

            // The wildlife, after the NavMesh, because a zone with nowhere to stand is a zone that
            // silently spawns nothing. Its zone centres hang off the POIs above, so the herds follow
            // the map when the seed changes instead of ending up in the sea.
            var animals = new GameObject("Animals");
            var wildlife = animals.AddComponent<AnimalSpawner>();
            AnimalFactory.BakeZones(profile, wildlife);

            // The people, after the wildlife and for the same reason: their camps hang off the POIs
            // and their bodies need somewhere to stand. Separate object from the animals because the
            // two spawners have separate off-switches - -noAnimals and -noNatives - and most tests
            // want exactly one of the two.
            var natives = new GameObject("Natives");
            var village = natives.AddComponent<NativeSpawner>();
            NativeFactory.BakeCamps(profile, village);

            WriteSpawnPoints(profile);

            // The first objective, and the only thing in this scene that is about the game rather
            // than about the world. It lives here because it is a property of this map: the arena
            // has no wreck to point at.
            var intro = new GameObject("Intro");
            intro.AddComponent<IslandIntro>();

            // Dormant unless -navWalk asks for it. It is the acceptance test for #37 living in the
            // scene it tests, so it cannot drift away from the island it was written against.
            var navCheck = new GameObject("NavCheck");
            navCheck.AddComponent<NavWalk>();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        /// <summary>
        /// Four places to stand, in a ring round the camp fire, facing in.
        ///
        /// Facing in matters more than it sounds. The first thing that happens in a fresh session is
        /// four people appearing at once, and if they spawn facing outward the first thing each of
        /// them sees is trees - so nobody knows anyone else is there until somebody turns round. A
        /// ring facing the middle means the first frame of the game is your three friends.
        ///
        /// The ring is placed off the camp's own POI entry rather than at a fixed coordinate, so it
        /// follows the camp when the seed changes or somebody moves it.
        /// </summary>
        static void WriteSpawnPoints(IslandProfile profile)
        {
            const int count = 4;
            const float radius = 6.5f;
            const float clearance = 1.2f;

            POIEntry camp = profile.Pois != null ? profile.Pois.Find("camp.base") : null;
            Vector2 centre = camp != null ? camp.Position : Vector2.zero;

            var shape = new IslandShape(profile);

            var root = new GameObject("SpawnPoints");
            var points = new Transform[count];

            for (int i = 0; i < count; i++)
            {
                float angle = i * Mathf.PI * 2f / count;
                float x = centre.x + Mathf.Sin(angle) * radius;
                float z = centre.y + Mathf.Cos(angle) * radius;

                // On the ground the terrain actually has, not on the ring's own plane: the camp pad
                // is level but the clearance still has to come off the baked surface.
                float y = shape.HeightAt(x, z) + clearance;

                var go = new GameObject($"SpawnPoint.{i}");
                go.transform.SetParent(root.transform, false);

                var facing = new Vector3(centre.x - x, 0f, centre.y - z).normalized;
                go.transform.SetPositionAndRotation(new Vector3(x, y, z),
                                                    Quaternion.LookRotation(facing, Vector3.up));
                points[i] = go.transform;
            }

            var component = root.AddComponent<SceneSpawnPoints>();
            var so = new SerializedObject(component);
            SerializedProperty list = so.FindProperty("_points");
            list.arraySize = count;
            for (int i = 0; i < count; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = points[i];
            so.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log($"[TerrainGenerator] {count} spawn points on a {radius}m ring round the camp at "
                      + $"({centre.x}, {centre.y}), ground {shape.HeightAt(centre.x, centre.y):F1}m.");
        }

        /// <summary>
        /// What the island actually came out as: how much of it is dry, how tall it got, and a
        /// picture. Cheap insurance against a parameter change that quietly drowns the whole thing.
        /// </summary>
        static void ReportShape(IslandProfile profile, float[,] heights, int resolution)
        {
            float total = profile.TotalHeight;
            float seaLevel = profile.SeabedDepth / total;

            int land = 0;
            int beach = 0;
            float peak = float.MinValue;
            double sumLand = 0d;

            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float metres = heights[z, x] * total - profile.SeabedDepth;
                    if (metres > peak) peak = metres;
                    if (metres <= 0f) continue;

                    land++;
                    sumLand += metres;
                    if (metres < profile.BeachBand) beach++;
                }
            }

            int cells = resolution * resolution;
            float landFraction = land / (float)cells;
            float meanLand = land > 0 ? (float)(sumLand / land) : 0f;

            Debug.Log($"[TerrainGenerator] Land {landFraction * 100f:F1}% of the square "
                      + $"({land * (profile.Size * profile.Size / cells) / 10000f:F1} hectares), "
                      + $"beach {beach * 100f / Mathf.Max(1, land):F1}% of the land, "
                      + $"mean land height {meanLand:F1}m, peak {peak:F1}m.");

            Debug.Log("[TerrainGenerator] Island map (~ deep, . shallow, : beach, - low, + hills, ^ peak):\n"
                      + AsciiMap(heights, resolution, seaLevel, profile));
        }

        static string AsciiMap(float[,] heights, int resolution, float seaLevel, IslandProfile profile)
        {
            var map = new StringBuilder(MapHeight * (MapWidth + 1));
            float total = profile.TotalHeight;

            // Rows are printed north to south, so the map reads the way the terrain looks from above
            // with +Z at the top.
            for (int row = MapHeight - 1; row >= 0; row--)
            {
                int z = Mathf.Clamp(Mathf.RoundToInt(row / (float)(MapHeight - 1) * (resolution - 1)), 0, resolution - 1);
                for (int column = 0; column < MapWidth; column++)
                {
                    int x = Mathf.Clamp(Mathf.RoundToInt(column / (float)(MapWidth - 1) * (resolution - 1)), 0, resolution - 1);
                    float metres = heights[z, x] * total - profile.SeabedDepth;

                    char glyph;
                    if (metres < -8f) glyph = '~';
                    else if (metres <= 0f) glyph = '.';
                    else if (metres < profile.BeachBand) glyph = ':';
                    else if (metres < 25f) glyph = '-';
                    else if (metres < 70f) glyph = '+';
                    else glyph = '^';

                    map.Append(glyph);
                }

                map.Append('\n');
            }

            return map.ToString();
        }

        /// <summary>
        /// Evaluates the cover rules over the alphamap grid. Slope comes from central differences on
        /// the shape itself rather than from the baked heightmap, so the painting does not inherit the
        /// stair-stepping of a coarser height sample.
        /// </summary>
        static float[,,] SampleSplat(IslandProfile profile, int resolution, out bool[,] land)
        {
            var shape = new IslandShape(profile);
            var splat = new IslandSplat(shape);
            var map = new float[resolution, resolution, IslandSplat.LayerCount];
            land = new bool[resolution, resolution];
            var weights = new float[IslandSplat.LayerCount];

            float half = profile.Size * 0.5f;
            float step = profile.Size / resolution;

            for (int z = 0; z < resolution; z++)
            {
                float worldZ = -half + (z + 0.5f) * step;
                for (int x = 0; x < resolution; x++)
                {
                    float worldX = -half + (x + 0.5f) * step;

                    float height = shape.HeightAt(worldX, worldZ);
                    land[z, x] = height > IslandShape.SeaLevel;

                    splat.Weights(height, shape.SlopeAt(worldX, worldZ), worldX, worldZ, weights);
                    for (int layer = 0; layer < IslandSplat.LayerCount; layer++)
                        map[z, x, layer] = weights[layer];
                }
            }

            return map;
        }

        /// <summary>
        /// Re-imports the painted layers (tools/art/terrain.py) without touching a height or a splat:
        /// both islands share the four layers, so neither needs regenerating for new ground art.
        /// </summary>
        [MenuItem("EWYF/Refresh Terrain Layers")]
        public static void RefreshLayers()
        {
            EnsureLayers(LoadOrCreateProfile());
            AssetDatabase.SaveAssets();
        }

        /// <summary>Nearest power of two at or below the request. Alphamaps are not 2^n+1, unlike heightmaps.</summary>
        static int ValidSplatResolution(int requested)
        {
            int clamped = Mathf.Clamp(requested, 64, 2048);
            int power = 64;
            while (power * 2 <= clamped) power *= 2;
            return power;
        }

        /// <summary>
        /// Hangs the four layers on the terrain and writes the alphamap. The resolution is set first
        /// for the same reason the heightmap resolution is: changing it throws the maps away.
        /// </summary>
        static void WriteSplat(IslandProfile profile, TerrainData data, float[,,] splat, int resolution)
        {
            data.terrainLayers = EnsureLayers(profile);
            data.alphamapResolution = resolution;
            data.SetAlphamaps(0, 0, splat);
            EditorUtility.SetDirty(data);
        }

        /// <summary>
        /// The terrain material, created once and reused so its GUID survives a regeneration. On the
        /// island's own terrain shader when it imported (<see cref="StyleLook.WearTerrain"/>), on URP's
        /// otherwise; <paramref name="stylized"/> says which.
        /// </summary>
        static Material EnsureTerrainMaterial(out bool stylized)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(TerrainMaterialPath);
            Shader shader = Shader.Find(StyleLook.TerrainShaderName);
            stylized = shader != null;

            if (!stylized)
            {
                Debug.LogError($"[TerrainGenerator] {StyleLook.TerrainShaderName} not found; the ground falls "
                               + "back to URP Terrain/Lit and does not match the models on it.");
                shader = Shader.Find("Universal Render Pipeline/Terrain/Lit");
            }

            if (shader == null)
            {
                Debug.LogError("[TerrainGenerator] URP terrain shader missing; terrain will render magenta.");
                return material;
            }

            if (material == null)
            {
                material = new Material(shader);
                Directory.CreateDirectory(Path.GetDirectoryName(TerrainMaterialPath));
                AssetDatabase.CreateAsset(material, TerrainMaterialPath);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
                EditorUtility.SetDirty(material);
            }

            if (stylized) StyleLook.WearTerrain(material);
            return material;
        }

        /// <summary>
        /// The four terrain layers, with textures generated the first time. Existing assets are reused
        /// so the terrain keeps pointing at the same GUIDs across a regeneration, and so an art pass
        /// that replaces a texture is not undone the next time somebody rerolls the seed.
        /// </summary>
        static TerrainLayer[] EnsureLayers(IslandProfile profile)
        {
            Directory.CreateDirectory(TerrainArtFolder);

            float[] tiling =
            {
                profile.SandTiling, profile.GrassTiling, profile.RockTiling, profile.DirtTiling
            };

            // Base colour and grain colour per layer. Placeholder ground until the art pass, but
            // placeholder ground you can read a slope off, which greybox grey cannot do.
            Color[] baseColours =
            {
                new Color(0.86f, 0.79f, 0.60f), new Color(0.33f, 0.46f, 0.22f),
                new Color(0.49f, 0.48f, 0.46f), new Color(0.45f, 0.35f, 0.25f)
            };

            Color[] grainColours =
            {
                new Color(0.75f, 0.68f, 0.50f), new Color(0.24f, 0.36f, 0.16f),
                new Color(0.33f, 0.33f, 0.32f), new Color(0.33f, 0.26f, 0.18f)
            };

            var layers = new TerrainLayer[IslandSplat.LayerCount];
            for (int i = 0; i < IslandSplat.LayerCount; i++)
            {
                string name = IslandSplat.LayerNames[i];
                Texture2D texture = EnsureTexture($"{TerrainArtFolder}/{name}.png", baseColours[i], grainColours[i], i);
                layers[i] = EnsureLayer($"{TerrainArtFolder}/{name}.terrainlayer", texture, tiling[i]);
            }

            return layers;
        }

        static TerrainLayer EnsureLayer(string path, Texture2D texture, float tiling)
        {
            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            bool fresh = layer == null;
            if (fresh) layer = new TerrainLayer();

            layer.diffuseTexture = texture;
            layer.tileSize = new Vector2(tiling, tiling);
            layer.tileOffset = Vector2.zero;
            layer.specular = Color.black;
            layer.metallic = 0f;
            layer.smoothness = 0.03f;

            if (fresh) AssetDatabase.CreateAsset(layer, path);
            EditorUtility.SetDirty(layer);
            return layer;
        }

        /// <summary>The painted layers' size (tools/art/terrain.py), which the importer must not halve.</summary>
        const int PaintedSize = 512;

        /// <summary>
        /// A layer texture. The painted ones (tools/art/terrain.py, #245) are kept, imported at their
        /// full 512. A missing one gets a placeholder grain: two octaves of the same noise the island
        /// is built from, cross-faded against a wrapped copy of themselves so the texture repeats
        /// without a visible seam every few metres.
        /// </summary>
        static Texture2D EnsureTexture(string path, Color baseColour, Color grainColour, int salt)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null)
            {
                if (AssetImporter.GetAtPath(path) is TextureImporter painted && painted.maxTextureSize != PaintedSize)
                {
                    painted.wrapMode = TextureWrapMode.Repeat;
                    painted.maxTextureSize = PaintedSize;
                    painted.textureCompression = TextureImporterCompression.Compressed;
                    painted.SaveAndReimport();
                }
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }

            const int size = 256;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float coarse = Tileable(x, y, size, 8, TextureSalt + salt * 31);
                    float fine = Tileable(x, y, size, 32, TextureSalt + salt * 31 + 7);
                    float grain = Mathf.Clamp01(coarse * 0.55f + fine * 0.45f);
                    pixels[y * size + x] = Color.Lerp(baseColour, grainColour, grain);
                }
            }

            texture.SetPixels32(pixels);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.maxTextureSize = size;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.SaveAndReimport();
            }

            Debug.Log($"[TerrainGenerator] Generated placeholder texture {path}.");
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>
        /// Noise that wraps. Four samples of the same field, one per corner of the wrapped square,
        /// blended by distance, which is the cheap standard trick for a seamless tile. Periods have to
        /// divide the texture evenly, hence integer cells per side.
        /// </summary>
        internal static float Tileable(int x, int y, int size, int cells, int seed)
        {
            float scale = cells / (float)size;
            float fx = x * scale;
            float fy = y * scale;

            float a = IslandShape.Noise(fx, fy, seed) * (cells - fx) * (cells - fy);
            float b = IslandShape.Noise(fx - cells, fy, seed) * fx * (cells - fy);
            float c = IslandShape.Noise(fx, fy - cells, seed) * (cells - fx) * fy;
            float d = IslandShape.Noise(fx - cells, fy - cells, seed) * fx * fy;

            return (a + b + c + d) / (cells * cells);
        }

        /// <summary>
        /// How the island came out painted. Reported by dominant layer over the dry land, because the
        /// seabed is three quarters of the square and is all sand, so a whole-square number says
        /// nothing about what anyone will walk on.
        /// </summary>
        static void ReportCover(float[,,] splat, bool[,] land, int resolution)
        {
            var counts = new int[IslandSplat.LayerCount];
            int dry = 0;

            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    if (!land[z, x]) continue;

                    int best = 0;
                    for (int layer = 1; layer < IslandSplat.LayerCount; layer++)
                        if (splat[z, x, layer] > splat[z, x, best]) best = layer;

                    counts[best]++;
                    dry++;
                }
            }

            var line = new StringBuilder("[TerrainGenerator] Cover of the dry land:");
            for (int layer = 0; layer < IslandSplat.LayerCount; layer++)
                line.Append($" {IslandSplat.LayerNames[layer]} {counts[layer] * 100f / Mathf.Max(1, dry):F1}%");

            Debug.Log(line.ToString());
        }

        /// <summary>
        /// Plants the island. Trees go in as terrain tree instances rather than as scene objects,
        /// which is the whole reason this is cheap: the terrain culls, LODs and batches them itself,
        /// and the scene file stays four kilobytes instead of carrying ten thousand transforms.
        ///
        /// Tree positions are normalised into 0..1 over the terrain, and the height is normalised
        /// over the vertical size the same way the heightmap is. Colours are forced to white because
        /// the default TreeInstance is all zeroes, and a tree with a black instance colour renders
        /// black.
        /// </summary>
        static void WriteFlora(IslandProfile profile, TerrainData data)
        {
            GameObject[] prefabs = FloraFactory.EnsurePrototypes();

            var prototypes = new TreePrototype[prefabs.Length];
            for (int i = 0; i < prefabs.Length; i++)
                prototypes[i] = new TreePrototype { prefab = prefabs[i], bendFactor = 0f };
            data.treePrototypes = prototypes;

            var stopwatch = Stopwatch.StartNew();
            var shape = new IslandShape(profile);
            var placed = new IslandFlora(shape).Scatter();
            stopwatch.Stop();

            float half = profile.Size * 0.5f;
            float total = profile.TotalHeight;

            var instances = new TreeInstance[placed.Count];
            var counts = new int[prefabs.Length];

            for (int i = 0; i < placed.Count; i++)
            {
                FloraInstance plant = placed[i];
                counts[plant.Prototype]++;

                instances[i] = new TreeInstance
                {
                    prototypeIndex = plant.Prototype,
                    position = new Vector3((plant.Position.x + half) / profile.Size,
                                           (plant.Position.y + profile.SeabedDepth) / total,
                                           (plant.Position.z + half) / profile.Size),
                    rotation = plant.Rotation,
                    widthScale = plant.Width,
                    heightScale = plant.Height,
                    color = Color.white,
                    lightmapColor = Color.white
                };
            }

            // Snapped to the baked heightmap rather than trusted at the sampled height: the shape is
            // continuous but the terrain is bilinear between samples, and a tree standing on the
            // maths instead of on the mesh floats a few centimetres over every hollow.
            data.SetTreeInstances(instances, true);

            var line = new StringBuilder($"[TerrainGenerator] Scattered {placed.Count} plants in "
                                         + $"{stopwatch.ElapsedMilliseconds}ms (placement hash {HashFlora(placed):X8}):");
            for (int species = 0; species < IslandFlora.SpeciesCount; species++)
            {
                FloraModel[] variants = IslandFlora.Variants[species];
                int sum = 0;
                for (int v = 0; v < variants.Length; v++) sum += counts[IslandFlora.VariantBase[species] + v];
                line.Append($" {IslandFlora.SpeciesNames[species]} {sum}");
            }

            Debug.Log(line.ToString());
            ReportTriangles(prefabs, counts, profile);
        }

        /// <summary>
        /// What the detail layer draws (#246), from tools/art/plants.py: the catalogue id, whether it
        /// sways, how far it tilts to the slope (1 lies flat on it), and its size range. Grass first.
        /// </summary>
        static readonly (string Id, bool Wind, float Align, float Min, float Max)[] Details =
        {
            ("DetailGrass", true, 0.3f, 0.9f, 1.5f),
            ("DetailGrassTall", true, 0.2f, 0.8f, 1.3f),
            ("DetailFlowers", true, 0.2f, 0.8f, 1.2f),
            ("DetailShell", false, 1f, 0.8f, 1.3f),
            ("DetailStarfish", false, 1f, 0.8f, 1.4f),
            ("DetailDriftwood", false, 1f, 0.7f, 1.3f),
            ("DetailSeaweed", false, 1f, 0.8f, 1.3f),
            ("DetailPebbles", false, 1f, 0.8f, 1.5f),
        };

        const int DetailGrass = 0, DetailTall = 1, DetailFlowers = 2, DetailShell = 3, DetailStar = 4,
                  DetailDrift = 5, DetailWeed = 6, DetailPebble = 7;

        const string DetailFolder = "Assets/_Project/Prefabs/Details";

        /// <summary>
        /// One detail as a prefab the terrain can instance: the mesh and its material on one object, no
        /// collider, no shadow. Saved over the same path, so the terrain keeps its reference.
        /// </summary>
        static GameObject DetailPrefab(string id, bool wind)
        {
            GameObject source = ArtLibrary.Source(id);
            MeshFilter filter = source != null ? source.GetComponentInChildren<MeshFilter>() : null;
            if (filter == null) return null;

            var go = new GameObject(id);
            go.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = filter.GetComponent<MeshRenderer>().sharedMaterials;
            if (wind) ArtLibrary.Sway(renderer, ArtCategory.Plant);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            Directory.CreateDirectory(DetailFolder);
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(go, $"{DetailFolder}/{id}.prefab");
            UnityEngine.Object.DestroyImmediate(go);
            return saved;
        }

        /// <summary>A stable 0..1 per detail cell and purpose, so a rebake scatters the same clutter.</summary>
        static float CellRandom(int x, int z, int salt)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)(salt * 83492791);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>
        /// The ground's detail layer, drawn instanced (#246). Grass in three clumps where the splatmap is
        /// green, with flowers among the thickest of it; shells and starfish on the sand, driftwood and
        /// kelp along the tide line, pebbles on the rock. The terrain drops whole patches past
        /// <see cref="IslandProfile.DetailDistance"/>, which TerrainQuality scales per tier.
        ///
        /// Grass density follows the grass weight of the splatmap, so the grass grows exactly where the
        /// ground is already painted green rather than in its own unrelated pattern.
        /// </summary>
        static void WriteDetail(IslandProfile profile, TerrainData data)
        {
            var prototypes = new DetailPrototype[Details.Length];
            for (int i = 0; i < Details.Length; i++)
            {
                var d = Details[i];
                GameObject prefab = DetailPrefab(d.Id, d.Wind);
                if (prefab == null)
                {
                    Debug.LogError($"[TerrainGenerator] No {d.Id} model: run tools/art/plants.py. The ground stays bare.");
                    return;
                }

                prototypes[i] = new DetailPrototype
                {
                    prototype = prefab,
                    usePrototypeMesh = true,
                    renderMode = DetailRenderMode.VertexLit,
                    useInstancing = true,
                    healthyColor = Color.white,
                    dryColor = Color.white,
                    minWidth = d.Min,
                    maxWidth = d.Max,
                    minHeight = d.Min,
                    maxHeight = d.Max,
                    noiseSpread = 0.3f,
                    alignToGround = d.Align,
                    positionJitter = 1f,
                    useDensityScaling = true
                };
            }

            data.detailPrototypes = prototypes;
            data.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);

            // Resolution before the layers, for the same reason the alphamap resolution comes before
            // the alphamap: changing it throws the maps away.
            int resolution = Mathf.Max(8, profile.DetailResolution);
            data.SetDetailResolution(resolution, Mathf.Max(8, profile.DetailPerPatch));

            var shape = new IslandShape(profile);
            var splat = new IslandSplat(shape);
            var weights = new float[IslandSplat.LayerCount];

            // Detail maps are indexed [z, x], the same way alphamaps are. Verified against the
            // terrain rather than assumed: a transposed grass map mirrors the island diagonally and
            // looks almost right, which is the worst kind of wrong.
            var layers = new int[Details.Length][,];
            for (int i = 0; i < layers.Length; i++) layers[i] = new int[resolution, resolution];
            var placed = new int[Details.Length];
            float half = profile.Size * 0.5f;
            float step = profile.Size / resolution;

            void Put(int layer, int z, int x, int count)
            {
                layers[layer][z, x] = count;
                placed[layer] += count;
            }

            for (int z = 0; z < resolution; z++)
            {
                float worldZ = -half + (z + 0.5f) * step;
                for (int x = 0; x < resolution; x++)
                {
                    float worldX = -half + (x + 0.5f) * step;

                    float height = shape.HeightAt(worldX, worldZ);
                    if (height <= 0.1f) continue;

                    splat.Weights(height, shape.SlopeAt(worldX, worldZ), worldX, worldZ, weights);

                    // Clutter by what the ground is, one in so many cells.
                    float roll = CellRandom(x, z, 1);
                    if (weights[IslandSplat.Sand] > 0.5f)
                    {
                        bool tide = height < 0.8f;
                        if (tide && roll < 0.03f) Put(DetailDrift, z, x, 1);
                        else if (tide && roll < 0.13f) Put(DetailWeed, z, x, 1);
                        else if (roll < 0.19f) Put(DetailShell, z, x, 1);
                        else if (roll < (tide ? 0.23f : 0.21f)) Put(DetailStar, z, x, 1);
                    }
                    if (weights[IslandSplat.Rock] > 0.5f && roll < 0.2f) Put(DetailPebble, z, x, 1);

                    if (height <= profile.FloraMinHeight) continue;
                    float grass = weights[IslandSplat.Grass];
                    if (grass < profile.GrassThreshold) continue;

                    // Linear from the threshold up, so the edge of a grass patch thins out instead of
                    // ending on a line you can see from across the bay.
                    // Twice the profile's count: a meshed tuft covers less ground than the billboard did.
                    float t = (grass - profile.GrassThreshold) / Mathf.Max(0.01f, 1f - profile.GrassThreshold);
                    int count = Mathf.RoundToInt(Mathf.Lerp(2f, profile.GrassPerCell * 2f, t));
                    if (count <= 0) continue;

                    Put(DetailGrass, z, x, count);
                    if (t > 0.4f) Put(DetailTall, z, x, Mathf.Max(1, count / 3));
                    if (CellRandom(x, z, 2) < 0.18f * t) Put(DetailFlowers, z, x, 1);
                }
            }

            for (int i = 0; i < layers.Length; i++) data.SetDetailLayer(0, 0, i, layers[i]);

            EditorUtility.SetDirty(data);
            var line = new StringBuilder($"[TerrainGenerator] Detail layer, {resolution}^2 cells, drawn to "
                                         + $"{profile.DetailDistance}m at density {profile.DetailDensity}:");
            for (int i = 0; i < Details.Length; i++)
            {
                Mesh mesh = Details[i].Id != null ? prototypes[i].prototype.GetComponent<MeshFilter>().sharedMesh : null;
                line.Append($" {Details[i].Id} {placed[i]} ({(mesh != null ? mesh.triangles.Length / 3 : 0)} tris, "
                            + $"{(mesh != null ? mesh.bounds.size.y : 0f):F2}m tall)");
            }
            Debug.Log(line.ToString());
        }

        /// <summary>
        /// Re-scatters both islands' detail layers from the current models without touching a height, a
        /// splat or a tree: the ground clutter can change without a full bake. One island at a time.
        /// </summary>
        [MenuItem("EWYF/Refresh Terrain Detail")]
        public static void RefreshDetail()
        {
            foreach (string id in new[] { FirstIsland, SecondIsland })
            {
                _id = id;
                var profile = AssetDatabase.LoadAssetAtPath<IslandProfile>(ProfilePath);
                var data = AssetDatabase.LoadAssetAtPath<TerrainData>(TerrainDataPath);
                if (profile == null || data == null) { Debug.LogError($"[TerrainGenerator] No {id} to refresh."); continue; }
                WriteDetail(profile, data);
            }
            _id = FirstIsland;
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// What the vegetation costs, in the only unit that can be measured without a screen. The
        /// frame rate itself needs the target machine; this is the budget the frame rate comes out of.
        /// </summary>
        static void ReportTriangles(GameObject[] prefabs, int[] counts, IslandProfile profile)
        {
            var line = new StringBuilder("[TerrainGenerator] Triangle budget:");
            int worst = 0;

            for (int i = 0; i < prefabs.Length; i++)
            {
                int near = 0;
                int far = 0;

                var group = prefabs[i].GetComponent<LODGroup>();
                LOD[] lods = group != null ? group.GetLODs() : new LOD[0];
                for (int level = 0; level < lods.Length; level++)
                {
                    int triangles = 0;
                    foreach (Renderer renderer in lods[level].renderers)
                    {
                        var filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
                        if (filter != null && filter.sharedMesh != null)
                            triangles += (int)(filter.sharedMesh.triangles.Length / 3);
                    }

                    if (level == 0) near = triangles; else far = triangles;
                }

                worst = Mathf.Max(worst, near);
                line.Append($" {prefabs[i].name} {near}/{far} tris x{counts[i]}");
            }

            Debug.Log(line.ToString());
            Debug.Log($"[TerrainGenerator] Worst case at full LOD: {profile.TreeMaximumFullLOD} trees x {worst} tris "
                      + $"= {profile.TreeMaximumFullLOD * worst} triangles, everything past that is the low LOD.");
        }

        /// <summary>FNV-1a over the placement, so two runs that plant one tree differently disagree here.</summary>
        static uint HashFlora(System.Collections.Generic.List<FloraInstance> placed)
        {
            unchecked
            {
                uint hash = 2166136261u;
                foreach (FloraInstance plant in placed)
                {
                    hash = (hash ^ (uint)plant.Prototype) * 16777619u;
                    hash = Mix(hash, plant.Position.x);
                    hash = Mix(hash, plant.Position.y);
                    hash = Mix(hash, plant.Position.z);
                    hash = Mix(hash, plant.Rotation);
                    hash = Mix(hash, plant.Height);
                    hash = Mix(hash, plant.Width);
                }

                return hash;
            }
        }

        static uint Mix(uint hash, float value)
        {
            unchecked
            {
                uint bits = (uint)BitConverter.SingleToInt32Bits(value);
                hash = (hash ^ (bits & 0xFF)) * 16777619u;
                hash = (hash ^ ((bits >> 8) & 0xFF)) * 16777619u;
                hash = (hash ^ ((bits >> 16) & 0xFF)) * 16777619u;
                hash = (hash ^ (bits >> 24)) * 16777619u;
                return hash;
            }
        }

        /// <summary>FNV-1a over an alphamap, same shape as the heightmap hash.</summary>
        static uint HashSplat(float[,,] splat)
        {
            unchecked
            {
                uint hash = 2166136261u;
                int depth = splat.GetLength(0);
                int width = splat.GetLength(1);
                int layers = splat.GetLength(2);

                for (int z = 0; z < depth; z++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        for (int layer = 0; layer < layers; layer++)
                        {
                            uint bits = (uint)BitConverter.SingleToInt32Bits(splat[z, x, layer]);
                            hash = (hash ^ (bits & 0xFF)) * 16777619u;
                            hash = (hash ^ ((bits >> 8) & 0xFF)) * 16777619u;
                            hash = (hash ^ ((bits >> 16) & 0xFF)) * 16777619u;
                            hash = (hash ^ (bits >> 24)) * 16777619u;
                        }
                    }
                }

                return hash;
            }
        }

        /// <summary>FNV-1a over the raw bits of every sample. Two runs that disagree anywhere disagree here.</summary>
        static uint HashHeights(float[,] heights)
        {
            unchecked
            {
                uint hash = 2166136261u;
                int height = heights.GetLength(0);
                int width = heights.GetLength(1);

                for (int z = 0; z < height; z++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        uint bits = (uint)BitConverter.SingleToInt32Bits(heights[z, x]);
                        hash = (hash ^ (bits & 0xFF)) * 16777619u;
                        hash = (hash ^ ((bits >> 8) & 0xFF)) * 16777619u;
                        hash = (hash ^ ((bits >> 16) & 0xFF)) * 16777619u;
                        hash = (hash ^ (bits >> 24)) * 16777619u;
                    }
                }

                return hash;
            }
        }

        /// <summary>FNV-1a over a file on disk, so the check covers serialisation and not just maths.</summary>
        static uint HashFile(string path)
        {
            if (!File.Exists(path)) return 0u;

            unchecked
            {
                uint hash = 2166136261u;
                foreach (byte value in File.ReadAllBytes(path))
                    hash = (hash ^ value) * 16777619u;

                return hash;
            }
        }
    }
}
