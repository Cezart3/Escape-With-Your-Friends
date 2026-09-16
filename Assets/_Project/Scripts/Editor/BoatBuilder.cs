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
    /// Generates the greybox boat and registers it as spawnable.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.BoatBuilder.Build
    ///
    /// Same shape of file as <see cref="VehicleBuilder"/> and for the same reason: the boxes are not
    /// the point, the anchors are. Two sets of numbers here get moved by feel and both produce a diff
    /// rather than a binary — where four people sit, and where the hull is tested against the water.
    ///
    /// The float layout is the one that matters. Six points in three pairs: without a bow pair and a
    /// stern pair the hull cannot tell pitch from roll, and more than eight would be paying for
    /// detail the sea does not have, since its shortest wave is seventeen metres long.
    ///
    /// **Doors open onto the deck, not into the sea.** A car puts people down beside itself; a boat
    /// doing that drowns its own passengers. The exits sit inside the hull footprint, so getting out
    /// means standing up on the boat.
    /// </summary>
    public static class BoatBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs";

        public const string BoatPath = PrefabDir + "/Boat.prefab";

        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        /// <summary>Half the beam. Seats and floats are both placed off it.</summary>
        const float HalfBeam = 1.2f;

        /// <summary>Half the length, bow to stern.</summary>
        const float HalfLength = 3f;

        /// <summary>Height of the deck above the keel. The hull box is this tall.</summary>
        const float DeckHeight = 1f;

        public static void Build()
        {
            Directory.CreateDirectory(PrefabDir);

            GameObject root = BuildBoat();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, BoatPath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[BoatBuilder] Failed to save {BoatPath}.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var vehicle = saved.GetComponent<Vehicle>();
            var boat = saved.GetComponent<BoatController>();

            Debug.Log($"[BoatBuilder] Built {BoatPath}: {vehicle.SeatCount} seat(s), "
                      + $"{boat.FloatCount} float(s), "
                      + $"cargo socket {(vehicle.CarrySocket != null ? "wired" : "MISSING")}.");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static GameObject BuildBoat()
        {
            var root = new GameObject("Boat");

            // One box for the whole hull, like the buggy's chassis: a hull made of six colliders is
            // six seams for a ragdoll to get wedged in.
            Primitive(root.transform, "Hull", PrimitiveType.Cube,
                      new Vector3(0f, DeckHeight * 0.5f, 0f),
                      new Vector3(HalfBeam * 2f, DeckHeight, HalfLength * 2f), collider: true);

            // A bow and a console, both cosmetic and both collider-free. Scenery that can push a body
            // is a catapult, and a catapult on a boat is a man overboard.
            GameObject bow = Primitive(root.transform, "Bow", PrimitiveType.Cube,
                                       new Vector3(0f, DeckHeight * 0.55f, HalfLength + 0.5f),
                                       new Vector3(HalfBeam * 1.4f, DeckHeight * 0.8f, 1.4f),
                                       collider: false);
            bow.transform.localRotation = Quaternion.Euler(-18f, 0f, 0f);

            Primitive(root.transform, "Console", PrimitiveType.Cube,
                      new Vector3(-0.45f, DeckHeight + 0.55f, 1.6f),
                      new Vector3(0.7f, 1.1f, 0.25f), collider: false);

            var seats = new List<Vehicle.Seat>();

            // Front pair, then rear pair. Order is the seating order: the first person in drives.
            seats.Add(Seat(root.transform, "Helm", new Vector3(-0.45f, 0.9f, 1f),
                           new Vector3(-0.7f, DeckHeight + 0.15f, 0.3f)));

            seats.Add(Seat(root.transform, "Shotgun", new Vector3(0.45f, 0.9f, 1f),
                           new Vector3(0.7f, DeckHeight + 0.15f, 0.3f)));

            seats.Add(Seat(root.transform, "PortBench", new Vector3(-0.45f, 0.9f, -0.6f),
                           new Vector3(-0.7f, DeckHeight + 0.15f, -1.3f)));

            seats.Add(Seat(root.transform, "StarboardBench", new Vector3(0.45f, 0.9f, -0.6f),
                           new Vector3(0.7f, DeckHeight + 0.15f, -1.3f)));

            // The transom. A body rides here rather than in a seat, for the same reason it rides in
            // the buggy's bed: a corpse taking a seat somebody could have used is the most annoying
            // object in the game.
            Transform cargo = Empty(root.transform, "CargoSocket",
                                    new Vector3(0f, DeckHeight + 0.25f, -2.1f));

            root.AddComponent<NetworkObject>();
            root.AddComponent<NetworkTransform>();

            var body = root.AddComponent<Rigidbody>();
            body.mass = 600f;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            // The hull drag in BoatController is linear and resists *translation* only. Without this
            // a boat given a touch of rudder and then left alone keeps rotating forever, because
            // nothing out here opposes yaw.
            body.angularDamping = 1.5f;

            var vehicle = root.AddComponent<Vehicle>();
            vehicle.Configure(seats.ToArray(), cargo, "boat");

            var boat = root.AddComponent<BoatController>();
            boat.Configure(Floats());

            return root;
        }

        /// <summary>
        /// Where the hull meets the water: three pairs along the keel. They sit at the very bottom of
        /// the hull box, so "how deep is this point" is also "how deep is the hull here".
        /// </summary>
        static Vector3[] Floats()
        {
            const float x = HalfBeam * 0.75f;

            return new[]
            {
                new Vector3(-x, 0f, HalfLength * 0.8f),
                new Vector3(x, 0f, HalfLength * 0.8f),
                new Vector3(-x, 0f, 0f),
                new Vector3(x, 0f, 0f),
                new Vector3(-x, 0f, -HalfLength * 0.8f),
                new Vector3(x, 0f, -HalfLength * 0.8f)
            };
        }

        /// <summary>
        /// One seat: where the body sits and where it is put down. The exit is on the deck rather
        /// than beside the hull — see the note on the class.
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
                Debug.LogError("[BoatBuilder] Saved prefab has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[BoatBuilder] missing {PrefabObjectsPath}; not registered.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);

            Debug.Log($"[BoatBuilder] spawnable prefabs now hold {prefabs.GetObjectCount()} object(s).");
        }
    }
}
