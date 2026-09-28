using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using FishNet;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// Playtest shortcuts, host only, development builds only. The economy is tuned for a boat in two
    /// or three evenings (EconomyTest holds it there), and a playtest of the ending cannot wait that
    /// long. F5: $1000 to everybody. F7: the aeroplane on this island is whole.
    ///
    /// <c>-cheatTest</c> runs both once and checks they did something.
    /// </summary>
    public class DevCheats : MonoBehaviour
    {
        const int Money = 1000;
        static bool _started;

        internal static void Begin()
        {
            if (_started || !(Debug.isDebugBuild || CommandLine.HasFlag("-cheats"))) return;
            _started = true;

            var go = new GameObject("DevCheats");
            DontDestroyOnLoad(go);
            var cheats = go.AddComponent<DevCheats>();
            if (CommandLine.HasFlag("-cheatTest")) cheats.StartCoroutine(cheats.Test());
        }

        void Update()
        {
            Keyboard keys = Keyboard.current;
            if (keys == null || !InstanceFinder.IsServerStarted) return;

            if (keys.f5Key.wasPressedThisFrame) GiveMoney();
            if (keys.f7Key.wasPressedThisFrame) FinishPlane();
        }

        internal static int GiveMoney()
        {
            Wallet[] wallets = FindObjectsByType<Wallet>(FindObjectsSortMode.None);
            foreach (Wallet wallet in wallets) wallet.ServerAdd(Money, "dev cheat");
            Debug.Log($"[DevCheats] ${Money} to {wallets.Length} wallet(s).");
            return wallets.Length;
        }

        internal static bool FinishPlane()
        {
            PlaneAssembly plane = FindAnyObjectByType<PlaneAssembly>();
            if (plane == null) return false;
            plane.ServerFitAll();
            Debug.Log("[DevCheats] the aeroplane is whole.");
            return true;
        }

        IEnumerator Test()
        {
            yield return new WaitForSeconds(15f);
            int passed = 0, failed = 0;
            void Check(string what, bool ok)
            {
                if (ok) passed++; else failed++;
                Debug.Log($"[CheatTest] {(ok ? "PASS" : "FAIL")} {what}");
            }

            Wallet wallet = FindAnyObjectByType<Wallet>();
            int before = wallet != null ? wallet.Balance : 0;
            Check("F5 finds a wallet", GiveMoney() > 0);
            Check($"and adds ${Money} to it", wallet != null && wallet.Balance == before + Money);

            Check("F7 finds the aeroplane", FinishPlane());
            yield return null;
            Check("and it is whole", PlaneAssembly.Owned);

            Debug.Log($"[CheatTest] {passed} passed, {failed} failed.");
            Application.Quit();
        }
    }
}
