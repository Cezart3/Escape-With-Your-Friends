using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
        const string ProfilePath = "Assets/_Project/Data/Island.asset";
        const string TerrainDataPath = "Assets/_Project/Data/IslandTerrain.asset";
        const string ScenePath = "Assets/_Project/Scenes/Island.unity";
        const string TerrainObjectName = "Island";
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

            Debug.Log($"[TerrainGenerator] Seed {profile.Seed}, {profile.Size}m square, {resolution}^2 "
                      + $"samples, heights {-profile.SeabedDepth}m to {profile.PeakHeight}m.");

            var stopwatch = Stopwatch.StartNew();
            float[,] heights = Sample(profile, resolution);
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
            if (profile != null) return profile;

            profile = ScriptableObject.CreateInstance<IslandProfile>();
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath));
            AssetDatabase.CreateAsset(profile, ProfilePath);
            Debug.Log($"[TerrainGenerator] Created {ProfilePath} with default parameters.");
            return profile;
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

            var island = new GameObject(TerrainObjectName);
            island.transform.position = profile.TerrainOrigin;

            var terrain = island.AddComponent<Terrain>();
            terrain.terrainData = data;

            // No material assigned: URP hands the terrain its own default, and the real splatmap
            // material arrives with #31.
            terrain.heightmapPixelError = 5f;
            terrain.basemapDistance = 400f;

            var collider = island.AddComponent<TerrainCollider>();
            collider.terrainData = data;

            var sun = new GameObject("Sun");
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            Light light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
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

            // One metre either side. Smaller and the gradient picks up noise detail no texture can
            // show; larger and cliff edges smear into the grass above them.
            const float delta = 1f;

            for (int z = 0; z < resolution; z++)
            {
                float worldZ = -half + (z + 0.5f) * step;
                for (int x = 0; x < resolution; x++)
                {
                    float worldX = -half + (x + 0.5f) * step;

                    float height = shape.HeightAt(worldX, worldZ);
                    land[z, x] = height > IslandShape.SeaLevel;
                    float gx = (shape.HeightAt(worldX + delta, worldZ) - shape.HeightAt(worldX - delta, worldZ)) * 0.5f / delta;
                    float gz = (shape.HeightAt(worldX, worldZ + delta) - shape.HeightAt(worldX, worldZ - delta)) * 0.5f / delta;
                    float slope = Mathf.Sqrt(gx * gx + gz * gz);

                    splat.Weights(height, slope, worldX, worldZ, weights);
                    for (int layer = 0; layer < IslandSplat.LayerCount; layer++)
                        map[z, x, layer] = weights[layer];
                }
            }

            return map;
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

        /// <summary>
        /// A tiling grain texture, generated once and then left alone. Two octaves of the same noise
        /// the island is built from, cross-faded against a wrapped copy of themselves so the texture
        /// repeats without a visible seam every few metres.
        /// </summary>
        static Texture2D EnsureTexture(string path, Color baseColour, Color grainColour, int salt)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

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
        static float Tileable(int x, int y, int size, int cells, int seed)
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
