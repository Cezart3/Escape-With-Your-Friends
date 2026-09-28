using System.Collections.Generic;
using System.IO;
using EscapeWithYourFriends.Casino;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The three slot cabinets along the casino's back wall.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.SlotFactory.Build
    ///
    /// **Every symbol is a mesh on one atlas.** Thirty-odd symbols in thirty colours would be thirty
    /// materials, and <c>LookTest</c> holds the scene to a budget of 48. So the factory paints one
    /// small texture with every colour on it, and bakes each symbol as a copy of a primitive (or a
    /// few, combined) whose UVs all point at its own texel. One material for the whole casino
    /// floor; a symbol changing is a mesh swap.
    ///
    /// Unlike the other casino factories this one always rebuilds: nothing here is dressed by hand
    /// or by <c>ArtDress</c>, and a saved-over prefab keeps its GUID and its spawnable entry.
    /// </summary>
    public static class SlotFactory
    {
        const string PrefabDir = "Assets/_Project/Prefabs/Stations";
        const string ArtDir = "Assets/_Project/Art/Casino";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";
        const string AtlasPath = ArtDir + "/SlotAtlas.png";
        const string MaterialPath = ArtDir + "/SlotAtlas.mat";
        const string MeshesPath = ArtDir + "/SlotMeshes.asset";

        internal const string SevensPath = PrefabDir + "/SlotSevens.prefab";
        internal const string VolcanoPath = PrefabDir + "/SlotVolcano.prefab";
        internal const string ReefPath = PrefabDir + "/SlotReef.prefab";

        const int AtlasSize = 8;

        enum Shape { Slab, Sphere, Cube, Drum, Pod, Diamond, Disc, Seven, Crown, Peak, Starfish, Fish, Card }

        /// <summary>A symbol: what it is called, how it is shaped, its colour, its size in cells.</summary>
        readonly struct Look
        {
            public readonly string Name;
            public readonly Shape Shape;
            public readonly Color Colour;
            public readonly float Scale;

            public Look(string name, Shape shape, Color colour, float scale)
            {
                Name = name;
                Shape = shape;
                Colour = colour;
                Scale = scale;
            }
        }

        static readonly Look[] SevensLooks =
        {
            new("Lime", Shape.Sphere, new Color(0.60f, 0.86f, 0.25f), 0.62f),
            new("Coconut", Shape.Sphere, new Color(0.42f, 0.27f, 0.15f), 0.8f),
            new("Mango", Shape.Pod, new Color(1.00f, 0.62f, 0.12f), 0.85f),
            new("Papaya", Shape.Pod, new Color(0.95f, 0.42f, 0.36f), 0.85f),
            new("Pineapple", Shape.Drum, new Color(0.96f, 0.80f, 0.20f), 0.75f),
            new("Melon", Shape.Sphere, new Color(0.13f, 0.52f, 0.20f), 0.92f),
            new("Seven", Shape.Seven, new Color(0.86f, 0.08f, 0.10f), 0.9f),
            new("Star", Shape.Diamond, new Color(1.00f, 0.86f, 0.18f), 0.9f),
        };

        static readonly Look[] VolcanoLooks =
        {
            new("Obsidian", Shape.Diamond, new Color(0.16f, 0.15f, 0.22f), 0.62f),
            new("Jade", Shape.Diamond, new Color(0.28f, 0.76f, 0.45f), 0.62f),
            new("Amber", Shape.Diamond, new Color(0.96f, 0.58f, 0.12f), 0.62f),
            new("Ruby", Shape.Diamond, new Color(0.86f, 0.10f, 0.22f), 0.66f),
            new("Pearl", Shape.Sphere, new Color(0.96f, 0.94f, 0.88f), 0.66f),
            new("Drum", Shape.Drum, new Color(0.56f, 0.34f, 0.18f), 0.78f),
            new("Mask", Shape.Card, new Color(0.25f, 0.62f, 0.70f), 0.85f),
            new("Idol", Shape.Pod, new Color(0.78f, 0.62f, 0.30f), 0.9f),
            new("Crown", Shape.Crown, new Color(1.00f, 0.80f, 0.16f), 0.9f),
            new("Volcano", Shape.Peak, new Color(0.62f, 0.22f, 0.12f), 0.95f),
            new("LavaOrb", Shape.Sphere, new Color(1.00f, 0.40f, 0.05f), 0.85f),
        };

        static readonly Look[] ReefLooks =
        {
            new("Kelp", Shape.Pod, new Color(0.24f, 0.58f, 0.30f), 0.8f),
            new("Shell", Shape.Disc, new Color(0.96f, 0.70f, 0.76f), 0.75f),
            new("Starfish", Shape.Starfish, new Color(1.00f, 0.52f, 0.22f), 0.85f),
            new("Urchin", Shape.Sphere, new Color(0.46f, 0.20f, 0.56f), 0.72f),
            new("Puffer", Shape.Sphere, new Color(0.96f, 0.86f, 0.32f), 0.9f),
            new("Clownfish", Shape.Fish, new Color(1.00f, 0.44f, 0.08f), 0.9f),
            new("Octopus", Shape.Sphere, new Color(0.80f, 0.24f, 0.46f), 0.92f),
            new("Chest", Shape.Cube, new Color(0.86f, 0.64f, 0.18f), 0.8f),
        };

        static readonly Look SpotMarked = new("SpotMarked", Shape.Disc, new Color(0.30f, 0.78f, 0.80f), 1f);
        static readonly Look SpotHot = new("SpotHot", Shape.Disc, new Color(1.00f, 0.28f, 0.62f), 1f);
        static readonly Look CardBack = new("CardBack", Shape.Card, new Color(0.20f, 0.30f, 0.72f), 1f);
        static readonly Look CardRed = new("CardRed", Shape.Card, new Color(0.86f, 0.10f, 0.10f), 1f);
        static readonly Look CardBlack = new("CardBlack", Shape.Card, new Color(0.07f, 0.07f, 0.09f), 1f);
        static readonly Look Screen = new("Screen", Shape.Slab, new Color(0.05f, 0.06f, 0.09f), 1f);

        // Filled by Build: one texel per distinct colour, one mesh per look.
        static readonly List<Color> Colours = new();
        static readonly Dictionary<string, Mesh> Meshes = new();

        public static void Build()
        {
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(ArtDir);

            Colours.Clear();
            Meshes.Clear();

            var all = new List<Look>();
            all.AddRange(SevensLooks);
            all.AddRange(VolcanoLooks);
            all.AddRange(ReefLooks);
            all.AddRange(new[] { SpotMarked, SpotHot, CardBack, CardRed, CardBlack, Screen });

            foreach (Look look in all)
                if (!Colours.Contains(look.Colour)) Colours.Add(look.Colour);

            Material material = Atlas();
            BakeMeshes(all);

            int built = 0;
            if (Cabinet(SlotKind.Sevens, SevensPath, SevensLooks, material, new Color(0.72f, 0.28f, 0.22f))) built++;
            if (Cabinet(SlotKind.Volcano, VolcanoPath, VolcanoLooks, material, new Color(0.13f, 0.14f, 0.17f))) built++;
            if (Cabinet(SlotKind.Reef, ReefPath, ReefLooks, material, new Color(0.32f, 0.38f, 0.48f))) built++;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[SlotFactory] {built} of 3 cabinets built, {Meshes.Count} symbol meshes on one "
                      + $"{AtlasSize}x{AtlasSize} atlas of {Colours.Count} colours.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        // ---------------------------------------------------------------- the atlas

        static Material Atlas()
        {
            var texture = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, false);
            var pixels = new Color[AtlasSize * AtlasSize];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = i < Colours.Count ? Colours[i] : Color.magenta;

            texture.SetPixels(pixels);
            File.WriteAllBytes(AtlasPath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(AtlasPath, ImportAssetOptions.ForceSynchronousImport);

            // Point, no mips, no compression: every texel is a flat colour and must stay exactly one.
            var importer = (TextureImporter)AssetImporter.GetAtPath(AtlasPath);
            importer.filterMode = FilterMode.Point;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();

            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath);

            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                material = new Material(shader) { name = "SlotAtlas", enableInstancing = true };
                AssetDatabase.CreateAsset(material, MaterialPath);
            }

            // The island's one shader once PR 158's EWYF/Stylized exists. StyleLook.Apply also
            // re-shades Art/Casino, but it runs before this factory, so the atlas would stay on
            // URP/Lit. The shader's defaults are StyleLook's numbers.
            Shader stylized = Shader.Find("EWYF/Stylized");
            if (stylized != null && material.shader != stylized) material.shader = stylized;

            material.SetTexture("_BaseMap", atlas);
            material.mainTexture = atlas;
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", 0.35f);
            EditorUtility.SetDirty(material);

            return material;
        }

        static Vector2 Texel(Color colour)
        {
            int i = Colours.IndexOf(colour);
            return new Vector2((i % AtlasSize + 0.5f) / AtlasSize, (i / AtlasSize + 0.5f) / AtlasSize);
        }

        // ---------------------------------------------------------------- the meshes

        /// <summary>
        /// One mesh per look, all in one asset file. Rebuilt from scratch each run; the prefabs that
        /// reference them are rebuilt in the same run, so nothing is left pointing at the old ones.
        /// </summary>
        static void BakeMeshes(List<Look> looks)
        {
            AssetDatabase.DeleteAsset(MeshesPath);

            Mesh first = null;
            foreach (Look look in looks)
            {
                if (Meshes.ContainsKey(look.Name)) continue;

                Mesh mesh = Bake(look);
                if (first == null)
                {
                    first = mesh;
                    AssetDatabase.CreateAsset(mesh, MeshesPath);
                }
                else
                {
                    AssetDatabase.AddObjectToAsset(mesh, first);
                }

                Meshes[look.Name] = mesh;
            }

            AssetDatabase.ImportAsset(MeshesPath);
        }

        /// <summary>Pieces of primitives, each (primitive, position, rotation, scale), combined and fitted into a unit cube.</summary>
        static Mesh Bake(Look look)
        {
            Mesh mesh = look.Shape == Shape.Peak ? Pyramid(look.Name) : Combine(look);

            var uv = new Vector2[mesh.vertexCount];
            Vector2 texel = Texel(look.Colour);
            for (int i = 0; i < uv.Length; i++) uv[i] = texel;
            mesh.uv = uv;

            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh Combine(Look look)
        {
            var q = Quaternion.identity;
            (PrimitiveType, Vector3, Quaternion, Vector3)[] parts = look.Shape switch
            {
                Shape.Sphere => new[] { (PrimitiveType.Sphere, Vector3.zero, q, Vector3.one) },
                Shape.Cube => new[] { (PrimitiveType.Cube, Vector3.zero, q, new Vector3(1f, 0.8f, 0.6f)) },
                Shape.Drum => new[] { (PrimitiveType.Cylinder, Vector3.zero, q, new Vector3(0.8f, 0.5f, 0.8f)) },
                Shape.Pod => new[] { (PrimitiveType.Capsule, Vector3.zero, Quaternion.Euler(0f, 0f, 25f), new Vector3(0.55f, 0.5f, 0.55f)) },
                Shape.Diamond => new[] { (PrimitiveType.Cube, Vector3.zero, Quaternion.Euler(0f, 0f, 45f), new Vector3(0.7f, 0.7f, 0.3f)) },
                Shape.Disc => new[] { (PrimitiveType.Cylinder, Vector3.zero, Quaternion.Euler(90f, 0f, 0f), new Vector3(1f, 0.08f, 1f)) },
                Shape.Card => new[] { (PrimitiveType.Cube, Vector3.zero, q, new Vector3(0.7f, 1f, 0.08f)) },
                Shape.Seven => new[]
                {
                    (PrimitiveType.Cube, new Vector3(0f, 0.4f, 0f), q, new Vector3(0.8f, 0.2f, 0.25f)),
                    (PrimitiveType.Cube, new Vector3(0.05f, -0.1f, 0f), Quaternion.Euler(0f, 0f, -25f), new Vector3(0.22f, 0.9f, 0.25f)),
                },
                Shape.Crown => new[]
                {
                    (PrimitiveType.Cube, new Vector3(0f, -0.25f, 0f), q, new Vector3(0.9f, 0.4f, 0.3f)),
                    (PrimitiveType.Cube, new Vector3(-0.35f, 0.2f, 0f), Quaternion.Euler(0f, 0f, 45f), new Vector3(0.25f, 0.25f, 0.3f)),
                    (PrimitiveType.Cube, new Vector3(0f, 0.25f, 0f), Quaternion.Euler(0f, 0f, 45f), new Vector3(0.3f, 0.3f, 0.3f)),
                    (PrimitiveType.Cube, new Vector3(0.35f, 0.2f, 0f), Quaternion.Euler(0f, 0f, 45f), new Vector3(0.25f, 0.25f, 0.3f)),
                },
                Shape.Starfish => new[]
                {
                    (PrimitiveType.Cube, Vector3.zero, Quaternion.Euler(0f, 0f, 45f), new Vector3(0.65f, 0.65f, 0.2f)),
                    (PrimitiveType.Cube, Vector3.zero, q, new Vector3(0.65f, 0.65f, 0.2f)),
                },
                Shape.Fish => new[]
                {
                    (PrimitiveType.Sphere, Vector3.zero, q, new Vector3(0.8f, 0.5f, 0.35f)),
                    (PrimitiveType.Cube, new Vector3(0.45f, 0f, 0f), Quaternion.Euler(0f, 0f, 45f), new Vector3(0.3f, 0.3f, 0.1f)),
                },
                _ => new[] { (PrimitiveType.Cube, Vector3.zero, q, Vector3.one) },
            };

            var combine = new CombineInstance[parts.Length];
            var temporary = new List<GameObject>();

            for (int i = 0; i < parts.Length; i++)
            {
                (PrimitiveType type, Vector3 position, Quaternion rotation, Vector3 scale) = parts[i];
                GameObject primitive = GameObject.CreatePrimitive(type);
                temporary.Add(primitive);

                combine[i] = new CombineInstance
                {
                    mesh = primitive.GetComponent<MeshFilter>().sharedMesh,
                    transform = Matrix4x4.TRS(position, rotation, scale),
                };
            }

            var mesh = new Mesh { name = "Slot_" + look.Name };
            mesh.CombineMeshes(combine, mergeSubMeshes: true, useMatrices: true);
            foreach (GameObject go in temporary) Object.DestroyImmediate(go);
            return mesh;
        }

        /// <summary>A four-sided pyramid with a flat top, the one shape no primitive gets close to.</summary>
        static Mesh Pyramid(string name)
        {
            Vector3[] corners =
            {
                new(-0.5f, -0.5f, -0.3f), new(0.5f, -0.5f, -0.3f), new(0.5f, -0.5f, 0.3f), new(-0.5f, -0.5f, 0.3f),
                new(-0.12f, 0.45f, -0.08f), new(0.12f, 0.45f, -0.08f), new(0.12f, 0.45f, 0.08f), new(-0.12f, 0.45f, 0.08f),
            };

            int[][] faces =
            {
                new[] { 0, 4, 5, 1 }, new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 },
                new[] { 4, 7, 6, 5 }, new[] { 0, 1, 2, 3 },
            };

            var vertices = new List<Vector3>();
            var triangles = new List<int>();

            // Flat shaded: every face its own four vertices.
            foreach (int[] face in faces)
            {
                int start = vertices.Count;
                foreach (int corner in face) vertices.Add(corners[corner]);
                triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }

            var mesh = new Mesh { name = "Slot_" + name };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        // ---------------------------------------------------------------- the cabinet

        /// <summary>
        /// A cabinet, front to +z, standing on its origin: a base with the buttons on its ledge, an
        /// upper body with the screen, a sign on top in the cabinet's colour. The screen is sized to
        /// the grid, so Reef's 7x7 gets the tallest glass.
        /// </summary>
        static bool Cabinet(SlotKind kind, string path, Look[] looks, Material atlas, Color trim)
        {
            int cols = SlotMath.Cols(kind);
            int rows = SlotMath.Rows(kind);

            const float width = 0.8f;
            float height = kind == SlotKind.Sevens ? 0.5f : kind == SlotKind.Volcano ? 0.66f : 0.8f;
            float cell = Mathf.Min(width / cols, height / rows);
            float screenY = 1.5f;

            var root = new GameObject(Path.GetFileNameWithoutExtension(path));

            Block(root.transform, "Base", new Vector3(0f, 0.45f, 0f), new Vector3(0.9f, 0.9f, 0.65f), trim, solid: true);
            Block(root.transform, "Ledge", new Vector3(0f, 0.92f, 0.12f), new Vector3(0.9f, 0.04f, 0.45f),
                  new Color(0.13f, 0.14f, 0.17f), solid: false);
            Block(root.transform, "Body", new Vector3(0f, 1.5f, -0.1f), new Vector3(0.9f, 1.2f, 0.45f),
                  new Color(0.13f, 0.14f, 0.17f), solid: true);
            Block(root.transform, "Sign", new Vector3(0f, 2.25f, -0.05f), new Vector3(0.96f, 0.3f, 0.55f), trim, solid: false);
            Block(root.transform, "Sign.Trim", new Vector3(0f, 2.25f, 0.23f), new Vector3(0.8f, 0.14f, 0.02f),
                  new Color(0.83f, 0.68f, 0.24f), solid: false);

            var glass = Part(root.transform, "Screen", Meshes[Screen.Name], atlas,
                             new Vector3(0f, screenY, 0.13f), new Vector3(width + 0.04f, height + 0.04f, 0.02f));
            glass.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;

            var screen = new GameObject("Reels").transform;
            screen.SetParent(root.transform, false);
            screen.localPosition = new Vector3(0f, screenY, 0.19f);

            var cells = new Transform[cols * rows];
            var spots = kind == SlotKind.Reef ? new MeshFilter[cols * rows] : new MeshFilter[0];
            var meshes = new Mesh[looks.Length];
            var scales = new float[looks.Length];

            for (int s = 0; s < looks.Length; s++)
            {
                meshes[s] = Meshes[looks[s].Name];
                scales[s] = looks[s].Scale;
            }

            for (int c = 0; c < cols; c++)
            for (int r = 0; r < rows; r++)
            {
                int i = c * rows + r;
                var at = new Vector3((c - (cols - 1) * 0.5f) * cell, ((rows - 1) * 0.5f - r) * cell, 0f);

                GameObject symbol = Part(screen, $"Cell{c}.{r}", meshes[(c + r) % meshes.Length], atlas,
                                         at, Vector3.one * cell * scales[(c + r) % meshes.Length]);
                symbol.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                cells[i] = symbol.transform;

                if (kind != SlotKind.Reef) continue;

                GameObject spot = Part(screen, $"Spot{c}.{r}", null, atlas, at + new Vector3(0f, 0f, -0.04f),
                                       new Vector3(cell * 0.6f, cell * 0.6f, cell * 0.1f));
                spot.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                spots[i] = spot.GetComponent<MeshFilter>();
            }

            MeshFilter card = null;
            if (kind == SlotKind.Sevens)
            {
                GameObject face = Part(root.transform, "Card", Meshes[CardBack.Name], atlas,
                                       new Vector3(0f, 1.08f, 0.14f), new Vector3(0.12f, 0.16f, 0.12f));
                card = face.GetComponent<MeshFilter>();
            }

            root.AddComponent<NetworkObject>();
            root.AddComponent<SlotMachine>().Configure(
                kind, cells, meshes, scales, cell, spots, Meshes[SpotMarked.Name], Meshes[SpotHot.Name],
                card, Meshes[CardBack.Name], Meshes[CardRed.Name], Meshes[CardBlack.Name], Palette.Named("Gold"));

            // The buttons, along the ledge: spin on the right where a hand falls, the stake on the
            // left, and between them the two card colours on Sevens or the bonus buy on the others.
            Button(root.transform, SlotAction.Spin, new Vector3(0.28f, 0.97f, 0.2f), new Color(0.25f, 0.70f, 0.30f), 1.3f);
            Button(root.transform, SlotAction.Bet, new Vector3(-0.3f, 0.97f, 0.2f), new Color(0.83f, 0.68f, 0.24f), 1f);

            if (kind == SlotKind.Sevens)
            {
                Button(root.transform, SlotAction.Red, new Vector3(-0.08f, 0.97f, 0.2f), new Color(0.72f, 0.28f, 0.22f), 0.8f);
                Button(root.transform, SlotAction.Black, new Vector3(0.08f, 0.97f, 0.2f), new Color(0.13f, 0.14f, 0.17f), 0.8f);
            }
            else
            {
                // The bonus buy, in the card's red so it costs the scene no new material.
                Button(root.transform, SlotAction.Buy, new Vector3(0f, 0.97f, 0.2f), new Color(0.72f, 0.28f, 0.22f), 0.9f);
            }

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[SlotFactory] Failed to save {path}.");
                return false;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>(), path);
            Debug.Log($"[SlotFactory] Built {path}: {SlotMath.Title(kind)}, {cols}x{rows}, {looks.Length} symbols.");
            return true;
        }

        /// <summary>A button: its own nested NetworkObject, with a collider for the crosshair to find.</summary>
        static void Button(Transform parent, SlotAction action, Vector3 position, Color colour, float size)
        {
            GameObject button = Block(parent, action + "Button", position,
                                      new Vector3(0.14f * size, 0.06f, 0.14f), colour, solid: true);
            button.AddComponent<NetworkObject>();
            button.AddComponent<SlotButton>().Configure(action);
        }

        static GameObject Part(Transform parent, string name, Mesh mesh, Material material, Vector3 position, Vector3 scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        /// <summary>Same as CasinoFactory.Block, copied for the reason given there.</summary>
        static GameObject Block(Transform parent, string name, Vector3 position, Vector3 size, Color color, bool solid)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = position;
            cube.transform.localScale = size;

            if (!solid)
            {
                Object.DestroyImmediate(cube.GetComponent<Collider>());
                cube.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
            }

            cube.GetComponent<Renderer>().sharedMaterial = Palette.For(color);
            return cube;
        }

        /// <summary>Same reasoning as PlayerPrefabBuilder.RegisterSpawnable; see the note there.</summary>
        static void RegisterSpawnable(NetworkObject networkObject, string path)
        {
            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (networkObject == null || prefabs == null)
            {
                Debug.LogError($"[SlotFactory] {path} cannot be registered to spawn.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);
        }
    }
}
