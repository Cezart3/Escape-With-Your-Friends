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

        /// <summary>The one swatch atlas every current Kenney kit is painted from.</summary>
        public const string Colormap = "colormap.png";

        /// <summary>A pack painted with real textures rather than one atlas. See <see cref="Pack.Atlas"/>.</summary>
        public const string Textured = "Textures";

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
            /// How the pack is painted. A file name: every model is painted from that one swatch atlas
            /// (<see cref="Colormap"/> for every current Kenney kit). Null: a material per colour, as in
            /// the older kits. <see cref="Textured"/>: real painted textures, one per material, which
            /// the extractor copies whole and the importer matches to each material by name.
            /// </summary>
            public readonly string Atlas;

            /// <summary>
            /// What the licence file says when the zip carries none. Null means a zip without one is
            /// refused. Set only for a pack whose page states the licence and whose download (a Google
            /// Drive folder, zipped by Drive) drops the file; ArtExtract writes this into License.txt
            /// and says so in the log, so the claim is visibly ours and not the zip's.
            /// </summary>
            public readonly string LicenceNote;

            public Pack(string author, string name, string zipHint, string page, string atlas,
                        string licenceNote = null)
            {
                Author = author;
                Name = name;
                ZipHint = zipHint;
                Page = page;
                Atlas = atlas;
                LicenceNote = licenceNote;
            }

            public string Folder => $"{Root}/{Author}/{Name}";
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
            new("Kenney", "Pirate", "pirate", "https://kenney.nl/assets/pirate-kit", Colormap),
            new("Kenney", "Survival", "survival", "https://kenney.nl/assets/survival-kit", Colormap),
            new("Kenney", "Platformer", "platformer", "https://kenney.nl/assets/platformer-kit", Colormap),
            new("Kenney", "Coaster", "coaster", "https://kenney.nl/assets/coaster-kit", Colormap),
            new("Kenney", "Castle", "castle", "https://kenney.nl/assets/castle-kit", Colormap),
            new("Kenney", "Holiday", "holiday", "https://kenney.nl/assets/holiday-kit", Colormap),

            // P4, the vehicles (T11).
            new("Kenney", "Car", "car-kit", "https://kenney.nl/assets/car-kit", Colormap),
            new("Kenney", "Watercraft", "watercraft", "https://kenney.nl/assets/watercraft-pack", Colormap),

            // P4, the weapons (T12). Flat colours like the furniture: grey, dark, wood and the rest.
            new("Kenney", "Weapon", "weapon", "https://kenney.nl/assets/weapon-pack", null),

            // The one kit still on flat colours: wood, carpet, metal, woodDark. See ArtLibrary.Remap.
            new("Kenney", "Furniture", "furniture", "https://kenney.nl/assets/furniture-kit", null),

            // P6 (ART-PLAN §9): one hand across the island. File names below were read from the zips
            // Cezar downloaded (`unzip -l`, 2026-09-27), not guessed.
            //
            // The nature kit is the free Standard tier: 68 of its 116 models, and no palms, which is
            // why the palms come from the pirate kit by the same author. Painted textures, with leaf
            // cards that need alpha clip. Its zip holds an "FBX (Unity)" folder, which ArtExtract
            // prefers over the plain "FBX" one.
            new("Quaternius", "NatureMegaKit", "nature", "https://quaternius.com/packs/stylizednaturemegakit.html",
                Textured),

            // Quaternius's own download is a Google Drive folder, and Drive zips it as
            // "drive-download-<date>.zip" with no licence file in it. An empty hint matches any zip;
            // the extractor then takes the one holding the most of these files, which no Kenney zip
            // does. Painted from one small swatch atlas, like a Kenney kit.
            new("Quaternius", "PirateKit", "", "https://quaternius.com/packs/piratekit.html", "Atlas_Pirate.png",
                "CC0 1.0 Universal, as stated on https://quaternius.com/packs/piratekit.html. The zip, "
                + "a Google Drive download, carried no licence file; this line was written by ArtExtract."),

            // The people (T9). No entries in Models, so ArtExtract passes over them: their file names
            // were never seen, and CharacterArt extracts them by kind instead.
            new("Quaternius", "UniversalBaseCharacters", "basecharacter",
                "https://quaternius.com/packs/universalbasecharacters.html", null),
            new("Quaternius", "UniversalAnimationLibrary", "animationlibrary",
                "https://quaternius.com/packs/universalanimationlibrary.html", null),
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
            // Quaternius since P6. The ids are the Kenney pass's, so IslandFlora and every builder
            // that places one of these is unchanged; only where the id points moved.
            new("PalmStraight", "PirateKit", "Environment_PalmTree_1", Tree, Height, 8.5f, true),
            new("PalmBend", "PirateKit", "Environment_PalmTree_2", Tree, Height, 7.5f, true),
            new("PalmTall", "PirateKit", "Environment_PalmTree_3", Tree, Height, 9.5f, true),
            new("PalmLean", "PirateKit", "Environment_PalmTree_2", Tree, Height, 8.5f, true),

            new("JungleRound", "NatureMegaKit", "CommonTree_1", Tree, Height, 9f, true),
            new("JungleTall", "NatureMegaKit", "CommonTree_2", Tree, Height, 10f, true),
            new("JungleSmall", "NatureMegaKit", "CommonTree_5", Tree, Height, 6.5f, true),
            new("JunglePalm", "PirateKit", "Environment_PalmTree_3", Tree, Height, 8f, true),

            new("Pine", "NatureMegaKit", "Pine_1", Tree, Height, 9f, true),
            new("PineTall", "NatureMegaKit", "Pine_3", Tree, Height, 12f, true),
            new("PineWide", "NatureMegaKit", "Pine_2", Tree, Height, 10f, true),
            new("PineSmall", "NatureMegaKit", "Pine_5", Tree, Height, 6f, true),

            // Ground plants are wider than they are tall, so they are sized across.
            new("Leafy", "NatureMegaKit", "Bush_Common", Plant, Width, 1.6f),
            new("Frond", "NatureMegaKit", "Fern_1", Plant, Width, 1.2f),
            new("Grass", "NatureMegaKit", "Grass_Wispy_Tall", Plant, Width, 1.0f),
            new("Flowers", "NatureMegaKit", "Flower_4_Group", Plant, Width, 1.2f),

            new("Rocks", "NatureMegaKit", "Rock_Medium_1", Rock, Width, 2.4f),
            new("RocksSmall", "NatureMegaKit", "Rock_Medium_2", Rock, Width, 1.4f),
            new("RocksSand", "NatureMegaKit", "Rock_Medium_3", Rock, Width, 3.2f),

            // Placed nowhere. Kenney's pirate kit still supplies the thatch and the crates, which
            // cannot tell which way is up; its palm, which can, stays in the catalogue to teach them.
            new("KenneyPalm", "Pirate", "palm-straight", Tree, Height, 8.5f, true),

            // The free nature kit has no log or stump, so these two stay Kenney's for now.
            new("Log", "Survival", "tree-log", Log, Width, 3f),
            new("Stump", "Survival", "tree-trunk", Log, Height, 0.8f),

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
            new("Wreck", "PirateKit", "Ship_Large", ArtCategory.Wreck),
            new("Crate", "Pirate", "crate", Prop),
            new("PirateBarrel", "PirateKit", "Prop_Barrel", Prop),
            new("RowBoat", "PirateKit", "Ship_Small", ArtCategory.Wreck),
            // A cliff face for the cave's walls. Filed with the wreck: one landmark piece, placed once,
            // allowed what a landmark costs rather than what a rock scattered a thousand times does.
            new("Cliff", "PirateKit", "Environment_Cliff1", ArtCategory.Wreck),

            // --- the vehicles (VehicleBuilder, BoatBuilder) -----------------------------------------
            // An open race car, not the plan's SUV: see VehicleBuilder.Dress.
            new("Buggy", "Car", "race", ArtCategory.Vehicle),
            new("Boat", "Watercraft", "boat-speed-j", ArtCategory.Vehicle),

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
