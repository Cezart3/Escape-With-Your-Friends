using System;
using System.Collections.Generic;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>The slot cabinets. Each one is a whole game in <see cref="SlotMath"/>. Append only: prefabs store the number.</summary>
    public enum SlotKind
    {
        /// <summary>Five reels, three rows, twenty lines, fruit and sevens, and a double-or-nothing card.</summary>
        Sevens,

        /// <summary>Six by five, pays anywhere, symbols tumble, lava orbs multiply the lot.</summary>
        Volcano,

        /// <summary>Seven by seven, clusters of five or more, and every spot that pops twice starts multiplying.</summary>
        Reef,

        /// <summary>Volcano's grid and tumbles, no base multipliers, bombs that multiply one sequence each in free spins.</summary>
        Fruit,

        /// <summary>Five reels, ten lines, fish worth chips, and a castaway wild who reels them in during free spins.</summary>
        Lagoon,
    }

    /// <summary>
    /// A tiny deterministic generator (SplitMix64). Not <see cref="System.Random"/>: that one's
    /// sequence for a given seed is an implementation detail of whichever runtime is underneath, and
    /// the whole point here is that the host and every client, Mono or IL2CPP, get the same spin out
    /// of the same seed.
    /// </summary>
    public struct SlotRng
    {
        ulong _state;

        public SlotRng(int seed) => _state = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x2545F4914F6CDD1DUL;

        public uint Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (uint)((z ^ (z >> 31)) >> 32);
        }

        /// <summary>0 to <paramref name="max"/>-1. The modulo bias is under one in a million for any range used here.</summary>
        public int Range(int max) => (int)(Next() % (uint)max);

        /// <summary>An index into <paramref name="weights"/>, picked in proportion to them.</summary>
        public int Pick(int[] weights, int total)
        {
            int roll = Range(total);
            for (int i = 0; i < weights.Length; i++)
            {
                roll -= weights[i];
                if (roll < 0) return i;
            }

            return weights.Length - 1;
        }
    }

    /// <summary>
    /// One picture of the machine. A spin is a list of these: the reels landing, then each tumble,
    /// then each free spin and its tumbles. A client plays them in order for <see cref="Seconds"/>
    /// each, which is also how the server knows how long to wait before it pays.
    /// </summary>
    public sealed class SlotFrame
    {
        /// <summary>Symbol per cell, <c>col * rows + row</c>, row 0 at the top.</summary>
        public int[] Grid;

        /// <summary>The cells that paid in this picture. They vanish before the next one.</summary>
        public bool[] Winning;

        /// <summary>Everything won so far this spin, in hundredths of the bet.</summary>
        public long RunningPct;

        /// <summary>The multiplier worth showing: the orbs' sum or the free-spin total (Volcano), 0 if none.</summary>
        public int Multiplier;

        /// <summary>Reef's multiplier spots, per cell: 0 nothing, 1 marked, 2..128 the multiplier. Null elsewhere.</summary>
        public int[] Spots;

        /// <summary>Free spins still to play after this one, or -1 in the base game.</summary>
        public int FreeSpinsLeft = -1;

        /// <summary>True when this picture is a fresh drop - the reels spin before it shows.</summary>
        public bool Drop;

        /// <summary>How long this picture stays up.</summary>
        public float Seconds;
    }

    /// <summary>What one press of the spin button came to. Everything in it follows from the seed.</summary>
    public sealed class SlotResult
    {
        public SlotKind Kind;
        public int Seed;
        public int Bet;

        /// <summary>Chips handed back, stake not included. Zero on a loss.</summary>
        public int Win;

        /// <summary>The same in hundredths of the bet, before rounding down to whole chips.</summary>
        public long WinPct;

        public int FreeSpins;
        public bool Capped;

        /// <summary>The feature was bought: the first drop was forced to trigger it.</summary>
        public bool Bought;

        /// <summary>Played with the ante: a quarter more staked, the feature twice as likely. See <see cref="SlotMath.AnteStake"/>.</summary>
        public bool Ante;
        public readonly List<SlotFrame> Frames = new();

        public float Seconds
        {
            get
            {
                float total = 0f;
                foreach (SlotFrame frame in Frames) total += frame.Seconds;
                return total;
            }
        }
    }

    /// <summary>
    /// The three games, as pure arithmetic: a seed and a bet in, every picture and the payout out.
    /// No Unity, no network, no clock - which is what lets the harness play a million spins of each
    /// in a few seconds and hold the return to player to a number, and what lets a client replay a
    /// spin from four bytes instead of being sent the grids.
    ///
    /// **The names, symbols and numbers are ours.** The mechanics are the genre's common stock -
    /// fixed lines with a double-up card, pay-anywhere with tumbles and multiplier orbs, clusters
    /// with sticky multiplier spots - and none of that is anybody's to own. Nothing here borrows a
    /// title, a character, a symbol set or a paytable.
    ///
    /// Every paytable is in hundredths of the total bet, so a win of 25 is a quarter of the stake.
    /// A spin's pays are summed in those units and rounded down to chips once, at the end.
    /// </summary>
    public static class SlotMath
    {
        /// <summary>Stakes a cabinet offers, in chips. The bet button cycles through them.</summary>
        public static readonly int[] Bets = { 10, 20, 50, 100 };

        /// <summary>No spin pays more than this many times the bet. Keeps a lucky night finite.</summary>
        public const int MaxWinX = 5000;

        public static int Cols(SlotKind kind) => kind switch { SlotKind.Sevens or SlotKind.Lagoon => 5, SlotKind.Volcano or SlotKind.Fruit => 6, _ => 7 };
        public static int Rows(SlotKind kind) => kind switch { SlotKind.Sevens or SlotKind.Lagoon => 3, SlotKind.Volcano or SlotKind.Fruit => 5, _ => 7 };

        public static string Title(SlotKind kind) => kind switch
        {
            SlotKind.Sevens => "Coconut Sevens",
            SlotKind.Volcano => "Wrath of the Volcano",
            SlotKind.Fruit => "Fruit Tumble",
            SlotKind.Lagoon => "Lagoon Catch",
            _ => "Reef Rush",
        };

        public static SlotResult Spin(SlotKind kind, int seed, int bet, bool buy = false, bool ante = false)
        {
            ante &= HasAnte(kind) && !buy;
            var result = new SlotResult { Kind = kind, Seed = seed, Bet = bet, Bought = buy, Ante = ante };
            var rng = new SlotRng(seed);

            switch (kind)
            {
                case SlotKind.Sevens: Sevens.Play(ref rng, result); break;
                case SlotKind.Volcano: Volcano.Play(ref rng, result, buy, ante, Volcano.Rules); break;
                case SlotKind.Fruit: Volcano.Play(ref rng, result, buy, ante, Fruit.Rules); break;
                case SlotKind.Lagoon: Lagoon.Play(ref rng, result, buy); break;
                default: Reef.Play(ref rng, result, buy); break;
            }

            // A drop one scatter short holds its last reels back. The screen needs the time for it, and
            // the server pays at the sum of the frames, so the time is the arithmetic's, not the screen's.
            foreach (SlotFrame frame in result.Frames)
                if (Tease(kind, frame) >= 0) frame.Seconds += TeaseSeconds;

            long cap = MaxWinX * 100L;
            if (result.WinPct >= cap)
            {
                result.WinPct = cap;
                result.Capped = true;
            }

            result.Win = (int)(bet * result.WinPct / 100);
            return result;
        }

        /// <summary>What a frame's running total reads as, in chips.</summary>
        public static int Chips(int bet, long pct) => (int)(bet * Math.Min(pct, MaxWinX * 100L) / 100);

        /// <summary>
        /// What buying the feature costs, in bets. Zero where there is nothing to buy. Simulated so a
        /// bought feature returns about the same as the base game (see the harness's Rtp band).
        /// </summary>
        public static int BuyPrice(SlotKind kind) => kind switch
        {
            SlotKind.Volcano => Volcano.BuyPrice,
            SlotKind.Fruit => Fruit.BuyPrice,
            SlotKind.Reef => Reef.BuyPrice,
            SlotKind.Lagoon => Lagoon.BuyPrice,
            _ => 0,
        };

        /// <summary>Games with an ante: a quarter more on every spin for twice the chance of the feature. No buying with it on.</summary>
        public static bool HasAnte(SlotKind kind) => kind is SlotKind.Volcano or SlotKind.Fruit;

        /// <summary>What a spin at <paramref name="bet"/> stakes with the ante on. Pays are still on the bet.</summary>
        public static int AnteStake(int bet) => bet + bet / 4;

        /// <summary>The symbol that pays or triggers wherever it lands, and how many it takes. The screen's anticipation reads it.</summary>
        public static (int Symbol, int Need) Scatter(SlotKind kind) => kind switch
        {
            SlotKind.Sevens => (Sevens.Star, 3),
            SlotKind.Volcano => (Volcano.Peak, 4),
            SlotKind.Fruit => (Fruit.Sun, 4),
            SlotKind.Reef => (Reef.Chest, 3),
            _ => (Lagoon.Hook, 3),
        };

        /// <summary>What a teased drop adds to its frame.</summary>
        public const float TeaseSeconds = 1.2f;

        /// <summary>
        /// The first reel a drop holds back for its scatter, or -1: the reels before it already show
        /// all but one of what the scatter needs. About one drop in forty on every game.
        /// </summary>
        public static int Tease(SlotKind kind, SlotFrame frame)
        {
            if (!frame.Drop || frame.Grid == null) return -1;

            (int symbol, int need) = Scatter(kind);
            int cols = Cols(kind), rows = Rows(kind), seen = 0;
            for (int c = 0; c < cols - 1; c++)
            {
                for (int r = 0; r < rows; r++) if (frame.Grid[c * rows + r] == symbol) seen++;
                if (seen >= need - 1) return c + 1;
            }

            return -1;
        }

        /// <summary>Games a player can leave spinning on their own: Lagoon Catch, the one built to be farmed.</summary>
        public static bool HasAutoplay(SlotKind kind) => kind == SlotKind.Lagoon;

        /// <summary>Every stake puts this many hundredths of itself into the shared jackpot.</summary>
        public const int JackpotPct = 1;

        /// <summary>What the jackpot restarts at after it drops, in chips.</summary>
        public const int JackpotSeed = 500;

        /// <summary>A stake of s drops the jackpot s times in this many - a 100-chip spin one in 1,500.</summary>
        public const int JackpotOdds = 150000;

        /// <summary>
        /// Whether this spin drops the jackpot. Its own salted generator, so the reels of every seed
        /// stay exactly what they were before the jackpot existed. A bigger stake hits more often, in
        /// proportion, so it is worth the same fraction of every stake, a bonus buy's included.
        /// </summary>
        public static bool JackpotHit(int seed, int stake)
        {
            var rng = new SlotRng(seed ^ 0x5EED1ACC);
            return rng.Range(JackpotOdds) < stake;
        }

        /// <summary>Turns random cells into <paramref name="symbol"/> until there are <paramref name="want"/>. Clears an orb it lands on.</summary>
        static void Force(ref SlotRng rng, int[] grid, int[] extras, int symbol, int want)
        {
            while (Count(grid, symbol) < want)
            {
                int cell = rng.Range(grid.Length);
                if (grid[cell] == symbol) continue;
                grid[cell] = symbol;
                if (extras != null) extras[cell] = 0;
            }
        }

        static int Count(int[] grid, int symbol)
        {
            int n = 0;
            foreach (int s in grid) if (s == symbol) n++;
            return n;
        }

        static int Sum(int[] weights)
        {
            int total = 0;
            foreach (int w in weights) total += w;
            return total;
        }

        // ================================================================ Coconut Sevens

        /// <summary>
        /// The classic. Five reels, three rows, twenty fixed lines read left to right, and a star that
        /// pays wherever it lands. No wild and no bonus round: the extra is the double-up card, which
        /// is <see cref="SlotMachine"/>'s, not the reels'.
        /// </summary>
        public static class Sevens
        {
            public const int Lime = 0, Coconut = 1, Mango = 2, Papaya = 3, Pineapple = 4, Melon = 5, Seven = 6, Star = 7;
            public const int Symbols = 8;

            public static readonly string[] Names =
                { "Lime", "Coconut", "Mango", "Papaya", "Pineapple", "Melon", "Seven", "Star" };

            /// <summary>Row per reel, for each of the twenty lines. Row 0 is the top.</summary>
            public static readonly int[][] Lines =
            {
                new[] { 1, 1, 1, 1, 1 }, new[] { 0, 0, 0, 0, 0 }, new[] { 2, 2, 2, 2, 2 },
                new[] { 0, 1, 2, 1, 0 }, new[] { 2, 1, 0, 1, 2 }, new[] { 1, 0, 0, 0, 1 },
                new[] { 1, 2, 2, 2, 1 }, new[] { 0, 0, 1, 2, 2 }, new[] { 2, 2, 1, 0, 0 },
                new[] { 1, 2, 1, 0, 1 }, new[] { 1, 0, 1, 2, 1 }, new[] { 0, 1, 1, 1, 0 },
                new[] { 2, 1, 1, 1, 2 }, new[] { 0, 1, 0, 1, 0 }, new[] { 2, 1, 2, 1, 2 },
                new[] { 1, 1, 0, 1, 1 }, new[] { 1, 1, 2, 1, 1 }, new[] { 0, 0, 2, 0, 0 },
                new[] { 2, 2, 0, 2, 2 }, new[] { 0, 2, 2, 2, 0 },
            };

            /// <summary>Per symbol, what 3, 4 and 5 in a row on one line pay, in hundredths of the total bet.</summary>
            public static readonly int[][] Pays =
            {
                new[] { 90, 250, 1000 },     // lime
                new[] { 90, 250, 1000 },     // coconut
                new[] { 100, 250, 1000 },    // mango
                new[] { 100, 250, 1000 },    // papaya
                new[] { 250, 1000, 2500 },   // pineapple
                new[] { 250, 1000, 2500 },   // melon
                new[] { 500, 5000, 25000 },  // seven
            };

            /// <summary>Stars anywhere: 3, 4, 5 of them, in hundredths of the total bet.</summary>
            public static readonly int[] StarPays = { 500, 2500, 12500 };

            public static readonly int[] Weights = { 20, 20, 20, 20, 10, 10, 4, 3 };
            static readonly int WeightTotal = Sum(Weights);

            public static void Play(ref SlotRng rng, SlotResult result)
            {
                var grid = new int[15];
                for (int i = 0; i < grid.Length; i++) grid[i] = rng.Pick(Weights, WeightTotal);

                var winning = new bool[15];
                long pct = Evaluate(grid, winning);

                result.WinPct = pct;
                result.Frames.Add(new SlotFrame
                {
                    Grid = grid, Winning = winning, RunningPct = pct, Drop = true,
                    Seconds = pct > 0 ? 3.2f : 2.4f,
                });
            }

            /// <summary>Line pays plus the stars. Public so the harness can hand it grids it built.</summary>
            public static long Evaluate(int[] grid, bool[] winning)
            {
                long pct = 0;

                foreach (int[] line in Lines)
                {
                    int first = grid[line[0]];
                    if (first == Star) continue;

                    int run = 1;
                    while (run < 5 && grid[run * 3 + line[run]] == first) run++;
                    if (run < 3) continue;

                    pct += Pays[first][run - 3];
                    if (winning != null)
                        for (int c = 0; c < run; c++) winning[c * 3 + line[c]] = true;
                }

                int stars = 0;
                foreach (int s in grid) if (s == Star) stars++;

                if (stars >= 3)
                {
                    pct += StarPays[Math.Min(stars, 5) - 3];
                    if (winning != null)
                        for (int i = 0; i < grid.Length; i++) if (grid[i] == Star) winning[i] = true;
                }

                return pct;
            }
        }

        // ================================================================ the tumble engine

        /// <summary>
        /// One pay-anywhere tumble game's numbers. Wrath of the Volcano and Fruit Tumble share the
        /// engine in <see cref="Volcano"/> and its layout: nine payers, then the scatter
        /// (<see cref="Volcano.Peak"/>), then the multiplier (<see cref="Volcano.Orb"/>).
        /// </summary>
        public sealed class TumbleRules
        {
            /// <summary>Per payer, 8-9, 10-11 and 12+ anywhere, in hundredths of the bet.</summary>
            public int[][] Pays;

            /// <summary>Scatters on the first drop: 4, 5, 6+, in hundredths of the bet.</summary>
            public int[] ScatterPays;

            public int FreeSpins;

            /// <summary>Base reels, base reels with the ante, free-spin reels. The multiplier is not in them.</summary>
            public int[] Weights, AnteWeights, FreeWeights;

            /// <summary>A multiplier in this many thousandths of cells, base game and free spins.</summary>
            public int OrbPerMille, FreeOrbPerMille;

            public int[] OrbValues, OrbWeights;

            /// <summary>
            /// True: in free spins every paying sequence's multipliers add to one that lasts the
            /// feature (Volcano). False: each sequence is multiplied by its own and no more (Fruit).
            /// </summary>
            public bool Carry;

            internal int WeightTotal, AnteTotal, FreeTotal, OrbTotal;

            public TumbleRules Ready()
            {
                WeightTotal = Sum(Weights);
                AnteTotal = Sum(AnteWeights);
                FreeTotal = Sum(FreeWeights);
                OrbTotal = Sum(OrbWeights);
                return this;
            }
        }

        // ================================================================ Wrath of the Volcano

        /// <summary>
        /// Six by five, and a symbol pays if there are eight or more of it anywhere. Winners burst,
        /// the rest fall, new ones drop in, and it goes again until nothing pays. Lava orbs carry a
        /// multiplier; when a sequence has paid, every orb still on screen adds into one multiplier
        /// for the lot. Four volcanoes start fifteen free spins, where the orbs keep adding up, and
        /// three more in a free spin add five. The ante bet thickens the volcanoes on the base reels.
        ///
        /// The engine below plays any <see cref="TumbleRules"/>; <see cref="Fruit"/> is the other.
        /// </summary>
        public static class Volcano
        {
            public const int Cols = 6, Rows = 5, Cells = Cols * Rows;

            public const int Obsidian = 0, Jade = 1, Amber = 2, Ruby = 3, Pearl = 4, Drum = 5, Mask = 6, Idol = 7, Crown = 8;
            public const int Peak = 9, Orb = 10;
            public const int Payers = 9;

            public static readonly string[] Names =
                { "Obsidian", "Jade", "Amber", "Ruby", "Pearl", "Drum", "Mask", "Idol", "Crown", "Volcano", "Lava orb" };

            /// <summary>Per symbol, 8-9, 10-11 and 12+ anywhere, in hundredths of the bet.</summary>
            public static readonly int[][] Pays =
            {
                new[] { 30, 85, 230 },
                new[] { 45, 105, 460 },
                new[] { 55, 115, 575 },
                new[] { 90, 140, 920 },
                new[] { 115, 170, 1150 },
                new[] { 170, 230, 1380 },
                new[] { 230, 575, 1725 },
                new[] { 290, 1150, 2875 },
                new[] { 1150, 2875, 5750 },
            };

            /// <summary>Volcanoes anywhere on the first drop: 4, 5, 6+, in hundredths of the bet.</summary>
            public static readonly int[] PeakPays = { 300, 500, 10000 };

            public const int FreeSpins = 15;

            /// <summary>Three scatters in a free spin add this many, on either game.</summary>
            public const int Retrigger = 5;

            /// <summary>Everything but orbs. Orbs are rolled separately so their rate is one number.</summary>
            public static readonly int[] Weights = { 300, 280, 260, 240, 220, 170, 150, 130, 100, 36 };

            /// <summary>The base reels with the ante on: only the volcano is heavier, enough to double the feature.</summary>
            public static readonly int[] AnteWeights = { 300, 280, 260, 240, 220, 170, 150, 130, 100, 45 };

            /// <summary>Free spins lean on the low symbols, so the screen pays more often while the orbs pile up.</summary>
            public static readonly int[] FreeWeights = { 380, 340, 290, 240, 210, 160, 140, 115, 90, 20 };

            /// <summary>An orb in this many thousandths of cells, base game and free spins.</summary>
            public const int OrbPerMille = 14;
            public const int FreeOrbPerMille = 32;

            public static readonly int[] OrbValues = { 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 50, 100, 250, 500 };
            public static readonly int[] OrbWeights = { 3000, 2000, 1500, 1100, 800, 600, 400, 250, 150, 100, 60, 30, 10, 3, 1 };

            /// <summary>The bonus buy, in bets.</summary>
            public const int BuyPrice = 100;

            public static readonly TumbleRules Rules = new TumbleRules
            {
                Pays = Pays, ScatterPays = PeakPays, FreeSpins = FreeSpins,
                Weights = Weights, AnteWeights = AnteWeights, FreeWeights = FreeWeights,
                OrbPerMille = OrbPerMille, FreeOrbPerMille = FreeOrbPerMille,
                OrbValues = OrbValues, OrbWeights = OrbWeights, Carry = true,
            }.Ready();

            const int Base = 0, WithAnte = 1, Free = 2;

            public static void Play(ref SlotRng rng, SlotResult result, bool buy, bool ante, TumbleRules g)
            {
                int reels = ante ? WithAnte : Base;
                var orbs = new int[Cells];
                var grid = new int[Cells];
                for (int i = 0; i < Cells; i++) grid[i] = Roll(ref rng, g, orbs, i, reels);
                if (buy) Force(ref rng, grid, orbs, Peak, 4);

                int peaks = Count(grid, Peak);
                long total = Sequence(ref rng, g, result, grid, orbs, reels, 0, -1, 0, out _);

                if (peaks >= 4)
                {
                    long scatter = g.ScatterPays[Math.Min(peaks, 6) - 4];
                    total += scatter;
                    result.Frames[result.Frames.Count - 1].RunningPct = total;

                    int left = g.FreeSpins;
                    result.FreeSpins = g.FreeSpins;
                    int running = 0;

                    while (left > 0 && total < MaxWinX * 100L)
                    {
                        left--;
                        orbs = new int[Cells];
                        grid = new int[Cells];
                        for (int i = 0; i < Cells; i++) grid[i] = Roll(ref rng, g, orbs, i, Free);

                        if (Count(grid, Peak) >= 3)
                        {
                            left += Retrigger;
                            result.FreeSpins += Retrigger;
                        }

                        total = Sequence(ref rng, g, result, grid, orbs, Free, total, left, running, out running);
                    }
                }

                result.WinPct = total;
            }

            static int Roll(ref SlotRng rng, TumbleRules g, int[] orbs, int cell, int reels)
            {
                if (rng.Range(1000) < (reels == Free ? g.FreeOrbPerMille : g.OrbPerMille))
                {
                    orbs[cell] = g.OrbValues[rng.Pick(g.OrbWeights, g.OrbTotal)];
                    return Orb;
                }

                orbs[cell] = 0;
                return reels switch
                {
                    Free => rng.Pick(g.FreeWeights, g.FreeTotal),
                    WithAnte => rng.Pick(g.AnteWeights, g.AnteTotal),
                    _ => rng.Pick(g.Weights, g.WeightTotal),
                };
            }

            /// <summary>
            /// One drop and all its tumbles. Returns the running total after it. In free spins,
            /// <paramref name="freeLeft"/> is not -1; with <see cref="TumbleRules.Carry"/> the orbs of
            /// every paying sequence add into <paramref name="runningMultiplier"/>, which then
            /// multiplies that sequence, and without it each sequence has only its own orbs.
            /// </summary>
            static long Sequence(ref SlotRng rng, TumbleRules g, SlotResult result, int[] grid, int[] orbs, int reels,
                                 long before, int freeLeft, int runningMultiplier, out int runningAfter)
            {
                long raw = 0;
                bool first = true;
                float pace = freeLeft >= 0 ? 0.75f : 1f;

                while (true)
                {
                    var winning = new bool[Cells];
                    long pays = Evaluate(g, grid, winning);
                    raw += pays;

                    var frame = new SlotFrame
                    {
                        Grid = (int[])grid.Clone(), Winning = winning, Drop = first, FreeSpinsLeft = freeLeft,
                        RunningPct = before + raw, Multiplier = runningMultiplier,
                        Seconds = (first ? 1.6f : 0.9f) * pace,
                    };
                    result.Frames.Add(frame);
                    first = false;

                    if (pays == 0) break;
                    Tumble(ref rng, g, grid, orbs, winning, reels);
                }

                int orbSum = 0;
                foreach (int o in orbs) orbSum += o;

                runningAfter = runningMultiplier;

                if (raw > 0 && orbSum > 0)
                {
                    int multiplier = freeLeft >= 0 && g.Carry ? (runningAfter += orbSum) : orbSum;

                    // The orbs going off: one more picture, the grid as it settled, with the total.
                    result.Frames.Add(new SlotFrame
                    {
                        Grid = (int[])grid.Clone(), Winning = new bool[Cells], FreeSpinsLeft = freeLeft,
                        RunningPct = before + raw * multiplier, Multiplier = multiplier, Seconds = 1.4f * pace,
                    });

                    return before + raw * multiplier;
                }

                return before + raw;
            }

            /// <summary>Volcano's pays on a grid the harness built.</summary>
            public static long Evaluate(int[] grid, bool[] winning) => Evaluate(Rules, grid, winning);

            public static long Evaluate(TumbleRules g, int[] grid, bool[] winning)
            {
                var counts = new int[Payers];
                foreach (int s in grid) if (s < Payers) counts[s]++;

                long pct = 0;
                for (int s = 0; s < Payers; s++)
                {
                    int n = counts[s];
                    if (n < 8) continue;

                    pct += g.Pays[s][n >= 12 ? 2 : n >= 10 ? 1 : 0];
                    if (winning != null)
                        for (int i = 0; i < grid.Length; i++) if (grid[i] == s) winning[i] = true;
                }

                return pct;
            }

            /// <summary>Winners out, everything above falls, new symbols in on top. Orbs and volcanoes fall like anything else.</summary>
            static void Tumble(ref SlotRng rng, TumbleRules g, int[] grid, int[] orbs, bool[] winning, int reels)
            {
                for (int c = 0; c < Cols; c++)
                {
                    int write = Rows - 1;
                    for (int r = Rows - 1; r >= 0; r--)
                    {
                        int i = c * Rows + r;
                        if (winning[i]) continue;

                        int to = c * Rows + write;
                        grid[to] = grid[i];
                        orbs[to] = orbs[i];
                        write--;
                    }

                    for (; write >= 0; write--)
                    {
                        int i = c * Rows + write;
                        grid[i] = Roll(ref rng, g, orbs, i, reels);
                    }
                }
            }
        }

        // ================================================================ Fruit Tumble

        /// <summary>
        /// The Volcano's engine with sweeter rules: no multipliers in the base game at all, ten free
        /// spins on four suns, and in them coconut bombs that multiply only the sequence they land
        /// in. Nothing carries from one free spin to the next, so a feature is many medium hits
        /// rather than one long climb. Same 6x5 grid, same layout of symbols.
        /// </summary>
        public static class Fruit
        {
            public const int Sun = Volcano.Peak, Bomb = Volcano.Orb;

            public static readonly string[] Names =
            {
                "Berry", "Lychee", "Kiwi", "Starfruit", "Guava", "Banana", "Dragonfruit", "Passionfruit",
                "Golden pineapple", "Sun", "Coconut bomb",
            };

            /// <summary>Per symbol, 8-9, 10-11 and 12+ anywhere, in hundredths of the bet.</summary>
            public static readonly int[][] Pays =
            {
                new[] { 80, 250, 660 },
                new[] { 130, 295, 1300 },
                new[] { 165, 330, 1650 },
                new[] { 265, 395, 2650 },
                new[] { 330, 495, 3300 },
                new[] { 495, 660, 3950 },
                new[] { 660, 1650, 4950 },
                new[] { 825, 3300, 8250 },
                new[] { 3300, 8250, 16500 },
            };

            public static readonly int[] SunPays = { 300, 500, 10000 };

            public const int FreeSpins = 10;

            public static readonly int[] Weights = { 300, 280, 260, 240, 220, 170, 150, 130, 100, 36 };
            public static readonly int[] AnteWeights = { 300, 280, 260, 240, 220, 170, 150, 130, 100, 45 };
            public static readonly int[] FreeWeights = { 380, 340, 290, 240, 210, 160, 140, 115, 90, 20 };

            /// <summary>No bombs on the base reels: that is the point of the game.</summary>
            public const int BombPerMille = 0;
            public const int FreeBombPerMille = 62;

            public static readonly int[] BombValues = { 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 50, 100 };
            public static readonly int[] BombWeights = { 3000, 2000, 1500, 1100, 800, 600, 400, 250, 150, 100, 60, 30, 10 };

            public const int BuyPrice = 100;

            public static readonly TumbleRules Rules = new TumbleRules
            {
                Pays = Pays, ScatterPays = SunPays, FreeSpins = FreeSpins,
                Weights = Weights, AnteWeights = AnteWeights, FreeWeights = FreeWeights,
                OrbPerMille = BombPerMille, FreeOrbPerMille = FreeBombPerMille,
                OrbValues = BombValues, OrbWeights = BombWeights, Carry = false,
            }.Ready();

            public static long Evaluate(int[] grid, bool[] winning) => Volcano.Evaluate(Rules, grid, winning);
        }

        // ================================================================ Lagoon Catch

        /// <summary>
        /// Five reels, three rows, ten lines, and fish that carry a chip value: x2, x5, x10, x25 or
        /// x50 the bet, one symbol per value so the grid alone says what every fish is worth. In the
        /// base game a fish is just a line symbol. Three golden hooks anywhere start free spins, and
        /// there the castaway lands: a wild on the lines who also reels in every fish on screen, each
        /// castaway the lot. Every fourth castaway caught adds ten spins and lifts what the castaways
        /// collect to x2, then x3, then x10, from the next spin on.
        /// </summary>
        public static class Lagoon
        {
            public const int Cols = 5, Rows = 3, Cells = Cols * Rows;

            public const int Shell = 0, Starfish = 1, Crab = 2, Bobber = 3, Tackle = 4, Rod = 5, Boat = 6;

            /// <summary>The first fish. Fish + i is worth <see cref="FishValues"/>[i] bets.</summary>
            public const int Fish = 7;
            public const int Castaway = 12, Hook = 13;

            public static readonly string[] Names =
            {
                "Shell", "Starfish", "Crab", "Bobber", "Tackle box", "Rod", "Boat",
                "Minnow", "Snapper", "Grouper", "Marlin", "Golden marlin", "Castaway", "Golden hook",
            };

            public static readonly int[] FishValues = { 2, 5, 10, 25, 50 };

            /// <summary>The first ten of Sevens' lines.</summary>
            public static readonly int[][] Lines = Sevens.Lines[..10];

            /// <summary>
            /// Per line symbol, 3, 4 and 5 in a row, in hundredths of the bet: the seven payers, then
            /// any fish, whatever it is worth.
            /// </summary>
            public static readonly int[][] Pays =
            {
                new[] { 125, 250, 1250 },
                new[] { 125, 250, 1250 },
                new[] { 125, 375, 1875 },
                new[] { 125, 375, 1875 },
                new[] { 250, 750, 3750 },
                new[] { 375, 1250, 5000 },
                new[] { 500, 2500, 12500 },
                new[] { 250, 750, 2500 },
            };

            /// <summary>Hooks anywhere: 3, 4, 5, in hundredths of the bet.</summary>
            public static readonly int[] HookPays = { 200, 1000, 5000 };

            /// <summary>Free spins for 3, 4 and 5 hooks.</summary>
            public static readonly int[] FreeSpinsFor = { 10, 15, 20 };

            /// <summary>What the castaways' catch is multiplied by, level by level. Four castaways to a level.</summary>
            public static readonly int[] Levels = { 1, 2, 3, 10 };
            public const int PerLevel = 4, LevelSpins = 10;

            /// <summary>Per symbol, in the order above. No castaway on the base reels; no hook in free spins.</summary>
            public static readonly int[] Weights = { 300, 300, 260, 260, 180, 140, 100, 70, 40, 20, 8, 2, 0, 42 };
            public static readonly int[] FreeWeights = { 300, 300, 260, 260, 180, 140, 100, 90, 45, 18, 5, 1, 35, 0 };
            static readonly int WeightTotal = Sum(Weights);
            static readonly int FreeTotal = Sum(FreeWeights);

            public const int BuyPrice = 94;

            public static bool IsFish(int symbol) => symbol >= Fish && symbol < Castaway;

            public static void Play(ref SlotRng rng, SlotResult result, bool buy)
            {
                var grid = new int[Cells];
                for (int i = 0; i < Cells; i++) grid[i] = rng.Pick(Weights, WeightTotal);
                if (buy) Force(ref rng, grid, null, Hook, 3);

                var winning = new bool[Cells];
                long total = Evaluate(grid, winning);

                int hooks = Count(grid, Hook);
                if (hooks >= 3)
                {
                    total += HookPays[Math.Min(hooks, 5) - 3];
                    for (int i = 0; i < Cells; i++) if (grid[i] == Hook) winning[i] = true;
                }

                result.Frames.Add(new SlotFrame
                {
                    Grid = grid, Winning = winning, RunningPct = total, Drop = true,
                    Seconds = total > 0 ? 3f : 2.2f,
                });

                if (hooks >= 3)
                {
                    int left = FreeSpinsFor[Math.Min(hooks, 5) - 3];
                    result.FreeSpins = left;
                    int caught = 0, level = 0;

                    while (left > 0 && total < MaxWinX * 100L)
                    {
                        left--;
                        grid = new int[Cells];
                        for (int i = 0; i < Cells; i++) grid[i] = rng.Pick(FreeWeights, FreeTotal);

                        winning = new bool[Cells];
                        total += Evaluate(grid, winning);
                        int multiplier = Levels[level];

                        result.Frames.Add(new SlotFrame
                        {
                            Grid = (int[])grid.Clone(), Winning = winning, RunningPct = total, Drop = true,
                            FreeSpinsLeft = left, Multiplier = level > 0 ? multiplier : 0, Seconds = 1.6f,
                        });

                        int castaways = Count(grid, Castaway);
                        if (castaways == 0) continue;

                        // The catch: every castaway reels in every fish.
                        long catchPct = Collect(grid) * castaways * multiplier;
                        if (catchPct > 0)
                        {
                            total += catchPct;
                            var reeled = new bool[Cells];
                            for (int i = 0; i < Cells; i++) reeled[i] = grid[i] == Castaway || IsFish(grid[i]);

                            result.Frames.Add(new SlotFrame
                            {
                                Grid = (int[])grid.Clone(), Winning = reeled, RunningPct = total, FreeSpinsLeft = left,
                                Multiplier = level > 0 ? multiplier : 0, Seconds = 1.4f,
                            });
                        }

                        // Every fourth castaway: ten more spins, and the next level from the next one.
                        int before = caught / PerLevel;
                        caught += castaways;
                        int gained = Math.Min(caught / PerLevel, Levels.Length - 1) - Math.Min(before, Levels.Length - 1);
                        if (gained > 0)
                        {
                            level += gained;
                            left += gained * LevelSpins;
                            result.FreeSpins += gained * LevelSpins;
                        }
                    }
                }

                result.WinPct = total;
            }

            /// <summary>Every fish on the grid, in hundredths of the bet. What one castaway at x1 catches.</summary>
            public static long Collect(int[] grid)
            {
                long pct = 0;
                foreach (int s in grid) if (IsFish(s)) pct += FishValues[s - Fish] * 100L;
                return pct;
            }

            /// <summary>Line pays, the castaway standing in for anything but a hook. Five castaways pay as boats.</summary>
            public static long Evaluate(int[] grid, bool[] winning)
            {
                long pct = 0;

                foreach (int[] line in Lines)
                {
                    int paying = -1, run = 0;

                    for (; run < Cols; run++)
                    {
                        int s = grid[run * Rows + line[run]];
                        if (s == Hook) break;
                        if (s == Castaway) continue;

                        int kind = IsFish(s) ? Fish : s;
                        if (paying < 0) paying = kind;
                        else if (kind != paying) break;
                    }

                    if (run < 3) continue;
                    if (paying < 0) paying = Boat;

                    pct += Pays[paying][run - 3];
                    if (winning != null)
                        for (int c = 0; c < run; c++) winning[c * Rows + line[c]] = true;
                }

                return pct;
            }
        }

        // ================================================================ Reef Rush

        /// <summary>
        /// Seven by seven. Five or more of a kind touching edge to edge pay, burst and tumble. Every
        /// cell a winner bursts out of gets marked; burst out of a marked cell again and it becomes
        /// a x2 spot, then x4, and so on to x128. A cluster over spots is multiplied by their sum.
        /// Three pearl chests start free spins, and in free spins the spots never reset.
        /// </summary>
        public static class Reef
        {
            public const int Cols = 7, Rows = 7, Cells = Cols * Rows;

            public const int Kelp = 0, Shell = 1, Star = 2, Urchin = 3, Puffer = 4, Clown = 5, Octopus = 6, Chest = 7;
            public const int Payers = 7;

            public static readonly string[] Names =
                { "Kelp", "Shell", "Starfish", "Urchin", "Pufferfish", "Clownfish", "Octopus", "Pearl chest" };

            /// <summary>What a cluster of 5 of each symbol pays, in hundredths of the bet. Bigger clusters scale it by <see cref="Size"/>.</summary>
            public static readonly int[] Base = { 60, 75, 95, 120, 180, 240, 360 };

            /// <summary>Scale per cluster size from 5 to 15+, in tenths.</summary>
            public static readonly int[] Size = { 10, 15, 20, 30, 40, 60, 90, 130, 180, 250, 400 };

            public static readonly int[] FreeSpinsFor = { 10, 12, 15, 20, 30 };
            public const int MaxSpot = 128;

            public static readonly int[] Weights = { 240, 220, 200, 180, 150, 130, 110, 7 };
            static readonly int WeightTotal = Sum(Weights);
            static readonly int[] FreeWeights = { 370, 315, 240, 172, 122, 92, 72, 5 };
            static readonly int FreeTotal = Sum(FreeWeights);

            /// <summary>The bonus buy, in bets.</summary>
            public const int BuyPrice = 119;

            public static void Play(ref SlotRng rng, SlotResult result, bool buy)
            {
                var spots = new int[Cells];
                var grid = new int[Cells];
                for (int i = 0; i < Cells; i++) grid[i] = rng.Pick(Weights, WeightTotal);
                if (buy) Force(ref rng, grid, null, Chest, 3);

                int chests = Count(grid, Chest);
                long total = Sequence(ref rng, result, grid, spots, Weights, WeightTotal, 0, -1);

                if (chests >= 3)
                {
                    int left = FreeSpinsFor[Math.Min(chests, 7) - 3];
                    result.FreeSpins = left;

                    // Spots carry over from the triggering spin into the feature, then persist.
                    while (left > 0 && total < MaxWinX * 100L)
                    {
                        left--;
                        grid = new int[Cells];
                        for (int i = 0; i < Cells; i++) grid[i] = rng.Pick(FreeWeights, FreeTotal);

                        int more = Count(grid, Chest);
                        if (more >= 3)
                        {
                            int extra = FreeSpinsFor[Math.Min(more, 7) - 3];
                            left += extra;
                            result.FreeSpins += extra;
                        }

                        total = Sequence(ref rng, result, grid, spots, FreeWeights, FreeTotal, total, left);
                    }
                }

                result.WinPct = total;
            }

            static long Sequence(ref SlotRng rng, SlotResult result, int[] grid, int[] spots,
                                 int[] weights, int weightTotal, long before, int freeLeft)
            {
                long running = before;
                bool first = true;
                float pace = freeLeft >= 0 ? 0.75f : 1f;

                while (true)
                {
                    var winning = new bool[Cells];
                    long pays = Evaluate(grid, spots, winning);
                    running += pays;

                    result.Frames.Add(new SlotFrame
                    {
                        Grid = (int[])grid.Clone(), Winning = winning, Spots = (int[])spots.Clone(),
                        RunningPct = running, Drop = first, FreeSpinsLeft = freeLeft,
                        Seconds = (first ? 1.6f : 0.9f) * pace,
                    });
                    first = false;

                    if (pays == 0) break;

                    Mark(spots, winning);
                    Tumble(ref rng, grid, winning, weights, weightTotal);
                }

                return running;
            }

            /// <summary>Every cell that just burst: unmarked becomes marked, marked becomes x2, a multiplier doubles.</summary>
            public static void Mark(int[] spots, bool[] winning)
            {
                for (int i = 0; i < spots.Length; i++)
                {
                    if (!winning[i]) continue;
                    spots[i] = spots[i] == 0 ? 1 : Math.Min(MaxSpot, spots[i] * 2);
                }
            }

            /// <summary>
            /// Every cluster of five or more, each paid its size's rate times the sum of the multiplier
            /// spots under it (a plain mark is not a multiplier). Spots are read, not changed.
            /// </summary>
            public static long Evaluate(int[] grid, int[] spots, bool[] winning)
            {
                var seen = new bool[Cells];
                var stack = new int[Cells];
                var members = new int[Cells];
                long pct = 0;

                for (int start = 0; start < Cells; start++)
                {
                    if (seen[start]) continue;
                    int symbol = grid[start];
                    seen[start] = true;
                    if (symbol >= Payers) continue;

                    int size = 0, top = 0;
                    stack[top++] = start;

                    while (top > 0)
                    {
                        int i = stack[--top];
                        members[size++] = i;

                        int c = i / Rows, r = i % Rows;
                        Push(c - 1, r); Push(c + 1, r); Push(c, r - 1); Push(c, r + 1);
                    }

                    if (size < 5) continue;

                    int multiplier = 0;
                    for (int m = 0; m < size; m++)
                        if (spots != null && spots[members[m]] >= 2) multiplier += spots[members[m]];

                    long pay = (long)Base[symbol] * Size[Math.Min(size, 15) - 5] / 10;
                    pct += pay * Math.Max(1, multiplier);

                    if (winning != null)
                        for (int m = 0; m < size; m++) winning[members[m]] = true;

                    void Push(int c, int r)
                    {
                        if (c < 0 || c >= Cols || r < 0 || r >= Rows) return;
                        int j = c * Rows + r;
                        if (seen[j] || grid[j] != symbol) return;
                        seen[j] = true;
                        stack[top++] = j;
                    }
                }

                return pct;
            }

            static void Tumble(ref SlotRng rng, int[] grid, bool[] winning, int[] weights, int weightTotal)
            {
                for (int c = 0; c < Cols; c++)
                {
                    int write = Rows - 1;
                    for (int r = Rows - 1; r >= 0; r--)
                    {
                        int i = c * Rows + r;
                        if (winning[i]) continue;
                        grid[c * Rows + write] = grid[i];
                        write--;
                    }

                    for (; write >= 0; write--) grid[c * Rows + write] = rng.Pick(weights, weightTotal);
                }
            }
        }
    }
}
