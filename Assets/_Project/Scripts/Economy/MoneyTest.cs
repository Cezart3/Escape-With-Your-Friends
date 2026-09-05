using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.UI;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Economy
{
    /// <summary>
    /// The acceptance test for #47, run inside a real session. Server side, behind <c>-moneyTest</c>.
    ///
    /// The criterion is four words - "a client cannot mint money" - and the honest way to check it is
    /// not to try a list of exploits, because a list is only ever as good as the imagination that
    /// wrote it. It is to check **conservation**: the total across every wallet in the game, before
    /// and after every operation a client is allowed to ask for. Minting shows up as that number
    /// going up with nothing on the server having decided it should.
    ///
    /// So the test needs two real players, and it does the one thing a client may ask for - paying a
    /// friend - in every way it could go wrong: more than you have, a negative amount, zero, yourself,
    /// a wallet that is not there. Then it does the legitimate one and checks the total did not move.
    ///
    /// The ledger is the second half of the answer. <see cref="Wallet.Minted"/> counts every coin the
    /// server ever created and by what reason; a transfer touches neither counter, which is what makes
    /// "did anything create money" a question with a number for an answer rather than an opinion.
    /// </summary>
    public class MoneyTest : MonoBehaviour
    {
        const float WaitForSecondPlayer = 60f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-moneyTest")) return;

            _started = true;

            var go = new GameObject("MoneyTest");
            DontDestroyOnLoad(go);
            go.AddComponent<MoneyTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            Wallet[] wallets = System.Array.Empty<Wallet>();
            float deadline = Time.time + WaitForSecondPlayer;

            while (Time.time < deadline && wallets.Length < 2)
            {
                wallets = FindObjectsByType<Wallet>(FindObjectsSortMode.None)
                          .Where(w => w != null && w.IsSpawned)
                          .ToArray();

                if (wallets.Length < 2) yield return new WaitForSeconds(0.5f);
            }

            if (wallets.Length < 2)
            {
                Debug.LogError("[MoneyTest] Needs two players; start a second process with "
                               + "-client -moneyTest. Nothing was checked.");
                yield break;
            }

            Wallet mine = wallets.FirstOrDefault(w => w.IsOwner) ?? wallets[0];
            Wallet theirs = wallets.First(w => w != mine);

            mine.ServerSetBalance(1000);
            theirs.ServerSetBalance(200);

            Wallet.ResetLedger();

            int total = Wallet.TotalInWallets();

            Debug.Log($"[MoneyTest] two wallets, {Purse.Text(mine.Balance)} and "
                      + $"{Purse.Text(theirs.Balance)}, {Purse.Text(total)} in the world.");

            Check("the world starts with what the two wallets hold", total == 1200);
            Check("and the ledger starts clean", Wallet.Minted == 0 && Wallet.Burned == 0);

            // ---------------------------------------------------------------- the readout

            Check("the corner reads a thousand as $1,000", Purse.Text(1000) == "$1,000");
            Check("and nothing as $0", Purse.Text(0) == "$0");

            // ---------------------------------------------------------------- paying a friend

            int moved = mine.ServerTransfer(theirs, 300);

            Check("a transfer moves what was asked", moved == 300);
            Check("out of one wallet", mine.Balance == 700);
            Check("and into the other", theirs.Balance == 500);
            Check("and creates nothing", Wallet.TotalInWallets() == total);
            Check("and the ledger agrees nothing was created",
                  Wallet.Minted == 0 && Wallet.Burned == 0);

            // ---------------------------------------------------------------- every way to cheat

            Check("you cannot pay more than you have", mine.ServerTransfer(theirs, 5000) == 0);
            Check("nor a negative amount", mine.ServerTransfer(theirs, -500) == 0);
            Check("nor nothing at all", mine.ServerTransfer(theirs, 0) == 0);
            Check("nor yourself", mine.ServerTransfer(mine, 100) == 0);
            Check("nor a wallet that is not there", mine.ServerTransfer(null, 100) == 0);

            Check("none of that moved a coin",
                  mine.Balance == 700 && theirs.Balance == 500
                  && Wallet.TotalInWallets() == total);

            // The client-facing door, which is the only one a modified client has. It refuses the
            // same five things before the message is ever sent, and the server refuses them again.
            mine.RequestPay(theirs, -1000);
            mine.RequestPay(mine, 1000);
            mine.RequestPay(null, 1000);

            yield return new WaitForSeconds(0.3f);

            Check("and the client-facing door refuses them too",
                  Wallet.TotalInWallets() == total && mine.Balance == 700);

            mine.RequestPay(theirs, 200);

            yield return new WaitForSeconds(0.3f);

            Check("while a real request goes through",
                  mine.Balance == 500 && theirs.Balance == 700);
            Check("still creating nothing", Wallet.TotalInWallets() == total);

            // ---------------------------------------------------------------- spending and earning

            Check("spending what you have works", mine.ServerTrySpend(500, "test purchase"));
            Check("and takes it", mine.Balance == 0);
            Check("spending what you do not have fails", !mine.ServerTrySpend(1, "test purchase"));
            Check("and takes nothing", mine.Balance == 0);
            Check("a negative purchase is refused", !mine.ServerTrySpend(-100, "test purchase"));

            Check("the ledger counted the burn", Wallet.Burned == 500);

            mine.ServerAdd(250, "test sale");

            Check("earning works", mine.Balance == 250);
            Check("and the ledger counted the mint", Wallet.Minted == 250);
            Check("adding nothing does nothing", Balanced(mine, () => mine.ServerAdd(0, "nothing")));
            Check("adding a negative does nothing",
                  Balanced(mine, () => mine.ServerAdd(-999, "nothing")));

            int end = Wallet.TotalInWallets();

            Check("the world's money is the start, minus what was spent, plus what was earned",
                  end == total - Wallet.Burned + Wallet.Minted);

            string line = $"[MoneyTest] {_passed} passed, {_failed} failed. "
                          + $"end: {Purse.Text(mine.Balance)} and {Purse.Text(theirs.Balance)}, "
                          + $"{Purse.Text(end)} in the world, {Wallet.Minted} minted, "
                          + $"{Wallet.Burned} burned.";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

        static bool Balanced(Wallet wallet, System.Action act)
        {
            int before = wallet.Balance;
            act();
            return wallet.Balance == before;
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[MoneyTest] FAILED: {what}.");
        }
    }
}
