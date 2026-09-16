using System;
using System.IO;
using EscapeWithYourFriends.World;
using FishNet.Component.Transforming;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Generates the three greybox plane parts and registers them as spawnable. #70.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.PlanePartBuilder.Build
    ///
    /// The boxes are not the point; the three numbers on each of them are. A part is described by how
    /// big it is, how badly it slows one person down, and how much of that a second pair of hands
    /// buys back — and those are exactly the numbers the playtest will want moved, so they live here
    /// as a table rather than being typed into three prefab inspectors.
    ///
    /// The spread across the table is deliberate. The engine is the one you need help with, the
    /// propeller is the one you can just about manage alone, and the wing is in between and five
    /// metres long, which is the one that gets stuck on things. Nothing enforces "you must bring a
    /// friend": one person can eventually walk any of them home, and finding out how long that takes
    /// is meant to be how a group learns to bring a friend.
    /// </summary>
    public static class PlanePartBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        public const string EnginePath = PrefabDir + "/PlanePart.Engine.prefab";
        public const string WingPath = PrefabDir + "/PlanePart.Wing.prefab";
        public const string PropellerPath = PrefabDir + "/PlanePart.Propeller.prefab";

        /// <summary>One part: what it is called, how big it is, and what it costs to carry.</summary>
        readonly struct Part
        {
            public readonly string Path;
            public readonly string Name;
            public readonly string Label;
            public readonly Vector3 Size;
            public readonly float Mass;
            public readonly float Alone;
            public readonly float Shared;

            public Part(string path, string name, string label, Vector3 size, float mass,
                        float alone, float shared)
            {
                Path = path;
                Name = name;
                Label = label;
                Size = size;
                Mass = mass;
                Alone = alone;
                Shared = shared;
            }
        }

        static readonly Part[] Parts =
        {
            // A radial engine: small, dense, and the reason the word "haul" is in the issue. Alone
            // it is slower than a crouch, which is the point - this is the one you fetch a friend for.
            new(EnginePath, "PlanePart.Engine", "engine",
                new Vector3(1.1f, 1.0f, 1.3f), mass: 260f, alone: 0.30f, shared: 0.75f),

            // Five metres of wing. Not heavy so much as impossible: it is the part that catches on
            // every tree between the wreck and the beach.
            new(WingPath, "PlanePart.Wing", "wing",
                new Vector3(0.35f, 0.3f, 5.0f), mass: 140f, alone: 0.40f, shared: 0.85f),

            // The one a single player can genuinely walk home, so that a group of two is never stuck.
            new(PropellerPath, "PlanePart.Propeller", "propeller",
                new Vector3(2.2f, 0.25f, 0.3f), mass: 70f, alone: 0.55f, shared: 0.90f)
        };

        /// <summary>Where the three prefabs live, in the order the parts are listed above.</summary>
        public static string[] Paths => new[] { EnginePath, WingPath, PropellerPath };

        public static void Build()
        {
            Directory.CreateDirectory(PrefabDir);

            foreach (Part part in Parts)
            {
                GameObject root = BuildPart(part);

                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, part.Path, out bool success);
                UnityEngine.Object.DestroyImmediate(root);

                if (!success || saved == null)
                {
                    Debug.LogError($"[PlanePartBuilder] Failed to save {part.Path}.");
                    if (Application.isBatchMode) EditorApplication.Exit(1);
                    return;
                }

                RegisterSpawnable(saved.GetComponent<NetworkObject>());

                Debug.Log($"[PlanePartBuilder] Built {part.Path}: \"{part.Label}\", {part.Mass}kg, "
                          + $"{part.Size.x:0.##}x{part.Size.y:0.##}x{part.Size.z:0.##}m, "
                          + $"{part.Alone:0.##} alone / {part.Shared:0.##} shared.");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static GameObject BuildPart(Part part)
        {
            var root = new GameObject(part.Name);

            root.AddComponent<NetworkObject>();

            // Server-driven, like the buggy and the wildlife: the host owns the physics and every
            // client interpolates what it is told. See PlanePart for why an objective may not be
            // simulated independently on four machines the way a corpse is.
            root.AddComponent<NetworkTransform>();

            var body = root.AddComponent<Rigidbody>();
            body.mass = part.Mass;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            var box = root.AddComponent<BoxCollider>();
            box.size = part.Size;
            box.center = new Vector3(0f, part.Size.y * 0.5f, 0f);

            // The visual is a child with its collider stripped, so the one collider on the root is
            // the only thing anybody has to ignore when they pick it up.
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Body";
            visual.transform.SetParent(root.transform, false);
            visual.transform.localPosition = new Vector3(0f, part.Size.y * 0.5f, 0f);
            visual.transform.localScale = part.Size;
            UnityEngine.Object.DestroyImmediate(visual.GetComponent<Collider>());

            var planePart = root.AddComponent<PlanePart>();

            SetFields(planePart, so =>
            {
                so.FindProperty("_label").stringValue = part.Label;
                so.FindProperty("_alone").floatValue = part.Alone;
                so.FindProperty("_shared").floatValue = part.Shared;
                so.FindProperty("_body").objectReferenceValue = body;
                so.FindProperty("_collider").objectReferenceValue = box;
            });

            return root;
        }

        static void SetFields(UnityEngine.Object target, Action<SerializedObject> configure)
        {
            var so = new SerializedObject(target);
            configure(so);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Same reasoning as PlayerPrefabBuilder.RegisterSpawnable; see the note there.</summary>
        static void RegisterSpawnable(NetworkObject networkObject)
        {
            if (networkObject == null)
            {
                Debug.LogError("[PlanePartBuilder] Saved prefab has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[PlanePartBuilder] missing {PrefabObjectsPath}; not registered.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);
        }
    }
}
