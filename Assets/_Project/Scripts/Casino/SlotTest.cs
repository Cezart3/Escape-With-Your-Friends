using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Player;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>
    /// The slot cabinets' harness, behind <c>-slotTest</c>. Wants <c>-scene island -noNatives
    /// -noAnimals</c>. A second process with <c>-slotTest -client</c> checks what a peer that is not
    /// the server sees.
    ///
    /// 1. The arithmetic, on paper. Hand-built grids pay what the tables say; the same seed plays
    ///    the same spin twice; a fixed run of seeds wins exactly the chips it won when the numbers
    ///    were tuned outside Unity, which is what proves <see cref="SlotRng"/> is the runtime's no
    ///    more than the paytables are; and a long run returns what a casino should.
    /// 2. The cabinets in the world. Every press takes the stake, pays exactly what the seed says,
    ///    and leaves the screen on the spin's last picture. Money never moves; the ledger balances.
    /// 3. The double-up card, and everything that is refused.
    /// 4. The bonus buy and the shared jackpot: a buy always triggers and returns what the tuning
    ///    said; every stake feeds one pot that every cabinet shows; a drop pays the pot, resets it
    ///    and throws the JACKPOT banner.
    /// </summary>
    public class SlotTest : MonoBehaviour
    {
        const float WaitForWorld = 60f;

        /// <summary>Seeds <c>i * 7919 + 13</c> at a bet of 100: what each game won over this many spins.</summary>
        const int GoldenSpins = 20000;

        /// <summary>
        /// Worked out with the same file compiled under .NET 8, outside Unity. If Mono or IL2CPP ever
        /// disagrees, a client's replay of a spin is not the spin the host paid.
        /// </summary>
        static readonly (SlotKind Kind, long Won)[] Golden =
        {
            (SlotKind.Sevens, 1909820),
            (SlotKind.Volcano, 1996820),
            (SlotKind.Reef, 2131545),
            (SlotKind.Fruit, 2170385),
        };

        /// <summary>The same seeds bought: what each feature won over <see cref="GoldenSpins"/> buys, under .NET.</summary>
        static readonly (SlotKind Kind, long Won)[] GoldenBuys =
        {
            (SlotKind.Volcano, 195264855),
            (SlotKind.Reef, 226769845),
            (SlotKind.Fruit, 190986010),
        };

        /// <summary>The same seeds with the ante on, under Mono.</summary>
        static readonly (SlotKind Kind, long Won)[] GoldenAnte =
        {
            (SlotKind.Volcano, 2197430),
            (SlotKind.Fruit, 2297590),
        };

        /// <summary>Spins per game for the return-to-player check.</summary>
        const int RtpSpins = 200000;

        const int SpinsPerCabinet = 3;
        const int Watched = 3;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-slotTest")) return;
            CasinoDays.AllOpen = true;   // the cabinets, not the calendar; -casinoDaysTest has that

            _started = true;

            var go = new GameObject("SlotTest");
            DontDestroyOnLoad(go);
            go.AddComponent<SlotTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null) yield return null;

            if (!InstanceFinder.NetworkManager.IsServerStarted)
            {
                yield return Watching();
                yield break;
            }

            // Several seconds of arithmetic, spread over frames so the host keeps answering the
            // transport while a paired client connects.
            Paper();
            yield return Replays();
            yield return Rtp();

            PlayerMotor player = null;
            float deadline = Time.time + WaitForWorld;

            while (Time.time < deadline && player == null)
            {
                player = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                         .FirstOrDefault(m => m != null && m.IsSpawned);
                if (player == null) yield return new WaitForSeconds(0.5f);
            }

            SlotMachine[] machines = { };
            while (Time.time < deadline && machines.Length < 3)
            {
                machines = FindObjectsByType<SlotMachine>(FindObjectsSortMode.None).Where(m => m.IsSpawned).ToArray();
                if (machines.Length < 3) yield return new WaitForSeconds(0.5f);
            }

            if (player == null || machines.Length < 3)
            {
                Check($"a player and three cabinets in the world ({machines.Length} found). Run "
                      + "SlotFactory.Build and bake the POIs with -rebuildPois", false);
                Report();
                yield break;
            }

            var wallet = player.GetComponent<Wallet>();
            NetworkObject actor = player.NetworkObject;

            Wiring(machines);

            wallet.ServerSetBalance(500);
            // Enough for a bonus buy at 20 after everything the gambling may lose.
            wallet.ServerSetChips(20000);
            Wallet.ResetLedger();
            yield return new WaitForSeconds(0.3f);

            // The wallets present now, not every wallet: a pair's client joins mid-test, and its
            // starting grant is minted into the totals while the host is still at the cabinets.
            Wallet[] present = FindObjectsByType<Wallet>(FindObjectsSortMode.None);
            int money = present.Sum(w => w.Balance);
            int chips = wallet.Chips;

            foreach (SlotMachine machine in machines.OrderBy(m => m.Kind))
            {
                player.ServerTeleport(machine.transform.position + machine.transform.forward * 1f, 0f);
                yield return Playing(machine, wallet, actor);
            }

            SlotMachine sevens = machines.First(m => m.Kind == SlotKind.Sevens);
            player.ServerTeleport(sevens.transform.position + sevens.transform.forward * 1f, 0f);
            yield return Gambling(sevens, wallet, actor);

            SlotMachine volcano = machines.First(m => m.Kind == SlotKind.Volcano);
            player.ServerTeleport(volcano.transform.position + volcano.transform.forward * 1f, 0f);
            yield return Buying(volcano, wallet, actor);
            yield return Anteing(volcano, wallet, actor);
            yield return Jackpot(volcano, machines, wallet, actor);

            int after = present.Sum(w => w.Balance);
            int joined = FindObjectsByType<Wallet>(FindObjectsSortMode.None).Except(present).Sum(w => w.Balance);
            Check($"money never moved at the cabinets ({money} before, {after} after)", after == money);
            Check($"and the ledger called none of it income (minted {Wallet.Minted}, {joined} of it a joiner's grant)",
                  Wallet.Minted == joined && Wallet.Burned == 0 && Wallet.Exchanged == 0);
            Check($"every chip is accounted for ({Wallet.Staked} staked, {Wallet.PaidOut} paid, {wallet.Chips} held)",
                  wallet.Chips == chips - Wallet.Staked + Wallet.PaidOut);

            // Last: it sets the stack outright, which the ledger above would rightly not balance.
            yield return Refusals(sevens, wallet, actor);

            Report();

            yield return Exhibition(sevens, wallet, actor);
        }

        // ---------------------------------------------------------------- 1. on paper

        void Paper()
        {
            // Sevens: a full screen of sevens is every line paying five of them.
            var sevens = new int[15];
            for (int i = 0; i < 15; i++) sevens[i] = SlotMath.Sevens.Seven;
            long full = SlotMath.Sevens.Evaluate(sevens, null);
            Check($"a screen of sevens pays all twenty lines ({full})",
                  full == SlotMath.Sevens.Lines.Length * SlotMath.Sevens.Pays[SlotMath.Sevens.Seven][2]);

            // Columns alternating lime and coconut: no line can run three.
            var stripes = new int[15];
            for (int i = 0; i < 15; i++) stripes[i] = i / 3 % 2 == 0 ? SlotMath.Sevens.Lime : SlotMath.Sevens.Coconut;
            Check("alternating reels pay nothing", SlotMath.Sevens.Evaluate(stripes, null) == 0);

            stripes[0] = stripes[7] = stripes[14] = SlotMath.Sevens.Star;
            var stars = new bool[15];
            long starPay = SlotMath.Sevens.Evaluate(stripes, stars);
            Check($"three stars anywhere pay the scatter ({starPay}) and light only themselves",
                  starPay == SlotMath.Sevens.StarPays[0] && stars.Count(w => w) == 3);

            // Volcano: eight crowns, and the rest spread so nothing else reaches eight.
            var volcano = new int[SlotMath.Volcano.Cells];
            for (int i = 0; i < volcano.Length; i++) volcano[i] = i < 8 ? SlotMath.Volcano.Crown : i % 8;
            long crowns = SlotMath.Volcano.Evaluate(volcano, null);
            Check($"eight crowns anywhere pay ({crowns})", crowns == SlotMath.Volcano.Pays[SlotMath.Volcano.Crown][0]);

            volcano[0] = SlotMath.Volcano.Orb;
            Check("seven do not", SlotMath.Volcano.Evaluate(volcano, null) == 0);

            // Reef: a checkerboard never clusters; five kelp in a row over a x2 and a x4 pays six times.
            var reef = new int[SlotMath.Reef.Cells];
            for (int i = 0; i < reef.Length; i++)
                reef[i] = (i / SlotMath.Reef.Rows + i % SlotMath.Reef.Rows) % 2 == 0 ? SlotMath.Reef.Shell : SlotMath.Reef.Star;
            var spots = new int[SlotMath.Reef.Cells];
            Check("a checkerboard reef pays nothing", SlotMath.Reef.Evaluate(reef, spots, null) == 0);

            for (int c = 0; c < 5; c++) reef[c * SlotMath.Reef.Rows] = SlotMath.Reef.Kelp;
            spots[0] = 2;
            spots[SlotMath.Reef.Rows] = 4;
            spots[2 * SlotMath.Reef.Rows] = 1;
            long kelp = SlotMath.Reef.Evaluate(reef, spots, null);
            long five = (long)SlotMath.Reef.Base[SlotMath.Reef.Kelp] * SlotMath.Reef.Size[0] / 10;
            Check($"five kelp over x2 and x4 pay six times a plain five ({kelp} = 6 x {five}); a mark is not a multiplier",
                  kelp == five * 6);

            var marks = new int[1];
            var hit = new[] { true };
            int[] seen = new int[10];
            for (int n = 0; n < seen.Length; n++)
            {
                SlotMath.Reef.Mark(marks, hit);
                seen[n] = marks[0];
            }

            Check($"a spot goes marked, x2, x4 ... and stops at x{SlotMath.Reef.MaxSpot} ({string.Join(" ", seen)})",
                  seen[0] == 1 && seen[1] == 2 && seen[2] == 4 && seen[6] == 64 && seen[7] == 128 && seen[9] == 128);

            // The big-win tiers: by the stake multiple, and the banner's count lands on the win.
            Check("9x is an ordinary win, 10x big, 25x mega, 50x epic",
                  BigWin.Tier(900, 100) == 0 && BigWin.Tier(1000, 100) == 1 && BigWin.Tier(2500, 100) == 2
                  && BigWin.Tier(5000, 100) == 3 && BigWin.Tier(100, 0) == 0);

            Check("a jackpot outranks every tier and says so",
                  BigWin.Name(BigWin.JackpotTier) == "JACKPOT" && BigWin.Seconds(BigWin.JackpotTier) > BigWin.Seconds(3));

            // The drop rate is the bet over the odds: one in 1,500 at 100, ten times rarer at 10.
            int hits100 = 0, hits10 = 0;
            for (int i = 0; i < 300000; i++)
            {
                if (SlotMath.JackpotHit(i * 7919 + 13, 100)) hits100++;
                if (SlotMath.JackpotHit(i * 7919 + 13, 10)) hits10++;
            }

            Check($"the jackpot drops about 1 in {SlotMath.JackpotOdds / 100} at 100 ({hits100} in 300000) "
                  + $"and about ten times rarer at 10 ({hits10})",
                  hits100 > 160 && hits100 < 240 && hits10 > 8 && hits10 < 35);

            int[] counted = Enumerable.Range(0, 11).Select(i => BigWin.Counted(3600, i * 0.35f, 3.5f)).ToArray();
            Check($"the banner counts up from 0 to the win without going back ({string.Join(" ", counted)})",
                  counted[0] == 0 && counted[10] == 3600 && counted.Zip(counted.Skip(1), (a, b) => a <= b).All(x => x));
        }

        IEnumerator Replays()
        {
            foreach (SlotKind kind in System.Enum.GetValues(typeof(SlotKind)).Cast<SlotKind>())
            {
                bool same = true;
                bool shapes = true;

                for (int seed = 1; seed <= 300; seed++)
                {
                    SlotResult a = SlotMath.Spin(kind, seed * 104729, 50);
                    SlotResult b = SlotMath.Spin(kind, seed * 104729, 50);

                    if (a.Win != b.Win || a.Frames.Count != b.Frames.Count) same = false;
                    for (int f = 0; same && f < a.Frames.Count; f++)
                        if (!a.Frames[f].Grid.SequenceEqual(b.Frames[f].Grid)) same = false;

                    int cells = SlotMath.Cols(kind) * SlotMath.Rows(kind);
                    if (a.Frames.Count == 0 || !a.Frames[0].Drop) shapes = false;
                    foreach (SlotFrame frame in a.Frames)
                        if (frame.Grid.Length != cells || frame.Winning.Length != cells || frame.Seconds <= 0f)
                            shapes = false;
                }

                Check($"{kind}: a seed plays the same spin twice, every picture of it", same);
                Check($"{kind}: every spin starts with a drop and every picture is a full grid", shapes);
                yield return null;
            }

            foreach ((SlotKind kind, long won) in Golden)
            {
                long total = 0;
                for (int i = 0; i < GoldenSpins; i++) total += SlotMath.Spin(kind, i * 7919 + 13, 100).Win;

                Check($"{kind}: {GoldenSpins} fixed seeds win {total}, as they did under .NET ({won})", total == won);
                yield return null;
            }

            foreach ((SlotKind kind, long won) in GoldenBuys)
            {
                long total = 0;
                bool triggered = true;

                for (int i = 0; i < GoldenSpins; i++)
                {
                    if (i % 2000 == 1999) yield return null;

                    SlotResult r = SlotMath.Spin(kind, i * 7919 + 13, 100, buy: true);
                    total += r.Win;
                    if (r.FreeSpins <= 0 || !r.Bought) triggered = false;
                }

                double rtp = (double)total / GoldenSpins / 100 / SlotMath.BuyPrice(kind);
                Check($"{kind}: every bought spin triggers the feature", triggered);
                Check($"{kind}: {GoldenSpins} buys win {total}, as under .NET ({won}), a return of {rtp:P1} "
                      + $"at {SlotMath.BuyPrice(kind)}x", total == won && rtp > 0.93 && rtp < 0.99);
            }

            Check("Sevens has nothing to buy", SlotMath.BuyPrice(SlotKind.Sevens) == 0);

            foreach ((SlotKind kind, long won) in GoldenAnte)
            {
                long total = 0;
                bool flagged = true;
                for (int i = 0; i < GoldenSpins; i++)
                {
                    SlotResult r = SlotMath.Spin(kind, i * 7919 + 13, 100, ante: true);
                    total += r.Win;
                    if (!r.Ante) flagged = false;
                }

                Check($"{kind}: {GoldenSpins} ante spins win {total}, as under Mono ({won})", total == won && flagged);
                Check($"{kind}: a bought spin ignores the ante", !SlotMath.Spin(kind, 13, 100, buy: true, ante: true).Ante);
            }

            Check("the Volcano and Fruit Tumble have an ante, the others do not",
                  SlotMath.HasAnte(SlotKind.Volcano) && SlotMath.HasAnte(SlotKind.Fruit)
                  && !SlotMath.HasAnte(SlotKind.Sevens) && !SlotMath.HasAnte(SlotKind.Reef));

            yield return Bombs();
            Check($"the ante stakes a quarter more ({SlotMath.AnteStake(100)} on 100)", SlotMath.AnteStake(100) == 125);
            Check($"orbs run to x{SlotMath.Volcano.OrbValues[^1]}", SlotMath.Volcano.OrbValues[^1] == 500);
        }

        IEnumerator Rtp()
        {
            // Bands, not points: a fixed seed run is one sample of a volatile game. The tuned figure
            // over millions of spins is about 95% for each; see docs/ARCHITECTURE.md.
            (SlotKind kind, bool ante, double low, double high)[] bands =
            {
                (SlotKind.Sevens, false, 0.93, 0.98),
                (SlotKind.Volcano, false, 0.88, 1.03),
                (SlotKind.Volcano, true, 0.88, 1.03),
                (SlotKind.Reef, false, 0.88, 1.03),
                (SlotKind.Fruit, false, 0.88, 1.03),
                (SlotKind.Fruit, true, 0.88, 1.03),
            };

            foreach ((SlotKind kind, bool ante, double low, double high) in bands)
            {
                float started = Time.realtimeSinceStartup;
                long staked = 0, won = 0;
                int hits = 0, features = 0, capped = 0, biggest = 0;

                for (int i = 0; i < RtpSpins; i++)
                {
                    if (i % 20000 == 19999) yield return null;

                    SlotResult r = SlotMath.Spin(kind, i * 7919 + 13, 100, ante: ante);
                    staked += ante ? SlotMath.AnteStake(100) : 100;
                    won += r.Win;
                    if (r.Win > 0) hits++;
                    if (r.FreeSpins > 0) features++;
                    if (r.Capped) capped++;
                    biggest = Mathf.Max(biggest, r.Win);
                }

                double rtp = (double)won / staked;
                Debug.Log($"[SlotTest] {kind}{(ante ? " ante" : "")}: {RtpSpins} spins, return {rtp:P2}, hits {100.0 * hits / RtpSpins:0.0}%, "
                          + $"features {features}, best {biggest / 100}x, capped {capped}, "
                          + $"{Time.realtimeSinceStartup - started:0.0}s.");

                Check($"{kind}{(ante ? " with the ante" : "")} returns {rtp:P2}, inside {low:P0}-{high:P0}", rtp >= low && rtp <= high);
                Check($"{kind} never pays past {SlotMath.MaxWinX}x", biggest <= SlotMath.MaxWinX * 100);
                if (kind != SlotKind.Sevens) Check($"{kind} reaches its feature ({features} times)", features > 100);
            }
        }

        // ---------------------------------------------------------------- 2. in the world

        void Wiring(SlotMachine[] machines)
        {
            Check("one cabinet of each game", machines.Select(m => m.Kind).Distinct().Count() == 3);

            foreach (SlotMachine machine in machines)
            {
                SlotButton[] buttons = machine.GetComponentsInChildren<SlotButton>();
                int wanted = machine.Kind == SlotKind.Sevens || SlotMath.HasAnte(machine.Kind) ? 4 : 3;

                Check($"{machine.Title} has {wanted} buttons ({buttons.Length}), each its own NetworkObject",
                      buttons.Length == wanted && buttons.Select(b => b.NetworkObject).Distinct().Count() == wanted
                      && buttons.All(b => b.NetworkObject != machine.NetworkObject && b.Machine == machine));

                Check($"{machine.Title} has a cell for every square of its grid",
                      machine.ShowingGrid().Length == machine.Cols * machine.Rows);
            }
        }

        IEnumerator Playing(SlotMachine machine, Wallet wallet, NetworkObject actor)
        {
            SlotButton spin = Button(machine, SlotAction.Spin);
            SlotButton bet = Button(machine, SlotAction.Bet);

            // Up to 20, the second stake, so the bet button is part of what is being checked.
            for (int n = 0; n < SlotMath.Bets.Length && machine.Bet != SlotMath.Bets[1]; n++) bet.ServerInteract(actor);
            Check($"{machine.Title}: the bet button sets the stake ({machine.Bet})", machine.Bet == SlotMath.Bets[1]);

            bool paid = true, replayed = true, shown = true;

            for (int n = 0; n < SpinsPerCabinet; n++)
            {
                int before = wallet.Chips;
                spin.ServerInteract(actor);

                if (wallet.Chips != before - machine.Bet || !machine.Busy)
                {
                    Check($"{machine.Title}: spin {n + 1} took the stake and started ({before - wallet.Chips})", false);
                    yield break;
                }

                SlotResult result = machine.LastResult;
                float giveUp = Time.time + result.Seconds + 10f;
                while ((machine.Busy || machine.Animating) && Time.time < giveUp) yield return null;

                // A jackpot can drop on any spin, one in several thousand at this stake.
                int jackpot = machine.LastJackpot;
                if (wallet.Chips != before - result.Bet + result.Win + jackpot)
                {
                    paid = false;
                    Debug.LogError($"[SlotTest] {machine.Title} seed {result.Seed}: paid "
                                   + $"{wallet.Chips - before + result.Bet}, not {result.Win}.");
                }

                if (SlotMath.Spin(machine.Kind, machine.LastSeed, machine.LastBet).Win != result.Win
                    || machine.LastWin != result.Win + jackpot)
                    replayed = false;

                if (!machine.ShowingGrid().SequenceEqual(result.Frames[result.Frames.Count - 1].Grid))
                    shown = false;

                Debug.Log($"[SlotTest] {machine.Title} spin {n + 1}: bet {result.Bet}, won {result.Win}, "
                          + $"{result.Frames.Count} picture(s) in {result.Seconds:0.0}s.");
            }

            Check($"{machine.Title}: every spin paid exactly what its seed says", paid);
            Check($"{machine.Title}: and the replicated seed replays to the same win", replayed);
            Check($"{machine.Title}: and the screen stopped on the spin's last picture", shown);

            string title = UI.SlotBoard.Title(machine);
            string line = UI.SlotBoard.Line(machine, wallet.Chips, -1);
            Check($"{machine.Title}: the board reads \"{title}\" over \"{line}\"",
                  title == machine.Title.ToUpperInvariant() && line.Contains($"bet {machine.Bet}"));
        }

        IEnumerator Buying(SlotMachine volcano, Wallet wallet, NetworkObject actor)
        {
            SlotButton buy = Button(volcano, SlotAction.Buy);
            int before = wallet.Chips;
            int cost = volcano.BuyCost;

            Check($"the buy button offers the feature for {cost} ({SlotMath.BuyPrice(volcano.Kind)} x {volcano.Bet})",
                  cost == volcano.Bet * SlotMath.Volcano.BuyPrice && buy.ServerCanInteract(actor));

            buy.ServerInteract(actor);
            SlotResult result = volcano.LastResult;

            Check($"a buy takes the price and starts the feature ({before - wallet.Chips} taken, {result?.FreeSpins} free spins)",
                  wallet.Chips == before - cost && volcano.Busy && result != null && result.Bought && result.FreeSpins > 0);
            if (result == null || !volcano.Busy) yield break;

            float giveUp = Time.time + result.Seconds + 10f;
            while ((volcano.Busy || volcano.Animating) && Time.time < giveUp) yield return null;

            Check($"and pays what its seed says ({result.Win}), replayed with the buy",
                  wallet.Chips == before - cost + result.Win + volcano.LastJackpot
                  && SlotMath.Spin(volcano.Kind, volcano.LastSeed, volcano.LastBet, buy: true).Win == result.Win);
        }

        /// <summary>
        /// Fruit Tumble's bombs against the Volcano's orbs, read off the pictures of bought features.
        /// A free spin's first picture shows the multiplier it starts with: always 0 on Fruit, where
        /// a bomb multiplies its own sequence and is gone, and the carried total on the Volcano.
        /// </summary>
        IEnumerator Bombs()
        {
            bool fruitCarried = false, fruitMultiplied = false, volcanoCarried = false, baseBomb = false;

            for (int i = 0; i < 2000; i++)
            {
                if (i % 200 == 199) yield return null;
                int seed = i * 7919 + 13;

                foreach (SlotFrame f in SlotMath.Spin(SlotKind.Fruit, seed, 100, buy: true).Frames)
                {
                    if (f.FreeSpinsLeft >= 0 && f.Drop && f.Multiplier > 0) fruitCarried = true;
                    if (f.FreeSpinsLeft >= 0 && !f.Drop && f.Multiplier > 0) fruitMultiplied = true;
                }

                foreach (SlotFrame f in SlotMath.Spin(SlotKind.Volcano, seed, 100, buy: true).Frames)
                    if (f.FreeSpinsLeft >= 0 && f.Drop && f.Multiplier > 0) volcanoCarried = true;

                foreach (SlotFrame f in SlotMath.Spin(SlotKind.Fruit, seed, 100).Frames)
                    if (f.FreeSpinsLeft < 0 && System.Array.IndexOf(f.Grid, SlotMath.Fruit.Bomb) >= 0) baseBomb = true;
            }

            Check("Fruit: bombs multiply a free spin's sequence", fruitMultiplied);
            Check("Fruit: and never carry into the next free spin", !fruitCarried);
            Check("Volcano: its orbs do carry", volcanoCarried);
            Check("Fruit: no bomb ever lands in the base game", !baseBomb);

            // Eight berries pay the same table whatever the game's rules carry.
            var grid = new int[SlotMath.Volcano.Cells];
            for (int c = 0; c < grid.Length; c++) grid[c] = c < 8 ? 0 : 1 + c % 8;
            Check($"Fruit: eight berries pay its own table ({SlotMath.Fruit.Evaluate(grid, null)})",
                  SlotMath.Fruit.Evaluate(grid, null) == SlotMath.Fruit.Pays[0][0]);
        }

        IEnumerator Anteing(SlotMachine volcano, Wallet wallet, NetworkObject actor)
        {
            SlotButton ante = Button(volcano, SlotAction.Ante);
            SlotButton buy = Button(volcano, SlotAction.Buy);
            SlotButton spin = Button(volcano, SlotAction.Spin);

            ante.ServerInteract(actor);
            Check($"the ante button turns it on: a spin now stakes {volcano.Stake} on a bet of {volcano.Bet}",
                  volcano.Ante && volcano.Stake == SlotMath.AnteStake(volcano.Bet));
            Check("and the buy is off while it is", volcano.BuyCost == 0 && !buy.ServerCanInteract(actor));

            int before = wallet.Chips;
            spin.ServerInteract(actor);
            SlotResult result = volcano.LastResult;
            Check($"an ante spin takes {before - wallet.Chips}", wallet.Chips == before - volcano.Stake && result != null && result.Ante);
            if (result == null || !volcano.Busy) yield break;

            float giveUp = Time.time + result.Seconds + 10f;
            while ((volcano.Busy || volcano.Animating) && Time.time < giveUp) yield return null;

            Check($"and pays what its seed says with the ante ({result.Win})",
                  wallet.Chips == before - volcano.Stake + result.Win + volcano.LastJackpot
                  && SlotMath.Spin(volcano.Kind, volcano.LastSeed, volcano.LastBet, ante: true).Win == result.Win
                  && volcano.Played != null && volcano.Played.Ante);

            ante.ServerInteract(actor);
            Check("pressed again, the ante is off and the buy is back", !volcano.Ante && volcano.BuyCost > 0);
        }

        IEnumerator Jackpot(SlotMachine volcano, SlotMachine[] machines, Wallet wallet, NetworkObject actor)
        {
            SlotButton spin = Button(volcano, SlotAction.Spin);

            Check($"every cabinet shows the same jackpot ({string.Join(" ", machines.Select(m => m.Jackpot))})",
                  machines.All(m => m.Jackpot == volcano.Jackpot) && volcano.Jackpot >= SlotMath.JackpotSeed);

            // Feed it: one ordinary spin puts its share of the stake in the pot.
            long pot = SlotMachine.ServerPot;
            spin.ServerInteract(actor);
            bool dropped = volcano.LastJackpot > 0;
            Check($"a stake of {volcano.Bet} feeds the pot {volcano.Bet * SlotMath.JackpotPct} hundredths "
                  + $"({pot} to {SlotMachine.ServerPot})",
                  dropped || SlotMachine.ServerPot == pot + volcano.Bet * SlotMath.JackpotPct);

            float giveUp = Time.time + 60f;
            while ((volcano.Busy || volcano.Animating) && Time.time < giveUp) yield return null;

            // Now drop it for certain.
            int before = wallet.Chips;
            int celebrated = BigWin.Celebrated;
            int expected = (int)((SlotMachine.ServerPot + volcano.Bet * SlotMath.JackpotPct) / 100);

            SlotMachine.ForceJackpot = true;
            spin.ServerInteract(actor);
            SlotResult result = volcano.LastResult;
            yield return null;

            Check($"a drop pays the whole pot ({volcano.LastJackpot}, expected {expected}) and resets it, "
                  + $"but the ticker holds until the reels stop ({volcano.Jackpot})",
                  volcano.LastJackpot == expected && SlotMachine.ServerPot == SlotMath.JackpotSeed * 100L
                  && volcano.Jackpot >= expected && !SlotMachine.ForceJackpot);

            giveUp = Time.time + result.Seconds + 10f;
            while ((volcano.Busy || volcano.Animating) && Time.time < giveUp) yield return null;
            yield return new WaitForSeconds(0.3f);

            Check($"the winner is paid the spin and the jackpot ({wallet.Chips - before + result.Bet} = {result.Win} + {expected})",
                  wallet.Chips == before - result.Bet + result.Win + expected && volcano.LastWin == result.Win + expected);
            Check($"then every cabinet shows the fresh pot ({string.Join(" ", machines.Select(m => m.Jackpot))})",
                  machines.All(m => m.Jackpot == SlotMath.JackpotSeed));
            Check($"and the host threw the JACKPOT banner (tier {BigWin.Latest.Tier}, {BigWin.Latest.Win})",
                  BigWin.Celebrated > celebrated && BigWin.Latest.Tier == BigWin.JackpotTier
                  && BigWin.Latest.Win == result.Win + expected);
            Check($"the board's ticker reads \"{UI.SlotBoard.JackpotLine(volcano.Jackpot)}\"",
                  UI.SlotBoard.JackpotLine(volcano.Jackpot).StartsWith("JACKPOT"));
        }

        IEnumerator Gambling(SlotMachine sevens, Wallet wallet, NetworkObject actor)
        {
            SlotButton spin = Button(sevens, SlotAction.Spin);

            // A third of Sevens spins win; forty tries without one is a broken machine, not bad luck.
            for (int n = 0; n < 40 && sevens.Gamble == 0; n++)
            {
                spin.ServerInteract(actor);
                float giveUp = Time.time + 15f;
                while (sevens.Busy && Time.time < giveUp) yield return null;
            }

            Check($"a Sevens win offers the card ({sevens.Gamble})", sevens.Gamble > 0 && sevens.GamblerId == actor.ObjectId);
            if (sevens.Gamble == 0) yield break;

            SlotButton red = Button(sevens, SlotAction.Red);
            Check("to whoever won it", red.ServerCanInteract(actor) && !sevens.CanGamble(null));

            int rounds = 0, wins = 0;
            bool fair = true;

            while (sevens.Gamble > 0 && rounds < SlotMachine.MaxGambles)
            {
                int before = wallet.Chips;
                int stake = sevens.Gamble;
                bool won = sevens.ServerGamble(actor, red: rounds % 2 == 0);
                rounds++;

                if (won) wins++;
                if (wallet.Chips != before + (won ? stake : -stake)) fair = false;
                if (sevens.Card != 1 && sevens.Card != 2) fair = false;
                if (!won && sevens.Gamble != 0) fair = false;
                if (won && rounds < SlotMachine.MaxGambles && sevens.Gamble != stake * 2) fair = false;

                yield return null;
            }

            Check($"the card paid double or took the stake every time ({rounds} turned, {wins} won)", fair);
            Check("and the offer ended with a loss or the last double", sevens.Gamble == 0 && !sevens.CanGamble(actor));
        }

        IEnumerator Refusals(SlotMachine sevens, Wallet wallet, NetworkObject actor)
        {
            SlotButton spin = Button(sevens, SlotAction.Spin);
            SlotButton bet = Button(sevens, SlotAction.Bet);

            wallet.ServerSetChips(sevens.Bet - 1);
            yield return new WaitForSeconds(0.2f);

            Check("a cabinet will not spin for a stack short of the bet",
                  !spin.ServerCanInteract(actor) && sevens.ServerSpin(actor) == 0 && wallet.Chips == sevens.Bet - 1);

            wallet.ServerSetChips(1000);
            yield return new WaitForSeconds(0.2f);

            Check("but spins once it can", sevens.ServerSpin(actor) > 0 && sevens.Busy);
            Check("and will not spin again, change the bet or deal a card mid-spin",
                  sevens.ServerSpin(actor) == 0 && !sevens.ServerNextBet() && !bet.ServerCanInteract(actor)
                  && !sevens.ServerGamble(actor, true));

            float giveUp = Time.time + 15f;
            while (sevens.Busy && Time.time < giveUp) yield return null;

            Check("then takes presses again", !sevens.Busy && spin.ServerCanInteract(actor));
        }

        /// <summary>Spins Sevens until killed, so a <c>-slotTest -client</c> has something to watch.</summary>
        IEnumerator Exhibition(SlotMachine sevens, Wallet wallet, NetworkObject actor)
        {
            while (true)
            {
                wallet.ServerSetChips(1000);
                sevens.ServerSpin(actor);

                float giveUp = Time.time + 15f;
                while (sevens.Busy && Time.time < giveUp) yield return null;
                yield return new WaitForSeconds(1f);
            }
        }

        // ---------------------------------------------------------------- the client's half

        /// <summary>
        /// A peer that is not the server gets the seed and the bet and nothing else. It must rebuild
        /// the same spin the host paid for - the win it works out must be the win the host
        /// replicated - and its own screen must stop on that spin's last picture.
        /// </summary>
        IEnumerator Watching()
        {
            while (!InstanceFinder.NetworkManager.IsClientStarted) yield return null;

            SlotMachine sevens = null;
            float deadline = Time.time + WaitForWorld;

            while (Time.time < deadline && sevens == null)
            {
                sevens = FindObjectsByType<SlotMachine>(FindObjectsSortMode.None)
                         .FirstOrDefault(m => m.IsSpawned && m.Kind == SlotKind.Sevens);
                if (sevens == null) yield return new WaitForSeconds(0.5f);
            }

            if (sevens == null)
            {
                Check("the client sees the Sevens cabinet", false);
                Report();
                yield break;
            }

            int seen = 0;
            float giveUp = Time.time + 180f;

            // A spin already running when this client arrived was never sent here; skip it.
            while (sevens.Busy && Time.time < giveUp) yield return null;

            while (seen < Watched && Time.time < giveUp)
            {
                while (!sevens.Busy && Time.time < giveUp) yield return null;
                while ((sevens.Busy || sevens.Animating) && Time.time < giveUp) yield return null;
                if (Time.time >= giveUp) break;

                SlotResult played = sevens.Played;
                bool agrees = played != null && played.Seed == sevens.LastSeed
                              && played.Win + sevens.PlayedJackpot == sevens.LastWin
                              && sevens.ShowingGrid().SequenceEqual(played.Frames[played.Frames.Count - 1].Grid);

                Check($"spin {seen + 1}: the host paid {sevens.LastWin}, this client replayed "
                      + $"{(played != null ? played.Win : -1)} and shows its last picture", agrees);
                seen++;
            }

            Check($"the client watched {seen} spin(s)", seen >= Watched);
            Check($"and sees the shared jackpot ({sevens.Jackpot})", sevens.Jackpot >= SlotMath.JackpotSeed);
            Report();
        }

        // ---------------------------------------------------------------- scaffolding

        static SlotButton Button(SlotMachine machine, SlotAction action)
            => machine.GetComponentsInChildren<SlotButton>().First(b => b.Action == action);

        void Report()
        {
            if (_failed == 0) Debug.Log($"[SlotTest] {_passed} passed, 0 failed.");
            else Debug.LogError($"[SlotTest] {_passed} passed, {_failed} FAILED.");
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                Debug.Log($"[SlotTest] PASS: {what}");
            }
            else
            {
                _failed++;
                Debug.LogError($"[SlotTest] FAIL: {what}");
            }
        }
    }
}
