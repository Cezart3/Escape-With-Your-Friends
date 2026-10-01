using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// When every player is dead, nobody is left to haul a body to the Revive Machine, and the run
    /// had no way on: a solo player killed by a boar watched their own corpse forever. The
    /// playthrough bot found it. After a few seconds of everybody dead, everybody gets up at their
    /// spawn on half health. What they carried stays with them.
    ///
    /// Each of them pays their own Revive Machine price for it, or all they have if that is less.
    /// A free wipe made dying together cheaper than reviving one friend: three players killing
    /// themselves beat paying 650 for the fourth.
    ///
    /// <c>-wipeTest</c> kills the host's player and checks they are back up at the spawn, charged.
    /// </summary>
    public class WipeGuard : MonoBehaviour
    {
        const float Delay = 8f;
        static bool _started;
        float _allDeadSince = -1f;

        internal static void Begin()
        {
            if (_started) return;
            _started = true;

            var go = new GameObject("WipeGuard");
            DontDestroyOnLoad(go);
            var guard = go.AddComponent<WipeGuard>();
            if (CommandLine.HasFlag("-wipeTest")) guard.StartCoroutine(guard.Test());
        }

        void Update()
        {
            if (!InstanceFinder.IsServerStarted) { _allDeadSince = -1f; return; }

            Health[] players = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                .Select(m => m.GetComponent<Health>()).Where(h => h != null).ToArray();
            if (players.Length == 0 || players.Any(h => !h.IsDead)) { _allDeadSince = -1f; return; }

            if (_allDeadSince < 0f) _allDeadSince = Time.time;
            if (Time.time - _allDeadSince < Delay) return;
            _allDeadSince = -1f;

            var machine = FindAnyObjectByType<ReviveMachine>();
            foreach (Health health in players) Respawn(health, machine);
        }

        static void Respawn(Health health, ReviveMachine machine)
        {
            var wallet = health.GetComponent<Economy.Wallet>();
            if (machine != null && wallet != null)
                wallet.ServerTrySpend(Mathf.Min(machine.PriceFor(health), wallet.Balance), "wipe");

            // Same order as the Revive Machine: alive first, then the skeleton and the capsule home.
            var motor = health.GetComponent<PlayerMotor>();
            Vector3 position = Vector3.up * 2f;
            Quaternion rotation = health.transform.rotation;
            if (PlayerSpawner.Instance != null) PlayerSpawner.Instance.GetSpawn(health.OwnerId, out position, out rotation);

            health.GetComponent<Carryable>()?.ServerDetach();
            bool revived = health.ServerRevive();
            var ragdoll = health.GetComponent<RagdollController>();
            if (ragdoll != null && ragdoll.HipBone != null) ragdoll.TeleportSkeleton(position + Vector3.up * 1.1f);
            if (motor != null) motor.ServerTeleport(position, rotation.eulerAngles.y);

            Debug.Log($"[WipeGuard] everybody was dead; owner {health.OwnerId} back at {position}, revived {revived}.");
        }

        IEnumerator Test()
        {
            yield return new WaitForSeconds(15f);
            int passed = 0, failed = 0;
            void Check(string what, bool ok)
            {
                if (ok) passed++; else failed++;
                Debug.Log($"[WipeTest] {(ok ? "PASS" : "FAIL")} {what}");
            }

            PlayerMotor motor = FindAnyObjectByType<PlayerMotor>();
            Health health = motor != null ? motor.GetComponent<Health>() : null;
            Check("there is a player", health != null);
            var machine = FindAnyObjectByType<ReviveMachine>();
            var wallet = health != null ? health.GetComponent<Economy.Wallet>() : null;
            Check("there is a Revive Machine and a wallet", machine != null && wallet != null);
            if (health != null && machine != null && wallet != null)
            {
                motor.ServerTeleport(motor.transform.position + new Vector3(30f, 5f, 30f), 0f);
                yield return new WaitForSeconds(1f);
                health.ServerKill(DamageInfo.World(0f, DamageType.Environment));
                yield return new WaitForSeconds(1f);
                Check("they are dead", health.IsDead);
                yield return new WaitForSeconds(Delay - 3f);
                Check("and still dead just before the delay", health.IsDead);
                int before = wallet.Balance, price = machine.PriceFor(health);
                yield return new WaitForSeconds(4f);
                Check("then up again", health.IsAlive);
                Check($"charged their revive price ({before} - {price} = {wallet.Balance})",
                      wallet.Balance == Mathf.Max(0, before - price));

                Vector3 spawn = Vector3.zero;
                PlayerSpawner.Instance?.GetSpawn(health.OwnerId, out spawn, out _);
                yield return new WaitForSeconds(1f);
                float off = Vector3.Distance(motor.transform.position, spawn);
                Check($"at their spawn ({off:0.0}m off)", off < 5f);
            }

            Debug.Log($"[WipeTest] {passed} passed, {failed} failed.");
            Application.Quit();
        }
    }
}
