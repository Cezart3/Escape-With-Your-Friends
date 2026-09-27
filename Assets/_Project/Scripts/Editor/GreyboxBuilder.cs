using System.Collections.Generic;
using System.IO;
using EscapeWithYourFriends.World;
using FishNet.Managing.Object;
using FishNet.Object;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The six landmarks, as boxes.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.GreyboxBuilder.BuildAll
    ///
    /// Blockouts exist so the island has structure and the economy loop can be walked through before
    /// any art exists. They are deliberately crude - primitives, five colours, no detail - because
    /// the moment a greybox starts looking finished it stops getting replaced, and everything here is
    /// meant to be thrown away in M8.
    ///
    /// What is not crude is the layout: each one is built around the thing a player does there. The
    /// shop has a counter you stand at, the casino has a table you gather round, the village has an
    /// open middle where a fight happens and huts to break line of sight. Those shapes are the part
    /// worth testing now, and they survive the art pass.
    /// </summary>
    public static class GreyboxBuilder
    {
        const string PrefabDir = "Assets/_Project/Prefabs/World";
        const string MaterialDir = "Assets/_Project/Art/Greybox";
        const string PrefabObjectsPath = "Assets/DefaultPrefabObjects.asset";

        [MenuItem("EWYF/Build greybox landmarks")]
        public static void BuildAll()
        {
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(MaterialDir);

            var built = new List<string>();

            built.Add(Save(BuildBaseCamp(), "BaseCamp"));
            built.Add(Save(BuildShop(), "Shop"));
            built.Add(Save(BuildCasino(), "Casino"));
            built.Add(Save(BuildVillage(), "NativeVillage"));
            built.Add(Save(BuildWreck(), "Wreck"));
            built.Add(Save(BuildCave(), "Cave"));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[GreyboxBuilder] Built {built.Count} landmarks: {string.Join(", ", built)}.");
        }

        // ---------------------------------------------------------------- the six

        /// <summary>
        /// Home. A shelter you spawn under, a crate to leave things in, a bench to make things at and
        /// a fire in the middle, arranged in a loose ring so four players landing at once do not all
        /// stand in the same box. The revive machine is placed separately, as its own POI, because it
        /// is a networked machine with behaviour rather than scenery.
        /// </summary>
        static GameObject BuildBaseCamp()
        {
            GameObject root = Root("BaseCamp", "Base Camp",
                                   "Where you wake up. Storage, a crafting bench, and the machine that "
                                   + "puts your friends back together.", radius: 18f, hostile: false);

            // Shelter: a canvas roof on four posts, open on every side so it never traps anyone.
            Box(root, "Shelter.Roof", "Canvas", new Vector3(-4f, 2.6f, 0f), new Vector3(5f, 0.15f, 5f));
            for (int i = 0; i < 4; i++)
            {
                float px = -4f + (i % 2 == 0 ? -2.2f : 2.2f);
                float pz = i < 2 ? -2.2f : 2.2f;
                Box(root, $"Shelter.Post{i}", "Wood", new Vector3(px, 1.3f, pz), new Vector3(0.2f, 2.6f, 0.2f));
            }

            // Two sleeping mats under it, purely so the shelter reads as somewhere you live.
            Box(root, "Shelter.Mat0", "Canvas", new Vector3(-5.2f, 0.08f, 0f), new Vector3(0.9f, 0.15f, 2f), solid: false);
            Box(root, "Shelter.Mat1", "Canvas", new Vector3(-2.8f, 0.08f, 0f), new Vector3(0.9f, 0.15f, 2f), solid: false);

            // Storage, at a height you can see into. Interaction comes with the inventory in M3; the
            // box is here now so the camp has a shape to test.
            Box(root, "Storage", "Wood", new Vector3(3.5f, 0.55f, -3f), new Vector3(2.2f, 1.1f, 1.2f));
            Box(root, "Storage.Lid", "Wood", new Vector3(3.5f, 1.18f, -3f), new Vector3(2.3f, 0.16f, 1.3f), solid: false);

            // Crafting bench: a table with a vice-sized lump on it, so it is not just another crate.
            Box(root, "Bench", "Wood", new Vector3(3.5f, 0.9f, 2.5f), new Vector3(2.6f, 0.15f, 1.1f));
            for (int i = 0; i < 4; i++)
            {
                float px = 3.5f + (i % 2 == 0 ? -1.1f : 1.1f);
                float pz = 2.5f + (i < 2 ? -0.4f : 0.4f);
                Box(root, $"Bench.Leg{i}", "Wood", new Vector3(px, 0.45f, pz), new Vector3(0.12f, 0.9f, 0.12f), solid: false);
            }
            Box(root, "Bench.Vice", "Metal", new Vector3(4.2f, 1.1f, 2.5f), new Vector3(0.4f, 0.25f, 0.4f), solid: false);

            // The fire, in the middle, where everyone ends up standing.
            Cylinder(root, "Fire.Ring", "Stone", new Vector3(0f, 0.12f, 0f), new Vector3(2f, 0.12f, 2f), solid: false);
            Box(root, "Fire.Logs", "Wood", new Vector3(0f, 0.3f, 0f), new Vector3(0.9f, 0.3f, 0.9f), solid: false);

            DressBaseCamp(root);
            return root;
        }

        /// <summary>
        /// The shop. A counter you stand at from outside, so the trade is a gesture rather than a menu
        /// that opens when you walk through a door. The NPC stands behind it in M4.
        /// </summary>
        static GameObject BuildShop()
        {
            GameObject root = Root("Shop", "Trading Post",
                                   "Sell what you find, buy weapons and vehicle upgrades.",
                                   radius: 10f, hostile: false);

            Box(root, "Hut", "Wood", new Vector3(0f, 1.4f, -1.6f), new Vector3(6f, 2.8f, 3.2f));
            Roof(root, "Roof", "Canvas", new Vector3(0f, 2.9f, -1.4f), 7f, 4.4f, 1.3f);

            // A door on the back wall and a window either side of it, so the hut has a front and a
            // back from a distance rather than being a box with a counter stuck to it.
            Opening(root, "Door", new Vector3(0f, 1f, -3.22f), new Vector3(1.1f, 2f, 0.1f));
            Opening(root, "Window.L", new Vector3(2.1f, 1.9f, -3.22f), new Vector3(1f, 0.8f, 0.1f));
            Opening(root, "Window.R", new Vector3(-2.1f, 1.9f, -3.22f), new Vector3(1f, 0.8f, 0.1f));

            // The counter, and the gap behind it that the shopkeeper occupies.
            Box(root, "Counter", "Wood", new Vector3(0f, 1f, 0.6f), new Vector3(5f, 0.2f, 0.9f));
            Box(root, "Counter.Front", "Wood", new Vector3(0f, 0.5f, 1f), new Vector3(5f, 1f, 0.15f));

            Box(root, "Sign", "Accent", new Vector3(0f, 3.5f, 0.6f), new Vector3(2.4f, 0.9f, 0.12f), solid: false);

            // A rack of things for sale, so it reads as a shop from a distance and not as a hut.
            for (int i = 0; i < 3; i++)
                Box(root, $"Stock{i}", "Metal", new Vector3(-1.6f + i * 1.6f, 1.35f, -0.2f),
                    new Vector3(0.5f, 0.5f, 0.5f), solid: false);

            Empty(root, "NpcStand", new Vector3(0f, 0f, -0.4f));
            DressShop(root);
            return root;
        }

        /// <summary>
        /// The casino, rebuilt for #65. The shape was always right - four players crowding one table
        /// is the whole scene - and what it was missing was any reason to believe somebody built it.
        ///
        /// So: a floor of mismatched decking, a doorway you walk through rather than a missing wall,
        /// stools that are crates, a bar with bottles on it, a sign nailed over the door, and five
        /// coloured lamps that will not sit still. Everything in here is salvage. The roulette table
        /// itself is no longer part of the building - it is a networked prefab placed at this POI by
        /// <c>CasinoFactory</c> (#64), and the room is built around where it lands.
        ///
        /// The lighting is the load-bearing part of "reads as a casino built by people stranded on an
        /// island". Every other room in the game is lit and left alone; see <see cref="TackyLights"/>.
        /// </summary>
        static GameObject BuildCasino()
        {
            GameObject root = Root("Casino", "The Shack",
                                   "Roulette, terrible decisions, and a man who will sell you a drink.",
                                   radius: 12f, hostile: false);

            // A floor, because a casino with sand underfoot is a shack. Flat on the pad, so the
            // table the POI drops here stands on the same ground the player walks in on.
            Box(root, "Floor", "Wood", new Vector3(0f, 0.03f, -1f), new Vector3(9f, 0.06f, 6.2f),
                solid: false);

            for (int i = 0; i < 5; i++)
                Box(root, $"Floor.Plank{i}", "Canvas",
                    new Vector3(-3.4f + i * 1.7f, 0.07f, -1f), new Vector3(0.5f, 0.04f, 6f),
                    solid: false);

            // Three walls and a front with a doorway in it. The gap is two metres, which is wide
            // enough that four people arriving at once do not wedge.
            Box(root, "Wall.Back", "Wood", new Vector3(0f, 1.6f, -4f), new Vector3(9f, 3.2f, 0.3f));
            Box(root, "Wall.Left", "Wood", new Vector3(-4.4f, 1.6f, -1f), new Vector3(0.3f, 3.2f, 6.2f));
            Box(root, "Wall.Right", "Wood", new Vector3(4.4f, 1.6f, -1f), new Vector3(0.3f, 3.2f, 6.2f));
            Box(root, "Wall.FrontLeft", "Wood", new Vector3(-3.1f, 1.6f, 2.0f), new Vector3(2.9f, 3.2f, 0.3f));
            Box(root, "Wall.FrontRight", "Wood", new Vector3(3.1f, 1.6f, 2.0f), new Vector3(2.9f, 3.2f, 0.3f));
            Box(root, "Door.Lintel", "Wood", new Vector3(0f, 2.9f, 2.0f), new Vector3(3.4f, 0.6f, 0.3f),
                solid: false);
            Roof(root, "Roof", "Canvas", new Vector3(0f, 3.2f, -1f), 9.6f, 7f, 2f);

            // The doorway is a real gap between the two front walls, so it needs no recess - only the
            // windows either side of it do.
            Opening(root, "Window.L", new Vector3(3.1f, 2.1f, 2.16f), new Vector3(1.4f, 1f, 0.1f));
            Opening(root, "Window.R", new Vector3(-3.1f, 2.1f, 2.16f), new Vector3(1.4f, 1f, 0.1f));

            // The sign, nailed over the door at an angle somebody could not be bothered to fix.
            GameObject sign = Box(root, "Sign", "Canvas", new Vector3(0f, 3.5f, 2.2f),
                                  new Vector3(4.2f, 0.9f, 0.12f), solid: false);
            sign.transform.localRotation = Quaternion.Euler(0f, 0f, -4f);
            Box(root, "Sign.Letters", "Accent", new Vector3(0f, 3.5f, 2.29f),
                new Vector3(3.4f, 0.35f, 0.06f), solid: false);

            // The bar, along the back wall, with bottles on it. Pulled forward from the wall in #66
            // to leave the barman somewhere to stand: the gap behind it is where the POI drops him,
            // and at the blockout's 0.2m he was standing inside the plank.
            Box(root, "Bar", "Wood", new Vector3(-2.6f, 1f, -3.05f), new Vector3(3f, 0.2f, 0.8f));
            Box(root, "Bar.Front", "Wood", new Vector3(-2.6f, 0.5f, -2.75f), new Vector3(3f, 1f, 0.15f));

            for (int i = 0; i < 6; i++)
                Box(root, $"Bar.Bottle{i}", "Metal",
                    new Vector3(-3.9f + i * 0.5f, 1.25f, -3.15f), new Vector3(0.12f, 0.3f, 0.12f),
                    solid: false);

            // Crates to sit on, round the table the POI puts at the origin. Not chairs: nobody
            // stranded on an island builds a chair before they build a bar.
            (float x, float z)[] stools = { (2.9f, 1.5f), (2.9f, -1.5f), (0.4f, -1.9f), (-2.6f, 1.6f) };

            for (int i = 0; i < stools.Length; i++)
            {
                Box(root, $"Stool{i}", "Wood",
                    new Vector3(stools[i].x, 0.3f, stools[i].z), new Vector3(0.6f, 0.55f, 0.6f));
                Box(root, $"Stool{i}.Cushion", "Accent",
                    new Vector3(stools[i].x, 0.6f, stools[i].z), new Vector3(0.62f, 0.08f, 0.62f),
                    solid: false);
            }

            // A chandelier of bottles on a line, because somebody had bottles and a line.
            Box(root, "Chandelier.Line", "Metal", new Vector3(0f, 3.15f, -1f), new Vector3(7f, 0.04f, 0.04f),
                solid: false);

            for (int i = 0; i < 7; i++)
                Box(root, $"Chandelier.Bottle{i}", "Metal",
                    new Vector3(-3f + i, 2.95f, -1f), new Vector3(0.1f, 0.34f, 0.1f), solid: false);

            // Five lamps, no two the same, none of them where a lighting designer would put one.
            var lamps = new[]
            {
                Lamp(root, "Lamp.Table", new Vector3(0f, 2.7f, -0.2f), new Color(1f, 0.35f, 0.75f), 4.5f, 8f),
                Lamp(root, "Lamp.Bar", new Vector3(-2.9f, 2.4f, -3f), new Color(0.3f, 0.9f, 1f), 3f, 6f),
                Lamp(root, "Lamp.Door", new Vector3(0f, 2.6f, 1.8f), new Color(1f, 0.8f, 0.25f), 2.5f, 6f),
                Lamp(root, "Lamp.Left", new Vector3(-3.6f, 2.8f, 0.6f), new Color(0.55f, 1f, 0.4f), 2f, 5f),
                Lamp(root, "Lamp.Right", new Vector3(3.6f, 2.8f, 0.6f), new Color(0.8f, 0.4f, 1f), 2f, 5f),
            };

            root.AddComponent<TackyLights>().Configure(lamps);

            Empty(root, "TableSeat", new Vector3(0f, 0f, 1.6f));
            Empty(root, "BarNpcStand", new Vector3(-2.6f, 0f, -3.62f));

            DressCasino(root);
            return root;
        }

        /// <summary>
        /// The art pass on the shack (docs/ART-PLAN.md §4). It runs after the blockout rather than
        /// instead of it, so every box above is still the layout: CasinoTest still finds a floor, a
        /// roof, three walls and a doorway it can measure, and the NavMesh still bakes from the same
        /// colliders. What changes is what you see - plank floors and walls, a thatched roof, a real
        /// bar with real stools, a string of coloured bulbs where the bottle chandelier hung.
        ///
        /// When the kits have not been extracted yet, every call below logs what is missing and
        /// leaves that box as it was, so a greybox build still works on a machine without the art.
        /// </summary>
        static void DressCasino(GameObject root)
        {
            Transform t = root.transform;

            if (ArtDress.Tile(Child(t, "Floor"), "Floor", 2.2f))
                for (int i = 0; i < 5; i++) ArtDress.Strip(Child(t, $"Floor.Plank{i}"));

            // One floor tile, stood on its edge, is a plank wall. ArtDress turns it to face the wall.
            foreach (string wall in new[] { "Wall.Back", "Wall.Left", "Wall.Right", "Wall.FrontLeft",
                                            "Wall.FrontRight", "Door.Lintel" })
                ArtDress.Tile(Child(t, wall), "Floor", 1.6f);

            // The pitched slabs keep their colliders and lose their looks under a thatch fitted over
            // the whole footprint. The thatch's own corner posts land just inside the wall corners.
            Transform roof = t.Find("Roof");
            if (roof != null && ArtDress.FitBox(t, new Bounds(new Vector3(0f, 2.7f, -1f), new Vector3(10f, 5.4f, 7.4f)),
                                                "ThatchRoof", false, "Roof.Art"))
                foreach (Transform slab in roof) ArtDress.Strip(slab.gameObject);

            // The counter replaces the plank and its front as one run of three.
            if (ArtDress.TileBox(t, new Bounds(new Vector3(-2.6f, 0.55f, -3.06f), new Vector3(3f, 1.1f, 0.78f)),
                                 "BarCounter", 1f, "Counter.Art"))
            {
                ArtDress.Strip(Child(t, "Bar"));
                ArtDress.Strip(Child(t, "Bar.Front"));
            }

            for (int i = 0; i < 6; i++) ArtDress.Fit(Child(t, $"Bar.Bottle{i}"), "Bottle", keepShape: true);

            for (int i = 0; i < 4; i++)
            {
                if (ArtDress.Fit(Child(t, $"Stool{i}"), "BarStool", keepShape: true))
                    ArtDress.Strip(Child(t, $"Stool{i}.Cushion"));
            }

            if (ArtDress.TileBox(t, new Bounds(new Vector3(0f, 3f, -1f), new Vector3(7f, 0.35f, 0.1f)),
                                 "StringLights", 2.4f, "Lights.Art"))
                ArtDress.Strip(Child(t, "Chandelier.Line"));

            for (int i = 0; i < 7; i++) ArtDress.Fit(Child(t, $"Chandelier.Bottle{i}"), "Bottle", keepShape: true);

            // The board over the door, still crooked: the angle is the piece's, the planks are new.
            ArtDress.Tile(Child(t, "Sign"), "FloorOld", 10f);

            // Clutter, with no collider and nothing a test counts: a crate of bottles past the end of
            // the bar and a lamp either side of the door.
            ArtDress.FitBox(t, new Bounds(new Vector3(-0.4f, 0.55f, -3.3f), new Vector3(1.1f, 1.1f, 1.3f)),
                            "CrateBottles", true, "Decor.Crate");
            ArtDress.FitBox(t, new Bounds(new Vector3(2.1f, 0.9f, 2.6f), new Vector3(0.6f, 1.8f, 0.6f)),
                            "Lantern", true, "Decor.LanternR");
            ArtDress.FitBox(t, new Bounds(new Vector3(-2.1f, 0.9f, 2.6f), new Vector3(0.6f, 1.8f, 0.6f)),
                            "Lantern", true, "Decor.LanternL");
        }

        /// <summary>
        /// Home: a canvas shelter on four posts, two bedrolls, a storage box, two workbenches and a
        /// fire pit, each where its box was. Same rules as <see cref="DressCasino"/>.
        /// </summary>
        static void DressBaseCamp(GameObject root)
        {
            Transform t = root.transform;

            if (ArtDress.FitBox(t, new Bounds(new Vector3(-4f, 1.35f, 0f), new Vector3(5f, 2.75f, 5f)),
                                "Canvas", false, "Shelter.Art"))
            {
                ArtDress.Strip(Child(t, "Shelter.Roof"));
                for (int i = 0; i < 4; i++) ArtDress.Strip(Child(t, $"Shelter.Post{i}"));
            }

            ArtDress.Fit(Child(t, "Shelter.Mat0"), "Bedroll");
            ArtDress.Fit(Child(t, "Shelter.Mat1"), "Bedroll");

            // The box is long across x and the kit's is long across z: a quarter turn, not a stretch.
            if (ArtDress.Fit(Child(t, "Storage"), "BoxLarge", quarterTurns: 1))
                ArtDress.Strip(Child(t, "Storage.Lid"));

            if (ArtDress.TileBox(t, new Bounds(new Vector3(3.5f, 0.5f, 2.5f), new Vector3(2.6f, 1f, 1.1f)),
                                 "Workbench", 1.3f, "Bench.Art"))
            {
                ArtDress.Strip(Child(t, "Bench"));
                ArtDress.Strip(Child(t, "Bench.Vice"));
                for (int i = 0; i < 4; i++) ArtDress.Strip(Child(t, $"Bench.Leg{i}"));
            }

            if (ArtDress.FitBox(t, new Bounds(new Vector3(0f, 0.2f, 0f), new Vector3(2f, 0.4f, 2f)),
                                "CampfirePit", true, "Fire.Art"))
            {
                ArtDress.Strip(Child(t, "Fire.Ring"));
                ArtDress.Strip(Child(t, "Fire.Logs"));
            }
        }

        /// <summary>
        /// The trader's stall: plank walls on three sides and open at the front, because the trader
        /// stands inside the hut's box and a fourth wall would hide him. Thatch over it, a run of
        /// counters in front, stock on the shelf.
        /// </summary>
        static void DressShop(GameObject root)
        {
            Transform t = root.transform;

            bool walls = ArtDress.TileBox(t, new Bounds(new Vector3(0f, 1.4f, -3.1f), new Vector3(6f, 2.8f, 0.2f)),
                                          "Floor", 1.6f, "Hut.Back");
            walls &= ArtDress.TileBox(t, new Bounds(new Vector3(-2.9f, 1.4f, -1.6f), new Vector3(0.2f, 2.8f, 3.2f)),
                                      "Floor", 1.6f, "Hut.Left");
            walls &= ArtDress.TileBox(t, new Bounds(new Vector3(2.9f, 1.4f, -1.6f), new Vector3(0.2f, 2.8f, 3.2f)),
                                      "Floor", 1.6f, "Hut.Right");
            if (walls) ArtDress.Strip(Child(t, "Hut"));

            Transform roof = t.Find("Roof");
            if (roof != null && ArtDress.FitBox(t, new Bounds(new Vector3(0f, 2.15f, -1.4f), new Vector3(7.4f, 4.3f, 4.8f)),
                                                "ThatchRoof", false, "Roof.Art"))
                foreach (Transform slab in roof) ArtDress.Strip(slab.gameObject);

            if (ArtDress.TileBox(t, new Bounds(new Vector3(0f, 0.55f, 0.64f), new Vector3(5f, 1.1f, 0.9f)),
                                 "BarCounter", 1f, "Counter.Art"))
            {
                ArtDress.Strip(Child(t, "Counter"));
                ArtDress.Strip(Child(t, "Counter.Front"));
            }

            ArtDress.Tile(Child(t, "Sign"), "FloorOld", 10f);

            string[] stock = { "Barrel", "Box", "Bottle" };
            for (int i = 0; i < 3; i++) ArtDress.Fit(Child(t, $"Stock{i}"), stock[i], keepShape: true);
        }

        /// <summary>
        /// Log palisade huts under thatch, a totem of stacked trunks with a log for arms, and a fire
        /// pit. The huts stay solid boxes to the physics: nobody goes inside a native hut.
        /// </summary>
        static void DressVillage(GameObject root)
        {
            Transform t = root.transform;

            for (int i = 0; i < 5; i++)
            {
                GameObject hut = Child(t, $"Hut{i}");
                if (hut == null) continue;

                Vector3 c = hut.transform.localPosition;
                bool walls = true;

                // Four walls, each a thin box on one face of the hut, tiled with palisade logs.
                walls &= ArtDress.TileBox(t, new Bounds(c + new Vector3(0f, 0f, 1.9f), new Vector3(4f, 2.6f, 0.2f)),
                                          "Palisade", 2f, $"Hut{i}.WallN");
                walls &= ArtDress.TileBox(t, new Bounds(c + new Vector3(0f, 0f, -1.9f), new Vector3(4f, 2.6f, 0.2f)),
                                          "Palisade", 2f, $"Hut{i}.WallS");
                walls &= ArtDress.TileBox(t, new Bounds(c + new Vector3(1.9f, 0f, 0f), new Vector3(0.2f, 2.6f, 4f)),
                                          "Palisade", 2f, $"Hut{i}.WallE");
                walls &= ArtDress.TileBox(t, new Bounds(c + new Vector3(-1.9f, 0f, 0f), new Vector3(0.2f, 2.6f, 4f)),
                                          "Palisade", 2f, $"Hut{i}.WallW");
                if (walls) ArtDress.Strip(hut);

                // The box centre is 1.3m up; the thatch's box runs from the ground to 4.7m.
                if (ArtDress.FitBox(t, new Bounds(c + new Vector3(0f, 1.05f, 0f), new Vector3(5f, 4.7f, 5f)),
                                    "ThatchRoof", false, $"Hut{i}.Thatch"))
                    ArtDress.Strip(Child(t, $"Hut{i}.Roof"));
            }

            ArtDress.Tile(Child(t, "Totem"), "Stump", 1.6f);
            ArtDress.Fit(Child(t, "Totem.Arms"), "Log", quarterTurns: 1);

            if (ArtDress.FitBox(t, new Bounds(new Vector3(0f, 0.2f, 3f), new Vector3(2.4f, 0.4f, 2.4f)),
                                "CampfirePit", true, "Fire.Art"))
                ArtDress.Strip(Child(t, "Fire"));
        }

        /// <summary>
        /// Kenney's wreck, hung under the hull box so it keeps the hull's 28 degree list. It is taller
        /// than the box - the box was a hull, the model has its masts - so it is fitted by footprint
        /// and allowed to rise, standing on the hull's floor.
        /// </summary>
        static void DressWreck(GameObject root)
        {
            Transform t = root.transform;
            GameObject hull = Child(t, "Hull");

            // Hull-local units: the box is 4.5 x 3 x 14. 10.1m of wreck is 3.37 of the hull's units.
            if (hull != null && ArtDress.FitBox(hull.transform, new Bounds(new Vector3(0f, 1.18f, 0f),
                                                                           new Vector3(1f, 3.37f, 1f)),
                                                "Wreck", true, "Art"))
            {
                ArtDress.Strip(hull);
                ArtDress.Strip(Child(t, "Deck"));
            }

            ArtDress.Fit(Child(t, "Mast"), "Log");

            string[] debris = { "Crate", "PirateBarrel", "RowBoat", "Crate" };
            for (int i = 0; i < 4; i++) ArtDress.Fit(Child(t, $"Debris{i}"), debris[i], keepShape: true);
        }

        /// <summary>
        /// Rock masses stretched over the stone boxes, which is what the boxes were standing in for.
        /// The inside - floor, ceiling, the ore - stays greybox: it is dark in there.
        /// </summary>
        static void DressCave(GameObject root)
        {
            Transform t = root.transform;

            foreach (string rock in new[] { "Rock.Left", "Rock.Right", "Rock.Lintel", "Rock.Back" })
                ArtDress.Fit(Child(t, rock), "Rocks");
        }

        /// <summary>A direct child by name, or an error naming the landmark - a renamed box is a layout change.</summary>
        static GameObject Child(Transform root, string name)
        {
            Transform child = root.Find(name);
            if (child == null) Debug.LogError($"[GreyboxBuilder] {root.name} has no '{name}' to dress.");
            return child != null ? child.gameObject : null;
        }

        /// <summary>
        /// A coloured point light with no shadows. Shadows off is not a saving here, it is the look:
        /// five shadow-casting lamps in one small room is a mess on any GPU and a slideshow on the
        /// one this game has to run on.
        /// </summary>
        static Light Lamp(GameObject root, string name, Vector3 position, Color colour,
                          float intensity, float range)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = position;

            var lamp = go.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.color = colour;
            lamp.intensity = intensity;
            lamp.range = range;
            lamp.shadows = LightShadows.None;

            return lamp;
        }

        /// <summary>
        /// The native village. Huts around an open middle, which is the fight: cover to break line of
        /// sight, and nowhere to stand that is safe from all of it. The totem is the thing you can see
        /// over the trees, so the village is findable without a map.
        /// </summary>
        static GameObject BuildVillage()
        {
            GameObject root = Root("NativeVillage", "Native Village",
                                   "They live here and they do not want you to. Food, ammo, and the "
                                   + "place your friends end up if the natives carry them off.",
                                   radius: 22f, hostile: true);

            for (int i = 0; i < 5; i++)
            {
                float angle = i * Mathf.PI * 2f / 5f;
                var centre = new Vector3(Mathf.Cos(angle) * 11f, 0f, Mathf.Sin(angle) * 11f);

                Box(root, $"Hut{i}", "Wood", centre + new Vector3(0f, 1.3f, 0f), new Vector3(4f, 2.6f, 4f));
                Box(root, $"Hut{i}.Roof", "Canvas", centre + new Vector3(0f, 2.9f, 0f), new Vector3(5f, 0.4f, 5f), solid: false);
            }

            // The totem, tall enough to clear the canopy at 320m draw distance.
            Box(root, "Totem", "Wood", new Vector3(0f, 4f, 0f), new Vector3(0.8f, 8f, 0.8f));
            Box(root, "Totem.Arms", "Accent", new Vector3(0f, 6.6f, 0f), new Vector3(3.2f, 0.5f, 0.5f), solid: false);

            Cylinder(root, "Fire", "Stone", new Vector3(0f, 0.12f, 3f), new Vector3(2.4f, 0.12f, 2.4f), solid: false);

            // Where the prison goes in #108. Marked now so the layout does not have to change later.
            Empty(root, "PrisonSite", new Vector3(0f, 0f, -6f));
            DressVillage(root);
            return root;
        }

        /// <summary>
        /// The wreck. Half a hull, tipped over, with the mast down. It is the reason there is a
        /// shipwright's worth of scrap on this island, and it is where the boat parts come from in M5.
        /// Deliberately on the tideline, so it is the first landmark seen from the water.
        /// </summary>
        static GameObject BuildWreck()
        {
            GameObject root = Root("Wreck", "The Wreck",
                                   "What you arrived on. Scrap, rope, and the first parts of a boat.",
                                   radius: 14f, hostile: false);

            GameObject hull = Box(root, "Hull", "Wood", new Vector3(0f, 1.6f, 0f), new Vector3(4.5f, 3f, 14f));
            hull.transform.localRotation = Quaternion.Euler(0f, 0f, 28f);

            GameObject deck = Box(root, "Deck", "Wood", new Vector3(-0.6f, 3f, 1f), new Vector3(4f, 0.2f, 8f), solid: false);
            deck.transform.localRotation = Quaternion.Euler(0f, 0f, 28f);

            GameObject mast = Box(root, "Mast", "Wood", new Vector3(4f, 0.8f, -2f), new Vector3(0.5f, 0.5f, 11f));
            mast.transform.localRotation = Quaternion.Euler(0f, 34f, 0f);

            for (int i = 0; i < 4; i++)
                Box(root, $"Debris{i}", "Metal", new Vector3(-5f + i * 2.4f, 0.3f, 6f + (i % 2) * 2f),
                    new Vector3(1.2f, 0.6f, 1.2f), solid: false);

            DressWreck(root);
            return root;
        }

        /// <summary>
        /// The cave. A mound with a mouth in it and a room behind, which is enough to be shelter at
        /// night and a place to put ore in M3. Built as boxes rather than as a hollowed mesh because a
        /// greybox cave that needs a mesh is a cave that will not get rebuilt when the layout changes.
        /// </summary>
        static GameObject BuildCave()
        {
            GameObject root = Root("Cave", "The Cave",
                                   "Out of the rain, out of the dark, and something worth mining in the back.",
                                   radius: 12f, hostile: false);

            // The mound, as two slabs either side of a gap and a lintel over it: a doorway, not a wall.
            Box(root, "Rock.Left", "Stone", new Vector3(-4f, 2.5f, 0f), new Vector3(5f, 5f, 8f));
            Box(root, "Rock.Right", "Stone", new Vector3(4f, 2.5f, 0f), new Vector3(5f, 5f, 8f));
            Box(root, "Rock.Lintel", "Stone", new Vector3(0f, 4.2f, 0f), new Vector3(3.2f, 1.6f, 8f));
            Box(root, "Rock.Back", "Stone", new Vector3(0f, 2.5f, -5.5f), new Vector3(13f, 5f, 3f));

            // Floor and ceiling of the room itself, so it is a space rather than a slot.
            Box(root, "Room.Floor", "Stone", new Vector3(0f, -0.1f, -2f), new Vector3(6f, 0.2f, 6f));
            Box(root, "Room.Ceiling", "Stone", new Vector3(0f, 3.6f, -2f), new Vector3(6f, 0.4f, 6f));

            Box(root, "Ore", "Accent", new Vector3(1.6f, 0.6f, -4f), new Vector3(1.2f, 1.2f, 1.2f), solid: false);

            Empty(root, "Shelter", new Vector3(0f, 0f, -2f));
            DressCave(root);
            return root;
        }

        // ---------------------------------------------------------------- plumbing

        static GameObject Root(string id, string displayName, string purpose, float radius, bool hostile)
        {
            var root = new GameObject(id);
            root.AddComponent<NetworkObject>();

            var landmark = root.AddComponent<Landmark>();
            landmark.Id = id;
            landmark.DisplayName = displayName;
            landmark.Purpose = purpose;
            landmark.Radius = radius;
            landmark.Hostile = hostile;

            return root;
        }

        static GameObject Box(GameObject root, string name, string material, Vector3 position,
                              Vector3 scale, bool solid = true)
            => Piece(root, name, PrimitiveType.Cube, material, position, scale, solid);

        /// <summary>
        /// A pitched roof: two slabs and the ridge between them, instead of the flat slab a blockout
        /// reaches for first (#78).
        ///
        /// This is the whole of the "consistent low-poly language" the issue asks for, in the only
        /// form a batch job can deliver it. Modelled buildings need Blender and a person, but the
        /// thing that actually makes a greybox read as *unfinished* rather than as *stylised* is that
        /// every building is a stack of axis-aligned boxes. One angle, used everywhere, is enough to
        /// flip that - and it costs four primitives per building instead of a mesh import.
        ///
        /// <paramref name="rise"/> is how far the ridge sits above the eaves. Keep it around a third
        /// of the depth; steeper reads as a chapel.
        /// </summary>
        static void Roof(GameObject root, string name, string material, Vector3 eaves,
                         float width, float depth, float rise)
        {
            float half = depth * 0.5f;
            float slant = Mathf.Sqrt(half * half + rise * rise);
            float pitch = Mathf.Atan2(rise, half) * Mathf.Rad2Deg;

            // The slabs hang off an empty named after the roof, so a building still has one child
            // called "Roof" and not three called "Roof.something". CasinoTest asks for exactly that,
            // and it was right to: "the casino has a roof" should not have to know how it is built.
            var group = new GameObject(name);
            group.transform.SetParent(root.transform, false);

            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? 1f : -1f;

                GameObject slab = Box(group, $"{name}.{(side == 0 ? "N" : "S")}", material,
                                      eaves + new Vector3(0f, rise * 0.5f, sign * half * 0.5f),
                                      new Vector3(width, 0.18f, slant));

                slab.transform.localRotation = Quaternion.Euler(sign * pitch, 0f, 0f);
            }

            Box(group, $"{name}.Ridge", material, eaves + new Vector3(0f, rise, 0f),
                new Vector3(width * 1.02f, 0.2f, 0.28f), solid: false);
        }

        /// <summary>
        /// A dark recess in a wall. Not a hole - a blockout with real openings is a blockout whose
        /// walls stop being convex, and a ragdoll finds every one of those. Read at ten metres, an
        /// inset reads the same and costs one non-solid box.
        /// </summary>
        static void Opening(GameObject root, string name, Vector3 position, Vector3 scale)
            => Box(root, name, "Dark", position, scale, solid: false);

        static GameObject Cylinder(GameObject root, string name, string material, Vector3 position,
                                   Vector3 scale, bool solid = true)
            => Piece(root, name, PrimitiveType.Cylinder, material, position, scale, solid);

        static GameObject Piece(GameObject root, string name, PrimitiveType shape, string material,
                                Vector3 position, Vector3 scale, bool solid)
        {
            GameObject go = GameObject.CreatePrimitive(shape);
            go.name = name;
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = position;

            // Unity's cylinder is two units tall, so a scale of 1 is a two-metre column. Halving the
            // vertical scale makes every number in this file a metre, which is worth the one line.
            go.transform.localScale = shape == PrimitiveType.Cylinder
                ? new Vector3(scale.x, scale.y * 0.5f, scale.z)
                : scale;

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sharedMaterial = EnsureMaterial(material);

            // Decoration keeps no collider. Every collider on a landmark is something a ragdoll can
            // get wedged behind, and a blockout has no business generating those by accident.
            //
            // It casts no shadow either. A sign, a crate and four bench legs are five extra draws in
            // the shadow pass for silhouettes nobody can pick out from two metres away, and the
            // shadow pass is where an integrated GPU spends its afternoon. The pieces that make the
            // building's shape - the ones with colliders - still cast.
            if (!solid)
            {
                Collider existing = go.GetComponent<Collider>();
                if (existing != null) Object.DestroyImmediate(existing);

                if (renderer != null) renderer.shadowCastingMode = ShadowCastingMode.Off;
            }

            return go;
        }

        static void Empty(GameObject root, string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = position;
        }

        /// <summary>The shared palette (#79). Nothing here owns a colour any more.</summary>
        static Material EnsureMaterial(string name) => Palette.Named(name);

        static string Save(GameObject root, string name)
        {
            string path = $"{PrefabDir}/{name}.prefab";

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path, out bool success);
            Object.DestroyImmediate(root);

            if (!success || saved == null)
            {
                Debug.LogError($"[GreyboxBuilder] Failed to save {path}.");
                return name + " (FAILED)";
            }

            RegisterSpawnable(saved.GetComponent<NetworkObject>(), path);

            int colliders = saved.GetComponentsInChildren<Collider>(true).Length;
            int renderers = saved.GetComponentsInChildren<MeshRenderer>(true).Length;
            return $"{name} ({renderers} parts, {colliders} solid)";
        }

        /// <summary>Same reasoning as PlayerPrefabBuilder.RegisterSpawnable; see the note there.</summary>
        static void RegisterSpawnable(NetworkObject networkObject, string path)
        {
            if (networkObject == null)
            {
                Debug.LogError($"[GreyboxBuilder] {path} has no NetworkObject.");
                return;
            }

            var prefabs = AssetDatabase.LoadAssetAtPath<PrefabObjects>(PrefabObjectsPath);
            if (prefabs == null)
            {
                Debug.LogError($"[GreyboxBuilder] missing {PrefabObjectsPath}; {path} cannot be spawned.");
                return;
            }

            prefabs.RemoveNull();
            prefabs.AddObject(networkObject, checkForDuplicates: true);
            EditorUtility.SetDirty(prefabs);
        }
    }
}
