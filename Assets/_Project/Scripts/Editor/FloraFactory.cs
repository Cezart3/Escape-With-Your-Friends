using System.Collections.Generic;
using System.IO;
using EscapeWithYourFriends.World;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Builds the plants. Every mesh here is generated from a handful of numbers, for the same
    /// reason the island is: a tree you can regenerate is a tree you can retune from the terminal,
    /// and nobody has to open Blender to find out whether the jungle wants taller trunks.
    ///
    /// Each species is a prefab with a two-level LODGroup and two materials, bark and foliage. The
    /// low level is the same generator run with fewer sides, so the silhouette matches and the swap
    /// is invisible at the distance it happens. Assets are reused when they already exist, so an art
    /// pass that replaces a mesh or retextures a material survives the next regeneration.
    /// </summary>
    public static class FloraFactory
    {
        public const string FloraFolder = "Assets/_Project/Art/Flora";

        // Fixed salt: the plants must not change shape when the island seed does. A different island
        // gets the same trees in different places, which is the whole point of prototypes.
        const int FloraSalt = 484848;

        /// <summary>
        /// The four prototypes in <see cref="IslandFlora"/> order, created on first run and reused
        /// afterwards. Order matters: the terrain stores a prototype index per tree.
        /// </summary>
        public static GameObject[] EnsurePrototypes()
        {
            Directory.CreateDirectory(FloraFolder);

            Material bark = EnsureMaterial("Bark", new Color(0.32f, 0.24f, 0.16f), 0.05f);
            Material palmBark = EnsureMaterial("PalmBark", new Color(0.46f, 0.39f, 0.28f), 0.05f);
            Material leaf = EnsureMaterial("Leaf", new Color(0.20f, 0.42f, 0.16f), 0.02f);
            Material frond = EnsureMaterial("Frond", new Color(0.26f, 0.48f, 0.20f), 0.02f);
            Material needle = EnsureMaterial("Needle", new Color(0.16f, 0.31f, 0.20f), 0.02f);
            Material shrub = EnsureMaterial("Shrub", new Color(0.24f, 0.40f, 0.18f), 0.02f);

            var prototypes = new GameObject[IslandFlora.SpeciesCount];
            prototypes[IslandFlora.Palm] = EnsurePrefab("Palm", palmBark, frond, 0.55f, 6.5f, BuildPalm);
            prototypes[IslandFlora.JungleTree] = EnsurePrefab("JungleTree", bark, leaf, 0.85f, 9f, BuildJungleTree);
            prototypes[IslandFlora.HighlandTree] = EnsurePrefab("HighlandTree", bark, needle, 0.7f, 11f, BuildHighlandTree);
            prototypes[IslandFlora.Bush] = EnsurePrefab("Bush", shrub, shrub, 0f, 1.1f, BuildBush);
            return prototypes;
        }

        /// <summary>
        /// The grass billboard. A few blades cut out of a 64x64 texture with alpha, which is all a
        /// detail billboard ever is - Unity draws it as a camera-facing quad and no mesh is involved.
        /// </summary>
        public static Texture2D EnsureGrassTexture()
        {
            Directory.CreateDirectory(FloraFolder);
            string path = $"{FloraFolder}/GrassBlades.png";

            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            var clear = new Color32(60, 110, 45, 0);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;

            // Seven blades, each a parabola from the bottom edge, thinning as it rises. Drawn by
            // walking up the blade rather than by testing every pixel: cheaper and gives clean tips.
            for (int blade = 0; blade < 7; blade++)
            {
                uint hash = (uint)IslandShape.Noise(blade * 3.7f, 1.3f, FloraSalt) * 1u;
                float root = 6f + blade * 8f + IslandShape.Noise(blade * 1.1f, 0.5f, FloraSalt) * 5f;
                float lean = (IslandShape.Noise(blade * 2.3f, 7.7f, FloraSalt) - 0.5f) * 26f;
                float top = 34f + IslandShape.Noise(blade * 0.9f, 4.2f, FloraSalt) * 26f;

                for (float t = 0f; t <= 1f; t += 0.01f)
                {
                    float y = t * top;
                    float x = root + lean * t * t;
                    float width = Mathf.Lerp(1.9f, 0.35f, t);

                    // Green darkens towards the root, which is what makes a flat billboard read as
                    // a tuft with depth instead of as a sticker.
                    var colour = new Color32(
                        (byte)Mathf.Lerp(48f, 96f, t),
                        (byte)Mathf.Lerp(92f, 156f, t),
                        (byte)Mathf.Lerp(34f, 62f, t), 255);

                    for (float dx = -width; dx <= width; dx += 0.5f)
                    {
                        int px = Mathf.RoundToInt(x + dx);
                        int py = Mathf.RoundToInt(y);
                        if (px < 0 || px >= size || py < 0 || py >= size) continue;
                        pixels[py * size + px] = colour;
                    }
                }
                _ = hash;
            }

            texture.SetPixels32(pixels);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.maxTextureSize = size;
                importer.SaveAndReimport();
            }

            Debug.Log($"[FloraFactory] Generated {path}.");
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ---------------------------------------------------------------- species

        /// <summary>
        /// A palm: one leaning tapered trunk and a crown of fronds. The lean is what stops a beach
        /// full of palms looking like a fence, so it is baked into the mesh rather than left to the
        /// random rotation, which only ever spins them.
        /// </summary>
        static Mesh BuildPalm(int detail)
        {
            var mesh = new MeshBuilder();
            int sides = detail == 0 ? 7 : 4;

            const float height = 7.5f;
            int segments = detail == 0 ? 5 : 2;
            mesh.Trunk(sides, segments, height, 0.28f, 0.16f, lean: 1.5f, bend: 0.55f);

            Vector3 crown = MeshBuilder.TrunkTop(height, 1.5f, 0.55f);
            int fronds = detail == 0 ? 9 : 5;
            for (int i = 0; i < fronds; i++)
            {
                float angle = i / (float)fronds * Mathf.PI * 2f;
                float droop = -0.35f - IslandShape.Noise(i * 2.1f, 0.4f, FloraSalt) * 0.5f;
                mesh.Frond(crown, angle, 3.4f, 0.75f, droop, detail == 0 ? 3 : 1);
            }

            return mesh.Build($"Palm_LOD{detail}");
        }

        /// <summary>A jungle tree: a straight trunk under three fat, offset canopy layers.</summary>
        static Mesh BuildJungleTree(int detail)
        {
            var mesh = new MeshBuilder();
            int sides = detail == 0 ? 8 : 5;

            const float height = 9.5f;
            mesh.Trunk(sides, detail == 0 ? 4 : 2, height, 0.45f, 0.22f, lean: 0.35f, bend: 0.2f);

            Vector3 top = MeshBuilder.TrunkTop(height, 0.35f, 0.2f);
            int canopySides = detail == 0 ? 9 : 5;
            mesh.Canopy(top + new Vector3(0.1f, -2.3f, -0.15f), 3.0f, 2.6f, canopySides);
            mesh.Canopy(top + new Vector3(-0.5f, -0.9f, 0.4f), 2.5f, 2.4f, canopySides);
            mesh.Canopy(top + new Vector3(0.3f, 0.6f, 0.2f), 1.7f, 2.0f, canopySides);

            return mesh.Build($"JungleTree_LOD{detail}");
        }

        /// <summary>A highland pine: narrow trunk, four stacked cones, deliberately boring.</summary>
        static Mesh BuildHighlandTree(int detail)
        {
            var mesh = new MeshBuilder();
            int sides = detail == 0 ? 7 : 4;

            const float height = 11f;
            mesh.Trunk(sides, detail == 0 ? 3 : 1, height, 0.34f, 0.1f, lean: 0.15f, bend: 0.1f);

            int coneSides = detail == 0 ? 8 : 5;
            for (int i = 0; i < 4; i++)
            {
                float t = i / 3f;
                float y = Mathf.Lerp(2.6f, 8.4f, t);
                float radius = Mathf.Lerp(2.3f, 0.85f, t);
                float length = Mathf.Lerp(3.2f, 2.4f, t);
                mesh.Cone(new Vector3(0f, y, 0f), radius, length, coneSides);
            }

            return mesh.Build($"HighlandTree_LOD{detail}");
        }

        /// <summary>A bush: two overlapping blobs, no trunk, no collider. Cover, not an obstacle.</summary>
        static Mesh BuildBush(int detail)
        {
            var mesh = new MeshBuilder();
            int sides = detail == 0 ? 8 : 5;
            int rings = detail == 0 ? 3 : 2;

            mesh.Blob(new Vector3(0f, 0.55f, 0f), 0.8f, 0.62f, sides, rings, MeshBuilder.Foliage);
            mesh.Blob(new Vector3(0.45f, 0.4f, 0.3f), 0.55f, 0.45f, sides, rings, MeshBuilder.Foliage);

            return mesh.Build($"Bush_LOD{detail}");
        }

        // ---------------------------------------------------------------- assets

        delegate Mesh MeshFor(int detail);

        /// <summary>
        /// Wraps two generated meshes in a prefab with an LODGroup. Trees get a capsule collider so
        /// the terrain builds tree colliders from them and you cannot walk through a trunk; bushes
        /// get none, because a bush that blocks you is infuriating and a bush you push through is
        /// free cover.
        /// </summary>
        static GameObject EnsurePrefab(string name, Material bark, Material foliage,
                                       float colliderRadius, float colliderHeight, MeshFor build)
        {
            string path = $"{FloraFolder}/{name}.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            var root = new GameObject(name);
            var lods = new LOD[2];

            for (int detail = 0; detail < 2; detail++)
            {
                Mesh mesh = EnsureMesh($"{FloraFolder}/{name}_LOD{detail}.asset", build, detail);

                var level = new GameObject($"{name}_LOD{detail}");
                level.transform.SetParent(root.transform, false);
                level.AddComponent<MeshFilter>().sharedMesh = mesh;

                var renderer = level.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = new[] { bark, foliage };
                // Every plant casting a shadow is the first thing to kill a weak GPU. The canopy
                // shadows are worth it; two-pixel bushes are not, so the far LOD drops them.
                renderer.shadowCastingMode = detail == 0
                    ? UnityEngine.Rendering.ShadowCastingMode.On
                    : UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = detail == 0;

                lods[detail] = new LOD(detail == 0 ? 0.22f : 0.035f, new Renderer[] { renderer });
            }

            var group = root.AddComponent<LODGroup>();
            group.SetLODs(lods);
            group.RecalculateBounds();

            if (colliderRadius > 0f)
            {
                var capsule = root.AddComponent<CapsuleCollider>();
                capsule.radius = colliderRadius;
                capsule.height = colliderHeight;
                capsule.center = new Vector3(0f, colliderHeight * 0.5f, 0f);
            }

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            Debug.Log($"[FloraFactory] Generated {path}.");
            return prefab;
        }

        static Mesh EnsureMesh(string path, MeshFor build, int detail)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null) return existing;

            Mesh mesh = build(detail);
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        static Material EnsureMaterial(string name, Color colour, float smoothness)
        {
            string path = $"{FloraFolder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var material = new Material(shader) { name = name };
            material.color = colour;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);

            // Instancing is the difference between four thousand draw calls and forty.
            material.enableInstancing = true;

            AssetDatabase.CreateAsset(material, path);
            Debug.Log($"[FloraFactory] Generated {path}.");
            return material;
        }

        // ---------------------------------------------------------------- geometry

        /// <summary>
        /// Small mesh accumulator with exactly two submeshes: bark and foliage. Two submeshes rather
        /// than two objects because the terrain renders a tree prototype as one instance, and the
        /// vertex count here is small enough that splitting it would only cost draw calls.
        /// </summary>
        class MeshBuilder
        {
            public const int Bark = 0;
            public const int Foliage = 1;

            readonly List<Vector3> _vertices = new List<Vector3>();
            readonly List<Vector3> _normals = new List<Vector3>();
            readonly List<Vector2> _uvs = new List<Vector2>();
            readonly List<int>[] _triangles = { new List<int>(), new List<int>() };

            /// <summary>Where a trunk of this height and lean ends up, so the crown can sit on it.</summary>
            public static Vector3 TrunkTop(float height, float lean, float bend)
            {
                return new Vector3(lean + bend, height, 0f);
            }

            /// <summary>
            /// A tapered trunk built as a stack of rings. Lean displaces the top sideways and bend
            /// curves the path to it, so the trunk arcs instead of tilting like a felled pole.
            /// </summary>
            public void Trunk(int sides, int segments, float height, float bottomRadius, float topRadius,
                              float lean, float bend)
            {
                int rings = Mathf.Max(1, segments) + 1;
                int first = _vertices.Count;

                for (int ring = 0; ring < rings; ring++)
                {
                    float t = ring / (float)(rings - 1);
                    float y = t * height;
                    float offset = lean * t + bend * t * t;
                    float radius = Mathf.Lerp(bottomRadius, topRadius, t);

                    for (int side = 0; side < sides; side++)
                    {
                        float angle = side / (float)sides * Mathf.PI * 2f;
                        var normal = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                        _vertices.Add(new Vector3(offset + normal.x * radius, y, normal.z * radius));
                        _normals.Add(normal);
                        _uvs.Add(new Vector2(side / (float)sides, t * height * 0.35f));
                    }
                }

                for (int ring = 0; ring < rings - 1; ring++)
                {
                    for (int side = 0; side < sides; side++)
                    {
                        int a = first + ring * sides + side;
                        int b = first + ring * sides + (side + 1) % sides;
                        int c = a + sides;
                        int d = b + sides;
                        Quad(Bark, a, c, d, b);
                    }
                }
            }

            /// <summary>A cone of foliage, apex up. The workhorse: pines are four, jungle canopy is three squashed.</summary>
            public void Cone(Vector3 origin, float radius, float height, int sides)
            {
                int first = _vertices.Count;
                _vertices.Add(origin + new Vector3(0f, height, 0f));
                _normals.Add(Vector3.up);
                _uvs.Add(new Vector2(0.5f, 1f));

                for (int side = 0; side < sides; side++)
                {
                    float angle = side / (float)sides * Mathf.PI * 2f;
                    var normal = new Vector3(Mathf.Cos(angle), 0.35f, Mathf.Sin(angle)).normalized;
                    _vertices.Add(origin + new Vector3(normal.x * radius, 0f, normal.z * radius));
                    _normals.Add(normal);
                    _uvs.Add(new Vector2(side / (float)sides, 0f));
                }

                for (int side = 0; side < sides; side++)
                {
                    _triangles[Foliage].Add(first);
                    _triangles[Foliage].Add(first + 1 + (side + 1) % sides);
                    _triangles[Foliage].Add(first + 1 + side);
                }
            }

            /// <summary>A squashed blob of foliage. Cheaper than a sphere and reads the same at ten metres.</summary>
            public void Canopy(Vector3 origin, float radius, float height, int sides)
            {
                Blob(origin, radius, height * 0.5f, sides, 2, Foliage);
            }

            /// <summary>An ellipsoid on a few rings. Poles are single vertices, so no wasted triangles at the top.</summary>
            public void Blob(Vector3 origin, float radius, float halfHeight, int sides, int rings, int submesh)
            {
                int first = _vertices.Count;

                _vertices.Add(origin + new Vector3(0f, halfHeight, 0f));
                _normals.Add(Vector3.up);
                _uvs.Add(new Vector2(0.5f, 1f));

                for (int ring = 0; ring < rings; ring++)
                {
                    float v = (ring + 1) / (float)(rings + 1);
                    float phi = v * Mathf.PI;
                    float y = Mathf.Cos(phi);
                    float r = Mathf.Sin(phi);

                    for (int side = 0; side < sides; side++)
                    {
                        float angle = side / (float)sides * Mathf.PI * 2f;
                        var normal = new Vector3(Mathf.Cos(angle) * r, y, Mathf.Sin(angle) * r);
                        _vertices.Add(origin + new Vector3(normal.x * radius, normal.y * halfHeight, normal.z * radius));
                        _normals.Add(normal.normalized);
                        _uvs.Add(new Vector2(side / (float)sides, 1f - v));
                    }
                }

                int bottom = _vertices.Count;
                _vertices.Add(origin + new Vector3(0f, -halfHeight, 0f));
                _normals.Add(Vector3.down);
                _uvs.Add(new Vector2(0.5f, 0f));

                for (int side = 0; side < sides; side++)
                {
                    _triangles[submesh].Add(first);
                    _triangles[submesh].Add(first + 1 + (side + 1) % sides);
                    _triangles[submesh].Add(first + 1 + side);
                }

                for (int ring = 0; ring < rings - 1; ring++)
                {
                    for (int side = 0; side < sides; side++)
                    {
                        int a = first + 1 + ring * sides + side;
                        int b = first + 1 + ring * sides + (side + 1) % sides;
                        Quad(submesh, a, a + sides, b + sides, b);
                    }
                }

                int last = first + 1 + (rings - 1) * sides;
                for (int side = 0; side < sides; side++)
                {
                    _triangles[submesh].Add(bottom);
                    _triangles[submesh].Add(last + side);
                    _triangles[submesh].Add(last + (side + 1) % sides);
                }
            }

            /// <summary>
            /// One palm frond: a flat strip that narrows and droops. Built double-sided, because a
            /// single-sided frond disappears from underneath and you spend a lot of this game on
            /// your back.
            /// </summary>
            public void Frond(Vector3 origin, float angle, float length, float width, float droop, int segments)
            {
                var forward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                var side = new Vector3(-forward.z, 0f, forward.x);
                int count = Mathf.Max(1, segments);

                for (int i = 0; i < count; i++)
                {
                    float t0 = i / (float)count;
                    float t1 = (i + 1) / (float)count;
                    Vector3 p0 = origin + forward * (length * t0) + Vector3.up * (droop * t0 * t0 * length);
                    Vector3 p1 = origin + forward * (length * t1) + Vector3.up * (droop * t1 * t1 * length);
                    float w0 = width * (1f - t0 * 0.85f);
                    float w1 = width * (1f - t1 * 0.85f);

                    int first = _vertices.Count;
                    Add(p0 - side * w0, Vector3.up, new Vector2(0f, t0));
                    Add(p0 + side * w0, Vector3.up, new Vector2(1f, t0));
                    Add(p1 + side * w1, Vector3.up, new Vector2(1f, t1));
                    Add(p1 - side * w1, Vector3.up, new Vector2(0f, t1));

                    Quad(Foliage, first, first + 1, first + 2, first + 3);
                    Quad(Foliage, first + 3, first + 2, first + 1, first);
                }
            }

            void Add(Vector3 position, Vector3 normal, Vector2 uv)
            {
                _vertices.Add(position);
                _normals.Add(normal);
                _uvs.Add(uv);
            }

            void Quad(int submesh, int a, int b, int c, int d)
            {
                _triangles[submesh].Add(a);
                _triangles[submesh].Add(b);
                _triangles[submesh].Add(c);
                _triangles[submesh].Add(a);
                _triangles[submesh].Add(c);
                _triangles[submesh].Add(d);
            }

            public Mesh Build(string name)
            {
                var mesh = new Mesh { name = name };
                mesh.SetVertices(_vertices);
                mesh.SetNormals(_normals);
                mesh.SetUVs(0, _uvs);
                mesh.subMeshCount = 2;
                mesh.SetTriangles(_triangles[Bark], Bark);
                mesh.SetTriangles(_triangles[Foliage], Foliage);
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();
                return mesh;
            }
        }
    }
}
