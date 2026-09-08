using System.Collections;
using System.Collections.Generic;
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
    /// The acceptance test for #51, run inside a real session. Server side, behind <c>-gunTest</c>.
    ///
    /// The criterion is "hits register correctly at 100 ms latency; no client-trusted damage", which
    /// is two separate claims, and the harness treats them as such.
    ///
    /// **At 100 ms latency** is a condition on the run, not an assertion about a number, so the test
    /// refuses to report a pass unless it is actually running under one - it reads the latency the
    /// bootstrap applied and fails if nobody asked for any. Everything after that check therefore
    /// happens with a hundred milliseconds each way between the two processes, including the checks
    /// that damage landed on a body the *other* process owns and predicts.
    ///
    /// **No client-trusted damage** cannot be proved by a test that only calls server methods, and
    /// pretending otherwise would be worse than not testing it. What is checkable, and checked, is
    /// the shape that makes it true: the client's only input to a shot is a direction, the direction
    /// is validated against the body's real facing, and every number that decides an outcome - damage,
    /// range, spread, pellet count, rate of fire, magazine size - is read from the server's copy of
    /// the definition inside <c>Weapon</c>. So the test asserts the guard rejects a shot aimed
    /// backwards, and that what landed equals what the server's asset says rather than anything a
    /// caller passed in.
    ///
    /// The ammunition economy gets the same treatment as the shop in #48: it is a conservation
    /// argument. Rounds do not appear. A magazine is filled out of the bag, one shot spends exactly
    /// one round however many pellets it throws, and reloading an empty bag does nothing at all.
    /// </summary>
    public class GunTest : MonoBehaviour
    {
        const float WaitForSecondPlayer = 60f;

        /// <summary>The latency the acceptance is written against.</summary>
        const int RequiredLatency = 100;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-gunTest")) return;

            _started = true;

            var go = new GameObject("GunTest");
            DontDestroyOnLoad(go);
            go.AddComponent<GunTest>();
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
                Debug.LogError("[GunTest] Needs two players; start a second process with "
                               + "-client -gunTest. Nothing was checked.");
                yield break;
            }

            Weapon attacker = weapons.FirstOrDefault(w => w.IsOwner) ?? weapons[0];
            Weapon victim = weapons.First(w => w != attacker);

            var bag = attacker.GetComponent<Inventory>();
            var health = victim.GetComponent<Health>();
            var stun = victim.GetComponent<StunState>();

            WeaponCatalog catalog = WeaponCatalog.Active;

            if (bag == null || health == null || catalog == null)
            {
                Debug.LogError("[GunTest] Missing an Inventory, a Health or the weapon catalog; run "
                               + "WeaponFactory.Build and PlayerPrefabBuilder.BuildPlayerPrefab.");
                yield break;
            }

            // ---------------------------------------------------------------- the condition

            int latency = CommandLine.GetInt("-latency", 0);

            Debug.Log($"[GunTest] running at {latency} ms of simulated latency, "
                      + $"victim is owned by connection {victim.Owner.ClientId}.");

            Check($"the run is under at least {RequiredLatency} ms of latency, which is the whole "
                  + "condition the acceptance is written against",
                  latency >= RequiredLatency);

            Check("the body being shot at belongs to the other process, so a hit has to survive the "
                  + "network rather than being resolved against ourselves",
                  !victim.IsOwner);

            // ---------------------------------------------------------------- the arsenal

            List<WeaponDef> guns = Enumerable.Range(1, catalog.Count)
                                             .Select(i => catalog.At((ushort)i))
                                             .Where(d => d != null && d.Kind == WeaponKind.Hitscan)
                                             .ToList();

            Debug.Log($"[GunTest] {guns.Count} gun(s): "
                      + string.Join(", ", guns.Select(g => g.Describe())));

            Check("there are at least four guns, which is what #51 asked for", guns.Count >= 4);
            Check("every gun is carried by an item, so it can be bought and dropped",
                  guns.All(g => g.Item != null));
            Check("every gun eats a specific kind of ammunition",
                  guns.All(g => g.Ammo != null));
            Check("every gun has a magazine, so reloading is a thing that happens",
                  guns.All(g => g.Magazine > 0));
            Check("every gun takes time to reload", guns.All(g => g.ReloadSeconds > 0f));
            Check("every gun kicks", guns.All(g => g.Recoil > 0f));

            // The four are meant to be four different answers, not four skins. If any of these
            // collapses, #51 shipped one gun with four names.
            Check("something in the arsenal throws more than one pellet",
                  guns.Any(g => g.Pellets > 1));
            Check("the rates of fire are genuinely different",
                  guns.Select(g => Mathf.RoundToInt(g.RoundsPerMinute)).Distinct().Count() == guns.Count);
            Check("the ranges are genuinely different",
                  guns.Select(g => Mathf.RoundToInt(g.Range)).Distinct().Count() == guns.Count);
            Check("the biggest magazine is at least five times the smallest",
                  guns.Max(g => g.Magazine) >= guns.Min(g => g.Magazine) * 5);

            // ---------------------------------------------------------------- the guard

            // The client's entire contribution to a shot. AimValidation is what stands between "I am
            // pointing at you" and "I say I am pointing at you"; see #17.
            Vector3 facing = Flat(attacker.transform.forward);

            Check("a shot aimed where the body is facing is accepted",
                  AimValidation.IsFacing(attacker.transform, facing, 100f));
            Check("a shot aimed at somebody standing behind the shooter is refused, which is the "
                  + "only thing a client gets to say about a shot",
                  !AimValidation.IsFacing(attacker.transform, -facing, 100f));

            // ---------------------------------------------------------------- every gun

            Vector3 lane = ClearBearing(attacker, 26f);

            Check("the attacker has an open lane to shoot down", lane != Vector3.zero);
            if (lane == Vector3.zero) lane = facing;

            int proven = 0;

            foreach (WeaponDef gun in guns)
            {
                // ------------------------------------------------ an empty gun

                bag.ServerClear();
                bag.Add(gun.Item, 1);
                bag.SelectSlot(0);

                yield return Settled();

                if (attacker.Equipped != gun)
                {
                    Debug.LogError($"[GunTest] holding {gun.Item.Id} equipped "
                                   + $"{(attacker.Equipped != null ? attacker.Equipped.Id : "nothing")}.");
                    continue;
                }

                attacker.ServerLoad(gun, 0);

                yield return Settled();

                Check($"a {gun.Id} straight out of the shop is empty - a magazine is something you buy",
                      attacker.Loaded == 0);

                // Stood up *before* being moved, and the order matters: DisableRagdoll repositions
                // the root under wherever the hips came to rest, so clearing a stun after a teleport
                // drags the body straight back to where it was lying. The previous gun left them in
                // a heap somewhere downrange, and the next gun would then be shooting at an empty
                // patch of arena and reporting itself broken.
                Reset(health, stun);

                yield return Settled();

                float reach = Mathf.Clamp(gun.Range * 0.25f, 3f, 20f);
                Stand(victim, attacker, reach, lane);

                yield return Settled();

                float before = health.Current;

                int landed = 0;
                for (int i = 0; i < 5; i++) landed += attacker.ServerAttackNow(Toward(attacker, victim));

                Check($"an empty {gun.Id} hits nobody however often the trigger is pulled",
                      landed == 0 && Mathf.Approximately(health.Current, before));

                // ------------------------------------------------ reloading with nothing to reload

                Check($"a {gun.Id} cannot be reloaded out of an empty bag", !attacker.ServerReload());

                yield return Settled();

                Check($"and a refused reload leaves the {gun.Id} empty", attacker.Loaded == 0);

                // ------------------------------------------------ reloading properly

                // Two magazines' worth, so a full reload has to take exactly one of them and leave
                // the rest. One would pass whether the code took what fits or took everything.
                int stocked = gun.Magazine * 2;
                bag.Add(gun.Ammo, stocked);

                yield return Settled();

                int inBagBefore = bag.CountOf(gun.Ammo);

                Check($"the bag is holding {stocked} round(s) for the {gun.Id}", inBagBefore == stocked);
                Check($"a {gun.Id} with ammunition in the bag starts reloading", attacker.ServerReload());
                Check($"and it is busy while it does", attacker.IsReloading);

                float reloadStarted = Time.time;

                while (attacker.IsReloading && Time.time - reloadStarted < gun.ReloadSeconds + 3f)
                    yield return null;

                yield return Settled();

                float took = Time.time - reloadStarted;

                Check($"a {gun.Id} reload takes about the {gun.ReloadSeconds:F1}s its asset says "
                      + $"(took {took:F2}s)",
                      took >= gun.ReloadSeconds && took < gun.ReloadSeconds + 1f);

                Check($"a reloaded {gun.Id} holds a full magazine of {gun.Magazine}",
                      attacker.Loaded == gun.Magazine);

                Check($"and the rounds came out of the bag rather than out of nowhere",
                      bag.CountOf(gun.Ammo) == inBagBefore - gun.Magazine);

                Check($"a full {gun.Id} refuses to reload again, so topping up cannot eat the bag",
                      !attacker.ServerReload());

                // ------------------------------------------------ one round per shot

                Reset(health, stun);

                int loadedBefore = attacker.Loaded;
                int hits = attacker.ServerAttackNow(Toward(attacker, victim));
                int spent = loadedBefore - attacker.Loaded;

                Check($"one pull of a {gun.Id} trigger spends exactly one round, whatever it throws "
                      + $"({gun.Pellets} pellet(s), {hits} landed)",
                      spent == 1);

                // ------------------------------------------------ what it does when it connects

                // Fired until something lands rather than once. Spread is not a bug and a single shot
                // is a dice roll: a shotgun at nine metres throws most of its pellets past a person,
                // and asserting on one trigger pull would make this test flaky by design.
                // Until damage actually lands, not until a ray connects. A rescue grants two seconds
                // of invulnerability (see Health.OnStartServer), so a shot can legitimately register a
                // hit for nothing at all, and stopping at the first ray would measure that instead of
                // the gun.
                float dealt = 0f;
                hits = 0;

                for (int shot = 0; shot < 25 && dealt <= 0f; shot++)
                {
                    attacker.ServerLoad(gun, gun.Magazine);

                    // Stood up and put back between shots. A landed shot ragdolls the victim and a
                    // ragdolled victim drifts, so without this the shot after the first hit would be
                    // aimed at where somebody used to be.
                    Reset(health, stun);
                    Stand(victim, attacker, reach, lane);

                    yield return new WaitForSeconds(0.15f);

                    before = health.Current;
                    hits = attacker.ServerAttackNow(Toward(attacker, victim));
                    dealt = before - health.Current;
                }

                if (dealt > 0f)
                {
                    Check($"a {gun.Id} deals the {gun.Hit.Damage:F0} per pellet its own asset says, "
                          + $"never a number the caller supplied ({dealt:F0} across {hits})",
                          Mathf.Abs(dealt - gun.Hit.Damage * hits) < 0.01f);

                    proven++;
                }
                else
                {
                    Debug.LogError($"[GunTest] {gun.Id} landed nothing in 25 shots at {reach:F1}m.");
                }

                // ------------------------------------------------ emptying it

                attacker.ServerLoad(gun, gun.Magazine);

                int fired = 0;
                while (attacker.Loaded > 0 && fired < gun.Magazine + 5)
                {
                    attacker.ServerAttackNow(Toward(attacker, victim));
                    fired++;
                    Reset(health, stun);
                }

                Check($"the victim survived a whole {gun.Id} magazine, because the harness keeps "
                      + "healing them and a corpse is not a target",
                      health.IsAlive);

                Check($"a {gun.Id} magazine of {gun.Magazine} lasts exactly {gun.Magazine} shots "
                      + $"(fired {fired})",
                      fired == gun.Magazine && attacker.Loaded == 0);

                Check($"and then the {gun.Id} is empty and hits nobody",
                      attacker.ServerAttackNow(Toward(attacker, victim)) == 0);

                Debug.Log($"[GunTest]   {gun.Describe()} -> reload {took:F2}s, "
                          + $"{gun.Magazine} shot(s), {bag.CountOf(gun.Ammo)} round(s) left in the bag");
            }

            Check("every gun in the catalog landed a hit at 100 ms of latency, for exactly the damage "
                  + "the server's own asset states",
                  proven == guns.Count);

            // ---------------------------------------------------------------- the shotgun's point

            // A pellet count is only real if several of them can land on one person at once, and the
            // dedupe that melee needs would silently make a shotgun a pistol. Fired close, where the
            // spread cone is narrower than a body.
            WeaponDef buckshot = guns.OrderByDescending(g => g.Pellets).First();

            if (buckshot.Pellets > 1)
            {
                bag.ServerClear();
                bag.Add(buckshot.Item, 1);
                bag.SelectSlot(0);

                yield return Settled();

                Reset(health, stun);

                yield return Settled();

                Stand(victim, attacker, 2.5f, lane);

                yield return Settled();

                int best = 0;
                for (int shot = 0; shot < 12 && best < 2; shot++)
                {
                    attacker.ServerLoad(buckshot, buckshot.Magazine);
                    Reset(health, stun);
                    Stand(victim, attacker, 2.5f, lane);

                    yield return new WaitForSeconds(0.15f);

                    best = Mathf.Max(best, attacker.ServerAttackNow(Toward(attacker, victim)));
                }

                Debug.Log($"[GunTest] {buckshot.Id} at 2.5m landed {best} pellet(s) in one shot.");

                Check($"a {buckshot.Id} lands more than one of its {buckshot.Pellets} pellets on one "
                      + "person at close range, which is the entire reason it exists",
                      best >= 2);
            }

            Report();
        }

        void Report()
        {
            string line = $"[GunTest] {_passed} passed, {_failed} failed.";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

        // ---------------------------------------------------------------- helpers

        static WaitForSeconds Settled() => new(0.3f);

        /// <summary>
        /// Alive, whole, and back on their feet.
        ///
        /// Reviving first is not politeness. <c>Health.Heal</c> refuses anything that is not
        /// <c>Alive</c>, so a victim who went down under the rifle stays down for the rest of the run
        /// - and a downed body stays ragdolled through <c>ServerClearStun</c>, which means the next
        /// teleport moves a root while the skeleton and its colliders stay where they fell. Every gun
        /// after that one then shoots at an empty patch of arena and reports itself broken.
        /// </summary>
        static void Reset(Health health, StunState stun)
        {
            if (health != null)
            {
                if (health.IsDead) health.ServerRevive(1f);
                else if (health.IsDowned) health.ServerRescue();
            }

            if (stun != null) stun.ServerClearStun();
            if (health != null) health.Heal(health.Max);
        }

        static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.001f ? v.normalized : Vector3.forward;
        }

        /// <summary>Flat on purpose: the aim origin is at eye height and a transform is at the feet.</summary>
        static Vector3 Toward(Weapon from, Weapon to)
        {
            Vector3 d = to.transform.position - from.transform.position;
            d.y = 0f;

            return d.sqrMagnitude > 0.001f ? d.normalized : Flat(from.transform.forward);
        }

        static void Stand(Weapon victim, Weapon attacker, float distance, Vector3 direction)
        {
            var motor = victim.GetComponent<PlayerMotor>();
            if (motor == null) return;

            motor.ServerTeleport(attacker.transform.position + direction * distance, 0f);
        }

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
            Debug.LogError($"[GunTest] FAILED: {what}.");
        }
    }
}
