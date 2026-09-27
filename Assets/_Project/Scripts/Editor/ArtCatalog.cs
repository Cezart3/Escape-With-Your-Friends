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

            public Pack(string author, string name, string zipHint, string page, bool atlas,
                        bool textured = false)
            {
                Author = author;
                Name = name;
                ZipHint = zipHint;
                Page = page;
                Atlas = atlas;
                Textured = textured;
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

            // The one kit still on flat colours: wood, carpet, metal, woodDark. See ArtLibrary.Remap.
            new("Kenney", "Furniture", "furniture", "https://kenney.nl/assets/furniture-kit", false),

            // The people (T9). No entries in Models, so ArtExtract passes over them: their file names
            // were never seen, and CharacterArt extracts them by kind instead.
            new("Quaternius", "UniversalBaseCharacters", "basecharacter",
                "https://quaternius.com/packs/universalbasecharacters.html", false),
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
            new("PalmStraight", "Pirate", "palm-straight", Tree, Height, 8.5f, true),
            new("PalmBend", "Pirate", "palm-bend", Tree, Height, 7.5f, true),
            new("PalmTall", "Pirate", "palm-detailed-straight", Tree, Height, 9.5f, true),
            new("PalmLean", "Pirate", "palm-detailed-bend", Tree, Height, 8.5f, true),

            new("JungleRound", "Platformer", "tree", Tree, Height, 9f, true),
            new("JungleTall", "Coaster", "tree-large", Tree, Height, 10f, true),
            new("JungleSmall", "Coaster", "tree", Tree, Height, 6.5f, true),
            new("JunglePalm", "Pirate", "palm-detailed-bend", Tree, Height, 8f, true),

            new("Pine", "Survival", "tree", Tree, Height, 9f, true),
            new("PineTall", "Survival", "tree-tall", Tree, Height, 12f, true),
            new("PineWide", "Castle", "tree-large", Tree, Height, 10f, true),
            new("PineSmall", "Castle", "tree-small", Tree, Height, 6f, true),

            // Ground plants are wider than they are tall, so they are sized across.
            new("Leafy", "Pirate", "grass-plant", Plant, Width, 1.6f),
            new("Frond", "Platformer", "plant", Plant, Width, 1.2f),
            new("Grass", "Survival", "grass-large", Plant, Width, 1.0f),
            new("Flowers", "Platformer", "flowers", Plant, Width, 1.2f),

            new("Rocks", "Pirate", "rocks-a", Rock, Width, 2.4f),
            new("RocksSmall", "Pirate", "rocks-b", Rock, Width, 1.4f),
            new("RocksSand", "Pirate", "rocks-sand-a", Rock, Width, 3.2f),
            new("Log", "Survival", "tree-log", Log, Width, 3f),
            new("Stump", "Survival", "tree-trunk", Log, Height, 0.8f),

            // --- the casino (GreyboxBuilder, CasinoFactory) -----------------------------------------
            new("Floor", "Survival", "floor", Structure),
            new("FloorOld", "Survival", "floor-old", Structure),
            new("ThatchRoof", "Pirate", "structure-roof", Structure),
            new("BarCounter", "Furniture", "kitchenBar", Prop),
            new("BarStool", "Furniture", "stoolBar", Small, upright: true),
            new("Bottle", "Pirate", "bottle", Small),
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
            new("Barrel", "Survival", "barrel", Prop),
            new("BarrelOpen", "Survival", "barrel-open", Prop),
            new("Bucket", "Survival", "bucket", Small),
            new("Chest", "Survival", "chest", Prop),
            new("Workbench", "Survival", "workbench", Prop),
            new("CampfirePit", "Survival", "campfire-pit", Prop),
            new("Signpost", "Survival", "signpost", Small, upright: true),
            new("Palisade", "Survival", "fence-fortified", Structure),
            new("Wreck", "Pirate", "ship-wreck", ArtCategory.Wreck),
            new("Crate", "Pirate", "crate", Prop),
            new("PirateBarrel", "Pirate", "barrel", Prop),
            new("RowBoat", "Pirate", "boat-row-small", Prop),

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
