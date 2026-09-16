using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Player;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>
    /// The acceptance test for #63, behind <c>-chipsTest</c>. Wants <c>-scene island -noNatives
    /// -noAnimals</c>.
    ///
    /// The acceptance is a negative - "no path for real money to enter or leave" - and a negative
    /// cannot be proved by pressing buttons. What can be proved, and is what the issue actually
    /// rests on, is that **chips are conserved value and nothing else**: every chip in the world was
    /// made by destroying exactly one coin at a cage window, every coin taken out of chips was made
    /// by destroying exactly one chip at the other window, and the sum of the two never moves.
    ///
    /// So the suite watches three numbers across every exchange it can think of:
    ///
    /// 1. <c>TotalInWallets() + TotalChips()</c>, which no exchange may change.
    /// 2. <c>Minted</c> and <c>Burned</c>, which an exchange must not touch at all - converting is
    ///    not income, and a ledger that called it one would report the casino as printing money.
    /// 3. <c>Exchanged</c> against the chips actually in the world, which must agree: with everybody
    ///    starting on nothing, the chips that exist are exactly the money that went in.
    ///
    /// And one positive: the trader does not take chips. A player with a fat stack and an empty
    /// pocket cannot buy a rope, which is what "chips only exist inside the casino" means when it is
    /// a line of code rather than a sentence.
    /// </summary>
    public class ChipsTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const float WaitForCage = 60f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-chipsTest")) return;

            _started = true;

            var go = new GameObject("ChipsTest");
            DontDestroyOnLoad(go);
            go.AddComponent<ChipsTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

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
                Debug.LogError("[ChipsTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            Cashier buy = null;
            Cashier cash = null;
            float cageDeadline = Time.time + WaitForCage;

            while (Time.time < cageDeadline && (buy == null || cash == null))
            {
                Cashier[] windows = FindObjectsByType<Cashier>(FindObjectsSortMode.None)
                                    .Where(c => c != null && c.IsSpawned).ToArray();

                buy = windows.FirstOrDefault(c => c.Direction == CageDirection.Buy);
                cash = windows.FirstOrDefault(c => c.Direction == CageDirection.CashOut);

                if (buy == null || cash == null) yield return new WaitForSeconds(0.5f);
            }

            if (buy == null || cash == null)
            {
                Debug.LogError("[ChipsTest] The casino cage is not in the world. Run CasinoFactory."
                               + "Build and bake the POIs with -rebuildPois.");
                yield break;
            }

            var wallet = player.GetComponent<Wallet>();
            var bag = player.GetComponent<Inventory>();

            if (wallet == null)
            {
                Debug.LogError("[ChipsTest] The player has no Wallet.");
                yield break;
            }

            Wiring(buy, cash, wallet);

            yield return Buying(buy, wallet, player.NetworkObject);
            yield return Cashing(cash, wallet, player.NetworkObject);
            yield return Remainder(cash, wallet, player.NetworkObject);
            yield return Refusals(buy, cash, wallet, player.NetworkObject);
            yield return Conservation(buy, cash, wallet, player.NetworkObject);
            yield return NotMoney(wallet, bag, player);

            Report();
        }

        // ---------------------------------------------------------------- what is in the world

        void Wiring(Cashier buy, Cashier cash, Wallet wallet)
        {
            Check("the casino has a window that sells chips", buy.Direction == CageDirection.Buy);
            Check("and one that buys them back", cash.Direction == CageDirection.CashOut);

            Check($"they trade in stacks ({buy.Chunk} a press)", buy.Chunk > 0);

            Check("and they are two objects, so the crosshair can tell them apart",
                  buy.NetworkObject != cash.NetworkObject);

            float apart = Vector3.Distance(buy.transform.position, cash.transform.position);
            Check($"standing apart at the cage ({apart:0.0}m)", apart > 1f && apart < 12f);

            Check($"a player starts with money and no chips ({wallet.Describe()})",
                  wallet.Chips == 0);

            Debug.Log($"[ChipsTest] cage open: {buy.Chunk} a press, one for one. "
                      + $"Player has {wallet.Describe()}.");
        }

        // ---------------------------------------------------------------- money in

        IEnumerator Buying(Cashier buy, Wallet wallet, NetworkObject actor)
        {
            wallet.ServerSetBalance(500);
            wallet.ServerSetChips(0);
            Wallet.ResetLedger();

            yield return new WaitForSeconds(0.3f);

            int money = wallet.Balance;
            int worth = Worth();

            Check("the window will serve a player with money", buy.ServerCanInteract(actor));

            buy.ServerInteract(actor);

            yield return new WaitForSeconds(0.3f);

            Check($"one press buys a stack ({wallet.Chips} chips)", wallet.Chips == buy.Chunk);
            Check($"paid for out of the pocket ({money} -> {wallet.Balance})",
                  wallet.Balance == money - buy.Chunk);

            Check($"so nothing was created or destroyed ({worth} -> {Worth()})", Worth() == worth);

            Check($"and the exchange is not income ({Wallet.Minted} minted, {Wallet.Burned} burned)",
                  Wallet.Minted == 0 && Wallet.Burned == 0);

            Check($"every chip in the world is money that went in "
                  + $"({Wallet.TotalChips()} chips, {Wallet.Exchanged} exchanged)",
                  Wallet.TotalChips() == Wallet.Exchanged);

            Debug.Log($"[ChipsTest] bought a stack: {wallet.Describe()}, "
                      + $"{worth} of value before and {Worth()} after.");
        }

        // ---------------------------------------------------------------- and back out

        IEnumerator Cashing(Cashier cash, Wallet wallet, NetworkObject actor)
        {
            int money = wallet.Balance;
            int chips = wallet.Chips;
            int worth = Worth();

            Check("the other window will serve a player holding chips", cash.ServerCanInteract(actor));

            cash.ServerInteract(actor);

            yield return new WaitForSeconds(0.3f);

            Check($"one press cashes a stack in ({chips} -> {wallet.Chips} chips)",
                  wallet.Chips == chips - Mathf.Min(cash.Chunk, chips));

            Check($"and the money comes back ({money} -> {wallet.Balance})",
                  wallet.Balance == money + Mathf.Min(cash.Chunk, chips));

            Check($"with nothing created or destroyed ({worth} -> {Worth()})", Worth() == worth);

            Check($"and the ledger still clean ({Wallet.Minted} minted, {Wallet.Burned} burned)",
                  Wallet.Minted == 0 && Wallet.Burned == 0);

            Debug.Log($"[ChipsTest] cashed a stack in: {wallet.Describe()}.");
        }

        // ---------------------------------------------------------------- the last few

        /// <summary>
        /// The cage must never eat a remainder. A player who lost most of a stack has to be able to
        /// get the rest of it back, or chips are a one-way door with extra steps.
        /// </summary>
        IEnumerator Remainder(Cashier cash, Wallet wallet, NetworkObject actor)
        {
            wallet.ServerSetChips(40);

            yield return new WaitForSeconds(0.3f);

            int money = wallet.Balance;

            cash.ServerInteract(actor);

            yield return new WaitForSeconds(0.3f);

            Check($"an odd stack comes back whole (40 chips -> {wallet.Balance - money} money)",
                  wallet.Chips == 0 && wallet.Balance == money + 40);

            Debug.Log($"[ChipsTest] cashed in an odd 40: {wallet.Describe()}.");
        }

        // ---------------------------------------------------------------- what it will not do

        IEnumerator Refusals(Cashier buy, Cashier cash, Wallet wallet, NetworkObject actor)
        {
            wallet.ServerSetBalance(0);
            wallet.ServerSetChips(0);

            yield return new WaitForSeconds(0.3f);

            Check("a broke player is not offered chips", !buy.ServerCanInteract(actor));
            Check("and the crosshair says nothing", string.IsNullOrEmpty(buy.Prompt));

            buy.ServerInteract(actor);

            yield return new WaitForSeconds(0.3f);

            Check($"pressing it anyway mints nothing ({wallet.Describe()})",
                  wallet.Chips == 0 && wallet.Balance == 0);

            Check("a player with no chips is not offered cash", !cash.ServerCanInteract(actor));

            cash.ServerInteract(actor);

            yield return new WaitForSeconds(0.3f);

            Check($"and that press mints nothing either ({wallet.Describe()})",
                  wallet.Chips == 0 && wallet.Balance == 0);

            Check($"the ledger agrees nothing happened ({Wallet.Minted} minted, {Wallet.Burned} burned)",
                  Wallet.Minted == 0 && Wallet.Burned == 0);
        }

        // ---------------------------------------------------------------- the sum never moves

        IEnumerator Conservation(Cashier buy, Cashier cash, Wallet wallet, NetworkObject actor)
        {
            wallet.ServerSetBalance(450);
            wallet.ServerSetChips(0);
            Wallet.ResetLedger();

            yield return new WaitForSeconds(0.3f);

            int worth = Worth();
            bool held = true;

            // Four in, four out, alternating once in the middle so the two doors are interleaved
            // rather than run as two tidy blocks.
            for (int i = 0; i < 4; i++)
            {
                buy.ServerInteract(actor);
                if (Worth() != worth) held = false;

                if (i == 2)
                {
                    cash.ServerInteract(actor);
                    if (Worth() != worth) held = false;
                }

                yield return null;
            }

            while (wallet.Chips > 0)
            {
                cash.ServerInteract(actor);
                if (Worth() != worth) held = false;

                yield return null;
            }

            Check($"value survives a run on the cage ({worth} throughout)", held);

            Check($"and everything is money again at the end ({wallet.Describe()})",
                  wallet.Chips == 0 && wallet.Balance == worth);

            Check($"with nothing minted or burned by any of it "
                  + $"({Wallet.Minted} minted, {Wallet.Burned} burned)",
                  Wallet.Minted == 0 && Wallet.Burned == 0);

            Check($"and the exchange counter back to zero ({Wallet.Exchanged})",
                  Wallet.Exchanged == 0);

            Debug.Log($"[ChipsTest] nine presses at the cage: {worth} of value in, "
                      + $"{Worth()} out, {Wallet.Minted} minted, {Wallet.Burned} burned.");
        }

        // ---------------------------------------------------------------- chips are not money

        /// <summary>
        /// The trader takes coins. A stack of chips at the counter is worth exactly nothing, which
        /// is what keeps "chips only exist inside the casino" true in code rather than in a comment.
        /// </summary>
        IEnumerator NotMoney(Wallet wallet, Inventory bag, PlayerMotor player)
        {
            ShopCounter counter = FindObjectsByType<ShopCounter>(FindObjectsSortMode.None)
                                  .FirstOrDefault(c => c != null && c.IsSpawned);

            if (counter == null || bag == null)
            {
                Debug.Log("[ChipsTest] no trader in this world; skipped the counter check.");
                yield break;
            }

            // Stand at the counter first. The first version of this check ran from wherever the
            // player happened to be and the trader refused with "you are not at the counter" - a
            // pass for the wrong reason, which is worse than a failure because it is quiet. The
            // refusal has to be about the money and nothing else, so the walk has to be out of the
            // way before the question is asked.
            player.ServerTeleport(counter.transform.position + Vector3.forward * 2f, 0f);

            wallet.ServerSetBalance(0);
            wallet.ServerSetChips(5000);
            bag.ServerClear();

            yield return new WaitForSeconds(0.3f);

            Check($"the player is standing at the counter ("
                  + $"{Vector3.Distance(counter.transform.position, player.transform.position):0.0}m)",
                  counter.InReach(player.transform.position));

            int bought = counter.ServerBuy(bag, wallet, offer: 0, count: 1, out string why);

            Check($"the trader will not take chips ({why})", bought == 0);
            Check("and refuses over the money, not the distance", !why.Contains("counter"));
            Check($"the stack is untouched ({wallet.Describe()})", wallet.Chips == 5000);

            Debug.Log($"[ChipsTest] 5000 chips at the trader's counter bought {bought}: {why}.");

            // The control: same spot, same stack, one coin's worth of actual money. If this does not
            // sell, the refusal above proved nothing about chips.
            wallet.ServerSetBalance(1000);

            yield return new WaitForSeconds(0.2f);

            int paid = counter.ServerBuy(bag, wallet, offer: 0, count: 1, out string reason);

            Check($"but sells the same thing for money ({paid} bought, {reason})", paid > 0);

            Debug.Log($"[ChipsTest] the same counter, the same stack, {1000 - wallet.Balance} of "
                      + $"money spent: {paid} bought.");

            wallet.ServerSetChips(0);
            bag.ServerClear();
        }

        // ---------------------------------------------------------------- scaffolding

        /// <summary>Everything anybody owns, in either form. The number an exchange must not move.</summary>
        static int Worth() => Wallet.TotalInWallets() + Wallet.TotalChips();

        void Report()
        {
            if (_failed == 0) Debug.Log($"[ChipsTest] {_passed} passed, 0 failed.");
            else Debug.LogError($"[ChipsTest] {_passed} passed, {_failed} FAILED.");
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[ChipsTest] FAILED: {what}.");
        }
    }
}
