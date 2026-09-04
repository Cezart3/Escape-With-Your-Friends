using System.Collections.Generic;
using System.IO;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.World;
using FishNet.Component.Transforming;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Generates the greybox player prefab, its weapon data assets, and registers the prefab with
    /// FishNet so it can be spawned.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.PlayerPrefabBuilder.BuildPlayerPrefab
    ///
    /// Written as a generator rather than assembled by hand because a ragdoll is a dozen bodies, a
    /// dozen colliders and eleven joints, and every one of them has numbers that will be tuned. Tuning
    /// a prefab by clicking produces a binary nobody can review; tuning these constants produces a
    /// diff. It also means the whole rig can be rebuilt from a terminal after any change to the
    /// components it wires together.
    ///
    /// The proportions are deliberately wrong — big head, thin limbs — because the character art is a
    /// joke and the silhouette has to read at a distance. Real models replace the primitives later
    /// (#8); the skeleton and the wiring stay.
    /// </summary>
    public static class PlayerPrefabBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs";
        const string PrefabPath = PrefabDir + "/Player.prefab";

        const string DataDir = "Assets/_Project/Data";
        const string FistsPath = DataDir + "/Fists.asset";
        const string TaserPath = DataDir + "/Taser.asset";

        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        const string InputAssetPath = "Assets/_Project/Input/PlayerControls.inputactions";

        /// <summary>Roughly a person: 1.75m to the top of the head.</summary>
        const float ControllerHeight = 1.75f;
        const float ControllerRadius = 0.3f;

        /// <summary>
        /// One bone of the physics skeleton. The bone transform sits at the joint pivot and carries the
        /// rigidbody; the visible mesh hangs off it as an unrotated child, so a scaled body part never
        /// scales the bones below it.
        /// </summary>
        readonly struct Bone
        {
            public readonly string Name;
            public readonly string Parent;
            public readonly Vector3 Pivot;
            public readonly PrimitiveType Shape;
            public readonly Vector3 MeshOffset;
            public readonly Vector3 MeshEuler;
            public readonly Vector3 MeshScale;
            public readonly float Mass;

            public Bone(string name, string parent, Vector3 pivot, PrimitiveType shape,
                        Vector3 meshOffset, Vector3 meshEuler, Vector3 meshScale, float mass)
            {
                Name = name;
                Parent = parent;
                Pivot = pivot;
                Shape = shape;
                MeshOffset = meshOffset;
                MeshEuler = meshEuler;
                MeshScale = meshScale;
                Mass = mass;
            }
        }

        /// <summary>
        /// The skeleton, in build order: a bone is always listed after the bone it hangs from.
        /// Masses add up to about 56kg — light for a human, which makes hits send people further.
        /// </summary>
        static readonly Bone[] Skeleton =
        {
            new("Hips", null, new(0f, 0.95f, 0f), PrimitiveType.Cube,
                Vector3.zero, Vector3.zero, new(0.34f, 0.24f, 0.22f), 12f),

            new("Chest", "Hips", new(0f, 1.12f, 0f), PrimitiveType.Cube,
                new(0f, 0.13f, 0f), Vector3.zero, new(0.38f, 0.34f, 0.24f), 14f),

            // Oversized on purpose. The head is what you aim at and what you watch fly.
            new("Head", "Chest", new(0f, 1.42f, 0f), PrimitiveType.Sphere,
                new(0f, 0.16f, 0f), Vector3.zero, new(0.32f, 0.32f, 0.32f), 5f),

            new("UpperArm.L", "Chest", new(0.20f, 1.36f, 0f), PrimitiveType.Capsule,
                new(0.13f, 0f, 0f), new(0f, 0f, 90f), new(0.11f, 0.13f, 0.11f), 2f),
            new("LowerArm.L", "UpperArm.L", new(0.46f, 1.36f, 0f), PrimitiveType.Capsule,
                new(0.13f, 0f, 0f), new(0f, 0f, 90f), new(0.10f, 0.13f, 0.10f), 1.5f),

            new("UpperArm.R", "Chest", new(-0.20f, 1.36f, 0f), PrimitiveType.Capsule,
                new(-0.13f, 0f, 0f), new(0f, 0f, 90f), new(0.11f, 0.13f, 0.11f), 2f),
            new("LowerArm.R", "UpperArm.R", new(-0.46f, 1.36f, 0f), PrimitiveType.Capsule,
                new(-0.13f, 0f, 0f), new(0f, 0f, 90f), new(0.10f, 0.13f, 0.10f), 1.5f),

            new("UpperLeg.L", "Hips", new(0.11f, 0.90f, 0f), PrimitiveType.Capsule,
                new(0f, -0.21f, 0f), Vector3.zero, new(0.12f, 0.21f, 0.12f), 5f),
            new("LowerLeg.L", "UpperLeg.L", new(0.11f, 0.48f, 0f), PrimitiveType.Capsule,
                new(0f, -0.22f, 0f), Vector3.zero, new(0.11f, 0.22f, 0.11f), 4f),

            new("UpperLeg.R", "Hips", new(-0.11f, 0.90f, 0f), PrimitiveType.Capsule,
                new(0f, -0.21f, 0f), Vector3.zero, new(0.12f, 0.21f, 0.12f), 5f),
            new("LowerLeg.R", "UpperLeg.R", new(-0.11f, 0.48f, 0f), PrimitiveType.Capsule,
                new(0f, -0.22f, 0f), Vector3.zero, new(0.11f, 0.22f, 0.11f), 4f),
        };

        public static void BuildPlayerPrefab()
        {
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(DataDir);

            MeleeWeaponDef fists = EnsureAsset<MeleeWeaponDef>(FistsPath);
            TaserDef taser = EnsureAsset<TaserDef>(TaserPath);

            var controls = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputAssetPath);
            if (controls == null)
                Debug.LogWarning($"[PlayerPrefabBuilder] missing {InputAssetPath}; "
                                 + "run InputAssetBuilder.BuildInputAsset first or nobody will move.");

            GameObject root = BuildHierarchy(fists, taser, controls);

            // Overwrite rather than merge. This prefab is generated output; anything edited into it by
            // hand would be lost on the next run anyway, so losing it loudly is better.
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[PlayerPrefabBuilder] Failed to save {PrefabPath}.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[PlayerPrefabBuilder] Built {PrefabPath}: {Skeleton.Length} bones, "
                      + $"{Skeleton.Length - 1} joints.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static GameObject BuildHierarchy(MeleeWeaponDef fists, TaserDef taser, InputActionAsset controls)
        {
            var root = new GameObject("Player");

            var bones = new Dictionary<string, Transform>();
            foreach (Bone bone in Skeleton)
                bones[bone.Name] = BuildBone(bone, root.transform, bones);

            // Eye height. Everything that aims — melee, taser, later the camera — starts here rather
            // than at the feet, so the server can validate a shot against the same origin the client
            // used. See AimValidation.
            var aimOrigin = new GameObject("AimOrigin").transform;
            aimOrigin.SetParent(root.transform, false);
            aimOrigin.localPosition = new Vector3(0f, 1.55f, 0f);

            // Where a carried body hangs. In front and low, so the carrier can still see.
            var carrySocket = new GameObject("CarrySocket").transform;
            carrySocket.SetParent(root.transform, false);
            carrySocket.localPosition = new Vector3(0f, 1.0f, 0.6f);

            var controller = root.AddComponent<CharacterController>();
            controller.height = ControllerHeight;
            controller.radius = ControllerRadius;
            controller.center = new Vector3(0f, ControllerHeight * 0.5f, 0f);

            var networkObject = root.AddComponent<NetworkObject>();

            // Spectators see this body through the NetworkTransform; the owner never does.
            var networkTransform = root.AddComponent<NetworkTransform>();

            ConfigurePrediction(networkObject, networkTransform);

            // RagdollController first: StunState, ShockState and Carryable all require it, and adding
            // them before it would make Unity add a second, unconfigured one.
            var ragdoll = root.AddComponent<RagdollController>();
            SetFields(ragdoll, so =>
            {
                so.FindProperty("_hipBone").objectReferenceValue = bones["Hips"];
                so.FindProperty("_standingCollider").objectReferenceValue = controller;
                // _disableWhileRagdolled stays empty until there is a movement script to disable.
            });

            var health = root.AddComponent<Health>();
            var stun = root.AddComponent<StunState>();
            var shock = root.AddComponent<ShockState>();
            root.AddComponent<Carryable>();

            var carrySystem = root.AddComponent<CarrySystem>();
            SetFields(carrySystem, so =>
            {
                so.FindProperty("_carrySocket").objectReferenceValue = carrySocket;
                so.FindProperty("_aimOrigin").objectReferenceValue = aimOrigin;
            });

            // Same cast as carrying, aimed at machines instead of bodies. Built before the input
            // component because that one has to prefer it.
            // Both halves of the rescue (#105). Rescuable is what a teammate aims at; RescueSystem
            // is the hold this player runs when they are the one doing the helping. Every player is
            // both, because everybody ends up on the floor eventually.
            var rescuable = root.AddComponent<Rescuable>();
            SetFields(rescuable, so =>
            {
                so.FindProperty("_health").objectReferenceValue = health;
            });

            var rescueSystem = root.AddComponent<RescueSystem>();
            SetFields(rescueSystem, so =>
            {
                so.FindProperty("_health").objectReferenceValue = health;
                so.FindProperty("_stun").objectReferenceValue = stun;
            });

            var interactor = root.AddComponent<PlayerInteractor>();
            SetFields(interactor, so =>
            {
                so.FindProperty("_aimOrigin").objectReferenceValue = aimOrigin;
            });

            // What this player can spend. The Revive Machine bills it; the shop and the casino
            // (#47, #77) will too.
            root.AddComponent<Wallet>();

            // What this player is carrying. Twenty slots and forty kilograms: the slots are what the
            // UI in #46 has to draw, and the weight is what makes a second trip a decision. The
            // catalog is wired in here because a network message carries an index into it, and the
            // index means nothing without the same asset on every peer.
            var inventory = root.AddComponent<Inventory>();
            inventory.Configure(ItemFactory.Catalog(), slots: 20, carryLimit: 40f);

            var melee = root.AddComponent<MeleeAttack>();
            SetFields(melee, so =>
            {
                so.FindProperty("_aimOrigin").objectReferenceValue = aimOrigin;
                so.FindProperty("_fists").objectReferenceValue = fists;
            });

            var taserWeapon = root.AddComponent<TaserWeapon>();
            SetFields(taserWeapon, so =>
            {
                so.FindProperty("_aimOrigin").objectReferenceValue = aimOrigin;
                so.FindProperty("_taser").objectReferenceValue = taser;
            });

            var inputReader = root.AddComponent<PlayerInputReader>();
            SetFields(inputReader, so =>
            {
                so.FindProperty("_actions").objectReferenceValue = controls;
            });

            // What the dead do instead of nothing. Added before the death camera so its ghost root
            // exists by the time that camera looks for something to follow, and before the input
            // component because that one has to route Attack through it while the body is a corpse.
            var ghost = root.AddComponent<GhostController>();
            SetFields(ghost, so =>
            {
                so.FindProperty("_health").objectReferenceValue = health;
                so.FindProperty("_ragdoll").objectReferenceValue = ragdoll;
                so.FindProperty("_input").objectReferenceValue = inputReader;
                so.FindProperty("_melee").objectReferenceValue = melee;
                so.FindProperty("_carry").objectReferenceValue = carrySystem;
                so.FindProperty("_interactor").objectReferenceValue = interactor;
            });

            // Nothing else calls the combat systems: they all expose an owner-side Request method and
            // none of them poll input themselves, so without this component punching, tasing, carrying
            // and throwing are unreachable from a keyboard.
            var combatInput = root.AddComponent<PlayerCombatInput>();
            SetFields(combatInput, so =>
            {
                so.FindProperty("_input").objectReferenceValue = inputReader;
                so.FindProperty("_melee").objectReferenceValue = melee;
                so.FindProperty("_taser").objectReferenceValue = taserWeapon;
                so.FindProperty("_carry").objectReferenceValue = carrySystem;
                so.FindProperty("_interactor").objectReferenceValue = interactor;
                so.FindProperty("_ghost").objectReferenceValue = ghost;
                so.FindProperty("_rescue").objectReferenceValue = rescueSystem;
            });

            var motor = root.AddComponent<PlayerMotor>();
            SetFields(motor, so =>
            {
                so.FindProperty("_input").objectReferenceValue = inputReader;
                so.FindProperty("_standHeight").floatValue = ControllerHeight;
            });

            // The camera is built after the motor because it reads from it, and before the ragdoll
            // list because it must *not* be in that list: the camera has to keep working while limp —
            // watching yourself get dragged around is the point — so it follows the head bone instead
            // of the body root and never stops updating.
            var cameraRig = root.AddComponent<PlayerCameraRig>();
            SetFields(cameraRig, so =>
            {
                so.FindProperty("_input").objectReferenceValue = inputReader;
                so.FindProperty("_motor").objectReferenceValue = motor;
                so.FindProperty("_ragdoll").objectReferenceValue = ragdoll;
                so.FindProperty("_shock").objectReferenceValue = shock;
                so.FindProperty("_health").objectReferenceValue = health;
                so.FindProperty("_headBone").objectReferenceValue = bones["Head"];
                so.FindProperty("_aimOrigin").objectReferenceValue = aimOrigin;
            });

            // Death pulls the view out to third person. Built after the rig because it has to beat
            // the rig's priority, and separate from it because a second camera and a blend is how
            // every later view steal works too — the ghost, the vehicle chase, the revive machine.
            var deathCamera = root.AddComponent<DeathCamera>();
            SetFields(deathCamera, so =>
            {
                so.FindProperty("_health").objectReferenceValue = health;
                so.FindProperty("_ragdoll").objectReferenceValue = ragdoll;
                so.FindProperty("_ghost").objectReferenceValue = ghost;
            });

            // Now that the motor exists it can be switched off while the body is limp. The reader is
            // deliberately not in this list: disabling it releases the cursor and tears down its
            // action instance, and nothing would bind it again after standing up.
            SetFields(ragdoll, so =>
            {
                SerializedProperty disabled = so.FindProperty("_disableWhileRagdolled");
                disabled.arraySize = 1;
                disabled.GetArrayElementAtIndex(0).objectReferenceValue = motor;
            });

            // Server-side net for bodies that end up outside the world. See #110.
            root.AddComponent<FallGuard>();

            // Second to last, so its Awake sweep for renderers finds every body part.
            var identity = root.AddComponent<PlayerIdentity>();

            // After the identity, because an abandoned body has to unregister itself from the roster
            // and wants the reference rather than a GetComponent at runtime.
            var persistence = root.AddComponent<BodyPersistence>();
            SetFields(persistence, so =>
            {
                so.FindProperty("_health").objectReferenceValue = health;
                so.FindProperty("_identity").objectReferenceValue = identity;
            });

            // Last: it looks up Health in Awake and builds its playback object under the root, so it
            // wants every other component already there.
            root.AddComponent<VoiceChat>();

            return root;
        }

        /// <summary>
        /// Turns on client prediction for this body.
        ///
        /// State forwarding is switched off. With it on, FishNet would replicate the owner's predicted
        /// states to spectators, which needs the graphical mesh detached under a smoothed child object
        /// — and the mesh here is the ragdoll rig, which cannot be moved out from under the joints it
        /// is wired to. With it off, FishNet reconfigures the NetworkTransform to server-authoritative
        /// and stops sending it to the owner, so the owner is driven by prediction alone and everyone
        /// else by ordinary interpolation. See NetworkObject.Prediction.cs, line 298.
        /// </summary>
        static void ConfigurePrediction(NetworkObject networkObject, NetworkTransform networkTransform)
        {
            SetFields(networkObject, so =>
            {
                so.FindProperty("_enablePrediction").boolValue = true;
                // _predictionType stays Other: this is a CharacterController, not a rigidbody.
                so.FindProperty("_enableStateForwarding").boolValue = false;
                so.FindProperty("_networkTransform").objectReferenceValue = networkTransform;
            });
        }

        static Transform BuildBone(Bone bone, Transform root, Dictionary<string, Transform> built)
        {
            var go = new GameObject(bone.Name);
            Transform parent = bone.Parent == null ? root : built[bone.Parent];

            go.transform.SetParent(parent, false);
            go.transform.position = root.position + bone.Pivot;
            go.transform.rotation = root.rotation;

            var body = go.AddComponent<Rigidbody>();
            body.mass = bone.Mass;
            body.isKinematic = true; // RagdollController flips this; a body that starts limp falls over.

            // Physics runs at 50Hz and the screen does not. Without interpolation a ragdoll steps
            // between fixed frames, which is invisible on a body across the room and is the whole
            // picture when the camera is riding the head bone. See PlayerCameraRig.
            body.interpolation = RigidbodyInterpolation.Interpolate;

            // The mesh carries the collider. Unity attaches a collider to the nearest rigidbody above
            // it, so a scaled child collider still belongs to this bone.
            GameObject mesh = GameObject.CreatePrimitive(bone.Shape);
            mesh.name = "Mesh";
            mesh.transform.SetParent(go.transform, false);
            mesh.transform.localPosition = bone.MeshOffset;
            mesh.transform.localRotation = Quaternion.Euler(bone.MeshEuler);
            mesh.transform.localScale = bone.MeshScale;

            if (bone.Parent == null) return go.transform;

            var joint = go.AddComponent<CharacterJoint>();
            joint.connectedBody = built[bone.Parent].GetComponent<Rigidbody>();
            joint.autoConfigureConnectedAnchor = false;
            joint.anchor = Vector3.zero;
            joint.connectedAnchor = built[bone.Parent].InverseTransformPoint(go.transform.position);

            // Generous limits. A precisely jointed ragdoll looks like a corpse; a loose one looks
            // like someone having a bad day, which is the whole product.
            joint.lowTwistLimit = new SoftJointLimit { limit = -25f };
            joint.highTwistLimit = new SoftJointLimit { limit = 25f };
            joint.swing1Limit = new SoftJointLimit { limit = 50f };
            joint.swing2Limit = new SoftJointLimit { limit = 30f };
            joint.enablePreprocessing = false; // Preprocessing lets joints explode under big impulses.

            return go.transform;
        }

        /// <summary>
        /// Writes private serialized fields. The components keep their fields private on purpose —
        /// nothing at runtime should be able to re-point a carry socket — so the generator edits them
        /// the same way the inspector does.
        /// </summary>
        static void SetFields(Object target, System.Action<SerializedObject> configure)
        {
            var so = new SerializedObject(target);
            configure(so);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static T EnsureAsset<T>(string path) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null) return existing;

            // Created with defaults. Balance numbers are a playtesting decision, not a build-time one,
            // so this only guarantees the asset exists and is referenced.
            var asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            Debug.Log($"[PlayerPrefabBuilder] created {path} with default values.");
            return asset;
        }

        /// <summary>
        /// FishNet can only spawn a prefab that is in the spawnable list, and the list is what assigns
        /// the prefab id both peers use to agree on what was spawned. Its auto-scan runs on asset
        /// import, which does not reliably happen inside a single batchmode invocation, so register
        /// explicitly.
        /// </summary>
        static void RegisterSpawnable(NetworkObject networkObject)
        {
            if (networkObject == null)
            {
                Debug.LogError("[PlayerPrefabBuilder] Saved prefab has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[PlayerPrefabBuilder] missing {PrefabObjectsPath}; prefab not registered.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);

            Debug.Log($"[PlayerPrefabBuilder] spawnable prefabs now hold {prefabs.GetObjectCount()} object(s).");
        }
    }
}
