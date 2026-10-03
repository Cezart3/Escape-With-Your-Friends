using EscapeWithYourFriends.World;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The crater cap (#245): tools/art/volcano.py's Crater.fbx, made into the prefab that
    /// <see cref="Volcano"/> puts on the summit at run time. It lives in Resources because no scene
    /// holds it: the summit is wherever the island's height function put it, and the island scenes
    /// are generated.
    ///
    ///     Unity.exe -batchmode -quit -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.VolcanoFactory.Build
    ///
    /// The rock is on the shared stylized shader; the lava is unlit, because it is the one thing on
    /// the island that is its own light, and <see cref="Volcano"/> turns its colour up at night.
    /// </summary>
    public static class VolcanoFactory
    {
        const string Folder = "Assets/_Project/Art/Models/Volcano";
        const string ModelPath = Folder + "/Crater.fbx";
        const string PrefabPath = "Assets/_Project/Resources/" + Volcano.ResourceName + ".prefab";

        [MenuItem("EWYF/Build Volcano")]
        public static void Build()
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
            if (importer == null) { Debug.LogError($"[VolcanoFactory] no {ModelPath}: run tools/art/volcano.py."); return; }

            // Readable, for the mesh collider in a build; no materials of its own, the prefab assigns them.
            if (!importer.isReadable || importer.materialImportMode != ModelImporterMaterialImportMode.None)
            {
                importer.isReadable = true;
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                importer.SaveAndReimport();
            }

            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(ModelPath);
            Texture2D rockMap = Texture(Folder + "/Textures/Volcano.png");
            Texture2D lavaMap = Texture(Folder + "/Textures/Lava.png");
            if (mesh == null || rockMap == null || lavaMap == null) { Debug.LogError("[VolcanoFactory] the model or a texture is missing."); return; }

            Material rock = MaterialAt(Folder + "/VolcanoRock.mat", StyleLook.Stylized);
            rock.SetTexture("_BaseMap", rockMap);
            StyleLook.Wear(rock);

            Material lava = MaterialAt(Folder + "/Lava.mat", Shader.Find("Universal Render Pipeline/Unlit"));
            lava.SetTexture("_BaseMap", lavaMap);
            lava.SetColor("_BaseColor", Volcano.Glow(0f));

            var go = new GameObject(Volcano.ResourceName);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = new[] { rock, lava };
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.AddComponent<Volcano>();
            go.isStatic = true;

            PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            Object.DestroyImmediate(go);
            AssetDatabase.SaveAssets();
            Debug.Log($"[VolcanoFactory] {PrefabPath}: {mesh.triangles.Length / 3} tris, {mesh.subMeshCount} submeshes, "
                      + $"{mesh.bounds.size.x:F0} x {mesh.bounds.size.y:F0} x {mesh.bounds.size.z:F0} m.");
        }

        static Texture2D Texture(string path)
        {
            if (AssetImporter.GetAtPath(path) is TextureImporter t && (t.wrapMode != TextureWrapMode.Clamp || t.maxTextureSize != 256))
            {
                t.wrapMode = TextureWrapMode.Clamp;
                t.maxTextureSize = 256;
                t.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Material MaterialAt(string path, Shader shader)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) { material.shader = shader; return material; }
            material = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(material, path);
            return material;
        }
    }
}
