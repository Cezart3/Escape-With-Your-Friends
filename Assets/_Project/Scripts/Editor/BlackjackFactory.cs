using System.Collections.Generic;
using System.IO;
using EscapeWithYourFriends.Casino;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The blackjack table, against the casino's left wall.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath .
    ///     -executeMethod EscapeWithYourFriends.EditorTools.BlackjackFactory.Build
    ///
    /// Front to +z, where the four seats are; the dealer's side is -z, against the wall. Every seat
    /// has a big bet button at the rail and four small ones behind it - hit, stand, double, split -
    /// each its own nested <see cref="NetworkObject"/>. The cards are pre-placed and hidden, eight a
    /// hand, each with a face plate the table dresses in <c>CardFaces</c> at runtime. The prefab
    /// wears palette materials only, so it adds nothing to <c>LookTest</c>'s budget; the card atlas
    /// is one material more, and only once a hand is dealt.
    ///
    /// Always rebuilds, like <c>SlotFactory</c>: nothing here is dressed by hand.
    /// </summary>
    public static class BlackjackFactory
    {
        const string PrefabDir = "Assets/_Project/Prefabs/Stations";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        internal const string TablePath = PrefabDir + "/BlackjackTable.prefab";

        const float Top = 0.92f;
        const float SeatStep = 0.44f;

        static readonly Vector3 CardSize = new(0.06f, 0.004f, 0.09f);

        public static void Build()
        {
            Directory.CreateDirectory(PrefabDir);

            bool built = Table();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(built ? $"[BlackjackFactory] Built {TablePath}." : $"[BlackjackFactory] Failed to build {TablePath}.");

            if (Application.isBatchMode) EditorApplication.Exit(built ? 0 : 1);
        }

        static bool Table()
        {
            var root = new GameObject("BlackjackTable");
            Transform t = root.transform;

            Block(t, "Base", new Vector3(0f, 0.45f, 0f), new Vector3(1.9f, 0.9f, 0.9f), "WoodDark", solid: true);
            Block(t, "Felt", new Vector3(0f, Top - 0.01f, 0f), new Vector3(1.8f, 0.02f, 0.8f), "Felt", solid: false);
            Block(t, "Rail", new Vector3(0f, Top + 0.01f, 0.43f), new Vector3(1.9f, 0.05f, 0.06f), "WoodDark", solid: false);
            Block(t, "Tray", new Vector3(0f, Top + 0.02f, -0.34f), new Vector3(0.44f, 0.04f, 0.1f), "Gold", solid: false);

            var cards = new List<Renderer>();
            var faces = new List<Renderer>();

            for (int hand = 0; hand <= BlackjackMath.Dealer; hand++)
            {
                bool dealer = hand == BlackjackMath.Dealer;
                int seat = hand / 2;

                // A seat's own hand left of its centre, the split right; the dealer's in a row, unfanned.
                Vector3 first = dealer
                    ? new Vector3(-0.245f, Top, -0.2f)
                    : new Vector3((seat - 1.5f) * SeatStep + (hand % 2 == 0 ? -0.15f : 0.05f), Top, -0.02f);
                float step = dealer ? 0.07f : 0.02f;

                for (int i = 0; i < BlackjackTable.CardsShown; i++)
                {
                    // Each card a hair above the last, so the fan overlaps cleanly and its left edge shows.
                    Vector3 at = first + new Vector3(i * step, 0.004f + i * 0.002f, dealer ? 0f : -i * 0.004f);

                    GameObject card = Block(t, $"Card{hand}.{i}", at, CardSize, "Plastic", solid: false);
                    // The face plate sits just above the card's top; the table swaps its mesh for a CardFaces quad.
                    GameObject face = Block(t, $"Face{hand}.{i}", at + new Vector3(0f, 0.0021f, 0f),
                                            new Vector3(0.058f, 0.001f, 0.088f), "Plastic", solid: false);

                    card.GetComponent<Renderer>().enabled = false;
                    face.GetComponent<Renderer>().enabled = false;
                    cards.Add(card.GetComponent<Renderer>());
                    faces.Add(face.GetComponent<Renderer>());
                }
            }

            root.AddComponent<NetworkObject>();
            root.AddComponent<BlackjackTable>().Configure(cards.ToArray(), faces.ToArray(),
                                                          Palette.Named("Plastic"), Palette.Named("Cloth"));

            (BlackjackPress press, string colour, float x)[] small =
            {
                (BlackjackPress.Hit, "Leaf", -0.15f),
                (BlackjackPress.Stand, "Accent", -0.05f),
                (BlackjackPress.Double, "Plastic", 0.05f),
                (BlackjackPress.Split, "Cloth", 0.15f),
            };

            for (int seat = 0; seat < BlackjackMath.Seats; seat++)
            {
                float x = (seat - 1.5f) * SeatStep;

                Button(t, seat, BlackjackPress.Bet, new Vector3(x, Top + 0.02f, 0.3f), new Vector3(0.16f, 0.04f, 0.1f), "Gold");

                foreach ((BlackjackPress press, string colour, float dx) in small)
                    Button(t, seat, press, new Vector3(x + dx, Top + 0.02f, 0.16f), new Vector3(0.08f, 0.04f, 0.08f), colour);
            }

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, TablePath, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null) return false;

            RegisterSpawnable(saved.GetComponent<NetworkObject>());
            return true;
        }

        /// <summary>A button: its own nested NetworkObject, with a collider for the crosshair to find.</summary>
        static void Button(Transform parent, int seat, BlackjackPress press, Vector3 position, Vector3 size, string colour)
        {
            GameObject button = Block(parent, $"Seat{seat + 1}.{press}", position, size, colour, solid: true);
            button.AddComponent<NetworkObject>();
            button.AddComponent<BlackjackButton>().Configure(seat, press);
        }

        /// <summary>Same as SlotFactory.Block, by palette name rather than nearest colour.</summary>
        static GameObject Block(Transform parent, string name, Vector3 position, Vector3 size, string material, bool solid)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = position;
            cube.transform.localScale = size;

            if (!solid)
            {
                Object.DestroyImmediate(cube.GetComponent<Collider>());
                cube.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
            }

            cube.GetComponent<Renderer>().sharedMaterial = Palette.Named(material);
            return cube;
        }

        /// <summary>Same reasoning as PlayerPrefabBuilder.RegisterSpawnable; see the note there.</summary>
        static void RegisterSpawnable(NetworkObject networkObject)
        {
            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (networkObject == null || prefabs == null)
            {
                Debug.LogError($"[BlackjackFactory] {TablePath} cannot be registered to spawn.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);
        }
    }
}
