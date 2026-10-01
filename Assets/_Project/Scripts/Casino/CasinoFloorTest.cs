using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.World;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>
    /// The acceptance test for the big casino (#254), behind <c>-casinoFloorTest</c> with
    /// <c>-scene island</c>, solo.
    ///
    /// It checks that every game is in the building and on the side of the glass it belongs to, and
    /// that each one can be walked up to: a clear line from in front of it to the middle of its room.
    /// It checks that the VIP door refuses a player one chip short of the minimum and lets them in at
    /// the minimum, that it always lets a player out, and that the minimum grows with the days.
    /// Last, it checks that <c>-casinoAll</c> (<see cref="CasinoDays.AllOpen"/>) opens every game and
    /// the door.
    /// </summary>
    public class CasinoFloorTest : MonoBehaviour
    {
        static bool _started;
        int _passed, _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-casinoFloorTest")) return;
            _started = true;

            var go = new GameObject("CasinoFloorTest");
            DontDestroyOnLoad(go);
            go.AddComponent<CasinoFloorTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            PlayerMotor motor = null;
            VipDoor door = null;
            RouletteWheel table = null;
            for (float deadline = Time.time + 60f; Time.time < deadline;)
            {
                motor = FindAnyObjectByType<PlayerMotor>();
                door = FindObjectsByType<VipDoor>(FindObjectsSortMode.None).FirstOrDefault(d => d.IsSpawned);
                table = FindObjectsByType<RouletteWheel>(FindObjectsSortMode.None).FirstOrDefault(t => t.IsSpawned);
                if (motor != null && door != null && table != null) break;
                yield return new WaitForSeconds(0.5f);
            }

            Check("a player, the VIP door and the roulette table", motor != null && door != null && table != null);
            if (motor == null || door == null || table == null) { Report(); yield break; }

            Landmark casino = FindObjectsByType<Landmark>(FindObjectsSortMode.None)
                .OrderBy(l => (l.transform.position - door.transform.position).sqrMagnitude).First();
            Transform room = casino.transform;

            // ------------------------------------------------------------ every game, in its room

            SlotMachine[] slots = FindObjectsByType<SlotMachine>(FindObjectsSortMode.None).Where(m => m.IsSpawned).ToArray();
            var games = new List<(string name, Transform at, bool vip)>
            {
                ("roulette", table.transform, false),
            };

            BlackjackTable blackjack = FindObjectsByType<BlackjackTable>(FindObjectsSortMode.None).FirstOrDefault(b => b.IsSpawned);
            Check("a blackjack table", blackjack != null);
            if (blackjack != null) games.Add(("blackjack", blackjack.transform, false));

            foreach (SlotKind kind in System.Enum.GetValues(typeof(SlotKind)))
            {
                SlotMachine machine = slots.FirstOrDefault(m => m.Kind == kind);
                Check($"a {SlotMath.Title(kind)} cabinet", machine != null);
                if (machine != null) games.Add((machine.Title, machine.transform, kind is SlotKind.Volcano or SlotKind.Reef or SlotKind.Lagoon));
            }

            int cages = FindObjectsByType<Cashier>(FindObjectsSortMode.None).Count(c => c.IsSpawned);
            Check($"both cage windows ({cages})", cages == 2);

            // The middle of each room, at chest height: just inside the front door, just behind the VIP one.
            Vector3 floorHub = room.TransformPoint(0f, 1.5f, 7f);
            Vector3 vipHub = room.TransformPoint(0f, 1.5f, -4f);

            foreach ((string name, Transform at, bool vip) in games)
            {
                Vector3 local = room.InverseTransformPoint(at.position);
                Check($"{name} is inside the building (local {local.x:0.0}, {local.z:0.0})",
                      Mathf.Abs(local.x) < 10f && local.z > -10f && local.z < 9f && Mathf.Abs(local.y) < 0.5f);
                Check($"{name} is {(vip ? "in the VIP room" : "on the floor")}", door.Inside(at.position) == vip);

                // Slots and the blackjack table face their players; the roulette table is walked round.
                Vector3 front = at.position + at.forward * 1.3f + Vector3.up * 1.5f;
                Vector3 hub = vip ? vipHub : floorHub;
                bool clear = !Physics.Linecast(front, hub, out RaycastHit hit, ~0, QueryTriggerInteraction.Ignore);
                Check($"and can be walked up to{(clear ? "" : $" (blocked by {hit.collider.name})")}", clear);
            }

            // ------------------------------------------------------------ the door

            var wallet = motor.GetComponent<Wallet>();
            var actor = motor.NetworkObject;
            Vector3 outside = door.transform.position + door.transform.forward * 1.4f + Vector3.up * 0.1f;

            CasinoDays.TestDay = 0;
            Check($"the VIP minimum on day 1 is {VipDoor.Minimum}", VipDoor.Minimum == VipDoor.BaseMinimum);
            CasinoDays.TestDay = 2;
            Check($"and grows with the days ({VipDoor.Minimum} on day 3)", VipDoor.Minimum == CasinoDays.Scaled(VipDoor.BaseMinimum)
                                                                          && VipDoor.Minimum > VipDoor.BaseMinimum);
            int minimum = VipDoor.Minimum;

            motor.ServerTeleport(outside, 0f);
            yield return new WaitForSeconds(0.3f);
            wallet.ServerSetChips(minimum - 1);
            Check($"one chip short, the door refuses ({wallet.Chips})", !door.ServerCanInteract(actor));
            Check($"and says what it wants (\"{door.Prompt}\")", door.Prompt.Contains("VIP: hold") && door.Prompt.Contains(minimum.ToString("N0")));
            door.ServerInteract(actor);
            yield return new WaitForSeconds(0.3f);
            Check("pressing it anyway leaves them on the floor", !door.Inside(motor.transform.position));

            wallet.ServerSetChips(minimum);
            Check("at the minimum it lets them in", door.ServerCanInteract(actor));
            door.ServerInteract(actor);
            yield return new WaitForSeconds(0.3f);
            Check($"and they are in the VIP room ({room.InverseTransformPoint(motor.transform.position).z:0.0})",
                  door.Inside(motor.transform.position));

            wallet.ServerSetChips(0);
            Check($"inside, broke, the door still lets them out (\"{door.Prompt}\")",
                  door.ServerCanInteract(actor) && door.Prompt == "Leave the VIP room");
            door.ServerInteract(actor);
            yield return new WaitForSeconds(0.3f);
            Check("and they are back on the floor", !door.Inside(motor.transform.position));

            // ------------------------------------------------------------ -casinoAll

            CasinoDays.TestDay = 0;
            CasinoDays.AllOpen = true;
            Check("-casinoAll: the door is free", VipDoor.Minimum == 0 && door.ServerCanInteract(actor));
            Check("and every game is open on day 1",
                  System.Enum.GetValues(typeof(CasinoGame)).Cast<CasinoGame>().All(CasinoDays.IsOpen));
            CasinoDays.AllOpen = false;
            CasinoDays.TestDay = -1;

            Report();
        }

        void Check(string what, bool ok)
        {
            if (ok) _passed++; else _failed++;
            Debug.Log($"[CasinoFloorTest] {(ok ? "PASS" : "FAIL")} {what}");
        }

        void Report()
        {
            Debug.Log($"[CasinoFloorTest] {_passed} passed, {_failed} failed.");
            Application.Quit();
        }
    }
}
