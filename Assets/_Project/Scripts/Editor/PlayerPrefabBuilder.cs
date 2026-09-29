using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        const string TracerMaterialPath = "Assets/_Project/Art/Stylized/Tracer.mat";

        /// <summary>Additive and vertex-coloured, so a tracer glows, fades along its length, and adds up.</summary>
        static Material TracerMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(TracerMaterialPath);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit")) { name = "Tracer" };
                AssetDatabase.CreateAsset(material, TracerMaterialPath);
            }

            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 2f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            material.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(material);
            return material;
        }

        public static void BuildPlayerPrefab()
        {
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(DataDir);

            WeaponCatalog weapons = WeaponFactory.Catalog();
            TaserDef taser = EnsureAsset<TaserDef>(TaserPath);

            var controls = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputAssetPath);
            if (controls == null)
                Debug.LogWarning($"[PlayerPrefabBuilder] missing {InputAssetPath}; "
                                 + "run InputAssetBuilder.BuildInputAsset first or nobody will move.");

            GameObject root = BuildHierarchy(weapons, taser, controls);

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

        static GameObject BuildHierarchy(WeaponCatalog weapons, TaserDef taser,
                                        InputActionAsset controls)
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

            // What this player can spend. The Revive Machine bills it, the shop takes it, and the
            // casino (#64) will do both.
            var wallet = root.AddComponent<Wallet>();

            // What this player is carrying. Twenty slots and forty kilograms: the slots are what the
            // UI in #46 has to draw, and the weight is what makes a second trip a decision. The
            // catalog is wired in here because a network message carries an index into it, and the
            // index means nothing without the same asset on every peer.
            var inventory = root.AddComponent<Inventory>();
            inventory.Configure(ItemFactory.Catalog(), slots: 20, carryLimit: 40f);

            // How things leave the bag. Items leave from the eye, not the feet, so a thrown bag goes
            // where you were looking; the dropper ignores the thrower's own collider for a moment
            // because that spawn point is inside your capsule.
            var dropper = root.AddComponent<ItemDropper>();
            dropper.Configure(aimOrigin, inventory);

            // Hunger, thirst, stamina and warmth. Added before the motor, which reads stamina to
            // decide whether a sprint may start and reports back whether one is actually happening.
            var survival = root.AddComponent<SurvivalStats>();
            survival.Configure(SurvivalFactory.Ensure());

            // What is currently affecting you. Added before ItemUse, which applies buffs, and before
            // the motor, which reads the speed multiplier off it.
            var buffs = root.AddComponent<BuffState>();
            buffs.Configure(BuffFactory.Catalog());

            var itemUse = root.AddComponent<ItemUse>();
            itemUse.Configure(inventory, buffs);

            // Turning what you carry into something better. The recipe catalog is wired in for the
            // same reason the item catalog is: the wire carries an index into it, and the index means
            // nothing unless every peer holds the identical asset.
            var crafting = root.AddComponent<Crafting>();
            crafting.Configure(RecipeFactory.Catalog(), inventory);

            // The player's side of a trade. Added after both halves it moves, because a shop request
            // spends money and fills a bag in the same server call.
            var trading = root.AddComponent<Trading>();
            trading.Configure(inventory, wallet);

            // One component for every weapon in the game. It reads the selected hotbar slot and
            // asks the catalog what that item is, so there is nothing here to change when a weapon is
            // added - which is the whole of #49's acceptance.
            var weapon = root.AddComponent<Weapon>();
            weapon.Configure(weapons, inventory, aimOrigin);

            // Tracers are drawn from what the server already resolved, so this is a listener and
            // never a source of truth. Unlit and additive-ish: a bullet trail that takes lighting
            // disappears at night, which is the half of the day it matters most.
            //
            // Saved as an asset. It used to be made in memory and handed to the prefab, which does not
            // keep a reference to an object that is not an asset: every tracer anybody ever fired was
            // drawn with no material at all (#207).
            var tracers = root.AddComponent<TracerEffect>();
            tracers.Configure(weapon, TracerMaterial());

            // Turning the weapon you have into the next one in its line. Added after the weapon it
            // reads, because an upgrade carries the loaded magazine across, and after the wallet and
            // the bag because the swap spends both in one server call.
            var upgrading = root.AddComponent<Upgrading>();
            upgrading.Configure(UpgradeFactory.Catalog(), inventory, wallet, weapon);

            // Scopes, firepower and the rest, bought per weapon at the trader. Reads the weapon in
            // hand and spends the wallet, both already on this root.
            root.AddComponent<WeaponMods>();

            // The rod. Added after the bag it fills and after the aim origin it casts from, and kept
            // separate from Weapon on purpose: a rod is not a weapon with a strange swing, it is a
            // whole state machine that owns the Attack button for as long as a line is in the water.
            var fishing = root.AddComponent<Fishing>();
            fishing.Configure(FishFactory.Catalog(), inventory, aimOrigin);

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
                so.FindProperty("_weapon").objectReferenceValue = weapon;
                so.FindProperty("_carry").objectReferenceValue = carrySystem;
                so.FindProperty("_interactor").objectReferenceValue = interactor;
            });

            // Nothing else calls the combat systems: they all expose an owner-side Request method and
            // none of them poll input themselves, so without this component punching, tasing, carrying
            // and throwing are unreachable from a keyboard.
            // Before the input component, which holds a reference to it.
            var rider = root.AddComponent<Vehicles.VehicleRider>();

            var combatInput = root.AddComponent<PlayerCombatInput>();
            SetFields(combatInput, so =>
            {
                so.FindProperty("_input").objectReferenceValue = inputReader;
                so.FindProperty("_weapon").objectReferenceValue = weapon;
                so.FindProperty("_taser").objectReferenceValue = taserWeapon;
                so.FindProperty("_carry").objectReferenceValue = carrySystem;
                so.FindProperty("_interactor").objectReferenceValue = interactor;
                so.FindProperty("_ghost").objectReferenceValue = ghost;
                so.FindProperty("_rescue").objectReferenceValue = rescueSystem;
                so.FindProperty("_dropper").objectReferenceValue = dropper;
                so.FindProperty("_inventory").objectReferenceValue = inventory;
                so.FindProperty("_use").objectReferenceValue = itemUse;
                so.FindProperty("_fishing").objectReferenceValue = fishing;
                so.FindProperty("_rider").objectReferenceValue = rider;
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
                so.FindProperty("_weapon").objectReferenceValue = weapon;
                so.FindProperty("_buffs").objectReferenceValue = buffs;
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

            // #66's blur. Owner-only and screen-only, so on a headless host and on every peer that
            // is not this player it switches itself off in OnStartClient and costs nothing.
            var drunkVision = root.AddComponent<DrunkVision>();
            SetFields(drunkVision, so => so.FindProperty("_buffs").objectReferenceValue = buffs);

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

            // The Quaternius bodies over the ragdoll (T9), or null and the primitives stay.
            Renderer[] bands = Skin(root, bones);

            // Second to last, so its Awake sweep for renderers finds every body part. With bodies, it
            // tints only their headbands: a colour over a textured body turns the skin with it.
            var identity = root.AddComponent<PlayerIdentity>();
            if (bands != null)
                SetFields(identity, so =>
                {
                    SerializedProperty tinted = so.FindProperty("_tintedRenderers");
                    tinted.arraySize = bands.Length;
                    for (int i = 0; i < bands.Length; i++)
                        tinted.GetArrayElementAtIndex(i).objectReferenceValue = bands[i];
                });

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

            Dress(go.transform, mesh, bone.Name);

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
        /// Paints a bone and hangs the bits that make it read as a person on it (#76).
        ///
        /// The characters stay primitives. A modelled and skinned mesh would mean an armature, an
        /// importer, a retarget and a binary asset nobody in this workflow can look at, to replace a
        /// skeleton that already works with the ragdoll - and the acceptance criterion is "funny at a
        /// glance", which is not a thing a batch job can check anyway. What a batch job *can* do is
        /// the part that was actually missing: a silhouette with hands, boots, a head you can tell the
        /// front of, and colours from the one palette instead of default grey.
        ///
        /// Every detail here is mesh only - its collider is destroyed on the spot - so none of it
        /// changes a mass, a joint limit or how far a body flies when a car hits it.
        ///
        /// Since T9 this is the fallback: when <see cref="Skin"/> finds Quaternius bodies it hides all
        /// of this under one of them, and it is only what you see on a build without the characters.
        /// </summary>
        static void Dress(Transform bone, GameObject mesh, string name)
        {
            mesh.GetComponent<Renderer>().sharedMaterial = Palette.Named(Cloth(name));

            switch (name)
            {
                case "Head":
                    // A nose, because a sphere has no front, and a cap so four identical bodies are
                    // not four identical bodies from behind.
                    Detail(bone, "Nose", new(0f, 0.16f, 0.17f), new(0.07f, 0.07f, 0.10f), "Skin");
                    Detail(bone, "Cap", new(0f, 0.31f, 0f), new(0.30f, 0.08f, 0.30f), "Accent");
                    break;

                case "LowerArm.L":
                    Detail(bone, "Hand.L", new(0.28f, 0f, 0f), new(0.11f, 0.11f, 0.11f), "Skin");
                    break;
                case "LowerArm.R":
                    Detail(bone, "Hand.R", new(-0.28f, 0f, 0f), new(0.11f, 0.11f, 0.11f), "Skin");
                    break;

                case "LowerLeg.L":
                case "LowerLeg.R":
                    Detail(bone, "Foot", new(0f, -0.42f, 0.06f), new(0.13f, 0.07f, 0.23f), "Dark");
                    break;
            }
        }

        /// <summary>
        /// Which physics bone each model bone follows while limp, and the bone each points at. Parents
        /// first. ".L" is paired with Left here and swapped in <see cref="Wear"/> when the model's
        /// left is on the other side - which it is: the rig's ".L" sits at +x with the nose at +z,
        /// which is a person's right.
        ///
        /// The head follows from the neck because that is where the physics head pivots (1.42 m,
        /// the base of the neck), and the model's own head then rides on it as modelled.
        /// </summary>
        static readonly (string Physics, string PhysicsTo, HumanBodyBones Bone, HumanBodyBones To)[] Links =
        {
            ("Hips", "Chest", HumanBodyBones.Hips, HumanBodyBones.Spine),
            ("Chest", "Head", HumanBodyBones.Chest, HumanBodyBones.Neck),
            ("Head", "Head/Mesh", HumanBodyBones.Neck, HumanBodyBones.Head),
            ("UpperArm.L", "LowerArm.L", HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm),
            ("LowerArm.L", "LowerArm.L/Mesh", HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand),
            ("UpperArm.R", "LowerArm.R", HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm),
            ("LowerArm.R", "LowerArm.R/Mesh", HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
            ("UpperLeg.L", "LowerLeg.L", HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg),
            ("LowerLeg.L", "LowerLeg.L/Mesh", HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
            ("UpperLeg.R", "LowerLeg.R", HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg),
            ("LowerLeg.R", "LowerLeg.R/Mesh", HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
        };

        /// <summary>
        /// Dresses the ragdoll in every body <see cref="CharacterArt"/> made (T9). The primitives stop
        /// being drawn and stay everything else - colliders, masses, joints - so no number that tunes
        /// a hit changes. Returns the headbands, which are what the player colour goes on, or null
        /// when there is nothing to wear yet and the primitives remain the character.
        /// </summary>
        static Renderer[] Skin(GameObject root, Dictionary<string, Transform> bones)
        {
            GameObject[] models = CharacterArt.Bodies();
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(CharacterArt.ControllerPath);

            if (models.Length == 0 || controller == null)
            {
                Debug.LogWarning("[PlayerPrefabBuilder] No Quaternius bodies or no animator controller, so the "
                                 + "player stays primitives. Run CharacterArt.Build first (docs/ART-PLAN.md T9).");
                return null;
            }

            var holder = new GameObject("Skin").transform;
            holder.SetParent(root.transform, false);

            var bodies = new List<CharacterSkin.Body>();
            foreach (GameObject model in models)
            {
                CharacterSkin.Body body = Wear(model, holder, bones, controller);
                if (body != null) bodies.Add(body);
            }

            if (bodies.Count == 0)
            {
                Object.DestroyImmediate(holder.gameObject);
                Debug.LogWarning("[PlayerPrefabBuilder] No body could be fitted to the ragdoll; the player stays primitives.");
                return null;
            }

            foreach (Renderer primitive in bones["Hips"].GetComponentsInChildren<Renderer>(true))
                primitive.enabled = false;

            root.AddComponent<CharacterSkin>().Configure(bodies.ToArray());

            Debug.Log($"[PlayerPrefabBuilder] Skinned with {string.Join(", ", bodies.Select(b => b.Root.name))}.");
            return bodies.Select(b => b.Band).ToArray();
        }

        /// <summary>
        /// One body: stood on the player's feet, turned to face +z, scaled so its hips are the
        /// ragdoll's hips, and every link measured at rest. Saved inactive - CharacterSkin switches
        /// on the one the colour slot picks, and RagdollController, which grabs the first active
        /// animator it finds, finds none and leaves the switching to it.
        /// </summary>
        static CharacterSkin.Body Wear(GameObject model, Transform holder, Dictionary<string, Transform> bones,
                                       RuntimeAnimatorController controller)
        {
            GameObject instance = CharacterArt.Put(model, holder);
            Transform body = instance.transform;
            var animator = instance.GetComponent<Animator>();

            Transform Find(HumanBodyBones bone) => CharacterArt.Bone(animator, bone);

            Transform hips = Find(HumanBodyBones.Hips);
            float modelHips = hips != null ? hips.position.y - holder.position.y : 0f;
            if (modelHips <= 0.01f)
            {
                Debug.LogError($"[PlayerPrefabBuilder] {model.name} has no hips above its feet; skipped.");
                Object.DestroyImmediate(instance);
                return null;
            }

            float scale = Mathf.Clamp((bones["Hips"].position.y - holder.position.y) / modelHips, 0.7f, 1.4f);
            body.localScale *= scale;

            // The rig's ".L" is on +x; whichever of the model's arms is there is its partner.
            Transform left = Find(HumanBodyBones.LeftUpperArm);
            bool mirrored = left != null && left.position.x < holder.position.x;

            var links = new List<(Transform Bone, Transform To, Transform Physics, Transform PhysicsTo)>();
            foreach ((string physics, string physicsTo, HumanBodyBones bone, HumanBodyBones to) in Links)
            {
                Transform from = Find(mirrored ? Mirror(bone) : bone);
                Transform end = Find(mirrored ? Mirror(to) : to);
                if (from == null || end == null)
                {
                    Debug.LogError($"[PlayerPrefabBuilder] {model.name}'s avatar maps no {(from == null ? bone : to)}; skipped.");
                    Object.DestroyImmediate(instance);
                    return null;
                }

                links.Add((from, end, PhysicsBone(bones, physics), PhysicsBone(bones, physicsTo)));
            }

            var offsets = new Quaternion[links.Count];
            for (int i = 0; i < links.Count; i++)
            {
                (Transform from, Transform to, Transform physics, Transform physicsTo) = links[i];
                Quaternion align = Quaternion.FromToRotation(to.position - from.position,
                                                             physicsTo.position - physics.position);
                offsets[i] = Quaternion.Inverse(physics.rotation) * (align * from.rotation);
            }

            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            // A ragdoll goes a long way from its root. Bounds that stay on the root cull a body lying
            // three metres from where it was standing.
            SkinnedMeshRenderer[] skinned = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            float span = 2.4f / Mathf.Max(1e-4f, hips.lossyScale.x);
            foreach (SkinnedMeshRenderer mesh in skinned)
            {
                mesh.rootBone = hips;
                mesh.localBounds = new Bounds(Vector3.zero, Vector3.one * span);
                mesh.updateWhenOffscreen = false;
            }

            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
            Renderer band = CharacterArt.Band(Find(HumanBodyBones.Head), skinned, Palette.Named("Accent"), out _);

            instance.SetActive(false);

            return new CharacterSkin.Body
            {
                Root = instance,
                Animator = animator,
                Renderers = renderers,
                Band = band,
                Bones = links.Select(l => l.Bone).ToArray(),
                To = links.Select(l => l.To).ToArray(),
                Physics = links.Select(l => l.Physics).ToArray(),
                PhysicsTo = links.Select(l => l.PhysicsTo).ToArray(),
                Offsets = offsets,
                Hips = hips,
                HipsOffset = bones["Hips"].InverseTransformPoint(hips.position),
            };
        }

        static HumanBodyBones Mirror(HumanBodyBones bone)
        {
            string name = bone.ToString();
            if (name.StartsWith("Left")) return (HumanBodyBones)System.Enum.Parse(typeof(HumanBodyBones), "Right" + name.Substring(4));
            if (name.StartsWith("Right")) return (HumanBodyBones)System.Enum.Parse(typeof(HumanBodyBones), "Left" + name.Substring(5));
            return bone;
        }

        /// <summary>"LowerArm.L/Mesh": a physics bone, or a child of one.</summary>
        static Transform PhysicsBone(Dictionary<string, Transform> bones, string path)
        {
            string[] parts = path.Split('/');
            return parts.Length == 1 ? bones[parts[0]] : bones[parts[0]].Find(parts[1]);
        }

        /// <summary>What a bone is wearing. Skin where skin shows, cloth everywhere else.</summary>
        static string Cloth(string name) => name switch
        {
            "Head" => "Skin",
            "LowerArm.L" or "LowerArm.R" => "Skin",
            "LowerLeg.L" or "LowerLeg.R" => "Dark",
            _ => "Cloth",
        };

        static void Detail(Transform bone, string name, Vector3 at, Vector3 scale, string colour)
        {
            GameObject detail = GameObject.CreatePrimitive(PrimitiveType.Cube);
            detail.name = name;
            detail.transform.SetParent(bone, false);
            detail.transform.localPosition = at;
            detail.transform.localScale = scale;
            detail.GetComponent<Renderer>().sharedMaterial = Palette.Named(colour);

            // Decoration, not geometry. A collider here would change every mass and every impact.
            Object.DestroyImmediate(detail.GetComponent<Collider>());
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
