using System.IO;
using EscapeWithYourFriends.AI;
using EscapeWithYourFriends.World;
using FishNet.Component.Transforming;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Generates the castaway you go back for. #73.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.CastawayBuilder.Build
    ///
    /// A body, a head, a capsule and a navmesh agent - the same five lines every walking thing in
    /// this project is made of, minus everything that makes a native dangerous. No Health, no
    /// weapon, no spawner: there is one of them, they are not a threat, and the only question the
    /// game asks about them is whether they are on the aeroplane yet.
    ///
    /// They are a <see cref="Landmark"/> as well as a person, so the map and the nav report know
    /// where they are and the walk to them is measured like every other walk on the island.
    /// </summary>
    public static class CastawayBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        public const string CastawayPath = PrefabDir + "/Castaway.prefab";

        public static void Build()
        {
            Directory.CreateDirectory(PrefabDir);

            GameObject root = BuildCastaway();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, CastawayPath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[CastawayBuilder] Failed to save {CastawayPath}.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[CastawayBuilder] Built {CastawayPath}: one person, "
                      + $"{saved.GetComponent<NavMeshAgent>().speed:0.0} m/s on foot.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static GameObject BuildCastaway()
        {
            var root = new GameObject("Castaway");

            var landmark = root.AddComponent<Landmark>();
            landmark.Id = "castaway";
            landmark.DisplayName = "The One You Left";
            landmark.Purpose = "Somebody who has been waiting a very long time for a lift.";
            landmark.Radius = 6f;
            landmark.Hostile = false;

            Box(root.transform, "Body", new Vector3(0f, 0.85f, 0f), new Vector3(0.55f, 1.2f, 0.38f));
            Box(root.transform, "Head", new Vector3(0f, 1.62f, 0f), Vector3.one * 0.42f);

            // An arm up. It is a greybox and it will be replaced, but a shape waving at you from a
            // beach is legible from the air and a shape standing still is scenery.
            Box(root.transform, "Arm", new Vector3(0.42f, 1.5f, 0f), new Vector3(0.16f, 0.75f, 0.16f));

            var collider = root.AddComponent<CapsuleCollider>();
            collider.radius = 0.3f;
            collider.height = 1.8f;
            collider.center = new Vector3(0f, 0.9f, 0f);

            var agent = root.AddComponent<NavMeshAgent>();
            agent.radius = 0.4f;
            agent.height = 1.8f;
            agent.baseOffset = 0f;
            agent.autoBraking = true;
            agent.autoRepath = true;

            root.AddComponent<NetworkObject>();
            root.AddComponent<NetworkTransform>();
            root.AddComponent<Castaway>();

            return root;
        }

        static void Box(Transform parent, string name, Vector3 position, Vector3 scale)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;

            go.GetComponent<Renderer>().sharedMaterial = Palette.Named("Canvas");

            // The capsule on the root is the only collider that should ever be hit: a stack of solid
            // boxes inside a navmesh agent is an agent that fights its own shoulders.
            Collider existing = go.GetComponent<Collider>();
            if (existing != null) Object.DestroyImmediate(existing);
        }

        /// <summary>Same reasoning as PlayerPrefabBuilder.RegisterSpawnable; see the note there.</summary>
        static void RegisterSpawnable(NetworkObject networkObject)
        {
            if (networkObject == null)
            {
                Debug.LogError("[CastawayBuilder] Saved prefab has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[CastawayBuilder] missing {PrefabObjectsPath}; not registered.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);
        }
    }
}
