using System.Collections.Generic;
using System.IO;
using System.Linq;
using EscapeWithYourFriends.Casino;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The slot cabinets of the casino floor and the VIP room.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.SlotFactory.Build
    ///
    /// **Everything is modelled in Blender** by <c>tools/art/slots.py</c> (#252): every reel symbol in
    /// <c>SlotSymbols.fbx</c>, every cabinet and its three bulb groups in <c>SlotCabinets.fbx</c>, all
    /// painted from one ramp sheet. So the whole slot floor is one material, as before, plus the
    /// bulbs' glow; <c>LookTest</c> holds the scene to 48. A symbol changing is a mesh swap.
    ///
    /// The cabinet's body is the model; what the player walks into and presses is still boxes:
    /// colliders without renderers for the body, and the buttons, each its own NetworkObject.
    ///
    /// Unlike the other casino factories this one always rebuilds: nothing here is dressed by hand
    /// or by <c>ArtDress</c>, and a saved-over prefab keeps its GUID and its spawnable entry.
    /// </summary>
    public static class SlotFactory
    {
        const string PrefabDir = "Assets/_Project/Prefabs/Stations";
        const string ArtDir = "Assets/_Project/Art/Casino";
        const string ModelDir = ArtDir + "/Models";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";
        const string SymbolsPath = ModelDir + "/SlotSymbols.fbx";
        const string CabinetsPath = ModelDir + "/SlotCabinets.fbx";
        const string TexturePath = ModelDir + "/Textures/Symbols.png";
        const string MaterialPath = ArtDir + "/SlotAtlas.mat";

        internal const string SevensPath = PrefabDir + "/SlotSevens.prefab";
        internal const string VolcanoPath = PrefabDir + "/SlotVolcano.prefab";
        internal const string ReefPath = PrefabDir + "/SlotReef.prefab";
        internal const string FruitPath = PrefabDir + "/SlotFruit.prefab";
        internal const string LagoonPath = PrefabDir + "/SlotLagoon.prefab";

        /// <summary>The reel window's height per game; slots.py's WINDOW less the bezel's lip.</summary>
        static float Window(SlotKind kind) => kind is SlotKind.Sevens or SlotKind.Lagoon ? 0.5f
                                              : kind is SlotKind.Volcano or SlotKind.Fruit ? 0.66f : 0.8f;

        const float Width = 0.8f;

        /// <summary>A symbol: its mesh's name in the FBX less "Sym_", and its size in cells. Order is SlotMath's.</summary>
        static readonly (string Name, float Scale)[] SevensLooks =
        {
            ("Lime", 0.8f), ("Coconut", 0.84f), ("Mango", 0.86f), ("Papaya", 0.86f), ("Pineapple", 0.88f),
            ("Melon", 0.92f), ("Seven", 0.96f), ("Star", 0.96f),
        };

        static readonly (string, float)[] VolcanoLooks =
        {
            ("Obsidian", 0.78f), ("Jade", 0.8f), ("Amber", 0.8f), ("Ruby", 0.84f), ("Pearl", 0.84f), ("Drum", 0.88f),
            ("Mask", 0.9f), ("Idol", 0.92f), ("Crown", 0.94f), ("Volcano", 0.98f), ("LavaOrb", 0.9f),
        };

        static readonly (string, float)[] ReefLooks =
        {
            ("Kelp", 0.84f), ("Shell", 0.84f), ("Starfish", 0.88f), ("Urchin", 0.86f), ("Puffer", 0.92f),
            ("Clownfish", 0.94f), ("Octopus", 0.96f), ("Chest", 0.94f),
        };

        // Names carry a prefix: a look's name keys its mesh, and Sevens already has a pineapple.
        static readonly (string, float)[] FruitLooks =
        {
            ("FruitBerry", 0.8f), ("FruitLychee", 0.82f), ("FruitKiwi", 0.84f), ("FruitStarfruit", 0.86f),
            ("FruitGuava", 0.86f), ("FruitBanana", 0.9f), ("FruitDragonfruit", 0.92f), ("FruitPassionfruit", 0.92f),
            ("FruitGoldenPineapple", 0.96f), ("FruitSun", 0.98f), ("FruitCoconutBomb", 0.92f),
        };

        // The fish grow with their value, so a golden marlin reads across the room.
        static readonly (string, float)[] LagoonLooks =
        {
            ("LagoonShell", 0.8f), ("LagoonStarfish", 0.82f), ("LagoonCrab", 0.84f), ("LagoonBobber", 0.82f),
            ("LagoonTackle", 0.86f), ("LagoonRod", 0.9f), ("LagoonBoat", 0.92f),
            ("LagoonMinnow", 0.78f), ("LagoonSnapper", 0.84f), ("LagoonGrouper", 0.9f), ("LagoonMarlin", 0.96f),
            ("LagoonGoldenMarlin", 1f), ("LagoonCastaway", 0.96f), ("LagoonHook", 0.96f),
        };

        // Filled by Build from the two FBX files, keyed by the mesh's name.
        static readonly Dictionary<string, Mesh> Meshes = new();

        public static void Build()
        {
            Directory.CreateDirectory(PrefabDir);
            Meshes.Clear();

            foreach (string path in new[] { SymbolsPath, CabinetsPath })
            {
                if (!Import(path))
                {
                    Debug.LogError($"[SlotFactory] No {path}. Run tools/art/slots.py into {ModelDir} first.");
                    if (Application.isBatchMode) EditorApplication.Exit(1);
                    return;
                }

                foreach (Mesh mesh in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>()) Meshes[mesh.name] = mesh;
            }

            bool upright = CheckSeven();
            Material material = Symbols();
            Material bulbOn = StyleLook.Glowing("SlotBulbOn", new Color(1f, 0.93f, 0.7f), new Color(3.2f, 2.5f, 1.3f));

            int built = 0;
            if (Cabinet(SlotKind.Sevens, SevensPath, SevensLooks, material, bulbOn)) built++;
            if (Cabinet(SlotKind.Volcano, VolcanoPath, VolcanoLooks, material, bulbOn)) built++;
            if (Cabinet(SlotKind.Reef, ReefPath, ReefLooks, material, bulbOn)) built++;
            if (Cabinet(SlotKind.Fruit, FruitPath, FruitLooks, material, bulbOn)) built++;
            if (Cabinet(SlotKind.Lagoon, LagoonPath, LagoonLooks, material, bulbOn)) built++;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[SlotFactory] {built} of {System.Enum.GetValues(typeof(SlotKind)).Length} cabinets built from "
                      + $"{Meshes.Count} Blender meshes on one material; the seven {(upright ? "reads the right way round" : "is MIRRORED")}.");

            if (Application.isBatchMode) EditorApplication.Exit(upright ? 0 : 1);
        }

        // ---------------------------------------------------------------- the models

        /// <summary>Our own FBX, imported as meshes only: no materials of its own, no rig, the file's normals.</summary>
        static bool Import(string path)
        {
            if (AssetImporter.GetAtPath(path) is not ModelImporter importer) return false;

            importer.globalScale = 1f;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importBlendShapes = false;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.None;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.isReadable = false;
            importer.SaveAndReimport();
            return true;
        }

        /// <summary>
        /// The seven used to be mirrored (#252): a cabinet's front is +z, so the viewer's left is +x.
        /// The foot of a seven is left of its middle, so its lowest vertices must sit at +x; and the
        /// nearest thing to the viewer is its gloss, up and to their left, so its frontmost vertices
        /// sit high at +x. Mirrored, the foot moves; turned round, the front is the gold rim instead.
        /// </summary>
        static bool CheckSeven()
        {
            if (!Meshes.TryGetValue("Sym_Seven", out Mesh seven)) return false;

            Vector3[] vertices = seven.vertices;
            float bottom = vertices.Min(v => v.y);
            float footX = vertices.Where(v => v.y < bottom + 0.15f).Average(v => v.x);
            float front = vertices.Max(v => v.z);
            Vector3 gloss = vertices.Where(v => v.z > front - 0.03f).Aggregate(Vector3.zero, (a, v) => a + v)
                            / vertices.Count(v => v.z > front - 0.03f);

            bool right = footX > 0f && gloss.x > 0.1f && gloss.y > 0.2f;
            if (!right)
                Debug.LogError($"[SlotFactory] Sym_Seven reads wrong: foot at x {footX:F2}, frontmost at {gloss}. "
                               + "slots.py's front must face -Y.");
            return right;
        }

        /// <summary>The ramp sheet, filtered and mipped, on the material the reels have always worn.</summary>
        static Material Symbols()
        {
            if (AssetImporter.GetAtPath(TexturePath) is TextureImporter importer)
            {
                importer.mipmapEnabled = true;
                importer.filterMode = FilterMode.Bilinear;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);

            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                material = StyleLook.New("SlotAtlas");
                AssetDatabase.CreateAsset(material, MaterialPath);
            }

            material.SetTexture("_BaseMap", texture);
            material.mainTexture = texture;
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", 0.35f);

            // The island's one shader and its numbers: StyleLook.Apply runs before this factory in the bake.
            StyleLook.Wear(material);
            return material;
        }

        // ---------------------------------------------------------------- the cabinet

        /// <summary>
        /// A cabinet, front to +z, standing on its origin: the Blender body with the glass and the
        /// reels in its window, the bulbs, the colliders, and the buttons along the deck.
        /// </summary>
        static bool Cabinet(SlotKind kind, string path, (string Name, float Scale)[] looks, Material atlas, Material bulbOn)
        {
            int cols = SlotMath.Cols(kind);
            int rows = SlotMath.Rows(kind);

            float height = Window(kind);
            float cell = Mathf.Min(Width / cols, height / rows);
            const float screenY = 1.5f;

            var root = new GameObject(Path.GetFileNameWithoutExtension(path));

            var body = Part(root.transform, "Cabinet", Meshes[$"Cab_{kind}"], atlas, Vector3.zero, Vector3.one);
            body.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.On;

            var bulbs = new MeshRenderer[3];
            for (int g = 0; g < bulbs.Length; g++)
            {
                GameObject group = Part(root.transform, $"Bulbs{g}", Meshes[$"Cab_{kind}_Bulbs{g}"], atlas, Vector3.zero, Vector3.one);
                bulbs[g] = group.GetComponent<MeshRenderer>();
                bulbs[g].shadowCastingMode = ShadowCastingMode.Off;
            }

            // What a player bumps into: the base and the body, as the model stands.
            Collider(root.transform, "Base", new Vector3(0f, 0.44f, 0f), new Vector3(1f, 0.88f, 0.66f));
            Collider(root.transform, "Body", new Vector3(0f, 1.53f, -0.105f), new Vector3(1f, 1.18f, 0.45f));

            var glass = Part(root.transform, "Screen", Meshes["Sym_Screen"], atlas,
                             new Vector3(0f, screenY, 0.13f), new Vector3(Width + 0.04f, height + 0.04f, 0.2f));
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
                meshes[s] = Meshes["Sym_" + looks[s].Name];
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
                GameObject face = Part(root.transform, "Card", Meshes["Sym_CardBack"], atlas,
                                       new Vector3(0f, 1.07f, 0.14f), Vector3.one * 0.16f);
                card = face.GetComponent<MeshFilter>();
            }

            root.AddComponent<NetworkObject>();
            root.AddComponent<SlotMachine>().Configure(
                kind, cells, meshes, scales, cell, spots, Meshes["Sym_SpotMarked"], Meshes["Sym_SpotHot"],
                card, Meshes["Sym_CardBack"], Meshes["Sym_CardRed"], Meshes["Sym_CardBlack"], Palette.Named("Gold"),
                bulbs, bulbOn, atlas);

            // The buttons, along the deck: spin on the right where a hand falls, the stake on the
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

                // The ante, between the buy and the stake, in the stake's gold.
                if (SlotMath.HasAnte(kind))
                    Button(root.transform, SlotAction.Ante, new Vector3(-0.15f, 0.97f, 0.2f), new Color(0.83f, 0.68f, 0.24f), 0.7f);

                // Autoplay sits where the ante would, in the spin button's green.
                if (SlotMath.HasAutoplay(kind))
                    Button(root.transform, SlotAction.Auto, new Vector3(-0.15f, 0.97f, 0.2f), new Color(0.25f, 0.70f, 0.30f), 0.7f);
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
            var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
            button.name = action + "Button";
            button.transform.SetParent(parent, false);
            button.transform.localPosition = position;
            button.transform.localScale = new Vector3(0.14f * size, 0.06f, 0.14f);
            button.GetComponent<Renderer>().sharedMaterial = Palette.For(colour);
            button.AddComponent<NetworkObject>();
            button.AddComponent<SlotButton>().Configure(action);
        }

        static void Collider(Transform parent, string name, Vector3 centre, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var box = go.AddComponent<BoxCollider>();
            box.center = centre;
            box.size = size;
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
