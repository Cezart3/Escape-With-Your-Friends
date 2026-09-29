using System.IO;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// A three-quarter photo of a prefab for a PR, from batchmode (with graphics, so no
    /// <c>-nographics</c>): <c>-executeMethod EscapeWithYourFriends.EditorTools.PrefabShot.Shoot
    /// -prefab Assets/.../Plane.prefab -out D:\shots\plane.jpg</c>.
    /// </summary>
    public static class PrefabShot
    {
        public static void Shoot()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            string prefab = Arg(args, "-prefab"), output = Arg(args, "-out");

            var model = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(prefab));
            var bounds = new Bounds(model.transform.position, Vector3.zero);
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>())
                if (renderer.enabled) bounds.Encapsulate(renderer.bounds);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f);

            var camera = new GameObject("Camera").AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.62f, 0.75f, 0.86f);
            camera.fieldOfView = 35f;
            float distance = bounds.extents.magnitude / Mathf.Sin(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            camera.transform.position = bounds.center + Quaternion.Euler(20f, 215f, 0f) * Vector3.forward * -distance;
            camera.transform.LookAt(bounds.center);

            var target = new RenderTexture(1280, 720, 24);
            camera.targetTexture = target;
            camera.Render();

            RenderTexture.active = target;
            var image = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllBytes(output, image.EncodeToJPG(85));
            Debug.Log($"[PrefabShot] {prefab} -> {output}");
        }

        static string Arg(string[] args, string name)
        {
            int i = System.Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
