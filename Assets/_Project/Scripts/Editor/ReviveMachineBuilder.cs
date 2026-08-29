using System.IO;
using EscapeWithYourFriends.World;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Generates the greybox Revive Machine prefab and registers it as spawnable.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.ReviveMachineBuilder.BuildReviveMachine
    ///
    /// Same reasoning as <see cref="PlayerPrefabBuilder"/>: the shape is a box and a spinning lid, but
    /// the *sockets* are the part that matters — where a body has to be lying, where it hangs while
    /// the machine eats it, and where the living player is put down. Those are numbers that will be
    /// tuned by feel, and tuning them in a generator produces a diff instead of a binary.
    ///
    /// There is deliberately no NetworkTransform on it. The machine never moves; the only thing that
    /// animates is the intake socket, and that is driven on every peer from a replicated tick (see
    /// <see cref="ReviveMachine.Progress"/>). Replicating a transform that every client can compute
    /// for itself would be paying rent on nothing.
    /// </summary>
    public static class ReviveMachineBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs";
        const string PrefabPath = PrefabDir + "/ReviveMachine.prefab";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        public static void BuildReviveMachine()
        {
            Directory.CreateDirectory(PrefabDir);

            GameObject root = BuildHierarchy();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[ReviveMachineBuilder] Failed to save {PrefabPath}.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ReviveMachineBuilder] Built {PrefabPath}.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static GameObject BuildHierarchy()
        {
            var root = new GameObject("ReviveMachine");

            // The thing you aim at. PlayerInteractor searches upward from whatever collider it hits,
            // so the hit box being a child of the networked root is the normal case, not a special one.
            Primitive(root.transform, "Housing", PrimitiveType.Cube,
                      new Vector3(0f, 1.4f, 0f), new Vector3(3f, 2.8f, 2f), collider: true);

            // A lid that spins faster the closer the cycle is to finishing. Purely cosmetic and
            // therefore explicitly collider-free: a spinning collider next to a ragdoll is a
            // catapult, and this machine has enough ways to be funny already.
            Primitive(root.transform, "Rotor", PrimitiveType.Cylinder,
                      new Vector3(0f, 2.9f, 0f), new Vector3(1.2f, 0.25f, 1.2f), collider: false);

            // Marks the mouth. Also collider-free — the body has to pass through here.
            Primitive(root.transform, "Mouth", PrimitiveType.Cube,
                      new Vector3(0f, 1.6f, 1.02f), new Vector3(1.4f, 1.4f, 0.1f), collider: false);

            // Where a corpse has to be lying, or be held. In front, on the floor: the gesture is
            // dropping your friend at the machine's feet, or standing there holding them.
            Transform bay = Empty(root.transform, "Bay", new Vector3(0f, 0f, 2.0f));

            // Where the body hangs while it is swallowed. Starts at the mouth; ReviveMachine drags it
            // in and up over the cycle.
            Transform intake = Empty(root.transform, "Intake", new Vector3(0f, 1.6f, 1.1f));

            // Where the living player is put down: past the bay, facing away, so the first thing they
            // do is walk off rather than immediately clip into the machine that just ate them.
            Transform exit = Empty(root.transform, "Exit", new Vector3(0f, 0f, 3.4f));

            root.AddComponent<NetworkObject>();

            var machine = root.AddComponent<ReviveMachine>();
            SetFields(machine, so =>
            {
                so.FindProperty("_bay").objectReferenceValue = bay;
                so.FindProperty("_intake").objectReferenceValue = intake;
                so.FindProperty("_exit").objectReferenceValue = exit;
                so.FindProperty("_rotor").objectReferenceValue = root.transform.Find("Rotor");
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
                Debug.LogError("[ReviveMachineBuilder] Saved prefab has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[ReviveMachineBuilder] missing {PrefabObjectsPath}; not registered.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);

            Debug.Log($"[ReviveMachineBuilder] spawnable prefabs now hold {prefabs.GetObjectCount()} object(s).");
        }
    }
}
