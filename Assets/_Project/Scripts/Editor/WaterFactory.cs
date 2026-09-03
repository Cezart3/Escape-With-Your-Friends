using System.IO;
using EscapeWithYourFriends.World;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Builds the sea: a depth mask baked from the island itself, a tiling ripple normal map, the
    /// material, two meshes and the prefab that carries them. Same rule as everything else in this
    /// folder - nothing here is sculpted, all of it comes back identical from the seed.
    ///
    /// The geometry is deliberately split in two. A near patch of four-metre quads follows the
    /// camera and carries the vertex waves; a flat ring of eight vertices fills everything out to
    /// the horizon. Waving the whole ocean would mean either enormous quads that alias or a mesh
    /// nobody's GPU wants, and past a few hundred metres a wave is smaller than a pixel anyway.
    /// </summary>
    public static class WaterFactory
    {
        public const string WaterFolder = "Assets/_Project/Art/Water";
        public const string ShaderPath = WaterFolder + "/Water.shader";
        public const string MaterialPath = WaterFolder + "/Water.mat";
        public const string NormalPath = WaterFolder + "/WaterRipples.png";
        public const string DepthMaskPath = WaterFolder + "/WaterDepth.png";
        public const string PrefabPath = WaterFolder + "/Water.prefab";
        public const string PatchMeshPath = WaterFolder + "/WaterPatch.asset";
        public const string RingMeshPath = WaterFolder + "/WaterHorizon.asset";

        const int NormalSize = 256;
        const int RippleSalt = 5150;

        static readonly int DepthMaskId = Shader.PropertyToID("_DepthMask");
        static readonly int NormalMapId = Shader.PropertyToID("_NormalMap");

        /// <summary>
        /// The water prefab, rebuilt against the profile it is given. Returns null and logs rather
        /// than throwing if the shader is missing, because a scene with no water is a better failure
        /// than a terrain generation that dies half way through.
        /// </summary>
        public static GameObject EnsureWater(IslandProfile profile)
        {
            Directory.CreateDirectory(WaterFolder);

            Texture2D depth = BakeDepthMask(profile);
            Texture2D ripples = EnsureRipples();

            Material material = EnsureMaterial(profile, depth, ripples);
            if (material == null) return null;

            Mesh patch = EnsurePatchMesh(profile);
            Mesh ring = EnsureRingMesh(profile);

            Verify(profile, material, patch, ring);
            return EnsurePrefab(profile, material, patch, ring);
        }

        /// <summary>
        /// The three ways this can be quietly wrong, checked on every run. None of them throw an
        /// error anywhere - they show up as a crease in the ocean, a shimmering horizon, or waves
        /// that strobe - and all three are invisible in a batchmode build, which is where this
        /// project does all of its building.
        /// </summary>
        static void Verify(IslandProfile profile, Material material, Mesh patch, Mesh ring)
        {
            // 1. The seam. The shader has to have finished fading the waves out by the time the
            // geometry runs out, or the patch edge stands proud of the flat ring.
            float meshExtent = patch.bounds.extents.x;
            float fadeEnd = material.GetVector("_PatchFade").y;
            if (fadeEnd > meshExtent + 0.01f)
            {
                Debug.LogError($"[WaterFactory] The waves fade out at {fadeEnd}m but the patch only "
                               + $"reaches {meshExtent}m: the seam with the horizon ring will crack.");
            }

            // 2. Aliasing. A wave needs a handful of vertices to be a wave; below about four cells
            // per wavelength it turns into a strobing zigzag that gets worse the faster it moves.
            float cell = Mathf.Max(0.5f, profile.WaterCellSize);
            float shortest = float.MaxValue;
            for (int i = 0; i < WaterWaves.Count; i++)
                shortest = Mathf.Min(shortest, WaterWaves.Wavelengths[i]);

            float cellsPerWave = shortest / cell;
            if (cellsPerWave < 4f)
            {
                Debug.LogWarning($"[WaterFactory] Shortest wave is {shortest}m over {cell}m cells "
                                 + $"({cellsPerWave:F1} cells per wave). Under four it aliases.");
            }

            // 3. The horizon has to start exactly where the patch ends, or there is a hole in the sea.
            float ringInner = Mathf.Abs(ring.vertices[0].x);
            float ringOuter = ring.bounds.extents.x;
            if (Mathf.Abs(ringInner - meshExtent) > 0.01f)
            {
                Debug.LogError($"[WaterFactory] The patch ends at {meshExtent}m but the horizon ring "
                               + $"starts at {ringInner}m: there is a strip of missing sea between them.");
            }

            float swell = 0f;
            for (int i = 0; i < WaterWaves.Count; i++) swell += WaterWaves.Amplitudes[i];

            float fadeStart = material.GetVector("_PatchFade").x;
            Debug.Log($"[WaterFactory] Patch {meshExtent}m half-extent, {patch.vertexCount} verts, "
                      + $"{patch.triangles.Length / 3} tris; waves fade from {fadeStart}m to {fadeEnd}m; "
                      + $"ring {ringInner}m to {ringOuter}m. Shortest wave {shortest}m = "
                      + $"{cellsPerWave:F1} cells; peak-to-trough swell {swell * 2f:F2}m.");
        }

        /// <summary>
        /// The half-width the patch actually comes out as. The requested extent is snapped up to a
        /// whole number of cells, because a patch that ends three quarters of the way through a cell
        /// leaves a strip of sea between itself and the horizon ring. Everything that has to agree
        /// on where the patch ends - the mesh, the shader fade, the ring - asks this.
        /// </summary>
        static float PatchExtent(IslandProfile profile)
        {
            float cell = Mathf.Max(0.5f, profile.WaterCellSize);
            return PatchCells(profile) * cell * 0.5f;
        }

        static int PatchCells(IslandProfile profile)
        {
            float cell = Mathf.Max(0.5f, profile.WaterCellSize);
            float requested = Mathf.Max(8f, profile.WaterPatchExtent);
            // Even, so the patch is symmetric about its own origin and the camera snap stays centred.
            return Mathf.Max(2, Mathf.CeilToInt(requested * 2f / cell / 2f) * 2);
        }

        // ---------------------------------------------------------------- textures

        /// <summary>
        /// How deep the sea is, baked from <see cref="IslandShape"/> into a texture the water shader
        /// reads in world space. This is the whole reason the water costs nothing: the alternative,
        /// sampling the camera depth texture, turns the depth prepass on for every frame of the
        /// game, and this island has to run on an integrated GPU.
        ///
        /// Linear, uncompressed and clamped. Compressed, the block artefacts show up as blotches
        /// crawling along the surf line; sRGB, the shallows read far too dark; wrapped, the foam
        /// from the west beach appears in the ocean off the east one.
        /// </summary>
        static Texture2D BakeDepthMask(IslandProfile profile)
        {
            int size = Mathf.Max(32, profile.WaterDepthResolution);
            var shape = new IslandShape(profile);

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            var pixels = new Color32[size * size];

            float half = profile.Size * 0.5f;
            float step = profile.Size / size;
            float scale = Mathf.Max(0.01f, profile.ShoreFadeDepth);
            int wet = 0;

            for (int y = 0; y < size; y++)
            {
                float worldZ = -half + (y + 0.5f) * step;
                for (int x = 0; x < size; x++)
                {
                    float worldX = -half + (x + 0.5f) * step;
                    float height = shape.HeightAt(worldX, worldZ);

                    // Depth below sea level, normalised. Land is zero, which is also what the shader
                    // wants: no water is drawn there, and if any is, it is at its shallowest.
                    float depth = Mathf.Clamp01(-height / scale);
                    if (height < 0f) wet++;

                    var value = (byte)Mathf.RoundToInt(depth * 255f);
                    pixels[y * size + x] = new Color32(value, value, value, 255);
                }
            }

            texture.SetPixels32(pixels);
            File.WriteAllBytes(DepthMaskPath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(DepthMaskPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(DepthMaskPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.SingleChannel;
                importer.sRGBTexture = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.maxTextureSize = Mathf.Max(32, size);
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            Debug.Log($"[WaterFactory] Baked {DepthMaskPath} at {size}x{size}: "
                      + $"{wet * 100f / (size * size):F1}% of the square is under water, "
                      + $"full depth at {profile.ShoreFadeDepth}m.");

            return AssetDatabase.LoadAssetAtPath<Texture2D>(DepthMaskPath);
        }

        /// <summary>
        /// The ripple normals: two octaves of the same tiling noise the ground textures use, turned
        /// into a normal map by central difference. Generated once and then left alone, because it
        /// is a look, not a function of the seed - rerolling the island should not reroll what water
        /// looks like.
        /// </summary>
        static Texture2D EnsureRipples()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(NormalPath);
            if (existing != null) return existing;

            var heights = new float[NormalSize * NormalSize];
            for (int y = 0; y < NormalSize; y++)
            {
                for (int x = 0; x < NormalSize; x++)
                {
                    float coarse = TerrainGenerator.Tileable(x, y, NormalSize, 4, RippleSalt);
                    float fine = TerrainGenerator.Tileable(x, y, NormalSize, 11, RippleSalt + 3);
                    heights[y * NormalSize + x] = coarse * 0.65f + fine * 0.35f;
                }
            }

            var texture = new Texture2D(NormalSize, NormalSize, TextureFormat.RGBA32, true);
            var pixels = new Color32[NormalSize * NormalSize];
            const float strength = 6f;

            for (int y = 0; y < NormalSize; y++)
            {
                int up = ((y + 1) % NormalSize) * NormalSize;
                int down = ((y - 1 + NormalSize) % NormalSize) * NormalSize;
                int row = y * NormalSize;

                for (int x = 0; x < NormalSize; x++)
                {
                    int right = (x + 1) % NormalSize;
                    int left = (x - 1 + NormalSize) % NormalSize;

                    // Wrapped differences, so the tile's normals meet at the seam as well as its
                    // heights do. A normal map that tiles in value but not in slope shows a hard
                    // line every few metres in exactly the lighting you built it for.
                    float dx = (heights[row + right] - heights[row + left]) * strength;
                    float dy = (heights[up + x] - heights[down + x]) * strength;

                    Vector3 normal = new Vector3(-dx, -dy, 1f).normalized;
                    pixels[row + x] = new Color32(
                        (byte)Mathf.RoundToInt((normal.x * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((normal.y * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((normal.z * 0.5f + 0.5f) * 255f),
                        255);
                }
            }

            texture.SetPixels32(pixels);
            File.WriteAllBytes(NormalPath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(NormalPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(NormalPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.maxTextureSize = NormalSize;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.SaveAndReimport();
            }

            Debug.Log($"[WaterFactory] Generated {NormalPath}.");
            return AssetDatabase.LoadAssetAtPath<Texture2D>(NormalPath);
        }

        // ---------------------------------------------------------------- material

        /// <summary>
        /// The material. Created if missing, but its numbers are pushed every run: they are derived
        /// from the profile and from <see cref="WaterWaves"/>, and a material carrying last week's
        /// wavelengths while the physics uses this week's is exactly the bug this whole arrangement
        /// exists to prevent.
        /// </summary>
        static Material EnsureMaterial(IslandProfile profile, Texture2D depth, Texture2D ripples)
        {
            Shader shader = Shader.Find("EWYF/Water");
            if (shader == null)
            {
                Debug.LogError($"[WaterFactory] Shader 'EWYF/Water' not found. Is {ShaderPath} present "
                               + "and compiling? Skipping the water.");
                return null;
            }

            // Headless is the only place this project ever builds, so the compile has to be
            // checked here: a shader with an error still loads, still assigns, and only turns
            // magenta on a screen nobody is looking at during a batchmode run.
            if (ShaderUtil.ShaderHasError(shader))
            {
                var messages = ShaderUtil.GetShaderMessages(shader);
                foreach (ShaderMessage message in messages)
                    Debug.LogError($"[WaterFactory] Shader error: {message.message} ({message.file}:{message.line})");

                Debug.LogError($"[WaterFactory] Shader 'EWYF/Water' failed to compile, {messages.Length} messages.");
                return null;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                material = new Material(shader) { name = "Water" };
                AssetDatabase.CreateAsset(material, MaterialPath);
                Debug.Log($"[WaterFactory] Generated {MaterialPath}.");
            }
            else
            {
                material.shader = shader;
            }

            material.SetTexture(DepthMaskId, depth);
            material.SetTexture(NormalMapId, ripples);

            material.SetVector("_WaveA", WaterWaves.Packed(0));
            material.SetVector("_WaveB", WaterWaves.Packed(1));
            material.SetVector("_WaveC", WaterWaves.Packed(2));
            material.SetVector("_WaveSpeed", new Vector4(WaterWaves.AngularSpeed(0),
                                                         WaterWaves.AngularSpeed(1),
                                                         WaterWaves.AngularSpeed(2), 0f));

            float extent = PatchExtent(profile);
            float band = Mathf.Clamp(profile.WaterFadeBand, 1f, extent - 1f);
            material.SetVector("_PatchFade", new Vector4(extent - band, extent, 0f, 0f));

            material.SetFloat("_IslandSize", profile.Size);
            material.SetFloat("_ShoreDepth", profile.ShoreFadeDepth);
            material.SetFloat("_FoamWidth", profile.FoamWidth);

            // Two big meshes, no instancing to be had, and the SRP batcher handles them anyway.
            material.enableInstancing = false;
            EditorUtility.SetDirty(material);
            return material;
        }

        // ---------------------------------------------------------------- geometry

        /// <summary>
        /// The wavy part: a flat grid centred on the origin, in object space, because the shader
        /// fades the waves out by object-space distance from the middle and the root moves.
        /// Vertices are the only thing that varies here, so the mesh is rebuilt when the profile
        /// asks for a different size and reused otherwise.
        /// </summary>
        static Mesh EnsurePatchMesh(IslandProfile profile)
        {
            float cell = Mathf.Max(0.5f, profile.WaterCellSize);
            int cells = PatchCells(profile);
            float extent = PatchExtent(profile);

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(PatchMeshPath);
            if (existing != null && existing.vertexCount == (cells + 1) * (cells + 1)) return existing;

            var mesh = new Mesh { name = "WaterPatch" };
            if ((cells + 1) * (cells + 1) > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            var vertices = new Vector3[(cells + 1) * (cells + 1)];
            var uvs = new Vector2[vertices.Length];
            var normals = new Vector3[vertices.Length];
            var triangles = new int[cells * cells * 6];

            for (int z = 0; z <= cells; z++)
            {
                for (int x = 0; x <= cells; x++)
                {
                    int index = z * (cells + 1) + x;
                    vertices[index] = new Vector3(-extent + x * cell, 0f, -extent + z * cell);
                    uvs[index] = new Vector2(x / (float)cells, z / (float)cells);
                    normals[index] = Vector3.up;
                }
            }

            int t = 0;
            for (int z = 0; z < cells; z++)
            {
                for (int x = 0; x < cells; x++)
                {
                    int a = z * (cells + 1) + x;
                    int b = a + 1;
                    int c = a + cells + 1;
                    int d = c + 1;

                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                    triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
                }
            }

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = triangles;

            // The bounds have to allow for the waves lifting vertices, or the patch gets culled by
            // its own flat bounding box as soon as the camera looks along it.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(extent * 2f, 8f, extent * 2f));

            Write(mesh, PatchMeshPath);
            Debug.Log($"[WaterFactory] Generated {PatchMeshPath}: {cells}x{cells} cells of {cell}m, "
                      + $"{vertices.Length} verts, {triangles.Length / 3} tris.");
            return AssetDatabase.LoadAssetAtPath<Mesh>(PatchMeshPath);
        }

        /// <summary>
        /// Everything else out to the horizon: a square annulus, eight vertices, eight triangles,
        /// flat. It carries the same material, so it takes the same deep colour and the same fog,
        /// and the shader's fade has already brought the waves to zero by the time they reach it -
        /// which is why the seam neither cracks nor z-fights.
        /// </summary>
        static Mesh EnsureRingMesh(IslandProfile profile)
        {
            float inner = PatchExtent(profile);
            float outer = Mathf.Max(inner * 2f, profile.WaterHorizon);

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(RingMeshPath);
            if (existing != null
                && existing.vertexCount == 8
                && Mathf.Abs(existing.bounds.extents.x - outer) < 0.01f
                && Mathf.Abs(Mathf.Abs(existing.vertices[0].x) - inner) < 0.01f)
            {
                return existing;
            }

            var mesh = new Mesh { name = "WaterHorizon" };

            var vertices = new Vector3[8];
            var uvs = new Vector2[8];
            var normals = new Vector3[8];

            // Corners in the same winding order for both squares, so quad i joins corner i of the
            // inner square to corner i of the outer one and the four quads cover the ring exactly.
            Vector2[] corners =
            {
                new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(1f, 1f), new Vector2(-1f, 1f)
            };

            for (int i = 0; i < 4; i++)
            {
                vertices[i] = new Vector3(corners[i].x * inner, 0f, corners[i].y * inner);
                vertices[i + 4] = new Vector3(corners[i].x * outer, 0f, corners[i].y * outer);
                uvs[i] = corners[i] * 0.5f + Vector2.one * 0.5f;
                uvs[i + 4] = uvs[i];
                normals[i] = Vector3.up;
                normals[i + 4] = Vector3.up;
            }

            var triangles = new int[24];
            int t = 0;
            for (int i = 0; i < 4; i++)
            {
                int next = (i + 1) % 4;
                triangles[t++] = i; triangles[t++] = i + 4; triangles[t++] = next;
                triangles[t++] = next; triangles[t++] = i + 4; triangles[t++] = next + 4;
            }

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = triangles;
            // Bounds take a size, not an extent. Half-sized bounds on a mesh this large mean the
            // ring culls itself out of the frame the moment the camera looks at the horizon.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(outer * 2f, 1f, outer * 2f));

            Write(mesh, RingMeshPath);
            Debug.Log($"[WaterFactory] Generated {RingMeshPath}: ring from {inner}m to {outer}m, 8 tris.");
            return AssetDatabase.LoadAssetAtPath<Mesh>(RingMeshPath);
        }

        static void Write(Mesh mesh, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return;
            }

            // Overwrite in place rather than deleting: the prefab and the material reference this
            // asset by GUID, and replacing the file would leave both pointing at nothing.
            existing.Clear();
            existing.indexFormat = mesh.indexFormat;
            existing.vertices = mesh.vertices;
            existing.uv = mesh.uv;
            existing.normals = mesh.normals;
            existing.triangles = mesh.triangles;
            existing.bounds = mesh.bounds;
            EditorUtility.SetDirty(existing);
            Object.DestroyImmediate(mesh);
        }

        // ---------------------------------------------------------------- prefab

        static GameObject EnsurePrefab(IslandProfile profile, Material material, Mesh patch, Mesh ring)
        {
            var root = new GameObject("Water");
            root.transform.position = new Vector3(0f, IslandShape.SeaLevel, 0f);

            var surface = root.AddComponent<WaterSurface>();
            surface.SnapStep = Mathf.Max(0.5f, profile.WaterCellSize);
            surface.FollowCamera = true;

            AddPiece(root, "Surface", patch, material);
            AddPiece(root, "Horizon", ring, material);

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            Debug.Log($"[WaterFactory] Generated {PrefabPath}.");
            return prefab;
        }

        static void AddPiece(GameObject root, string name, Mesh mesh, Material material)
        {
            var piece = new GameObject(name);
            piece.transform.SetParent(root.transform, false);
            piece.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = piece.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;

            // Water casting a shadow on the seabed is both wrong and expensive, and it receives no
            // shadows because the shader never samples the cascade - one less keyword to compile.
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.allowOcclusionWhenDynamic = false;
        }
    }
}
