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
    /// The acceptance test for #64, behind <c>-rouletteTest</c>. Wants <c>-scene island -noNatives
    /// -noAnimals</c>.
    ///
    /// The acceptance - "clients never influence the outcome; the wheel always lands on the host's
    /// number" - is again a negative, and again the way to get at it is to check the positives that
    /// would have to break first:
    ///
    /// 1. The payout table is the real one. Eighteen reds, eighteen blacks, twelve to a dozen, and
    ///    zero beats every bet but a straight on zero. Every kind loses money in the long run by the
    ///    same 2.7%, which is the only reason a casino is a business.
    /// 2. A spin pays exactly what the number says. The suite does not care what comes up; it reads
    ///    the result back and asserts the wallet moved by precisely the amount that number implies.
    /// 3. The wheel is *shown* to land where the server said. It reads the pocket back off the
    ///    transform angle after the animation has finished, so a wheel that lied about a payout
    ///    would fail here rather than in somebody's screenshot.
    /// 4. Money does not move at a table. Only chips do, which is what keeps #63's rule true after
    ///    #64 exists.
    /// </summary>
    public class RouletteTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const float WaitForTable = 60f;

        /// <summary>Rounds played for the wallet arithmetic. Enough to see zero come up sometimes.</summary>
        const int Rounds = 12;

        const int Stake = 100;

        /// <summary>Spins a watching client must see land before it is satisfied.</summary>
        const int Watched = 4;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-rouletteTest")) return;

            _started = true;

            var go = new GameObject("RouletteTest");
            DontDestroyOnLoad(go);
            go.AddComponent<RouletteTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null) yield return null;

            // A client gets its own, much smaller suite: it cannot see the roll, only what it was
            // told and what its own wheel then did. That is the acceptance read literally, and it is
            // the half a host-only run cannot check - the host never receives its own RPC.
            if (!InstanceFinder.NetworkManager.IsServerStarted)
            {
                yield return Watching();
                yield break;
            }

            PlayerMotor player = null;
            float deadline = Time.time + WaitForPlayer;

            while (Time.time < deadline && player == null)
            {
                player = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                         .FirstOrDefault(m => m != null && m.IsSpawned);

                if (player == null) yield return new WaitForSeconds(0.5f);
            }

            if (player == null)
            {
                Debug.LogError("[RouletteTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            RouletteWheel table = null;
            float tableDeadline = Time.time + WaitForTable;

            while (Time.time < tableDeadline && table == null)
            {
                table = FindObjectsByType<RouletteWheel>(FindObjectsSortMode.None)
                        .FirstOrDefault(t => t != null && t.IsSpawned);

                if (table == null) yield return new WaitForSeconds(0.5f);
            }

            if (table == null)
            {
                Debug.LogError("[RouletteTest] No roulette table in the world. Run CasinoFactory.Build "
                               + "and bake the POIs with -rebuildPois.");
                yield break;
            }

            BetSpot[] spots = table.GetComponentsInChildren<BetSpot>();
            var wallet = player.GetComponent<Wallet>();

            if (wallet == null)
            {
                Debug.LogError("[RouletteTest] The player has no Wallet.");
                yield break;
            }

            player.ServerTeleport(table.transform.position + Vector3.forward * 2f, 0f);

            // A round nobody is waiting through. Twelve seconds of betting is right for a casino
            // and wrong for a test that plays a dozen rounds.
            table.ServerSetTiming(betWindow: 0.4f, spinSeconds: 0.6f);

            Wiring(table, spots);
            PayTable();
            Geometry();

            yield return Spinning(table, spots, wallet, player.NetworkObject);
            yield return Refusals(table, spots, wallet, player.NetworkObject);

            Report();

            yield return Exhibition(table, spots, wallet, player.NetworkObject);
        }

        /// <summary>
        /// Spins on, one round at a time, until the process is killed. Nothing here is graded: it
        /// exists so that a second process running <c>-rouletteTest -client</c> has wheels to watch,
        /// because the interesting half of #64 only happens on a peer that is not the server.
        /// </summary>
        IEnumerator Exhibition(RouletteWheel table, BetSpot[] spots, Wallet wallet, NetworkObject actor)
        {
            BetSpot red = spots.First(s => s.Kind == BetKind.Red);
            table.ServerSetTiming(betWindow: 0.4f, spinSeconds: 2f);

            while (true)
            {
                wallet.ServerSetChips(Stake);
                red.ServerInteract(actor);
                table.ServerCallIt();

                float giveUp = Time.time + 15f;
                while (!table.Spinning && Time.time < giveUp) yield return null;
                while (table.Spinning && Time.time < giveUp) yield return null;

                yield return new WaitForSeconds(1f);
            }
        }

        // ---------------------------------------------------------------- the client's half

        /// <summary>
        /// What a player who is not the host sees. The client never rolls anything and has no way to
        /// ask for a number; all it has is the <c>SyncVar</c> it was handed and the wheel its own
        /// machine drew. If those two ever disagree, the animation is lying about a payout that has
        /// already been made - which is the only failure mode the acceptance leaves room for.
        /// </summary>
        IEnumerator Watching()
        {
            while (!InstanceFinder.NetworkManager.IsClientStarted) yield return null;

            RouletteWheel table = null;
            float deadline = Time.time + WaitForTable;

            while (Time.time < deadline && table == null)
            {
                table = FindObjectsByType<RouletteWheel>(FindObjectsSortMode.None)
                        .FirstOrDefault(t => t != null && t.IsSpawned);

                if (table == null) yield return new WaitForSeconds(0.5f);
            }

            if (table == null)
            {
                Debug.LogError("[RouletteTest] The client never saw a roulette table.");
                yield break;
            }

            Check("a client can see the table without owning it", table.IsSpawned);

            int seen = 0;
            float giveUp = Time.time + 120f;

            // Let any round that was already turning when this client arrived finish unwatched. Its
            // RPC went out before there was anybody here to receive it, so that wheel never moved
            // and grading it would be grading the join, not the spin.
            while (table.Spinning && Time.time < giveUp) yield return null;

            while (seen < Watched && Time.time < giveUp)
            {
                while (!table.Spinning && Time.time < giveUp) yield return null;
                while (table.Spinning && Time.time < giveUp) yield return null;

                if (Time.time >= giveUp) break;

                // Wait for this machine's own wheel, not the server's clock. The RPC arrives a tick
                // after the roll and the easing runs locally, so the wheel is still moving for a
                // moment after the host has settled up.
                while (table.Animating && Time.time < giveUp) yield return null;

                int said = table.Result;
                int shown = table.ShowingPocket();

                Check($"spin {seen + 1}: the host said {said} and this client's wheel shows {shown}",
                      shown == said && said >= 0);

                seen++;
            }

            Check($"the client watched the wheel land {seen} time(s)", seen >= Watched);

            Debug.Log($"[RouletteTest] client watched {seen} spin(s); the wheel "
                      + (_failed == 0 ? "agreed with the host every time." : "did not agree."));

            Report();
        }

        // ---------------------------------------------------------------- what is on the table

        void Wiring(RouletteWheel table, BetSpot[] spots)
        {
            Check($"the casino has a table with bet spots ({spots.Length})", spots.Length >= 10);

            Check("every spot is its own NetworkObject, so aiming picks one",
                  spots.Select(s => s.NetworkObject).Distinct().Count() == spots.Length);

            Check("and no two spots are the same bet",
                  spots.Select(s => (s.Kind, s.Number)).Distinct().Count() == spots.Length);

            Check("all of them belong to this table", spots.All(s => s.Table == table));

            Check("the table is taking bets before anybody has bet", table.TakingBets);
            Check("and nothing is staked yet", table.Staked == 0 && table.BetCount == 0);

            Debug.Log($"[RouletteTest] table open: {spots.Length} spots - "
                      + $"{string.Join(", ", spots.Select(s => s.Label))}.");
        }

        // ---------------------------------------------------------------- the maths, on paper

        void PayTable()
        {
            int reds = 0, blacks = 0, odds = 0, evens = 0, lows = 0, highs = 0;

            for (int n = 0; n < RouletteWheel.Pockets; n++)
            {
                if (RouletteWheel.Wins(BetKind.Red, n, 0)) reds++;
                if (RouletteWheel.Wins(BetKind.Black, n, 0)) blacks++;
                if (RouletteWheel.Wins(BetKind.Odd, n, 0)) odds++;
                if (RouletteWheel.Wins(BetKind.Even, n, 0)) evens++;
                if (RouletteWheel.Wins(BetKind.Low, n, 0)) lows++;
                if (RouletteWheel.Wins(BetKind.High, n, 0)) highs++;
            }

            Check($"eighteen red and eighteen black ({reds}/{blacks})", reds == 18 && blacks == 18);
            Check($"eighteen odd and eighteen even ({odds}/{evens})", odds == 18 && evens == 18);
            Check($"eighteen low and eighteen high ({lows}/{highs})", lows == 18 && highs == 18);

            foreach (BetKind dozen in new[] { BetKind.DozenLow, BetKind.DozenMid, BetKind.DozenHigh })
            {
                int hits = 0;
                for (int n = 0; n < RouletteWheel.Pockets; n++)
                    if (RouletteWheel.Wins(dozen, n, 0)) hits++;

                Check($"twelve numbers in {dozen} ({hits})", hits == 12);
            }

            Check("zero beats red, black, odd, even, low and high",
                  !RouletteWheel.Wins(BetKind.Red, 0, 0) && !RouletteWheel.Wins(BetKind.Black, 0, 0)
                  && !RouletteWheel.Wins(BetKind.Odd, 0, 0) && !RouletteWheel.Wins(BetKind.Even, 0, 0)
                  && !RouletteWheel.Wins(BetKind.Low, 0, 0) && !RouletteWheel.Wins(BetKind.High, 0, 0));

            Check("and every dozen", !RouletteWheel.Wins(BetKind.DozenLow, 0, 0)
                                     && !RouletteWheel.Wins(BetKind.DozenMid, 0, 0)
                                     && !RouletteWheel.Wins(BetKind.DozenHigh, 0, 0));

            int straightHits = 0;
            for (int n = 0; n < RouletteWheel.Pockets; n++)
                if (RouletteWheel.Wins(BetKind.Straight, n, 7)) straightHits++;

            Check($"a straight bet wins on exactly one number ({straightHits})", straightHits == 1);
            Check("including zero, if that is the number you picked",
                  RouletteWheel.Wins(BetKind.Straight, 0, 0));

            // The edge, stated as arithmetic rather than as a percentage in a comment: a bet that
            // wins W of 37 pockets and returns P+1 pays back W*(P+1) for every 37 staked. Anything
            // that came to 37 or more would be a casino that loses money.
            foreach (BetKind kind in System.Enum.GetValues(typeof(BetKind)).Cast<BetKind>())
            {
                int hits = 0;
                for (int n = 0; n < RouletteWheel.Pockets; n++)
                    if (RouletteWheel.Wins(kind, n, 7)) hits++;

                int back = hits * (RouletteWheel.Payout(kind) + 1);
                Check($"{kind} pays back {back} of every 37 staked", back == 36);
            }

            Debug.Log("[RouletteTest] pay table: 36 back for every 37 staked, whatever you bet. "
                      + "The house keeps 2.7%.");
        }

        // ---------------------------------------------------------------- the wheel, as geometry

        void Geometry()
        {
            bool roundTrip = true;
            for (int n = 0; n < RouletteWheel.Pockets; n++)
                if (RouletteWheel.PocketAt(RouletteWheel.PocketAngle(n)) != n) roundTrip = false;

            Check("every pocket reads back as itself off the wheel angle", roundTrip);

            bool wrapped = true;
            for (int n = 0; n < RouletteWheel.Pockets; n++)
                if (RouletteWheel.PocketAt(RouletteWheel.PocketAngle(n) - 360f * 3f) != n) wrapped = false;

            Check("and still does after three whole turns", wrapped);

            Check("thirty-seven pockets, each number once",
                  Enumerable.Range(0, RouletteWheel.Pockets)
                            .Select(RouletteWheel.PocketAngle).Distinct().Count()
                  == RouletteWheel.Pockets);
        }

        // ---------------------------------------------------------------- playing it

        IEnumerator Spinning(RouletteWheel table, BetSpot[] spots, Wallet wallet, NetworkObject actor)
        {
            BetSpot red = spots.First(s => s.Kind == BetKind.Red);

            wallet.ServerSetChips(Rounds * Stake);
            wallet.ServerSetBalance(500);
            Wallet.ResetLedger();

            yield return new WaitForSeconds(0.3f);

            int money = Wallet.TotalInWallets();
            int chips = wallet.Chips;
            int wins = 0;
            int zeroes = 0;
            bool everyLanding = true;
            bool everyPayout = true;

            for (int round = 0; round < Rounds; round++)
            {
                int before = wallet.Chips;

                red.ServerInteract(actor);

                Check($"round {round + 1}: the stake left the wallet ({before - wallet.Chips})",
                      wallet.Chips == before - Stake);

                table.ServerCallIt();

                float giveUp = Time.time + 10f;
                while (!table.Spinning && Time.time < giveUp) yield return null;
                while (table.Spinning && Time.time < giveUp) yield return null;

                yield return null;

                int result = table.Result;
                bool won = RouletteWheel.Wins(BetKind.Red, result, 0);

                int expected = before - Stake
                               + (won ? Stake * (RouletteWheel.Payout(BetKind.Red) + 1) : 0);

                if (wallet.Chips != expected)
                {
                    everyPayout = false;
                    Debug.LogError($"[RouletteTest] round {round + 1} landed on {result} and paid "
                                   + $"{wallet.Chips - (before - Stake)}, not {expected - (before - Stake)}.");
                }

                if (table.ShowingPocket() != result)
                {
                    everyLanding = false;
                    Debug.LogError($"[RouletteTest] the wheel says {table.ShowingPocket()}, "
                                   + $"the host said {result}.");
                }

                if (won) wins++;
                if (result == 0) zeroes++;
            }

            Check($"every round paid exactly what its number said ({Rounds} rounds)", everyPayout);
            Check("and the wheel stopped on the host's number every time", everyLanding);

            Check($"the table cleared its bets ({table.BetCount} left)",
                  table.BetCount == 0 && table.Staked == 0);

            Check($"money never moved at the table ({money} before, {Wallet.TotalInWallets()} after)",
                  Wallet.TotalInWallets() == money);

            Check("and the ledger did not call any of it income",
                  Wallet.Minted == 0 && Wallet.Burned == 0 && Wallet.Exchanged == 0);

            int staked = Rounds * Stake;
            Check($"every stake is accounted for ({Wallet.Staked} staked, {Wallet.PaidOut} paid)",
                  Wallet.Staked == staked && wallet.Chips == chips - staked + Wallet.PaidOut);

            Debug.Log($"[RouletteTest] {Rounds} spins on red: {wins} won, {zeroes} zero(es). "
                      + $"{Wallet.Staked} chips staked, {Wallet.PaidOut} paid back, "
                      + $"{Wallet.Staked - Wallet.PaidOut} to the house. Wallet {wallet.Describe()}.");
        }

        // ---------------------------------------------------------------- what it will not do

        IEnumerator Refusals(RouletteWheel table, BetSpot[] spots, Wallet wallet, NetworkObject actor)
        {
            BetSpot black = spots.First(s => s.Kind == BetKind.Black);

            wallet.ServerSetChips(0);
            yield return new WaitForSeconds(0.2f);

            Check("a table will not take a bet from an empty stack",
                  !black.ServerCanInteract(actor));

            Check("and the spot says nothing to a player with no chips",
                  string.IsNullOrEmpty(black.Prompt));

            wallet.ServerSetChips(50);
            yield return new WaitForSeconds(0.2f);

            Check("staking more than you hold takes nothing",
                  table.ServerPlaceBet(actor, BetKind.Red, 0, 500) == 0);

            Check("and the stack is untouched", wallet.Chips == 50);

            Check("but the remainder can be staked",
                  table.ServerPlaceBet(actor, BetKind.Red, 0, 50) == 50);

            table.ServerCallIt();

            float giveUp = Time.time + 10f;
            while (!table.Spinning && Time.time < giveUp) yield return null;

            Check("a bet placed after the wheel starts turning is refused",
                  table.Spinning && table.ServerPlaceBet(actor, BetKind.Black, 0, 10) == 0);

            Check("and the spot goes quiet while it spins", string.IsNullOrEmpty(black.Prompt));

            while (table.Spinning && Time.time < giveUp) yield return null;

            Check("the table takes bets again once it has paid out", table.TakingBets);

            Debug.Log($"[RouletteTest] refusals done. Last number: {table.Result}, "
                      + $"wallet {wallet.Describe()}.");
        }

        // ---------------------------------------------------------------- scaffolding

        void Report()
        {
            if (_failed == 0) Debug.Log($"[RouletteTest] {_passed} passed, 0 failed.");
            else Debug.LogError($"[RouletteTest] {_passed} passed, {_failed} FAILED.");
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                Debug.Log($"[RouletteTest] PASS: {what}");
            }
            else
            {
                _failed++;
                Debug.LogError($"[RouletteTest] FAIL: {what}");
            }
        }
    }
}
