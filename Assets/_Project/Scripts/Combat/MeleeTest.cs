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
    /// The acceptance test for #50, run inside a real session. Server side, behind <c>-meleeTest</c>.
    ///
    /// The criterion is "melee hits feel chunky and still ragdoll people", which is two claims wearing
    /// one coat, and only one of them is testable by a machine.
    ///
    /// **Ragdolling people is measurable, so it is measured.** Not "was an impulse applied" - that
    /// passed for the whole of M1 while bodies stayed exactly where they were standing, because the
    /// entire blow went into whichever 2 kg forearm happened to be nearest the contact point and the
    /// other 54 kg were dragged along by joints. So this swings, waits for the physics to happen, and
    /// reads how far the hips actually travelled. A weapon that flails impressively and moves nobody
    /// fails here, which is the failure #50 exists to fix.
    ///
    /// It also checks the *ordering* rather than only the magnitudes: a heavier knockback number must
    /// produce a longer flight than a lighter one, across the whole melee half of the catalog. That is
    /// the property that makes the numbers in <c>WeaponFactory.Seeds</c> mean something to whoever
    /// tunes them next, and it is the one that silently dies if some component starts clamping.
    ///
    /// **Chunk is a feel judgement and belongs to a human**, so what is asserted here is only that the
    /// ingredients exist and are wired: every melee weapon has a wind-up and a stun, and a landed hit
    /// raises <c>Weapon.HitLanded</c> - the event the attacker's camera kick hangs off. Whether 0.18 s
    /// of wind-up reads as weight is decided in a playtest, not in a batch job.
    ///
    /// One honesty note about what is being measured: the hips are simulated locally on every peer,
    /// so this reads the server's own copy of the ragdoll. That is the copy the server would use for
    /// anything authoritative, and it is the one that decides where the body is when it stands up.
    /// </summary>
    public class MeleeTest : MonoBehaviour
    {
        const float WaitForSecondPlayer = 60f;

        /// <summary>How long the body is given to fly before the tape measure comes out.</summary>
        const float FlightTime = 1.0f;

        /// <summary>
        /// Metres a standing victim must cover. Deliberately low: this is a floor that catches "the
        /// impulse went nowhere", not a taste test. The real numbers are logged, and the weakest
        /// weapon in the game has to clear it, so raising it would only make the test about the knife.
        /// </summary>
        const float MinTravelStanding = 0.25f;

        /// <summary>A body already on the floor has friction to fight, so it is allowed to go less far.</summary>
        const float MinTravelDowned = 0.10f;

        static bool _started;

        int _passed;
        int _failed;
        int _hitEvents;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-meleeTest")) return;

            _started = true;

            var go = new GameObject("MeleeTest");
            DontDestroyOnLoad(go);
            go.AddComponent<MeleeTest>();
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
                Debug.LogError("[MeleeTest] Needs two players; start a second process with "
                               + "-client -meleeTest. Nothing was checked.");
                yield break;
            }

            Weapon attacker = weapons.FirstOrDefault(w => w.IsOwner) ?? weapons[0];
            Weapon victim = weapons.First(w => w != attacker);

            var bag = attacker.GetComponent<Inventory>();
            var health = victim.GetComponent<Health>();
            var stun = victim.GetComponent<StunState>();
            var ragdoll = victim.GetComponent<RagdollController>();

            WeaponCatalog catalog = WeaponCatalog.Active;

            if (bag == null || health == null || stun == null || ragdoll == null || catalog == null)
            {
                Debug.LogError("[MeleeTest] Missing an Inventory, Health, StunState, RagdollController "
                               + "or the weapon catalog. Run WeaponFactory.Build and "
                               + "PlayerPrefabBuilder.BuildPlayerPrefab.");
                yield break;
            }

            if (ragdoll.HipBone == null || ragdoll.HipBody == null)
            {
                Debug.LogError("[MeleeTest] The victim has no physics skeleton; nothing to measure.");
                yield break;
            }

            attacker.HitLanded += OnHitLanded;

            // ---------------------------------------------------------------- the data

            List<WeaponDef> melee = Enumerable.Range(1, catalog.Count)
                                              .Select(i => catalog.At((ushort)i))
                                              .Where(d => d != null && d.Kind == WeaponKind.Melee)
                                              .ToList();

            Debug.Log($"[MeleeTest] {melee.Count} melee weapon(s): "
                      + string.Join(", ", melee.Select(d => $"{d.Id} kb{d.Hit.Knockback:F0}")));

            Check("there is more than one melee weapon to compare", melee.Count > 1);
            Check("every melee weapon pushes with something", melee.All(d => d.Hit.Knockback > 0f));
            Check("every melee weapon knocks people down rather than only hurting them",
                  melee.All(d => d.Hit.StunDuration > 0f));

            // The two halves of chunk that are data rather than taste. A hit with no wind-up arrives
            // before the animation does and reads as a dropped frame, not as a blow.
            Check("every melee weapon has a wind-up, so the swing has somewhere to live",
                  melee.All(d => d.Windup > 0f));
            Check("every melee weapon arcs its victims upward rather than sliding them",
                  melee.All(d => d.Hit.UpwardBias > 0f));

            // The comedy invariant the whole game rests on: the funniest weapon and the strongest
            // weapon must be different objects, or picking the funny one costs nothing.
            WeaponDef hardestPush = melee.OrderByDescending(d => d.Hit.Knockback).First();
            WeaponDef hardestHit = melee.OrderByDescending(d => d.Hit.Damage).First();

            Check($"the biggest launcher ({hardestPush.Id}) is not also the biggest damage "
                  + $"({hardestHit.Id}), so choosing the funny one is a real trade",
                  hardestPush != hardestHit);

            // ---------------------------------------------------------------- the measurement

            var travelled = new Dictionary<WeaponDef, float>();

            foreach (WeaponDef def in melee)
            {
                if (def.Item == null) continue;   // fists, measured separately below

                yield return Equip(bag, attacker, def);

                if (attacker.Equipped != def)
                {
                    Debug.LogError($"[MeleeTest] holding {def.Item.Id} equipped "
                                   + $"{(attacker.Equipped != null ? attacker.Equipped.Id : "nothing")}.");
                    continue;
                }

                yield return Launch(attacker, victim, health, stun, ragdoll, def.Range * 0.5f);

                if (_travel < 0f)
                {
                    Debug.LogError($"[MeleeTest] {def.Id} never connected; nothing to measure.");
                    continue;
                }

                travelled[def] = _travel;

                Debug.Log($"[MeleeTest]   {def.Id,-8} kb {def.Hit.Knockback,5:F0} -> "
                          + $"{_travel:F2}m, ragdolled {_wasRagdolled}");

                Check($"a {def.Id} leaves its victim limp", _wasRagdolled);
                Check($"a {def.Id} actually moves somebody ({_travel:F2}m)", _travel >= MinTravelStanding);
            }

            Check("every carried melee weapon in the catalog was measured",
                  travelled.Count == melee.Count(d => d.Item != null));

            // Bare hands, which have no item and so are equipped by holding nothing at all.
            bag.ServerClear();
            bag.SelectSlot(0);
            yield return Settled();

            if (attacker.Equipped == catalog.Fists)
            {
                yield return Launch(attacker, victim, health, stun, ragdoll,
                                    catalog.Fists.Range * 0.5f);

                if (_travel >= 0f)
                {
                    travelled[catalog.Fists] = _travel;
                    Debug.Log($"[MeleeTest]   {"fists",-8} kb {catalog.Fists.Hit.Knockback,5:F0} -> "
                              + $"{_travel:F2}m, ragdolled {_wasRagdolled}");

                    Check($"a punch still launches people ({_travel:F2}m)",
                          _travel >= MinTravelStanding);
                }
            }

            // ---------------------------------------------------------------- the ordering

            // The property that makes the knockback column worth tuning. Physics is noisy - a body
            // that clips a rock goes nowhere through no fault of the weapon - so this asks for a
            // majority of concordant pairs rather than a perfect ranking, and reports the pairs that
            // disagreed so a bad number is findable rather than merely reported.
            if (travelled.Count > 1)
            {
                List<WeaponDef> measured = travelled.Keys.OrderBy(d => d.Hit.Knockback).ToList();

                int agreed = 0;
                int pairs = 0;

                for (int i = 0; i < measured.Count; i++)
                for (int j = i + 1; j < measured.Count; j++)
                {
                    WeaponDef weaker = measured[i];
                    WeaponDef stronger = measured[j];

                    if (Mathf.Approximately(weaker.Hit.Knockback, stronger.Hit.Knockback)) continue;

                    pairs++;

                    if (travelled[stronger] > travelled[weaker]) agreed++;
                    else
                        Debug.Log($"[MeleeTest]   out of order: {stronger.Id} "
                                  + $"(kb {stronger.Hit.Knockback:F0}) went {travelled[stronger]:F2}m, "
                                  + $"{weaker.Id} (kb {weaker.Hit.Knockback:F0}) went "
                                  + $"{travelled[weaker]:F2}m");
                }

                Check($"a bigger knockback number means a longer flight ({agreed}/{pairs} pairs)",
                      pairs > 0 && agreed * 100 >= pairs * 70);

                WeaponDef lightest = measured.First();
                WeaponDef heaviest = measured.Last();

                Check($"the {heaviest.Id} throws people meaningfully further than the {lightest.Id} "
                      + $"({travelled[heaviest]:F2}m vs {travelled[lightest]:F2}m)",
                      travelled[heaviest] > travelled[lightest] * 1.5f);
            }

            // ---------------------------------------------------------------- the body on the floor

            // Hitting somebody who is already down is most of what a bat is for. It must shove them
            // and it must not hurt them, because a downed player is waiting on a rescue, not on a
            // second death - see Health.TakeDamage, which refuses anything that is not Alive.
            WeaponDef launcher = travelled.Count > 0
                ? travelled.Keys.OrderByDescending(d => d.Hit.Knockback).First()
                : hardestPush;

            if (launcher.Item != null) yield return Equip(bag, attacker, launcher);

            Reset(health, stun);
            yield return Settled();

            Stand(victim, attacker, launcher.Range * 0.5f, ClearLane(attacker, 6f));
            yield return Settled();

            health.ServerDown(default);
            yield return new WaitForSeconds(0.5f);

            Check("a downed victim is on the floor before we hit them", health.IsDowned);

            float healthBefore = health.Current;
            Vector3 from = ragdoll.HipBone.position;

            int landed = 0;
            for (int swing = 0; swing < 12 && landed == 0; swing++)
            {
                landed = attacker.ServerAttackNow(Toward(attacker, victim));
                if (landed == 0) yield return new WaitForSeconds(0.25f);
            }

            yield return new WaitForSeconds(FlightTime);

            float shoved = Horizontal(ragdoll.HipBone.position - from);

            Debug.Log($"[MeleeTest]   a downed body took a {launcher.Id} and slid {shoved:F2}m");

            Check("hitting somebody already down still shoves them", landed > 0 && shoved >= MinTravelDowned);
            Check("hitting somebody already down does not hurt them further",
                  Mathf.Approximately(health.Current, healthBefore));
            Check("hitting somebody already down does not kill them", !health.IsDead);

            health.ServerRescue();
            yield return Settled();

            // ---------------------------------------------------------------- the feedback

            Check("a landed hit raises HitLanded, which is what the attacker's camera kick hangs off",
                  _hitEvents > 0);

            attacker.HitLanded -= OnHitLanded;

            Report();
        }

        void OnHitLanded(Vector3 contact) => _hitEvents++;

        void Report()
        {
            string line = $"[MeleeTest] {_passed} passed, {_failed} failed.";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

        // ---------------------------------------------------------------- the tape measure

        float _travel;
        bool _wasRagdolled;

        /// <summary>
        /// Stands the victim up, puts them a swing away, hits them until something connects, and
        /// measures how far the hips moved horizontally. Sets <see cref="_travel"/> to -1 when
        /// nothing ever landed, which is a different failure from "landed and went nowhere" and is
        /// reported differently.
        ///
        /// Horizontally on purpose: a body that is knocked upward comes back down, and counting the
        /// arc would flatter a weapon with a high upward bias for something the player never sees.
        /// Where they end up is what everyone laughs at.
        /// </summary>
        IEnumerator Launch(Weapon attacker, Weapon victim, Health health, StunState stun,
                           RagdollController ragdoll, float reach)
        {
            _travel = -1f;
            _wasRagdolled = false;

            Reset(health, stun);
            yield return Settled();

            Stand(victim, attacker, Mathf.Max(1f, reach), ClearLane(attacker, 6f));
            yield return Settled();

            Vector3 from = ragdoll.HipBone.position;
            Vector3 aim = Toward(attacker, victim);

            int landed = 0;
            for (int swing = 0; swing < 12 && landed == 0; swing++)
            {
                // ServerAttackNow goes straight to the resolver, so the cooldown is not in the way -
                // but spawn invulnerability is, and it is two seconds long. See Health.OnStartServer.
                landed = attacker.ServerAttackNow(aim);
                if (landed == 0) yield return new WaitForSeconds(0.25f);
            }

            if (landed == 0) yield break;

            _wasRagdolled = ragdoll.IsRagdolled;
            Vector3 launchVelocity = ragdoll.HipBody.linearVelocity;

            yield return new WaitForSeconds(FlightTime);

            _travel = Horizontal(ragdoll.HipBone.position - from);

            if (CommandLine.HasFlag("-meleeLog"))
                Debug.Log($"[MeleeTest]     from {from} to {ragdoll.HipBone.position}, "
                          + $"root {victim.transform.position}, launch v {launchVelocity.magnitude:F2} "
                          + $"{launchVelocity}, still limp {ragdoll.IsRagdolled}, "
                          + $"swings {landed}");
        }

        static IEnumerator Equip(Inventory bag, Weapon attacker, WeaponDef def)
        {
            // Emptied first so the weapon lands in slot 0: Inventory.ServerSelect wraps modulo the
            // five hotbar slots rather than clamping. See WeaponTest for the whole story.
            bag.ServerClear();
            bag.Add(def.Item, 1);
            bag.SelectSlot(0);

            yield return Settled();
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>A SyncVar written this frame is read next tick, not next frame. See #46.</summary>
        static WaitForSeconds Settled() => new(0.3f);

        static void Reset(Health health, StunState stun)
        {
            if (stun != null) stun.ServerClearStun();
            if (health != null) health.Heal(health.Max);
        }

        static float Horizontal(Vector3 delta)
        {
            delta.y = 0f;
            return delta.magnitude;
        }

        static Vector3 Toward(Weapon from, Weapon to)
        {
            Vector3 d = to.transform.position - from.transform.position;
            d.y = 0f;

            return d.sqrMagnitude > 0.001f ? d.normalized : Vector3.forward;
        }

        static void Stand(Weapon victim, Weapon attacker, float distance, Vector3 direction)
        {
            var motor = victim.GetComponent<PlayerMotor>();
            if (motor == null) return;

            motor.ServerTeleport(attacker.transform.position + direction * distance, 0f);
        }

        /// <summary>
        /// A bearing with enough clear ground down it for a body to fly along. Wider than the one in
        /// WeaponTest, because a launched victim needs somewhere to land: a bat that puts somebody
        /// into a wall two metres away measures as a bat that does nothing.
        /// </summary>
        static Vector3 ClearLane(Weapon attacker, float metres)
        {
            Vector3 eye = attacker.transform.position + Vector3.up * 1.55f;
            Vector3 best = Vector3.forward;
            float bestClearance = -1f;

            for (int i = 0; i < 12; i++)
            {
                Vector3 direction = Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward;

                float clearance = Physics.Raycast(eye, direction, out RaycastHit hit, metres, ~0,
                                                  QueryTriggerInteraction.Ignore)
                    ? hit.distance
                    : metres;

                if (clearance > bestClearance)
                {
                    bestClearance = clearance;
                    best = direction;
                }

                if (clearance >= metres) break;
            }

            return best;
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[MeleeTest] FAILED: {what}.");
        }
    }
}
