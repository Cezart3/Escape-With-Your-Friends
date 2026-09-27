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
            (SlotKind.Volcano, 2055330),
            (SlotKind.Reef, 2131545),
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
            wallet.ServerSetChips(5000);
            Wallet.ResetLedger();
            yield return new WaitForSeconds(0.3f);

            int money = Wallet.TotalInWallets();
            int chips = wallet.Chips;

            foreach (SlotMachine machine in machines.OrderBy(m => m.Kind))
            {
                player.ServerTeleport(machine.transform.position + machine.transform.forward * 1f, 0f);
                yield return Playing(machine, wallet, actor);
            }

            SlotMachine sevens = machines.First(m => m.Kind == SlotKind.Sevens);
            player.ServerTeleport(sevens.transform.position + sevens.transform.forward * 1f, 0f);
            yield return Gambling(sevens, wallet, actor);

            Check($"money never moved at the cabinets ({money} before, {Wallet.TotalInWallets()} after)",
                  Wallet.TotalInWallets() == money);
            Check("and the ledger called none of it income", Wallet.Minted == 0 && Wallet.Burned == 0 && Wallet.Exchanged == 0);
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
        }

        IEnumerator Rtp()
        {
            // Bands, not points: a fixed seed run is one sample of a volatile game. The tuned figure
            // over millions of spins is about 95% for each; see docs/ARCHITECTURE.md.
            (SlotKind kind, double low, double high)[] bands =
            {
                (SlotKind.Sevens, 0.93, 0.98),
                (SlotKind.Volcano, 0.88, 1.03),
                (SlotKind.Reef, 0.88, 1.03),
            };

            foreach ((SlotKind kind, double low, double high) in bands)
            {
                float started = Time.realtimeSinceStartup;
                long staked = 0, won = 0;
                int hits = 0, features = 0, capped = 0, biggest = 0;

                for (int i = 0; i < RtpSpins; i++)
                {
                    if (i % 20000 == 19999) yield return null;

                    SlotResult r = SlotMath.Spin(kind, i * 7919 + 13, 100);
                    staked += 100;
                    won += r.Win;
                    if (r.Win > 0) hits++;
                    if (r.FreeSpins > 0) features++;
                    if (r.Capped) capped++;
                    biggest = Mathf.Max(biggest, r.Win);
                }

                double rtp = (double)won / staked;
                Debug.Log($"[SlotTest] {kind}: {RtpSpins} spins, return {rtp:P2}, hits {100.0 * hits / RtpSpins:0.0}%, "
                          + $"features {features}, best {biggest / 100}x, capped {capped}, "
                          + $"{Time.realtimeSinceStartup - started:0.0}s.");

                Check($"{kind} returns {rtp:P2}, inside {low:P0}-{high:P0}", rtp >= low && rtp <= high);
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
                int wanted = machine.Kind == SlotKind.Sevens ? 4 : 2;

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

                if (wallet.Chips != before - result.Bet + result.Win)
                {
                    paid = false;
                    Debug.LogError($"[SlotTest] {machine.Title} seed {result.Seed}: paid "
                                   + $"{wallet.Chips - before + result.Bet}, not {result.Win}.");
                }

                if (SlotMath.Spin(machine.Kind, machine.LastSeed, machine.LastBet).Win != result.Win
                    || machine.LastWin != result.Win)
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
                bool agrees = played != null && played.Seed == sevens.LastSeed && played.Win == sevens.LastWin
                              && sevens.ShowingGrid().SequenceEqual(played.Frames[played.Frames.Count - 1].Grid);

                Check($"spin {seen + 1}: the host paid {sevens.LastWin}, this client replayed "
                      + $"{(played != null ? played.Win : -1)} and shows its last picture", agrees);
                seen++;
            }

            Check($"the client watched {seen} spin(s)", seen >= Watched);
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
