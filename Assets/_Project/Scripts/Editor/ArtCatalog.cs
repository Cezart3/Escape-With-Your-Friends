using EscapeWithYourFriends.World;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Every third-party model the game uses, and where it comes from (#79, docs/ART-PLAN.md).
    ///
    /// One table, so that "what did we take from which pack" has one answer: <see cref="ArtExtract"/>
    /// copies exactly these files out of the downloaded zips and nothing else, <see cref="ArtLibrary"/>
    /// imports exactly these, and the plan's licence table is checked against this list rather than
    /// against a folder somebody unzipped by hand.
    ///
    /// Every Kenney file name here was read out of the kit itself (a CC0 mirror of the whole library,
    /// see ART-PLAN §1), so a name that fails to extract means the zip is a different version, not
    /// that the name was guessed.
    /// </summary>
    public static class ArtCatalog
    {
        public const string Root = "Assets/_Project/Art/ThirdParty";

        /// <summary>
        /// The author of the models this project makes itself, from scripts under tools/art. They are
        /// committed, not extracted from a zip, and live under <see cref="OwnRoot"/>.
        /// </summary>
        public const string Own = "EWYF";
        public const string OwnRoot = "Assets/_Project/Art/Models";

        public readonly struct Pack
        {
            public readonly string Author;

            /// <summary>The folder under <see cref="Root"/>/Author, and the key models refer to.</summary>
            public readonly string Name;

            /// <summary>
            /// A substring of the zip's file name. Zips are matched loosely because Kenney renames them
            /// between versions; the extractor then prefers the candidate that holds the most of the
            /// files it wants, which also sorts a 3D kit from a 2D pack with a similar name.
            /// </summary>
            public readonly string ZipHint;

            public readonly string Page;

            /// <summary>
            /// Whether every model in the pack is painted from one <c>colormap.png</c>. True for every
            /// current Kenney kit; false for the older ones that still use a material per colour.
            /// </summary>
            public readonly bool Atlas;

            /// <summary>
            /// Painted from its own textures, a material per slot the artist named: the Quaternius kits
            /// (ART-PLAN P6). The zip's textures are copied to <c>Textures/</c> beside the models, and a
            /// texture with an alpha channel is a leaf card, clipped. Leave <see cref="Atlas"/> false.
            /// </summary>
            public readonly bool Textured;

            /// <summary>
            /// The atlas's file name when <see cref="Atlas"/> is set. Kenney's are all colormap.png;
            /// Quaternius's pirate kit paints from Atlas_Pirate.png, a swatch sheet of the same kind.
            /// </summary>
            public readonly string AtlasFile;

            public Pack(string author, string name, string zipHint, string page, bool atlas,
                        bool textured = false, string atlasFile = "colormap.png")
            {
                Author = author;
                Name = name;
                ZipHint = zipHint;
                Page = page;
                Atlas = atlas;
                Textured = textured;
                AtlasFile = atlasFile;
            }

            public string Folder => Author == Own ? $"{OwnRoot}/{Name}" : $"{Root}/{Author}/{Name}";
        }

        /// <summary>How a model's target size is measured.</summary>
        public enum Measure
        {
            /// <summary>Not scaled to a size. The model is fitted to a greybox box instead.</summary>
            Fitted,
            Height,
            /// <summary>The larger horizontal extent: a rock's width, a log's length.</summary>
            Width,
        }

        public readonly struct Model
        {
            public readonly string Id;
            public readonly string Pack;

            /// <summary>File name inside the kit, without the extension. The FBX is the one taken.</summary>
            public readonly string File;

            public readonly ArtCategory Category;
            public readonly Measure Measure;
            public readonly float Size;
            public readonly bool Upright;

            public Model(string id, string pack, string file, ArtCategory category,
                         Measure measure = Measure.Fitted, float size = 0f, bool upright = false)
            {
                Id = id;
                Pack = pack;
                File = file;
                Category = category;
                Measure = measure;
                Size = size;
                Upright = upright;
            }
        }

        public static readonly Pack[] Packs =
        {
            new("Kenney", "Pirate", "pirate", "https://kenney.nl/assets/pirate-kit", true),
            new("Kenney", "Survival", "survival", "https://kenney.nl/assets/survival-kit", true),
            new("Kenney", "Platformer", "platformer", "https://kenney.nl/assets/platformer-kit", true),
            new("Kenney", "Coaster", "coaster", "https://kenney.nl/assets/coaster-kit", true),
            new("Kenney", "Castle", "castle", "https://kenney.nl/assets/castle-kit", true),
            new("Kenney", "Holiday", "holiday", "https://kenney.nl/assets/holiday-kit", true),

            // P4, the vehicles (T11).
            new("Kenney", "Car", "car-kit", "https://kenney.nl/assets/car-kit", true),
            new("Kenney", "Watercraft", "watercraft", "https://kenney.nl/assets/watercraft-pack", true),

            // P4, the weapons (T12). Flat colours like the furniture: grey, dark, wood and the rest.
            new("Kenney", "Weapon", "weapon", "https://kenney.nl/assets/weapon-pack", false),

            // #206, what lies on the ground. From the CC0 mirror (github.com/shorepine/kenney), glb
            // converted to FBX by tools/art/glb2fbx.py, like the weapon pack.
            new("Kenney", "Food", "food", "https://kenney.nl/assets/food-kit", true),

            // The one kit still on flat colours: wood, carpet, metal, woodDark. See ArtLibrary.Remap.
            new("Kenney", "Furniture", "furniture", "https://kenney.nl/assets/furniture-kit", false),

            // The buildings: timber walls, windows, doors and gable roofs on a one-unit grid, so a
            // wall is built from modules rather than a floor tile stood on its edge.
            new("Kenney", "Town", "fantasy-town", "https://kenney.nl/assets/fantasy-town-kit", true),

            // P6 V1: the island's trees, bushes and rocks. The free Standard edition, CC0 (its
            // License_Standard.txt). No palms in it, so the palms stay Kenney's.
            new("Quaternius", "Nature", "Nature MegaKit",
                "https://quaternius.com/packs/stylizednaturemegakit.html", false, textured: true),

            // P6 V2: the palms, the props, the wreck and the cave's cliffs, by the same hand as the
            // nature kit. Quaternius's download is a Google Drive folder that Drive zips as
            // "drive-download-<date>.zip", so the hint is empty: any zip may be it, and the extractor
            // takes the one holding the most of these files, which no Kenney zip holds. That zip has
            // no licence file; ArtExtract then wants one saved beside it (see ArtExtract.LicenceFor).
            // File names read from `unzip -l` of the zip, triangle counts from its glTF (2026-09-27).
            new("Quaternius", "PirateKit", "", "https://quaternius.com/packs/piratekit.html", true,
                atlasFile: "Atlas_Pirate.png"),

            // #282, the palms: modelled by tools/art/palms.py, which writes the FBX files and the two ramp
            // textures they are painted from. Each file carries a near mesh and a "_Far" one for the
            // far LOD (ArtLibrary.EnsureFloraPrefab).
            new(Own, "Palms", "", "tools/art/palms.py", false, textured: true),

            // #248, the jungle's broadleaf trees, from tools/art/trees.py. As the palms, plus a "_Deco"
            // mesh (roots, lianas) that the trunk collider is measured without.
            new(Own, "Trees", "", "tools/art/trees.py", false, textured: true),

            // #248, rocks, fallen logs and stumps, from tools/art/rocks.py.
            new(Own, "Rocks", "", "tools/art/rocks.py", false, textured: true),

            // #246, the undergrowth and the flowers, from tools/art/plants.py: one material, "Leaves".
            new(Own, "Plants", "", "tools/art/plants.py", false, textured: true),

            // #248, the highland pines, from tools/art/pines.py: bark and a two-sided needle material.
            new(Own, "Pines", "", "tools/art/pines.py", false, textured: true),

            // #287, the animals: modelled, rigged and animated by tools/art/animals.py, every clip in
            // the animal's own file (AnimalArt). Painted from the characters' ramp sheet.
            new(Own, "Animals", "", "tools/art/animals.py", false, textured: true),

            // The people's moves (T9; the bodies are our own since #76). No entries in Models, so
            // ArtExtract passes over it: its file names were never seen, and CharacterArt extracts it
            // by kind instead.
            new("Quaternius", "UniversalAnimationLibrary", "animationlibrary",
                "https://quaternius.com/packs/universalanimationlibrary.html", false),
        };

        const ArtCategory Tree = ArtCategory.Tree;
        const ArtCategory Plant = ArtCategory.Plant;
        const ArtCategory Rock = ArtCategory.Rock;
        const ArtCategory Log = ArtCategory.Log;
        const ArtCategory Small = ArtCategory.SmallProp;
        const ArtCategory Prop = ArtCategory.Prop;
        const ArtCategory Structure = ArtCategory.Structure;
        const ArtCategory Weapon = ArtCategory.Weapon;

        const Measure Height = Measure.Height;
        const Measure Width = Measure.Width;

        /// <summary>
        /// Sizes are metres in the game, not the kit's units: the survival kit is built on a half-unit
        /// grid and the pirate kit on two and a half, and neither is anybody's metre. The flora sizes
        /// are the middle of the range; the island's own scale roll spreads them.
        /// </summary>
        public static readonly Model[] Models =
        {
            // --- the island (IslandFlora.Variants) ---------------------------------------------
            // Our own palms (#282, tools/art/palms.py), 3 400-6 300 triangles near and 270-760 far.
            // Seven coconut palms for the beach, a fan palm and a clumping palm for the jungle.
            new("PalmStraight", "Palms", "Palm_Straight", Tree, Height, 10f, true),
            new("PalmBend", "Palms", "Palm_Bend", Tree, Height, 9f, true),
            new("PalmTall", "Palms", "Palm_Tall", Tree, Height, 12f, true),
            new("PalmLean", "Palms", "Palm_Lean", Tree, Height, 8f, true),
            new("PalmTwin", "Palms", "Palm_Twin", Tree, Height, 9.5f, true),
            new("PalmYoung", "Palms", "Palm_Young", Tree, Height, 4.5f, true),
            new("PalmOld", "Palms", "Palm_Old", Tree, Height, 12.5f, true),

            // Placed nowhere. Kenney's pirate kit still supplies the rocks, the thatch and the crates,
            // which cannot tell which way is up; its palm, which can, stays here to teach them.
            new("KenneyPalm", "Pirate", "palm-straight", Tree, Height, 8.5f, true),

            // Our broadleaf trees. The rain tree and the flame tree are wider than they are tall, so
            // they are not marked upright and take the pack's axis from the others.
            new("TreeRain", "Trees", "Tree_Rain", Tree, Height, 10f),
            new("TreeKapok", "Trees", "Tree_Kapok", Tree, Height, 16f, true),
            new("TreeFig", "Trees", "Tree_Fig", Tree, Height, 12.5f, true),
            new("TreeFlame", "Trees", "Tree_Flame", Tree, Height, 8.5f),
            new("TreeYoung", "Trees", "Tree_Young", Tree, Height, 6f, true),
            new("TreeOld", "Trees", "Tree_Old", Tree, Height, 13f, true),

            new("JunglePalm", "Palms", "Palm_Fan", Tree, Height, 8.5f, true),
            new("JungleClump", "Palms", "Palm_Clump", Tree, Height, 7.5f, true),

            // Our highland pines (tools/art/pines.py), 2 200 to 3 900 triangles near and a seventh of that far.
            new("Pine", "Pines", "Pine_Classic", Tree, Height, 9.5f, true),
            new("PineWide", "Pines", "Pine_Wide", Tree, Height, 9f, true),
            new("PineTall", "Pines", "Pine_Tall", Tree, Height, 12.5f, true),
            new("PineSparse", "Pines", "Pine_Sparse", Tree, Height, 10.5f, true),
            new("PineYoung", "Pines", "Pine_Young", Tree, Height, 5.5f, true),

            // Our undergrowth. Most of it is wider than tall, so sized across; the heliconia, the bird of
            // paradise, the lupins and the grass stand up, and the heliconia teaches the pack its axis.
            new("Bush", "Plants", "Plant_Bush", Plant, Width, 2f),
            new("Fern", "Plants", "Plant_Fern", Plant, Width, 1.8f),
            new("Leafy", "Plants", "Plant_ElephantEar", Plant, Width, 1.9f),
            new("Flowers", "Plants", "Flowers_Wild", Plant, Width, 1.1f),
            new("FernTall", "Plants", "Plant_FernTall", Plant, Width, 1.5f),
            new("Hibiscus", "Plants", "Plant_Hibiscus", Plant, Width, 2f),
            new("Croton", "Plants", "Plant_Croton", Plant, Width, 1.4f),
            new("Bromeliad", "Plants", "Plant_Bromeliad", Plant, Width, 1.1f),
            new("Heliconia", "Plants", "Plant_Heliconia", Plant, Height, 2.2f, true),
            new("Strelitzia", "Plants", "Plant_Strelitzia", Plant, Height, 1.4f, true),
            new("FlowersDaisy", "Plants", "Flowers_Daisy", Plant, Width, 1f),
            new("FlowersLily", "Plants", "Flowers_Lily", Plant, Width, 1.1f),
            new("FlowersSpike", "Plants", "Flowers_Spike", Plant, Height, 0.9f, true),
            new("GrassTuft", "Plants", "Plant_Grass", Plant, Height, 0.9f, true),
            // The terrain's detail layer (#246), drawn by TerrainGenerator.WriteDetail at their own size.
            new("DetailGrass", "Plants", "Detail_Grass", Plant),
            new("DetailGrassTall", "Plants", "Detail_GrassTall", Plant),
            new("DetailFlowers", "Plants", "Detail_Flowers", Plant),
            new("DetailShell", "Plants", "Detail_Shell", Plant),
            new("DetailStarfish", "Plants", "Detail_Starfish", Plant),
            new("DetailDriftwood", "Plants", "Detail_Driftwood", Plant),
            new("DetailSeaweed", "Plants", "Detail_Seaweed", Plant),
            new("DetailPebbles", "Plants", "Detail_Pebbles", Plant),

            // Our rocks and wood. The tall rock is the pack's one upright model and teaches the rest its
            // axis. "Rocks" is the greybox's lintel (GreyboxBuilder), "Log" and "Stump" its totem.
            new("Rocks", "Rocks", "Rock_Slab", Rock, Width, 2.4f),
            new("Boulder", "Rocks", "Rock_Boulder", Rock, Width, 2.4f),
            new("BoulderSmall", "Rocks", "Rock_Small", Rock, Width, 1.4f),
            new("BoulderWide", "Rocks", "Rock_Slab", Rock, Width, 3.2f),
            new("RockTall", "Rocks", "Rock_Tall", Rock, Height, 2.2f, true),
            new("RockMossy", "Rocks", "Rock_Mossy", Rock, Width, 2.2f),
            new("RockCluster", "Rocks", "Rock_Cluster", Rock, Width, 3f),
            new("RockSand", "Rocks", "Rock_Sand", Rock, Width, 2f),
            new("Log", "Rocks", "Log_Fallen", Log, Width, 3.4f),
            new("LogMossy", "Rocks", "Log_Mossy", Log, Width, 4f),
            new("Stump", "Rocks", "Stump_Cut", Log, Height, 0.7f),
            new("StumpBroken", "Rocks", "Stump_Broken", Log, Height, 1.3f),

            // --- the casino (GreyboxBuilder, CasinoFactory) -----------------------------------------
            new("Floor", "Survival", "floor", Structure),
            new("FloorOld", "Survival", "floor-old", Structure),
            new("ThatchRoof", "Pirate", "structure-roof", Structure),
            new("BarCounter", "Furniture", "kitchenBar", Prop),
            new("BarStool", "Furniture", "stoolBar", Small, upright: true),
            new("Bottle", "PirateKit", "Prop_Bottle_1", Small),
            new("CrateBottles", "Pirate", "crate-bottles", Prop),
            new("StringLights", "Holiday", "lights-colored", Prop),
            new("Lantern", "Holiday", "lantern", Small, upright: true),
            new("Table", "Furniture", "table", Prop),

            // --- the buildings' walls and roofs (GreyboxBuilder.Walls, .Gable) --------------------
            // Each wall module is a panel on the +x edge of a one-unit cell, its outside facing +x;
            // each roof has its ridge along z.
            new("TownWall", "Town", "wall-wood", Structure),
            new("TownWindow", "Town", "wall-wood-window-shutters", Structure),
            new("TownCross", "Town", "wall-wood-detail-cross", Structure),
            new("TownDoor", "Town", "wall-wood-door", Structure),
            new("StoneWall", "Town", "wall", Structure),
            new("StoneWindow", "Town", "wall-window-shutters", Structure),
            new("TownRoof", "Town", "roof-gable", Structure),
            new("TownRoofHigh", "Town", "roof-high-gable", Structure),
            new("TownPost", "Town", "pillar-wood", Structure),
            new("TownPlanks", "Town", "planks", Structure),
            new("TownBanner", "Town", "banner-red", Prop),
            new("TownStall", "Town", "stall-red", Prop),
            new("TownLantern", "Town", "lantern", Prop, upright: true),

            // --- the other landmarks, and the stations of P2 (extracted now so the zips open once) ---
            new("Canvas", "Survival", "structure-canvas", Structure),
            new("Booth", "Survival", "structure", Structure),
            new("Bedroll", "Survival", "bedroll", Small),
            new("BoxLarge", "Survival", "box-large", Prop),
            new("Box", "Survival", "box", Small),
            new("Barrel", "PirateKit", "Prop_Barrel", Prop),
            new("BarrelOpen", "Survival", "barrel-open", Prop),
            new("Bucket", "PirateKit", "Prop_Bucket", Small),
            new("Chest", "PirateKit", "Prop_Chest_Closed", Prop),
            new("Workbench", "Survival", "workbench", Prop),
            new("CampfirePit", "Survival", "campfire-pit", Prop),
            new("Signpost", "Survival", "signpost", Small, upright: true),
            new("Palisade", "Survival", "fence-fortified", Structure),
            // A whole ship on its side under the hull's list, 20 636 triangles: one on the island.
            new("Wreck", "PirateKit", "Ship_Large", ArtCategory.Wreck),
            new("Crate", "Pirate", "crate", Prop),
            new("PirateBarrel", "PirateKit", "Prop_Barrel", Prop),
            // The wreck's small boat (5 578) and the cave's cliff face (8 596, three of them) are
            // landmark pieces placed a handful of times, so they are budgeted as the wreck is.
            new("RowBoat", "PirateKit", "Ship_Small", ArtCategory.Wreck),
            new("Cliff", "PirateKit", "Environment_Cliff1", ArtCategory.Wreck),

            // --- the animals (AnimalArt) ----------------------------------------------------------
            // An animal's id is its species id with a capital; a species with no row keeps its boxes.
            new("Boar", "Animals", "Animal_Boar", ArtCategory.Animal),
            new("Deer", "Animals", "Animal_Deer", ArtCategory.Animal),
            new("Stag", "Animals", "Animal_Stag", ArtCategory.Animal),
            new("Jaguar", "Animals", "Animal_Jaguar", ArtCategory.Animal),
            new("Gull", "Animals", "Animal_Gull", ArtCategory.Animal),

            // --- items lying on the ground (ItemArtFactory, #206) --------------------------------
            new("MeatRaw", "Food", "meat-raw", Small),
            new("MeatCooked", "Food", "meat-cooked", Small),
            new("FishCooked", "Food", "fish", Small),
            new("Coconut", "Food", "coconut", Small),
            new("Rum", "Food", "bottle-oil", Small),
            new("FishRaw", "Survival", "fish", Small),
            new("GlassBottle", "Survival", "bottle", Small),
            new("WaterBottle", "Survival", "bottle-large", Small),
            new("Planks", "Survival", "resource-planks", Small),
            new("Flint", "Survival", "resource-stone", Small),
            new("Firewood", "Survival", "resource-wood", Small),
            new("ScrapMetal", "Survival", "metal-panel-screws-half", Small),
            new("ClothRoll", "Survival", "bedroll-packed", Small),

            // --- the weapons (WeaponFactory.Art) -------------------------------------------------
            // Fitted to the weapon's old box. The knife stands on its handle in the kit, and says so
            // here so the weapon pack learns its axis from it; the guns lie along +z already.
            new("Pistol", "Weapon", "pistol", Weapon),
            new("PistolSilenced", "Weapon", "pistolSilencer", Weapon),
            new("PistolAuto", "Weapon", "uziSilencer", Weapon),
            new("Smg", "Weapon", "uziLong", Weapon),
            new("Shotgun", "Weapon", "shotgun", Weapon),
            new("Rifle", "Weapon", "sniper", Weapon),
            new("Knife", "Weapon", "knife_sharp", Weapon, upright: true),
            new("Axe", "Survival", "tool-axe", Weapon),
            new("FireAxe", "Survival", "tool-axe-upgraded", Weapon),
            new("Shovel", "Survival", "tool-shovel", Weapon),
            new("Club", "Survival", "tree-log-small", Weapon),
        };

        public static Model Find(string id)
        {
            foreach (Model model in Models)
                if (model.Id == id) return model;

            throw new System.ArgumentException($"[ArtCatalog] No model called '{id}'.");
        }

        public static Pack PackOf(Model model) => FindPack(model.Pack);

        public static Pack FindPack(string name)
        {
            foreach (Pack pack in Packs)
                if (pack.Name == name) return pack;

            throw new System.ArgumentException($"[ArtCatalog] No pack called '{name}'.");
        }

        /// <summary>Where the extractor puts a model and the importer looks for it.</summary>
        public static string PathOf(Model model) => $"{PackOf(model).Folder}/{model.File}.fbx";
    }
}
