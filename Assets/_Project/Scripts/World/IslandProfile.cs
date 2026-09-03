using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// Every number that decides what the island looks like, in one text asset.
    ///
    /// It is a ScriptableObject and not a pile of constants because the island has to be tunable
    /// from the terminal: the asset is YAML, so a parameter can be changed with sed and the terrain
    /// regenerated with one batchmode command. It also means the shape is versioned in git next to
    /// the code that reads it, and a bad island is a one-line revert.
    ///
    /// Units are metres and world space unless a field says otherwise. Sea level is y = 0 and the
    /// island is centred on the origin, so a coordinate is in [-Size/2, +Size/2] on both axes.
    /// </summary>
    [CreateAssetMenu(fileName = "Island", menuName = "EWYF/Island Profile")]
    public class IslandProfile : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Everything derives from this. Same seed plus same parameters means the same island, byte for byte.")]
        public int Seed = 20260830;

        [Header("Extent")]
        [Tooltip("Side of the square terrain in metres. 1024 is the M2 target: about one square kilometre.")]
        public float Size = 1024f;

        [Tooltip("Heightmap samples per side. Unity demands 2^n+1. 1025 over 1024m is one sample per metre.")]
        public int Resolution = 1025;

        [Tooltip("How far the seabed drops below sea level. Deep enough that the water plane hides it from the shore.")]
        public float SeabedDepth = 40f;

        [Tooltip("Ceiling for land height. The terrain object is this tall plus SeabedDepth.")]
        public float PeakHeight = 160f;

        [Header("Inland relief")]
        [Tooltip("Metres per cell of the first noise octave. Bigger means fewer, wider hills.")]
        public float HillFeatureSize = 380f;

        [Tooltip("Noise octaves stacked into the base relief. Six is the point where more costs time and adds nothing visible.")]
        public int HillOctaves = 6;

        [Tooltip("Amplitude multiplier per octave. 0.5 is standard fBm.")]
        public float HillGain = 0.5f;

        [Tooltip("Frequency multiplier per octave. Slightly off 2.0 so octaves do not line up into grid artefacts.")]
        public float HillLacunarity = 2.03f;

        [Tooltip("Vertical scale of the base relief in metres.")]
        public float HillHeight = 46f;

        [Tooltip("Noise value that maps to sea level. Below 0.5 means more of the island is above water.")]
        public float HillWaterLine = 0.40f;

        [Header("Domain warp")]
        [Tooltip("How far the sampling position is dragged, in metres. This is what stops the hills looking like blobs.")]
        public float WarpStrength = 110f;

        [Tooltip("Metres per cell of the warp noise. Larger than the hills, so it bends whole ridges rather than shaking them.")]
        public float WarpFeatureSize = 500f;

        [Header("Coast")]
        [Tooltip("Fraction of the half-size where land is still guaranteed. Inside this radius the falloff mask is 1.")]
        public float CoastInnerRadius = 0.42f;

        [Tooltip("Fraction of the half-size where the mask reaches 0. Past this everything is seabed.")]
        public float CoastOuterRadius = 0.88f;

        [Tooltip("How much noise is added to the radius, as a fraction of the half-size. This is what makes bays and headlands.")]
        public float CoastRaggedness = 0.17f;

        [Tooltip("Metres per cell of the coastline noise. Small values give a fiddly, fjord-like shore.")]
        public float CoastFeatureSize = 260f;

        [Header("Beaches")]
        [Tooltip("Height band either side of sea level that gets flattened into beach, in metres.")]
        public float BeachBand = 5f;

        [Tooltip("Height multiplier at sea level. 0.3 means the shore rises at a third of its natural slope.")]
        public float BeachFlatten = 0.3f;

        [Header("Mountain")]
        [Tooltip("Where the peak sits, as a fraction of the half-size on each axis. (0,0) is the middle of the island.")]
        public Vector2 MountainCentre = new Vector2(-0.18f, 0.22f);

        [Tooltip("Radius of the dome as a fraction of the half-size.")]
        public float MountainRadius = 0.34f;

        [Tooltip("Metres the dome adds at its centre, before ridge noise.")]
        public float MountainHeight = 112f;

        [Tooltip("Exponent on the dome falloff. Higher is a sharper, more conical peak.")]
        public float MountainSharpness = 2.4f;

        [Tooltip("How much ridged noise carves the dome, 0 to 1. This is what turns a smooth hemisphere into a mountain.")]
        public float MountainRidge = 0.35f;

        [Tooltip("Metres per cell of the ridge noise.")]
        public float MountainRidgeFeatureSize = 150f;

        [Header("Ground cover")]
        [Tooltip("Metres above sea level where sand gives way to whatever grows inland.")]
        public float SandTop = 3.5f;

        [Tooltip("Height over which sand fades out, in metres. Wider is a softer dune edge.")]
        public float SandBlend = 3f;

        [Tooltip("Slope where rock takes over, as a gradient (rise over run). 0.7 is about 35 degrees.")]
        public float RockSlope = 0.7f;

        [Tooltip("Slope range over which rock fades in.")]
        public float RockSlopeBlend = 0.25f;

        [Tooltip("Height in metres above which the ground goes bare regardless of slope.")]
        public float RockHeight = 78f;

        [Tooltip("Metres over which the bare high ground fades in.")]
        public float RockHeightBlend = 24f;

        [Tooltip("Metres per cell of the dirt patch noise. This is roughly the size of a clearing.")]
        public float DirtFeatureSize = 90f;

        [Tooltip("Noise level above which grass becomes dirt, 0 to 1. Higher means fewer patches.")]
        public float DirtThreshold = 0.58f;

        [Tooltip("Width of the dirt patch edge, in noise units. Small values give hard-edged patches.")]
        public float DirtBlend = 0.07f;

        [Tooltip("How much noise wobbles the cover boundaries, in metres of height and gradient. Stops the shoreline reading as a contour line.")]
        public float CoverJitter = 1.6f;

        [Tooltip("Metres per cell of the boundary jitter noise.")]
        public float CoverJitterFeatureSize = 26f;

        [Header("Splatmap")]
        [Tooltip("Alphamap resolution. 512 over a 1024m island is one splat texel every two metres.")]
        public int SplatResolution = 512;

        [Tooltip("Metres per repeat of the sand texture.")]
        public float SandTiling = 9f;

        [Tooltip("Metres per repeat of the grass texture.")]
        public float GrassTiling = 7f;

        [Tooltip("Metres per repeat of the rock texture.")]
        public float RockTiling = 12f;

        [Tooltip("Metres per repeat of the dirt texture.")]
        public float DirtTiling = 7f;

        [Header("Vegetation")]
        [Tooltip("Metres between canopy candidates. One tree per cell at most, so this is the densest the forest can ever be.")]
        public float CanopyCellSize = 4f;

        [Tooltip("Metres between undergrowth candidates. Finer than the canopy so bushes fill in underneath it.")]
        public float UndergrowthCellSize = 2.4f;

        [Tooltip("How far a plant may wander inside its cell, as a fraction of the cell. 0 is a visible grid, 1 lets neighbours touch.")]
        public float FloraJitter = 0.9f;

        [Tooltip("Metres above sea level below which nothing grows. Keeps the waterline clear of half-drowned trees.")]
        public float FloraMinHeight = 0.35f;

        [Tooltip("Smallest size multiplier a plant can roll.")]
        public float FloraMinScale = 0.8f;

        [Tooltip("Largest size multiplier a plant can roll.")]
        public float FloraMaxScale = 1.35f;

        [Tooltip("Chance a suitable beach cell grows a palm, before the grove mask.")]
        public float PalmDensity = 0.8f;

        [Tooltip("Chance a suitable inland cell grows a jungle tree, before the grove mask.")]
        public float JungleDensity = 0.95f;

        [Tooltip("Chance a suitable high cell grows a highland tree, before the grove mask.")]
        public float HighlandDensity = 0.9f;

        [Tooltip("Chance a suitable cell grows a bush, before the grove mask.")]
        public float BushDensity = 0.6f;

        [Tooltip("Metres per cell of the grove noise. Roughly the size of one thicket or one clearing.")]
        public float GroveFeatureSize = 140f;

        [Tooltip("Grove noise below this thins out to nothing, 0 to 1. Higher means more clearings.")]
        public float GroveFloor = 0.36f;

        [Tooltip("How far above the floor the grove noise has to climb for full density. Small, because the noise rarely strays far from the middle.")]
        public float GroveSpan = 0.14f;

        [Header("Grass detail")]
        [Tooltip("Detail map resolution. 512 over a 1024m island is one grass cell every two metres.")]
        public int DetailResolution = 512;

        [Tooltip("Detail cells per patch. Unity draws a patch at a time, so this is the culling granularity.")]
        public int DetailPerPatch = 32;

        [Tooltip("Most grass billboards allowed in one detail cell.")]
        public int GrassPerCell = 6;

        [Tooltip("Least grass cover a cell needs before any grass grows there, 0 to 1.")]
        public float GrassThreshold = 0.35f;

        [Header("Vegetation performance")]
        [Tooltip("Metres at which trees stop drawing entirely.")]
        public float TreeDistance = 320f;

        [Tooltip("Metres at which trees drop to billboards. The gap up to TreeDistance is the cheap band.")]
        public float TreeBillboardDistance = 90f;

        [Tooltip("Metres over which a tree fades between mesh and billboard. Zero pops.")]
        public float TreeCrossFade = 25f;

        [Tooltip("Most full-mesh trees drawn at once. Everything beyond this is a billboard however close it is.")]
        public int TreeMaximumFullLOD = 60;

        [Tooltip("Metres at which grass stops drawing. The single biggest cost knob on a weak GPU.")]
        public float DetailDistance = 85f;

        [Tooltip("Global multiplier on grass density, 0 to 1. The min-spec escape hatch.")]
        public float DetailDensity = 0.8f;

        [Header("Points of interest")]
        [Tooltip("Where the camps, the shop and the village stand. Their pads are part of the height function, so changing this changes the island.")]
        public POICatalog Pois;

        [Header("Water")]
        [Tooltip("Half-width in metres of the wavy patch that follows the camera. Everything beyond it is the flat ring.")]
        public float WaterPatchExtent = 320f;

        [Tooltip("Metres per quad of the water patch. Waves shorter than about four cells alias, so this bounds the wavelengths.")]
        public float WaterCellSize = 4f;

        [Tooltip("Metres over which the waves flatten out before the seam with the far ring. Too narrow and the seam creases.")]
        public float WaterFadeBand = 70f;

        [Tooltip("Half-width in metres of the flat ring that fills the horizon. Sixteen vertices, so it may as well be large.")]
        public float WaterHorizon = 4000f;

        [Tooltip("Side of the baked water-depth mask. 512 over a 1024m island is one texel every two metres, fine enough for the surf line.")]
        public int WaterDepthResolution = 512;

        [Tooltip("How deep the seabed has to be for the water to read as fully open ocean.")]
        public float ShoreFadeDepth = 14f;

        [Tooltip("Metres of depth over which the shoreline foam is drawn. Roughly how far up the beach the surf reaches.")]
        public float FoamWidth = 1.6f;

        /// <summary>Total vertical range of the terrain object: seabed plus the tallest land allowed.</summary>
        public float TotalHeight => SeabedDepth + PeakHeight;

        /// <summary>
        /// Corner of the terrain in world space. The terrain is pushed down by the seabed depth so that
        /// a heightmap value of 0 lands on the bottom of the sea and sea level stays at y = 0, where
        /// the water plane and every buoyancy calculation expect it.
        /// </summary>
        public Vector3 TerrainOrigin => new Vector3(-Size * 0.5f, -SeabedDepth, -Size * 0.5f);
    }
}
