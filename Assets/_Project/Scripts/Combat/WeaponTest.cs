using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Player;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// The acceptance test for #49, run inside a real session. Server side, behind <c>-weaponTest</c>.
    ///
    /// The criterion is "a new weapon is one <c>.asset</c> file plus a prefab, no new code", and that
    /// is an awkward thing to assert, because the obvious test - add a weapon, see it work - is a test
    /// of the person writing it rather than of the code. So this checks the two properties that make
    /// the claim true, and would break the moment either stopped holding:
    ///
    /// 1. **Nothing in the runtime knows any weapon's name.** Every number used to resolve an attack
    ///    is read off the definition, so the test picks weapons out of the catalog by index, never by
    ///    id, and asserts the damage that landed matches what that asset says. A hard-coded ten
    ///    damage in <c>Weapon</c> would survive a "does punching work" test and die here.
    /// 2. **Equipping is a lookup, not a code path.** Nobody calls an equip function. Putting the
    ///    machete in the selected slot is the entire mechanism, so the test moves items between slots
    ///    and asserts the equipped weapon followed - including the fallback to fists when what you are
    ///    holding is a fish.
    ///
    /// It also proves the one branch that exists is real on both sides: a melee cone that catches
    /// several people at once and a hitscan ray that reaches somebody 30 m away, resolved through the
    /// same <c>ServerResolve</c>. And the anti-cheat that survives from #17: the server reads range,
    /// cone and damage off its own copy, so what a client asks for is a direction and nothing else.
    /// </summary>
    public class WeaponTest : MonoBehaviour
    {
        const float WaitForSecondPlayer = 60f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-weaponTest")) return;

            _started = true;

            var go = new GameObject("WeaponTest");
            DontDestroyOnLoad(go);
            go.AddComponent<WeaponTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            Weapon[] weapons = System.Array.Empty<Weapon>();
            float deadline = Time.time + WaitForSecondPlayer;

            while (Time.time < deadline && weapons.Length < 2)
            {
                weapons = FindObjectsByType<Weapon>(FindObjectsSortMode.None)
                          .Where(w => w != null && w.IsSpawned)
                          .ToArray();

                if (weapons.Length < 2) yield return new WaitForSeconds(0.5f);
            }

            if (weapons.Length < 2)
            {
                Debug.LogError("[WeaponTest] Needs two players; start a second process with "
                               + "-client -weaponTest. Nothing was checked.");
                yield break;
            }

            Weapon attacker = weapons.FirstOrDefault(w => w.IsOwner) ?? weapons[0];
            Weapon other = weapons.First(w => w != attacker);

            var bag = attacker.GetComponent<Inventory>();
            var victimHealth = other.GetComponent<Health>();
            var victimStun = other.GetComponent<StunState>();

            WeaponCatalog catalog = WeaponCatalog.Active;

            if (bag == null || victimHealth == null || catalog == null)
            {
                Debug.LogError("[WeaponTest] Missing an Inventory, a Health or the weapon catalog; "
                               + "run WeaponFactory.Build and PlayerPrefabBuilder.BuildPlayerPrefab.");
                yield break;
            }

            Debug.Log($"[WeaponTest] catalog holds {catalog.Count} weapon(s): "
                      + string.Join(", ", Enumerable.Range(1, catalog.Count)
                                                    .Select(i => catalog.At((ushort)i).Id)));

            // ---------------------------------------------------------------- the catalog

            Check("index 0 is nobody's weapon", catalog.At(0) == null);
            Check("index past the end is nobody's weapon", catalog.At((ushort)(catalog.Count + 1)) == null);
            Check("there are fists", catalog.Fists != null);
            Check("fists are carried by nobody", catalog.Fists != null && catalog.Fists.Item == null);

            bool sorted = true;
            for (int i = 2; i <= catalog.Count; i++)
                if (string.CompareOrdinal(catalog.At((ushort)(i - 1)).Id, catalog.At((ushort)i).Id) >= 0)
                    sorted = false;

            Check("the catalog is sorted by id, which is what makes the index a wire format", sorted);

            bool roundTrips = true;
            for (int i = 1; i <= catalog.Count; i++)
            {
                WeaponDef def = catalog.At((ushort)i);
                if (def == null || catalog.IndexOf(def) != i || catalog.Find(def.Id) != def)
                    roundTrips = false;
            }

            Check("every index round-trips through the definition and back", roundTrips);
            Check("every weapon in it is valid",
                  Enumerable.Range(1, catalog.Count).All(i => catalog.At((ushort)i).IsValid));

            // Both branches have to exist for the switch in ServerResolve to mean anything.
            WeaponDef anyMelee = First(catalog, WeaponKind.Melee, carried: true);
            WeaponDef anyGun = First(catalog, WeaponKind.Hitscan, carried: true);

            Check("something in the catalog is a carried melee weapon", anyMelee != null);
            Check("something in the catalog is a gun", anyGun != null);

            if (anyMelee == null || anyGun == null) { Report(); yield break; }

            Check("a gun's cooldown comes from its rate of fire",
                  Mathf.Abs(anyGun.Cooldown - 60f / anyGun.RoundsPerMinute) < 0.0001f);
            Check("a gun reaches further than a swing", anyGun.Range > anyMelee.Range * 5f);

            // ---------------------------------------------------------------- equipping

            bag.ServerClear();
            bag.SelectSlot(0);

            yield return Settled();

            Check("an empty hand is fists", attacker.Equipped == catalog.Fists);

            bag.Add(anyMelee.Item, 1);
            bag.SelectSlot(SlotOf(bag, anyMelee.Item));

            yield return Settled();

            Check($"holding a {anyMelee.Item.Id} equips the {anyMelee.Id}",
                  attacker.Equipped == anyMelee);

            bag.Add(anyGun.Item, 1);
            bag.SelectSlot(SlotOf(bag, anyGun.Item));

            yield return Settled();

            Check($"holding a {anyGun.Item.Id} equips the {anyGun.Id}", attacker.Equipped == anyGun);

            // The fallback. You can punch somebody while carrying groceries.
            ItemDef notAWeapon = bag.Catalog.Find("coconut");
            if (notAWeapon != null)
            {
                bag.Add(notAWeapon, 1);
                bag.SelectSlot(SlotOf(bag, notAWeapon));

                yield return Settled();

                Check("holding something that is not a weapon falls back to fists",
                      attacker.Equipped == catalog.Fists);
            }

            // ---------------------------------------------------------------- a swing

            Vector3 lane = ClearBearing(attacker, anyGun.Range + 30f);

            Check("the attacker has an open lane to shoot down", lane != Vector3.zero);
            if (lane == Vector3.zero) lane = Flat(attacker.transform.forward);

            Debug.Log($"[WeaponTest] clear lane {lane} from {attacker.transform.position}.");

            Stand(other, attacker, 1.2f, lane);
            bag.SelectSlot(SlotOf(bag, anyMelee.Item));

            yield return Settled();

            // Health ignores damage for `_spawnInvulnerability` seconds after a spawn - two of them,
            // so nobody is killed on the pad they arrived on. The first version of this test swung
            // inside that window and reported a working hatchet as one dealing zero damage. Swinging
            // until one lands is better than sleeping a magic number that goes stale when the grace
            // period is retuned.
            float giveUpAt = Time.time + 10f;

            while (Time.time < giveUpAt)
            {
                Reset(victimHealth, victimStun);

                if (attacker.ServerAttackNow(Toward(attacker, other)) > 0
                    && victimHealth.Current < victimHealth.Max) break;

                yield return new WaitForSeconds(0.25f);
            }

            Reset(victimHealth, victimStun);
            float before = victimHealth.Current;
            Vector3 at = Toward(attacker, other);

            int hits = attacker.ServerAttackNow(at);

            float dealtBySwing = before - victimHealth.Current;

            Debug.Log($"[WeaponTest] {anyMelee.Id} at 1.2m: {hits} hit(s), {dealtBySwing:F1} damage, "
                      + $"asset says {anyMelee.Hit.Damage:F1}. "
                      + $"equipped {(attacker.Equipped != null ? attacker.Equipped.Id : "nothing")}, "
                      + $"victim {victimHealth.Current:F0}/{victimHealth.Max:F0} "
                      + $"alive={victimHealth.IsAlive} downed={victimHealth.IsDowned} "
                      + $"dead={victimHealth.IsDead}, "
                      + $"apart {Apart(attacker, other):F2}m, before={before:F1}.");

            Check("a swing at somebody standing in front of you connects", hits >= 1);
            Check($"for exactly what the asset says ({anyMelee.Hit.Damage:F0}), not a number in the "
                  + $"code (dealt {dealtBySwing:F1} over {hits} hit(s))",
                  Mathf.Abs(dealtBySwing - anyMelee.Hit.Damage) < 0.01f);
            Check("and it stuns them if the asset says it should",
                  anyMelee.Hit.StunDuration <= 0f || victimStun == null || victimStun.IsStunned);

            // Behind you is not in front of you. The cone is the reach check, and it is server-side.
            Stand(other, attacker, 1.2f, lane, behind: true);

            yield return Settled();

            Reset(victimHealth, victimStun);
            before = victimHealth.Current;

            Check("swinging forwards misses somebody standing behind you",
                  attacker.ServerAttackNow(Toward(attacker, other) * -1f) == 0
                  || Mathf.Approximately(before, victimHealth.Current));

            // Out of reach is out of reach, however hard you swing.
            Stand(other, attacker, anyMelee.Range + 6f, lane);

            yield return Settled();

            Reset(victimHealth, victimStun);
            before = victimHealth.Current;
            attacker.ServerAttackNow(Toward(attacker, other));

            Check($"a {anyMelee.Range:F1}m weapon does not reach {anyMelee.Range + 6f:F1}m",
                  Mathf.Approximately(before, victimHealth.Current));

            // ---------------------------------------------------------------- a shot

            bag.SelectSlot(SlotOf(bag, anyGun.Item));
            Stand(other, attacker, 30f, lane);

            yield return Settled();

            Reset(victimHealth, victimStun);
            before = victimHealth.Current;

            // Fired until one lands rather than once. A 1.5-degree spread throws a single pellet about
            // 0.8 m sideways at thirty metres, which is wider than the person being shot at - so one
            // shot missing is the gun working, and asserting on one shot is asserting on a dice roll.
            // What is being tested here is that a ray reaches thirty metres at all, and it does.
            int shots = 0;

            for (hits = 0; shots < 20 && hits == 0; shots++)
                hits = attacker.ServerAttackNow(Toward(attacker, other));

            float dealtByShot = before - victimHealth.Current;

            Debug.Log($"[WeaponTest] {anyGun.Id} at 30m: {hits} hit(s) in {shots} shot(s), "
                      + $"{dealtByShot:F1} damage, apart {Apart(attacker, other):F2}m, "
                      + $"line hits {FirstThingOnTheLine(attacker, other)}.");

            Check("a gun reaches somebody thirty metres away", hits >= 1);
            Check($"for what its own asset says ({anyGun.Hit.Damage:F0} per pellet)",
                  Mathf.Abs(dealtByShot - anyGun.Hit.Damage * hits) < 0.01f);

            // Past its range it stops, which is the difference between a pistol and a rifle.
            Stand(other, attacker, anyGun.Range + 25f, lane);

            yield return Settled();

            Reset(victimHealth, victimStun);
            before = victimHealth.Current;

            for (int i = 0; i < 20; i++) attacker.ServerAttackNow(Toward(attacker, other));

            Check($"and twenty shots do not reach past {anyGun.Range:F0}m",
                  Mathf.Approximately(before, victimHealth.Current));

            // ---------------------------------------------------------------- nonsense

            Reset(victimHealth, victimStun);
            before = victimHealth.Current;

            Check("attacking in no direction at all does nothing",
                  attacker.ServerAttackNow(Vector3.zero) == 0);
            Check("and hurt nobody", Mathf.Approximately(before, victimHealth.Current));

            // ---------------------------------------------------------------- the whole point

            // Every carried weapon in the catalog, driven purely by putting it in the hand. If this
            // passes for five it passes for fifty, which is the acceptance in one loop.
            int drivenByData = 0;
            for (int i = 1; i <= catalog.Count; i++)
            {
                WeaponDef def = catalog.At((ushort)i);
                if (def == null || def.Item == null) continue;

                // Emptied first, so the weapon lands in slot 0. Inventory.ServerSelect *wraps* modulo
                // the five hotbar slots rather than clamping - a scroll wheel that sticks at the end
                // of the row feels broken - so a sixth item in the bag would quietly select the first.
                bag.ServerClear();
                bag.Add(def.Item, 1);
                bag.SelectSlot(0);

                yield return Settled();

                if (attacker.Equipped != def)
                {
                    Debug.LogError($"[WeaponTest] holding {def.Item.Id} equipped "
                                   + $"{(attacker.Equipped != null ? attacker.Equipped.Id : "nothing")}, "
                                   + $"not {def.Id}.");
                    continue;
                }

                float reach = def.Kind == WeaponKind.Hitscan ? 20f : def.Range * 0.5f;
                Stand(other, attacker, reach, lane);

                yield return Settled();

                Reset(victimHealth, victimStun);
                float start = victimHealth.Current;

                int landed = 0;
                for (int shot = 0; shot < 20 && landed == 0; shot++)
                    landed = attacker.ServerAttackNow(Toward(attacker, other));

                if (landed < 1) continue;

                float dealt = start - victimHealth.Current;
                if (Mathf.Abs(dealt - def.Hit.Damage) > 0.01f)
                {
                    Debug.LogError($"[WeaponTest] {def.Id} dealt {dealt}, its asset says "
                                   + $"{def.Hit.Damage}.");
                    continue;
                }

                Debug.Log($"[WeaponTest]   {def.Describe()} -> {dealt:F0} damage at {reach:F1}m");
                drivenByData++;
            }

            Check("every carried weapon in the catalog equips itself and deals its own damage, "
                  + "with nothing in the code naming any of them",
                  drivenByData == catalog.Weapons.Count(w => w != null && w.Item != null));

            Report();
        }

        void Report()
        {
            string line = $"[WeaponTest] {_passed} passed, {_failed} failed.";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>A SyncVar written this frame is read next tick, not next frame. See #46.</summary>
        static WaitForSeconds Settled() => new(0.3f);

        /// <summary>
        /// Back to full and back on their feet between attacks. The stun has to be cleared as well as
        /// the health: a ragdolled victim is driven by physics rather than by its controller, and the
        /// next <see cref="Stand"/> would be putting a corpse somewhere it does not stay.
        /// </summary>
        static void Reset(Health health, StunState stun)
        {
            if (stun != null) stun.ServerClearStun();
            if (health != null) health.Heal(health.Max);
        }

        static WeaponDef First(WeaponCatalog catalog, WeaponKind kind, bool carried)
        {
            for (int i = 1; i <= catalog.Count; i++)
            {
                WeaponDef def = catalog.At((ushort)i);
                if (def != null && def.Kind == kind && (!carried || def.Item != null)) return def;
            }

            return null;
        }

        static int SlotOf(Inventory bag, ItemDef def)
        {
            for (int i = 0; i < bag.SlotCount; i++)
                if (bag[i].Def == def) return i;

            return 0;
        }

        /// <summary>
        /// A flat aim from one body to the other. Flat on purpose: the aim origin is at eye height and
        /// a transform position is at the feet, so aiming straight at somebody's origin points the ray
        /// into the ground. That is invisible at a metre and fatal at thirty.
        /// </summary>
        static Vector3 Toward(Weapon from, Weapon to)
        {
            Vector3 d = to.transform.position - from.transform.position;
            d.y = 0f;

            return d.sqrMagnitude > 0.001f ? d.normalized : Flat(from.transform.forward);
        }

        static float Apart(Weapon a, Weapon b)
            => (a.transform.position - b.transform.position).magnitude;

        /// <summary>
        /// What a shot would actually meet first. A miss at thirty metres is almost always a wall or a
        /// tree rather than a bug in the weapon, and guessing which is a waste of a build.
        /// </summary>
        static string FirstThingOnTheLine(Weapon from, Weapon to)
        {
            Vector3 eye = from.transform.position + Vector3.up * 1.55f;
            Vector3 direction = Toward(from, to);

            if (!Physics.Raycast(eye, direction, out RaycastHit hit, 200f, ~0,
                                 QueryTriggerInteraction.Ignore))
                return "nothing at all";

            return $"{hit.collider.name} at {hit.distance:F1}m";
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.001f ? v.normalized : Vector3.forward;
        }

        /// <summary>
        /// Puts the victim a measured distance from the attacker along <paramref name="direction"/>.
        ///
        /// The direction is passed in rather than taken from the attacker's facing because a greybox
        /// arena has walls in it: the first run of this test placed a victim thirty metres away with a
        /// building seven metres along the line, and reported a working pistol as a broken one. What a
        /// weapon does when a wall is in the way is worth testing, but not by accident.
        /// </summary>
        static void Stand(Weapon victim, Weapon attacker, float distance, Vector3 direction,
                          bool behind = false)
        {
            var motor = victim.GetComponent<PlayerMotor>();
            if (motor == null) return;

            Vector3 spot = attacker.transform.position
                           + (behind ? -direction : direction) * distance;

            motor.ServerTeleport(spot, 0f);
        }

        /// <summary>
        /// A compass bearing from the attacker with nothing solid on it for <paramref name="distance"/>
        /// metres, or zero when the attacker is boxed in. Twelve bearings, thirty degrees apart.
        /// </summary>
        static Vector3 ClearBearing(Weapon attacker, float distance)
        {
            Vector3 eye = attacker.transform.position + Vector3.up * 1.55f;

            for (int i = 0; i < 12; i++)
            {
                Vector3 direction = Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward;

                if (!Physics.Raycast(eye, direction, distance + 2f, ~0,
                                     QueryTriggerInteraction.Ignore))
                    return direction;
            }

            return Vector3.zero;
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[WeaponTest] FAILED: {what}.");
        }
    }
}
