using System.Collections.Generic;
using System.IO;
using System.Linq;
using EscapeWithYourFriends.Vehicles;
using EscapeWithYourFriends.World;
using FishNet.Component.Transforming;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Generates the greybox plane and registers it as spawnable. #71, then #72.
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
    ///
    /// **#72 made it a vehicle.** Four seats, a rigidbody, a NetworkTransform and a
    /// <see cref="PlaneController"/>. Two sets of numbers here get moved by feel: where the four of
    /// them sit, and where the three wheels touch. Everything else about how it flies lives in the
    /// controller's serialized fields, where it can be tuned without regenerating the prefab.
    /// </summary>
    public static class PlaneBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        public const string PlanePath = PrefabDir + "/Plane.prefab";

        /// <summary>The one physics material the whole airframe wears. See <see cref="Slippery"/>.</summary>
        const string SkinPath = PrefabDir + "/PlaneSkin.asset";

        /// <summary>Half the fuselage, nose to tail. The whole aircraft is built off this.</summary>
        const float HalfLength = 3.5f;

        /// <summary>Half the span of one wing panel, measured out from the fuselage side.</summary>
        const float WingSpan = 5f;

        /// <summary>
        /// How far the wheels stand the airframe off the ground. Everything cosmetic is placed above
        /// this, so the belly clears the strip.
        /// </summary>
        const float WheelRadius = 0.35f;

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

            var vehicle = saved.GetComponent<Vehicle>();

            Debug.Log($"[PlaneBuilder] Built {PlanePath}: {holes} hole(s) to fill, "
                      + $"{vehicle.SeatCount} seat(s), {saved.GetComponent<Rigidbody>().mass:0}kg.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static GameObject BuildPlane()
        {
            var root = new GameObject("Plane");

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

            // The only three things that touch the strip. Solid, and on a slippery material: rubber
            // friction on a box the size of a fuselage is a plane that cannot reach flying speed.
            Wheel(root, "Gear.Port", new Vector3(-1.1f, WheelRadius, 0.6f));
            Wheel(root, "Gear.Starboard", new Vector3(1.1f, WheelRadius, 0.6f));
            Wheel(root, "Gear.Tail", new Vector3(0f, WheelRadius, -HalfLength + 0.3f));

            // ---- the three holes. The name after the dot is the PlanePart label that fills it.

            Box(root, "Fitted.engine", new Vector3(0f, 1.7f, HalfLength + 0.55f),
                new Vector3(1.2f, 1.2f, 1.1f), solid: true);

            Box(root, "Fitted.wing", new Vector3(WingSpan * 0.5f + 0.6f, 1.6f, 0.4f),
                new Vector3(WingSpan, 0.22f, 1.5f), solid: true);

            Box(root, "Fitted.propeller", new Vector3(0f, 1.7f, HalfLength + 1.2f),
                new Vector3(2.4f, 0.22f, 0.12f), solid: false);

            // T14. After the holes exist, so the model's loose pieces have somewhere to go.
            Dress(root.transform);

            // ---- #72. Four of you get off this island, so four seats.

            var seats = new List<Vehicle.Seat>
            {
                // Order is the seating order: the first person in flies it.
                Seat(root.transform, "Pilot", new Vector3(-0.35f, 2.15f, 1.3f),
                     new Vector3(-1.6f, 0.2f, 1.3f)),

                Seat(root.transform, "Copilot", new Vector3(0.35f, 2.15f, 1.3f),
                     new Vector3(1.6f, 0.2f, 1.3f)),

                Seat(root.transform, "PortRow", new Vector3(-0.35f, 2.15f, -0.3f),
                     new Vector3(-1.6f, 0.2f, -0.3f)),

                Seat(root.transform, "StarboardRow", new Vector3(0.35f, 2.15f, -0.3f),
                     new Vector3(1.6f, 0.2f, -0.3f))
            };

            // The back of the cabin. A body rides here rather than in a seat, same as the boat's
            // transom and the buggy's bed: a corpse in a seat is a seat a survivor cannot have.
            Transform cargo = Empty(root.transform, "CargoSocket", new Vector3(0f, 1.9f, -1.9f));

            // Every collider, not only the three that are meant to touch. The first pass set a
            // material on the wheels alone and the saved prefab came back with m_Material: {fileID: 0}
            // on all seven of them, because a PhysicsMaterial built in memory is dropped when the
            // prefab is serialised - so the whole aeroplane was sitting on Unity's default rubber at
            // 0.6, which is ~6500N of stiction against 9000N of thrust. It would not roll.
            //
            // The strip is flat, but the belly and a wingtip still find the ground on the way in, and
            // an arcade aeroplane that stops dead the moment anything but a wheel touches is not a
            // physics problem the player can read. One material, every box.
            PhysicsMaterial skin = Slippery();

            foreach (Collider piece in root.GetComponentsInChildren<Collider>())
                piece.sharedMaterial = skin;

            root.AddComponent<NetworkObject>();
            root.AddComponent<NetworkTransform>();

            var body = root.AddComponent<Rigidbody>();
            body.mass = 1100f;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            // PlaneController's drag resists translation only, and its wings-levelling spring has no
            // damper of its own. This is that damper; if the wings ever start rocking, raise it.
            body.angularDamping = 1.5f;

            // Low, because a light aircraft that rolls when it is nudged is an aircraft nobody can
            // taxi. The wing and tail boxes are well above it, so it still rights itself in the air.
            body.centerOfMass = new Vector3(0f, 0.9f, 0f);

            var vehicle = root.AddComponent<Vehicle>();
            vehicle.Configure(seats.ToArray(), cargo, "plane");

            root.AddComponent<PlaneController>();

            // #60. A wing at 30 m/s is a blunt instrument, and taxiing through your friends should
            // cost them something.
            root.AddComponent<VehicleImpact>();

            // #73. Fly past the edge of the map and hold it, and everybody arrives on the other
            // island. Same line in the sea as the boat's, because it asks BoatVoyage where it is.
            root.AddComponent<PlaneVoyage>();

            // PlaneAssembly last, so the holes above are already children when its Awake walks them.
            // No VehicleCondition: an aeroplane that runs out of fuel over open water is the
            // run-ending outcome #61 exists to avoid, and the three parts are gate enough.
            root.AddComponent<PlaneAssembly>();

            return root;
        }

        /// <summary>
        /// Quarter turns about the vertical that point the model's nose along +z, the way the
        /// airframe flies. Not seen yet: if the first build shows it flying tail first, this is 2.
        /// </summary>
        const int PlaneTurns = 0;

        /// <summary>The boxes the model replaces. The wheels stay: they are what touches the strip.</summary>
        static readonly string[] Airframe =
        {
            "Fuselage", "Cockpit", "Wing.Port", "Tail.Fin", "Tail.Stabiliser",
            "Fitted.engine", "Fitted.wing", "Fitted.propeller",
        };

        /// <summary>
        /// The catalogue's plane over the whole airframe, kept in shape (T14, docs/ART-PLAN.md).
        ///
        /// A hole has to stay a hole, so any piece of the model named like a part - a propeller, an
        /// engine, the starboard wing - is moved under its <c>Fitted.*</c> box, and
        /// <see cref="PlaneAssembly"/> hides and shows it with the box. A model that is one mesh has
        /// nothing to move: it is drawn whole, and those holes keep their boxes so they still read.
        /// Every collider stays where the greybox put it.
        /// </summary>
        static void Dress(Transform root)
        {
            var pieces = new List<GameObject>();
            Bounds box = default;

            foreach (string name in Airframe)
            {
                Transform piece = root.Find(name);
                if (piece == null) continue;

                // Unit cubes, unrotated, so a box is its position and scale.
                var bounds = new Bounds(piece.localPosition, piece.localScale);
                if (pieces.Count == 0) box = bounds;
                else box.Encapsulate(bounds);
                pieces.Add(piece.gameObject);
            }

            // No catalogue row, no model: T14 keeps the greybox until a CC0 plane is found.
            if (!ArtCatalog.Models.Any(m => m.Id == "Plane")) return;
            if (!ArtDress.FitBox(root, box, "Plane", true, "Art", PlaneTurns)) return;

            Transform art = root.Find("Art");

            // An FBX instance's children cannot be re-parented; the prefab only needs the meshes.
            PrefabUtility.UnpackPrefabInstance(art.GetChild(0).gameObject, PrefabUnpackMode.Completely,
                                               InteractionMode.AutomatedAction);
            var filled = new HashSet<Transform>();

            foreach (Renderer renderer in art.GetComponentsInChildren<Renderer>(true))
            {
                string name = renderer.name.ToLowerInvariant();
                float side = root.InverseTransformPoint(renderer.bounds.center).x;

                string hole = name.Contains("prop") ? "Fitted.propeller"
                    : name.Contains("engine") || name.Contains("motor") ? "Fitted.engine"
                    : name.Contains("wing") && side > 0.5f ? "Fitted.wing"
                    : null;
                if (hole == null) continue;

                // Under an unscaled holder: straight under the stretched box, a blade at 45 degrees
                // would shear.
                Transform target = root.Find(hole);
                Transform holder = target.Find("Art");
                if (holder == null)
                {
                    holder = new GameObject("Art").transform;
                    holder.SetParent(target, false);
                    Vector3 s = target.localScale;
                    holder.localScale = new Vector3(1f / s.x, 1f / s.y, 1f / s.z);
                }

                renderer.transform.SetParent(holder, true);
                filled.Add(target);
            }

            // One mesh, or none of its pieces named: the model would draw the plane whole over its
            // own holes. The greybox reads as unfinished; that plane would not.
            if (filled.Count == 0)
            {
                Object.DestroyImmediate(art.gameObject);
                Debug.LogWarning($"[PlaneBuilder] {ArtCatalog.Find("Plane").File} has no separate propeller, engine "
                                 + "or starboard wing, so it cannot show the holes. Kept the greybox.");
                return;
            }

            // The moved pieces leave the wrapper's LOD group; PlaneAssembly shows and hides them now.
            var group = art.GetComponent<LODGroup>();
            if (group != null)
            {
                LOD[] lods = group.GetLODs();
                group.SetLODs(new[] { new LOD(lods[0].screenRelativeTransitionHeight,
                                              art.GetComponentsInChildren<Renderer>(true)) });
                group.RecalculateBounds();
            }

            foreach (GameObject piece in pieces)
                if (!piece.name.StartsWith("Fitted.") || filled.Contains(piece.transform))
                    ArtDress.Strip(piece);

            Debug.Log($"[PlaneBuilder] Dressed as {ArtCatalog.Find("Plane").File}; "
                      + $"{filled.Count} of 3 holes hold a piece of it.");
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
            go.transform.localScale = new Vector3(WheelRadius * 2f, 0.12f, WheelRadius * 2f);

            // ponytail: a capsule, not a WheelCollider. Three of those want suspension travel, a
            // steering axle and a tuning session, and none of that is visible at greybox; what the
            // aeroplane actually needs from its undercarriage is to hold the belly off the strip and
            // not grip it. Swap them in if taxiing ever has to feel like anything.
        }

        /// <summary>One seat: where the body sits, and where it is put down when it gets out.</summary>
        static Vehicle.Seat Seat(Transform parent, string name, Vector3 anchor, Vector3 door)
            => new()
            {
                Anchor = Empty(parent, $"Seat.{name}", anchor),
                Exit = Empty(parent, $"Exit.{name}", door)
            };

        /// <summary>
        /// The airframe's physics material, as an asset rather than an instance: anything a prefab
        /// points at has to exist on disk, or the reference is a null by the time it is saved.
        /// </summary>
        static PhysicsMaterial Slippery()
        {
            var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(SkinPath);
            bool fresh = material == null;

            if (fresh) material = new PhysicsMaterial("PlaneSkin");

            material.dynamicFriction = 0.05f;
            material.staticFriction = 0.05f;
            material.frictionCombine = PhysicsMaterialCombine.Minimum;

            if (fresh) AssetDatabase.CreateAsset(material, SkinPath);
            else EditorUtility.SetDirty(material);

            return material;
        }

        static Transform Empty(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
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
