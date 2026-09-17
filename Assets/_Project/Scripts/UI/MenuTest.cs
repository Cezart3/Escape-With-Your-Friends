using System.Collections;
using EscapeWithYourFriends.Core;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.UI
{
    /// <summary>
    /// What a terminal can check about the main menu (#82), behind <c>-menuTest</c>. Run it with no
    /// <c>-host</c> and no <c>-client</c>: the point is the state a shipped build starts in, which is
    /// a player looking at a menu with no session running.
    ///
    /// Pixels and "is it obvious" belong to the #29 playtest. What is checkable here is the wiring,
    /// and the wiring is the part that silently rots: that the menu builds itself when nothing else
    /// started a session, that Host actually starts one, that the menu gets out of the way once it
    /// has, and that leaving brings it back. <see cref="MenuScreen"/> is allowed to build its canvas
    /// under this flag even headless, which is the only reason any of it can be asked.
    /// </summary>
    public class MenuTest : MonoBehaviour
    {
        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-menuTest")) return;
            _started = true;

            var go = new GameObject("MenuTest");
            DontDestroyOnLoad(go);
            go.AddComponent<MenuTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            yield return null;
            yield return null;

            MenuScreen menu = MenuScreen.Instance;

            Check("a build with no session on the command line opens on the menu", menu != null);

            if (menu == null)
            {
                Report();
                yield break;
            }

            Check("and the menu is on screen", menu.Shown);
            Check($"which says what it can do without Steam ({menu.Status})",
                  menu.Status.Contains("Steam is not running"));

            Check("with nothing connected behind it",
                  InstanceFinder.NetworkManager != null
                  && !InstanceFinder.NetworkManager.IsServerStarted);

            menu.HostClicked();

            yield return Until(() => InstanceFinder.NetworkManager.IsServerStarted
                                     && InstanceFinder.NetworkManager.IsClientStarted, 30f);

            Check("Host starts the server", InstanceFinder.NetworkManager.IsServerStarted);
            Check("and the local client with it", InstanceFinder.NetworkManager.IsClientStarted);

            yield return Until(() => !menu.Shown, 5f);
            Check("and the menu gets out of the way", !menu.Shown);

            menu.LeaveClicked();

            yield return Until(() => !InstanceFinder.NetworkManager.IsServerStarted
                                     && !InstanceFinder.NetworkManager.IsClientStarted, 15f);

            Check("leaving stops the session", !InstanceFinder.NetworkManager.IsServerStarted);

            yield return Until(() => menu.Shown, 5f);
            Check("and the menu is what is left", menu.Shown);

            Report();
        }

        static IEnumerator Until(System.Func<bool> done, float seconds)
        {
            float deadline = Time.time + seconds;
            while (Time.time < deadline && !done()) yield return null;
        }

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[MenuTest] FAILED: {what}.");
        }

        void Report()
        {
            Debug.Log($"[MenuTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[MenuTest] {_failed} check(s) failed.");
        }
    }
}
