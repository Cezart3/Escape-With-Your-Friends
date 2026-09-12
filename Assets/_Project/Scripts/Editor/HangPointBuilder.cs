using System.IO;
using EscapeWithYourFriends.World;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Generates the greybox hang point prefab - the frame the natives string people up on - and
    /// registers it as spawnable.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.HangPointBuilder.BuildHangPoint
    ///
    /// Same reasoning as <see cref="ReviveMachineBuilder"/>, and the same conclusion for the same
    /// reason: the shape is two posts and a beam, but the *socket* is the part that matters, because
    /// the socket's rotation is the only thing that makes hanging look different from carrying. That
    /// is a number somebody will tune by feel, and tuning it in a generator produces a diff rather
    /// than a binary.
    ///
    /// Networked and spawned rather than part of the village greybox, for the reason the Revive
    /// Machine is: it is a machine with behaviour and a replicated occupant, not scenery. Scenery
    /// ships inside <c>NativeVillage.prefab</c>; anything a player can interact with gets its own
    /// entry in the POI catalogue so its position is a number in a text file.
    /// </summary>
    public static class HangPointBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs";
        public const string PrefabPath = PrefabDir + "/HangPoint.prefab";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        public static void BuildHangPoint()
        {
            Directory.CreateDirectory(PrefabDir);

            GameObject root = BuildHierarchy();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[HangPointBuilder] Failed to save {PrefabPath}.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[HangPointBuilder] Built {PrefabPath}.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static GameObject BuildHierarchy()
        {
            var root = new GameObject("HangPoint");

            // Two posts and a crossbeam. The posts carry the colliders, because they are what a
            // player walks into and what PlayerInteractor's spherecast is going to hit - it searches
            // upward from whatever collider it finds, so a hit on a post finds the HangPoint above it.
            Primitive(root.transform, "Post.L", PrimitiveType.Cube,
                      new Vector3(-1.1f, 1.4f, 0f), new Vector3(0.22f, 2.8f, 0.22f), collider: true);
            Primitive(root.transform, "Post.R", PrimitiveType.Cube,
                      new Vector3(1.1f, 1.4f, 0f), new Vector3(0.22f, 2.8f, 0.22f), collider: true);
            Primitive(root.transform, "Beam", PrimitiveType.Cube,
                      new Vector3(0f, 2.75f, 0f), new Vector3(2.6f, 0.2f, 0.2f), collider: true);

            // The rope. Cosmetic, and explicitly collider-free: a body is going to be parented
            // through this space, and a collider here would fight the ragdoll for it.
            Primitive(root.transform, "Rope", PrimitiveType.Cylinder,
                      new Vector3(0f, 2.35f, 0f), new Vector3(0.06f, 0.4f, 0.06f), collider: false);

            // The socket, and the whole point of the prefab. Rotated 180 degrees about Z so the hips
            // are parented upside down: the body's legs go up to the beam and its head hangs at
            // roughly chest height, which is where somebody standing in front of it can reach.
            //
            // Height is picked so a hung player clears the ground with their head - a metre and a
            // half of ragdoll below a socket at 2.3 leaves them swinging just above it.
            Transform socket = Empty(root.transform, "Socket", new Vector3(0f, 2.3f, 0f));
            socket.localRotation = Quaternion.Euler(0f, 0f, 180f);

            root.AddComponent<NetworkObject>();

            var hook = root.AddComponent<HangPoint>();
            SetFields(hook, so =>
            {
                so.FindProperty("_socket").objectReferenceValue = socket;
                so.FindProperty("_claimRadius").floatValue = 14f;
            });

            return root;
        }

        static GameObject Primitive(Transform parent, string name, PrimitiveType shape,
                                    Vector3 localPosition, Vector3 localScale, bool collider)
        {
            GameObject go = GameObject.CreatePrimitive(shape);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;

            if (!collider)
            {
                Collider existing = go.GetComponent<Collider>();
                if (existing != null) Object.DestroyImmediate(existing);
            }

            return go;
        }

        static Transform Empty(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        static void SetFields(Object target, System.Action<SerializedObject> configure)
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
                Debug.LogError("[HangPointBuilder] Saved prefab has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[HangPointBuilder] missing {PrefabObjectsPath}; not registered.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);

            Debug.Log($"[HangPointBuilder] spawnable prefabs now hold {prefabs.GetObjectCount()} object(s).");
        }
    }
}
