using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.Vehicles;
using EscapeWithYourFriends.World;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// The acceptance test for #75, behind <c>-saveTest write</c> and <c>-saveTest read</c>. It wants
    /// <c>-save -savePath &lt;file&gt; -playerKey &lt;key&gt; -noNatives -noAnimals</c>, and the same
    /// three values in both runs.
    ///
    /// The acceptance is *"quit and resume a run without losing progress"*, and the only honest test
    /// of that is two processes. So this is one test in two halves: the first run plays a little,
    /// saves and quits; the second run is a genuinely fresh process, with a fresh scene and fresh
    /// SyncVars, which has nothing but the file to go on.
    ///
    /// The write half also does the whole round trip inside its own process - save, forget, re-read,
    /// re-apply - so that a broken serializer fails in one run rather than in a pair somebody has to
    /// remember to run both halves of. What that half cannot prove, and what the read half exists
    /// for, is that nothing was quietly surviving in a static.
    /// </summary>
    public class SaveTest : MonoBehaviour
    {
        const float WaitForPlayers = 90f;
        const float WaitForWorld = 60f;

        /// <summary>What the write half puts in the wallet, and the read half expects back.</summary>
        const int Money = 1234;
        const int Chips = 77;

        /// <summary>The tier the write half bolts to the vehicle's second part slot.</summary>
        const int Tier = 2;

        static bool _started;

        string _phase;
        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started) return;

            string phase = CommandLine.GetString("-saveTest", null);
            if (string.IsNullOrWhiteSpace(phase)) return;

            _started = true;

            var go = new GameObject("SaveTest");
            DontDestroyOnLoad(go);
            go.AddComponent<SaveTest>()._phase = phase.Trim().ToLowerInvariant();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            if (!RunSave.Armed)
            {
                Debug.LogError("[SaveTest] Saving is not armed. Pass -save (a headless build does "
                               + "not save unless asked). Nothing was checked.");
                yield break;
            }

            PlayerMotor player = null;
            float deadline = Time.time + WaitForPlayers;

            while (Time.time < deadline && player == null)
            {
                player = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                         .FirstOrDefault(p => p != null && p.IsSpawned);

                if (player == null) yield return new WaitForSeconds(0.5f);
            }

            if (player == null)
            {
                Debug.LogError("[SaveTest] Nobody spawned. Nothing was checked.");
                yield break;
            }

            // The catalogue is loaded by the scene, not by the network, so it can lag the spawn.
            deadline = Time.time + WaitForWorld;
            while (Time.time < deadline && (ItemCatalog.Active == null || PlaneAssembly.Instance == null))
                yield return new WaitForSeconds(0.5f);

            if (_phase == "write") yield return Writing(player);
            else if (_phase == "read") yield return Reading(player);
            else Debug.LogError($"[SaveTest] '{_phase}' is not a phase; use 'write' or 'read'.");

            Report();
        }

        // ---------------------------------------------------------------- half one: play and quit

        IEnumerator Writing(PlayerMotor player)
        {
            // A clean sheet, in the file and in memory both. Without the reload the merge would keep
            // whatever a previous run of this same test left behind, and the counts below would be
            // right for the wrong reason.
            if (File.Exists(RunSave.Path)) File.Delete(RunSave.Path);
            RunSave.Reload();

            var wallet = player.GetComponent<Wallet>();
            var bag = player.GetComponent<Inventory>();

            if (wallet == null || bag == null)
            {
                Fail("the player has a wallet and a bag");
                yield break;
            }

            wallet.ServerSetBalance(Money);
            wallet.ServerSetChips(Chips);

            List<ItemDef> stock = ItemCatalog.Active.Items.Where(i => i != null).Take(2).ToList();
            if (stock.Count < 2)
            {
                Fail("the catalogue has two items to put in a bag");
                yield break;
            }

            bag.ServerClear();
            bag.Add(stock[0], 1);
            bag.Add(stock[1], 1);
            yield return null;

            PlaneAssembly plane = PlaneAssembly.Instance;
            if (plane != null) plane.ServerFitAll();

            VehicleUpgrades vehicle = FindObjectsByType<VehicleUpgrades>(FindObjectsSortMode.None)
                                      .Where(v => v != null && v.IsSpawned)
                                      .OrderBy(v => v.name)
                                      .FirstOrDefault();

            // By name: both halves must pick the same vehicle, the file is keyed by name, and
            // FindObjectsByType promises no order between the buggy and the boat.
            if (vehicle != null)
            {
                var tiers = new int[VehicleUpgrades.Slots];
                tiers[1] = Tier;
                vehicle.ServerRestoreTiers(tiers);
            }

            yield return null;

            // What the bag looks like before the quit. Compared slot for slot afterwards, because a
            // restore that put everything back in a different order would still add up and would
            // still be wrong - somebody's hotbar is an arrangement, not a total.
            List<string> before = Layout(bag);

            RunSave.ServerSave();

            Check("the file exists after a save", File.Exists(RunSave.Path));

            string text = File.Exists(RunSave.Path) ? File.ReadAllText(RunSave.Path) : "";
            Check($"and it holds this player's key ({PlayerKey.Short(PlayerKey.Local)})",
                  text.Contains(PlayerKey.Local));
            Check($"and the money ({Money})", text.Contains(Money.ToString()));
            Check($"and the items by id, not by catalogue index ({stock[0].Id})",
                  text.Contains(stock[0].Id) && text.Contains(stock[1].Id));
            Check($"and the island it was written on ({GameSceneLoader.Current})",
                  text.Contains(GameSceneLoader.Current));

            // The round trip, without a second process: throw the state away, forget the file was
            // ever read, read it again and put it back.
            wallet.ServerSetBalance(0);
            wallet.ServerSetChips(0);
            bag.ServerClear();
            yield return null;

            Check("the state really was thrown away", wallet.Balance == 0 && Layout(bag).Count == 0);

            RunSave.Reload();
            RunSave.ServerApply(PlayerKey.Local, player.gameObject);
            yield return null;

            Check($"the money comes back off disk ({wallet.Balance})", wallet.Balance == Money);
            Check($"and the chips ({wallet.Chips})", wallet.Chips == Chips);

            List<string> after = Layout(bag);
            Check($"and the bag, slot for slot ({string.Join(" | ", after)})",
                  after.SequenceEqual(before));

            Debug.Log($"[SaveTest] wrote {RunSave.Path}: {Money} money, {Chips} chip(s), "
                      + $"{before.Count} stack(s), plane {(plane != null && plane.Complete ? "whole" : "unfinished")}, "
                      + $"island {GameSceneLoader.Current}.");
        }

        // ---------------------------------------------------------------- half two: a fresh process

        IEnumerator Reading(PlayerMotor player)
        {
            var wallet = player.GetComponent<Wallet>();
            var bag = player.GetComponent<Inventory>();

            if (wallet == null || bag == null)
            {
                Fail("the player has a wallet and a bag");
                yield break;
            }

            // Nothing is forced here on purpose. Everything below was put back by the game's own
            // load path before this coroutine ran: the world by RunSave's loop, the wallet and the
            // bag by PlayerSpawner the moment this body was stamped with its key.
            yield return new WaitForSeconds(1f);

            Check($"the money survived the quit ({wallet.Balance})", wallet.Balance == Money);
            Check($"and the chips ({wallet.Chips})", wallet.Chips == Chips);

            List<string> carried = Layout(bag);
            Check($"and two stacks are back in the bag ({string.Join(" | ", carried)})",
                  carried.Count == 2);

            Check($"on the island it was saved on ({GameSceneLoader.Current})",
                  GameSceneLoader.Current == RunSave.Run.island);

            PlaneAssembly plane = PlaneAssembly.Instance;
            Check("the aeroplane is still built", plane != null && plane.Complete);
            Check("and the group is still remembered as having built one", PlaneAssembly.Owned);

            VehicleUpgrades vehicle = FindObjectsByType<VehicleUpgrades>(FindObjectsSortMode.None)
                                      .Where(v => v != null && v.IsSpawned)
                                      .OrderBy(v => v.name)
                                      .FirstOrDefault();

            if (vehicle != null)
                Check($"and the upgrade is still bolted on (tier {vehicle.TierOf((VehiclePart)1)})",
                      vehicle.TierOf((VehiclePart)1) == Tier);

            Debug.Log($"[SaveTest] resumed from {RunSave.Path}: {wallet.Balance} money, "
                      + $"{wallet.Chips} chip(s), {carried.Count} stack(s) on {GameSceneLoader.Current}.");
        }

        // ---------------------------------------------------------------- bookkeeping

        /// <summary>The bag as "slot:item xN" lines, in slot order. Comparable between two moments.</summary>
        static List<string> Layout(Inventory bag)
        {
            var rows = new List<string>();

            for (int i = 0; i < bag.SlotCount; i++)
            {
                ItemStack stack = bag[i];
                if (stack.IsEmpty || stack.Def == null) continue;

                rows.Add($"{i}:{stack.Def.Id} x{stack.Count}");
            }

            return rows;
        }

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[SaveTest] FAILED: {what}.");
        }

        void Fail(string what) => Check(what, false);

        void Report()
        {
            Debug.Log($"[SaveTest] {_phase}: {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[SaveTest] {_failed} check(s) failed.");
        }
    }
}
