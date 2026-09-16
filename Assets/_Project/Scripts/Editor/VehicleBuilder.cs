using System.Collections.Generic;
using System.IO;
using EscapeWithYourFriends.Vehicles;
using FishNet.Component.Transforming;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Generates the greybox buggy and registers it as spawnable.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.VehicleBuilder.Build
    ///
    /// The shape is four cylinders and a box; none of that is the point. The *anchors* are: where
    /// four people sit, which way they face, and which patch of ground each of them is put down on
    /// when they get out. Those are numbers that will be moved by feel the first time somebody tries
    /// to drive with a friend's head in the way, and moving them in a generator produces a diff
    /// rather than a binary.
    ///
    /// Seat 0 is the driver, front left. The two rear seats face forward like the front ones rather
    /// than outward, because a passenger looking sideways at speed is a passenger who cannot see the
    /// tree. Exits alternate sides so four people leaving at once do not land inside each other.
    ///
    /// Each wheel is two objects: the cylinder you see, rolled onto its side, and a WheelCollider
    /// standing upright beside it. They cannot share a transform — a WheelCollider suspends along its
    /// own local Y, and the visual's local Y points out of the hub.
    ///
    /// The tuning lives in <see cref="Wheel"/> and in <see cref="Vehicles.CarController"/>; the shape
    /// here only decides where the wheels are and therefore how easily the thing trips over.
    /// </summary>
    public static class VehicleBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs";

        public const string BuggyPath = PrefabDir + "/Buggy.prefab";

        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        /// <summary>Half the track width. Seats and wheels are both placed off it.</summary>
        const float HalfWidth = 0.78f;

        /// <summary>Wheel radius, which is also the height the axles sit at.</summary>
        const float WheelRadius = 0.45f;

        public static void Build()
        {
            Directory.CreateDirectory(PrefabDir);

            GameObject root = BuildBuggy();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, BuggyPath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[VehicleBuilder] Failed to save {BuggyPath}.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var vehicle = saved.GetComponent<Vehicle>();
            Debug.Log($"[VehicleBuilder] Built {BuggyPath}: {vehicle.SeatCount} seat(s), "
                      + $"{saved.GetComponentsInChildren<WheelCollider>().Length} wheel(s), "
                      + $"cargo socket {(vehicle.CarrySocket != null ? "wired" : "MISSING")}.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static GameObject BuildBuggy()
        {
            var root = new GameObject("Buggy");

            // The thing you aim at, and the thing #60 will eventually hit people with. One box: a
            // chassis made of six colliders is six chances for a ragdoll to get wedged in a seam.
            Primitive(root.transform, "Chassis", PrimitiveType.Cube,
                      new Vector3(0f, 0.75f, 0f), new Vector3(1.9f, 0.7f, 3.6f), collider: true);

            // A roll cage you can see over. Cosmetic and collider-free, like the revive machine's
            // rotor and for the same reason: scenery that can push a body is a catapult.
            Primitive(root.transform, "Bar", PrimitiveType.Cube,
                      new Vector3(0f, 1.75f, -0.3f), new Vector3(1.7f, 0.12f, 0.12f), collider: false);

            var wheels = new List<WheelCollider>();
            var visuals = new List<Transform>();

            foreach ((string name, float x, float z) in Wheels())
            {
                GameObject wheel = Primitive(root.transform, name, PrimitiveType.Cylinder,
                                             new Vector3(x, 0.45f, z),
                                             new Vector3(0.45f, 0.16f, 0.45f), collider: false);

                // Cylinders stand up by default and a wheel does not.
                wheel.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);

                visuals.Add(wheel.transform);
                wheels.Add(Wheel(root.transform, name + ".Collider", new Vector3(x, WheelRadius, z)));
            }

            var seats = new List<Vehicle.Seat>();

            // Front pair, then rear pair. Order is the seating order: the first person in drives.
            seats.Add(Seat(root.transform, "Driver", new Vector3(-0.42f, 0.95f, 0.55f),
                           new Vector3(-1.55f, 0.05f, 0.55f)));

            seats.Add(Seat(root.transform, "Shotgun", new Vector3(0.42f, 0.95f, 0.55f),
                           new Vector3(1.55f, 0.05f, 0.55f)));

            seats.Add(Seat(root.transform, "RearLeft", new Vector3(-0.42f, 0.95f, -0.75f),
                           new Vector3(-1.55f, 0.05f, -0.75f)));

            seats.Add(Seat(root.transform, "RearRight", new Vector3(0.42f, 0.95f, -0.75f),
                           new Vector3(1.55f, 0.05f, -0.75f)));

            // The bed. A body rides here rather than in a chair, because a corpse that took a seat
            // somebody could have used would be the single most annoying object in the game.
            Transform cargo = Empty(root.transform, "CargoSocket", new Vector3(0f, 1.15f, -1.55f));

            root.AddComponent<NetworkObject>();

            // Server-driven, like the wildlife: nothing owns the buggy's transform, the host moves it
            // and every client interpolates. Ownership of the *object* follows the driver (see
            // Vehicle.ServerEnter) and changes nothing about that until #58 asks it to.
            root.AddComponent<NetworkTransform>();

            var body = root.AddComponent<Rigidbody>();
            body.mass = 900f;

            // The chassis is drawn every frame and stepped every fixed update, and four riders are
            // glued to it. Without interpolation the seats stutter at whatever the physics rate is,
            // and the stutter is much easier to see on a passenger's head than on the car.
            body.interpolation = RigidbodyInterpolation.Interpolate;

            // #60. At 22 m/s a discrete step moves the chassis 44cm, which is wider than the person
            // standing in front of it: a car that tunnels through its victim never reports the
            // collision the run-over is built on.
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var vehicle = root.AddComponent<Vehicle>();
            vehicle.Configure(seats.ToArray(), cargo, "buggy");

            var car = root.AddComponent<CarController>();
            car.Configure(wheels.ToArray(), visuals.ToArray());

            // #60. Running a friend over is the point of owning a car.
            root.AddComponent<VehicleImpact>();

            return root;
        }

        /// <summary>
        /// One WheelCollider. Separate from the cylinder you can see, because the visual is rolled
        /// ninety degrees onto its side and a WheelCollider's suspension runs down its own local Y —
        /// putting both on one transform would suspend the car sideways.
        ///
        /// The tuning is the arcade brief in five numbers. Soft, long suspension so it wallows and
        /// leans; stiffness well above 1 on both friction curves so it grips like a go-kart and turns
        /// far sharper than its mass deserves. The combination is a car that corners flat out and
        /// then trips over itself on a rock, which is the joke.
        /// </summary>
        static WheelCollider Wheel(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;

            var wheel = go.AddComponent<WheelCollider>();
            wheel.radius = WheelRadius;
            wheel.mass = 30f;
            wheel.wheelDampingRate = 0.5f;

            wheel.suspensionDistance = 0.3f;
            wheel.forceAppPointDistance = 0.15f;

            JointSpring spring = wheel.suspensionSpring;
            spring.spring = 32000f;
            spring.damper = 4200f;
            spring.targetPosition = 0.5f;
            wheel.suspensionSpring = spring;

            WheelFrictionCurve forward = wheel.forwardFriction;
            forward.extremumSlip = 0.4f;
            forward.extremumValue = 1f;
            forward.asymptoteSlip = 0.8f;
            forward.asymptoteValue = 0.6f;
            forward.stiffness = 2.2f;
            wheel.forwardFriction = forward;

            WheelFrictionCurve sideways = wheel.sidewaysFriction;
            sideways.extremumSlip = 0.25f;
            sideways.extremumValue = 1f;
            sideways.asymptoteSlip = 0.6f;
            sideways.asymptoteValue = 0.75f;
            sideways.stiffness = 2.6f;
            wheel.sidewaysFriction = sideways;

            return wheel;
        }

        static IEnumerable<(string Name, float X, float Z)> Wheels()
        {
            yield return ("WheelFL", -HalfWidth, 1.25f);
            yield return ("WheelFR", HalfWidth, 1.25f);
            yield return ("WheelRL", -HalfWidth, -1.25f);
            yield return ("WheelRR", HalfWidth, -1.25f);
        }

        /// <summary>
        /// One seat: where the body sits and where it is put down. Both face the vehicle's forward,
        /// so a rider glued to the anchor is looking out of the windscreen and a rider dumped at the
        /// door is looking the way the car is pointed rather than into its side.
        /// </summary>
        static Vehicle.Seat Seat(Transform parent, string name, Vector3 anchor, Vector3 door)
            => new()
            {
                Anchor = Empty(parent, $"Seat.{name}", anchor),
                Exit = Empty(parent, $"Exit.{name}", door)
            };

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

        /// <summary>Same reasoning as PlayerPrefabBuilder.RegisterSpawnable; see the note there.</summary>
        static void RegisterSpawnable(NetworkObject networkObject)
        {
            if (networkObject == null)
            {
                Debug.LogError("[VehicleBuilder] Saved prefab has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[VehicleBuilder] missing {PrefabObjectsPath}; not registered.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);

            Debug.Log($"[VehicleBuilder] spawnable prefabs now hold {prefabs.GetObjectCount()} object(s).");
        }
    }
}
