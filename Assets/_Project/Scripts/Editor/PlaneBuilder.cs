using System.IO;
using EscapeWithYourFriends.World;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Generates the greybox plane and registers it as spawnable. #71.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.PlaneBuilder.Build
    ///
    /// The shape is not the point. What matters is that three of its children are named
    /// <c>Fitted.engine</c>, <c>Fitted.wing</c> and <c>Fitted.propeller</c>, because that naming is
    /// the entire wiring between this file and <see cref="PlaneAssembly"/>: the component hides every
    /// <c>Fitted.*</c> child it finds and puts each one back when the part with the matching label
    /// goes in. Nothing is serialized, nothing is dragged into an inspector, and a fourth part later
    /// is a fourth box here.
    ///
    /// It stands there missing its starboard wing, its engine and its propeller, which is a silhouette
    /// you can read from the other end of the beach. That is what "legible at a glance" means for a
    /// thing this size - a progress bar floating over it would be a worse version of the same fact.
    /// </summary>
    public static class PlaneBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        public const string PlanePath = PrefabDir + "/Plane.prefab";

        /// <summary>Half the fuselage, nose to tail. The whole aircraft is built off this.</summary>
        const float HalfLength = 3.5f;

        /// <summary>Half the span of one wing panel, measured out from the fuselage side.</summary>
        const float WingSpan = 5f;

        public static void Build()
        {
            Directory.CreateDirectory(PrefabDir);

            GameObject root = BuildPlane();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PlanePath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[PlaneBuilder] Failed to save {PlanePath}.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            int holes = 0;
            foreach (Transform child in saved.transform)
                if (child.name.StartsWith("Fitted.")) holes++;

            Debug.Log($"[PlaneBuilder] Built {PlanePath}: {holes} hole(s) to fill.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static GameObject BuildPlane()
        {
            var root = new GameObject("Plane");

            root.AddComponent<NetworkObject>();

            // No NetworkTransform: it stands on its wheels and does not move until #72 gives it a
            // reason to. The spawn message carries where it is, same as the stations and the chests.
            var landmark = root.AddComponent<Landmark>();
            landmark.Id = "plane";
            landmark.DisplayName = "The Plane";
            landmark.Purpose = "The way off this island, once it has all of its pieces.";
            landmark.Radius = 12f;
            landmark.Hostile = false;

            // ---- what is already there

            Box(root, "Fuselage", new Vector3(0f, 1.6f, 0f), new Vector3(1.3f, 1.3f, HalfLength * 2f),
                solid: true);

            Box(root, "Cockpit", new Vector3(0f, 2.45f, 0.9f), new Vector3(1.1f, 0.7f, 1.8f),
                solid: false);

            // The port wing is on. The starboard one is a hole, which is the fastest way to read a
            // half-finished aeroplane from a distance.
            Box(root, "Wing.Port", new Vector3(-(WingSpan * 0.5f + 0.6f), 1.6f, 0.4f),
                new Vector3(WingSpan, 0.22f, 1.5f), solid: true);

            Box(root, "Tail.Fin", new Vector3(0f, 2.8f, -HalfLength + 0.4f),
                new Vector3(0.18f, 1.7f, 1.2f), solid: false);

            Box(root, "Tail.Stabiliser", new Vector3(0f, 2.1f, -HalfLength + 0.5f),
                new Vector3(3f, 0.18f, 0.9f), solid: false);

            Wheel(root, "Gear.Port", new Vector3(-1.1f, 0.5f, 0.6f));
            Wheel(root, "Gear.Starboard", new Vector3(1.1f, 0.5f, 0.6f));
            Wheel(root, "Gear.Tail", new Vector3(0f, 0.35f, -HalfLength + 0.3f));

            // ---- the three holes. The name after the dot is the PlanePart label that fills it.

            Box(root, "Fitted.engine", new Vector3(0f, 1.7f, HalfLength + 0.55f),
                new Vector3(1.2f, 1.2f, 1.1f), solid: true);

            Box(root, "Fitted.wing", new Vector3(WingSpan * 0.5f + 0.6f, 1.6f, 0.4f),
                new Vector3(WingSpan, 0.22f, 1.5f), solid: true);

            Box(root, "Fitted.propeller", new Vector3(0f, 1.7f, HalfLength + 1.2f),
                new Vector3(2.4f, 0.22f, 0.12f), solid: false);

            root.AddComponent<PlaneAssembly>();

            return root;
        }

        static GameObject Box(GameObject root, string name, Vector3 position, Vector3 scale, bool solid)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;

            if (!solid)
            {
                Collider existing = go.GetComponent<Collider>();
                if (existing != null) Object.DestroyImmediate(existing);
            }

            return go;
        }

        static void Wheel(GameObject root, string name, Vector3 position)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = position;
            go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            go.transform.localScale = new Vector3(0.7f, 0.12f, 0.7f);

            Collider existing = go.GetComponent<Collider>();
            if (existing != null) Object.DestroyImmediate(existing);
        }

        /// <summary>Same reasoning as PlayerPrefabBuilder.RegisterSpawnable; see the note there.</summary>
        static void RegisterSpawnable(NetworkObject networkObject)
        {
            if (networkObject == null)
            {
                Debug.LogError("[PlaneBuilder] Saved prefab has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[PlaneBuilder] missing {PrefabObjectsPath}; not registered.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);
        }
    }
}
