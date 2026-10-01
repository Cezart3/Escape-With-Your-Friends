using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.World;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>
    /// The acceptance test for <see cref="CasinoDays"/>, behind <c>-casinoDaysTest</c> with
    /// <c>-scene island -save -savePath X</c>, solo. It checks that the floor opens one game a day,
    /// that a closed cabinet refuses a player with chips, that every stake grows half again per day
    /// up to the cap, and that a saved run keeps its days.
    /// </summary>
    public class CasinoDaysTest : MonoBehaviour
    {
        static bool _started;
        int _passed, _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-casinoDaysTest")) return;
            _started = true;

            var go = new GameObject("CasinoDaysTest");
            DontDestroyOnLoad(go);
            go.AddComponent<CasinoDaysTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            PlayerMotor motor = null;
            SlotMachine sevens = null;
            BlackjackButton seat = null;
            for (float deadline = Time.time + 60f; Time.time < deadline;)
            {
                motor = FindAnyObjectByType<PlayerMotor>();
                sevens = FindObjectsByType<SlotMachine>(FindObjectsSortMode.None)
                    .FirstOrDefault(m => m.IsSpawned && m.Kind == SlotKind.Sevens);
                seat = FindObjectsByType<BlackjackButton>(FindObjectsSortMode.None).FirstOrDefault(b => b.IsSpawned && b.Press == BlackjackPress.Bet);
                if (motor != null && sevens != null && seat != null) break;
                yield return new WaitForSeconds(0.5f);
            }

            var wallet = motor != null ? motor.GetComponent<Wallet>() : null;
            var cashier = FindObjectsByType<Cashier>(FindObjectsSortMode.None).FirstOrDefault(c => c.IsSpawned);
            Check("a player, the sevens cabinet, a blackjack seat and the cage",
                  wallet != null && sevens != null && seat != null && cashier != null);
            if (wallet == null || sevens == null || seat == null || cashier == null) { Report(); yield break; }

            NetworkObject actor = motor.NetworkObject;
            SlotButton spin = sevens.GetComponentsInChildren<SlotButton>().First(b => b.Action == SlotAction.Spin);
            wallet.ServerSetChips(100000);

            // ------------------------------------------------------------ one game a day

            CasinoDays.TestDay = 0;
            Check("day 1: roulette is open, blackjack and the slots are not",
                  CasinoDays.IsOpen(CasinoGame.Roulette) && !CasinoDays.IsOpen(CasinoGame.Blackjack)
                  && !CasinoDays.IsOpen(CasinoGame.Sevens) && !CasinoDays.IsOpen(CasinoGame.Reef));
            Check("a closed cabinet refuses a player with chips", !spin.ServerCanInteract(actor));
            Check("a closed table refuses them too", !seat.ServerCanInteract(actor));
            Check($"and says when it opens ('{CasinoDays.Closed(sevens.Title, CasinoGame.Sevens)}')",
                  CasinoDays.Closed(sevens.Title, CasinoGame.Sevens).Contains("day 3"));

            CasinoDays.TestDay = 1;
            Check("day 2: blackjack opens, the sevens still wait",
                  CasinoDays.IsOpen(CasinoGame.Blackjack) && !CasinoDays.IsOpen(CasinoGame.Sevens));
            Check("and the table takes a bet", seat.ServerCanInteract(actor));

            CasinoDays.TestDay = 2;
            Check("day 3: the sevens cabinet spins", spin.ServerCanInteract(actor));

            CasinoDays.TestDay = 4;
            Check("day 5: every game is open",
                  System.Enum.GetValues(typeof(CasinoGame)).Cast<CasinoGame>().All(CasinoDays.IsOpen));

            // ------------------------------------------------------------ bigger chips every day

            CasinoDays.TestDay = 0;
            int jack0 = BlackjackTable.Chunk, slot0 = sevens.Bet, cage0 = cashier.Chunk;
            Check($"day 1 stakes are the base ones (blackjack {jack0}, slot {slot0}, cage {cage0})",
                  jack0 == 50 && slot0 == SlotMath.Bets[0] && cage0 == 100);

            CasinoDays.TestDay = 2;
            Check($"day 3 stakes are 2.25 times that (blackjack {BlackjackTable.Chunk}, slot {sevens.Bet}, cage {cashier.Chunk})",
                  Mathf.Abs(BlackjackTable.Chunk - 112.5f) <= 1f && Mathf.Abs(sevens.Bet - 22.5f) <= 1f && cashier.Chunk == 225);
            Check($"a blackjack seat holds ten bets ({BlackjackTable.MaxBet})", BlackjackTable.MaxBet == BlackjackTable.Chunk * 10);

            CasinoDays.TestDay = CasinoDays.TopDay;
            int top = BlackjackTable.Chunk;
            CasinoDays.TestDay = CasinoDays.TopDay + 5;
            Check($"the stakes stop growing at day {CasinoDays.TopDay + 1} ({top})", BlackjackTable.Chunk == top && top > 2000);

            // ------------------------------------------------------------ the days survive a save

            CasinoDays.TestDay = -1;
            Check("the run save is armed (pass -save)", RunSave.Armed && RunSave.Run != null);
            if (RunSave.Run != null)
            {
                int live = CasinoDays.Day;
                Check($"a fresh run is on day {WorldClock.Day + 1} ({live + 1})", live == WorldClock.Day);

                RunSave.Run.day = 6;
                CasinoDays.NewSession();
                Check($"a run saved on day 7 resumes there ({CasinoDays.Day + 1})", CasinoDays.Day == 6 + WorldClock.Day);

                RunSave.ServerSave();
                Check($"and saving does not count the days twice ({RunSave.Run.day}, then {CasinoDays.Day})",
                      RunSave.Run.day == 6 + WorldClock.Day && CasinoDays.Day == 6 + WorldClock.Day);

                RunSave.Run.day = 0;
                CasinoDays.NewSession();
            }

            Report();
        }

        void Check(string what, bool ok)
        {
            if (ok) _passed++; else _failed++;
            Debug.Log($"[CasinoDaysTest] {(ok ? "PASS" : "FAIL")} {what}");
        }

        void Report()
        {
            Debug.Log($"[CasinoDaysTest] {_passed} passed, {_failed} failed.");
            Application.Quit();
        }
    }
}
