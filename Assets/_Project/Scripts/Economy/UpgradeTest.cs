using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.World;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Economy
{
    /// <summary>
    /// The acceptance test for #52, run inside a real session. Server side, behind <c>-upgradeTest</c>.
    ///
    /// The criterion is "a visible power curve from island 1 gear to island 2 gear", and *visible* is
    /// the word that decides what this file does. A curve nobody can see is a table of numbers, so the
    /// curve here is measured the same way #50 measured knockback: **equip every weapon in every line
    /// in turn, hit somebody with it, and read what came off their health.** A tier-two weapon that is
    /// only better in the inspector fails here.
    ///
    /// Three properties are checked, and between them they cover every way an upgrade system goes
    /// wrong:
    ///
    /// 1. **The shelf does not lie.** Every advertised delta is recomputed from the two weapons it
    ///    names. <c>UpgradeFactory</c> derives them, so this can only fail if somebody hand-edited an
    ///    asset - which is precisely when a player would be sold "+30% damage" and handed +3%.
    /// 2. **Every step is a step up.** A tier higher, and worse at nothing: not damage, not
    ///    knockback, not reach, not rate of fire, not magazine size, and recoil no higher. That
    ///    predicate being true of all six steps *is* the power curve.
    /// 3. **Nothing is created and nothing vanishes.** An upgrade takes one weapon, exactly the
    ///    materials it listed and exactly the money it asked for, and gives back one weapon. Every
    ///    refusal leaves the bag and the wallet exactly as they were - which is the half that matters,
    ///    because a refusal that half-charges you is the bug players actually notice.
    ///
    /// And one thing that needs no runtime check, only saying out loud: **the request carries an index
    /// into the catalog and nothing else.** Look at <see cref="Upgrading.RequestUpgrade"/> - there is
    /// nowhere to put a price, a material list, or a target weapon. A modified client can ask for the
    /// wrong upgrade, and it is refused for not holding the weapon; it cannot ask for a cheap one.
    /// </summary>
    public class UpgradeTest : MonoBehaviour
    {
        const float WaitForSecondPlayer = 60f;

        /// <summary>Where a gun is fired from. Close enough that spread and scenery cannot decide it.</summary>
        const float GunRange = 8f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-upgradeTest")) return;

            _started = true;

            var go = new GameObject("UpgradeTest");
            DontDestroyOnLoad(go);
            go.AddComponent<UpgradeTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            Weapon[] players = System.Array.Empty<Weapon>();
            float deadline = Time.time + WaitForSecondPlayer;

            while (Time.time < deadline && players.Length < 2)
            {
                players = FindObjectsByType<Weapon>(FindObjectsSortMode.None)
                          .Where(w => w != null && w.IsSpawned)
                          .ToArray();

                if (players.Length < 2) yield return new WaitForSeconds(0.5f);
            }

            if (players.Length < 2)
            {
                Debug.LogError("[UpgradeTest] Needs two players; start a second process with "
                               + "-client -upgradeTest. Nothing was checked.");
                yield break;
            }

            Weapon attacker = players.FirstOrDefault(w => w.IsOwner) ?? players[0];
            Weapon victim = players.First(w => w != attacker);

            var bag = attacker.GetComponent<Inventory>();
            var wallet = attacker.GetComponent<Wallet>();
            var upgrading = attacker.GetComponent<Upgrading>();
            var victimHealth = victim.GetComponent<Health>();
            var victimStun = victim.GetComponent<StunState>();

            if (bag == null || wallet == null || upgrading == null || victimHealth == null)
            {
                Debug.LogError("[UpgradeTest] A player is missing Inventory, Wallet, Upgrading or "
                               + "Health; run PlayerPrefabBuilder.BuildPlayerPrefab.");
                yield break;
            }

            UpgradeCatalog catalog = upgrading.Catalog;
            WeaponCatalog weapons = WeaponCatalog.Active;
            ItemCatalog items = bag.Catalog;

            if (catalog == null || weapons == null || items == null)
            {
                Debug.LogError("[UpgradeTest] No upgrade, weapon or item catalog. Run "
                               + "ItemFactory.Build, WeaponFactory.Build and UpgradeFactory.Build.");
                yield break;
            }

            Debug.Log($"[UpgradeTest] {catalog.Count} upgrade(s) over {weapons.Count} weapon(s).");
            foreach (UpgradeDef def in catalog.Upgrades)
                Debug.Log($"[UpgradeTest]   {def.Describe()}");

            // ---------------------------------------------------------------- the data

            yield return Data(catalog, weapons);

            // ---------------------------------------------------------------- the venues

            CraftingStation bench = CraftingStation.Nearest(CraftStation.Bench, attacker.transform.position);
            ShopCounter counter = FindObjectsByType<ShopCounter>(FindObjectsSortMode.None)
                                  .FirstOrDefault(c => c != null && c.IsSpawned);

            if (bench == null || counter == null)
            {
                Debug.LogError("[UpgradeTest] This scene has "
                               + $"{(bench == null ? "no bench" : "a bench")} and "
                               + $"{(counter == null ? "no counter" : "a counter")}; both halves of "
                               + "the economy need one each. Run with -scene island.");
                Report();
                yield break;
            }

            Debug.Log($"[UpgradeTest] bench at {bench.transform.position.ToString("F0")}, "
                      + $"counter at {counter.transform.position.ToString("F0")}, "
                      + $"{(bench.transform.position - counter.transform.position).magnitude:F0}m apart.");

            yield return Transactions(catalog, items, bag, wallet, upgrading, attacker, bench, counter);

            // ---------------------------------------------------------------- the curve

            yield return Curve(catalog, bag, attacker, victim, victimHealth, victimStun);

            Report();
        }

        // ---------------------------------------------------------------- part one: the data

        IEnumerator Data(UpgradeCatalog catalog, WeaponCatalog weapons)
        {
            Check("there are upgrades at all", catalog.Count > 0);

            int lies = 0;
            int sidegrades = 0;
            int unlinked = 0;

            foreach (UpgradeDef def in catalog.Upgrades)
            {
                if (def == null) continue;

                if (!def.Verify(out string mismatch))
                {
                    Debug.LogError($"[UpgradeTest] {def.Id} advertises what it does not deliver: "
                                   + $"{mismatch}.");
                    lies++;
                }

                if (!def.IsStrictlyBetter)
                {
                    Debug.LogError($"[UpgradeTest] {def.Id} is not an upgrade: "
                                   + $"t{def.From.Tier} -> t{def.To.Tier}, {def.Advertisement()}.");
                    sidegrades++;
                }

                if (def.From.UpgradesTo != def.To)
                {
                    Debug.LogError($"[UpgradeTest] {def.From.Id} says it becomes "
                                   + $"{(def.From.UpgradesTo != null ? def.From.UpgradesTo.Id : "nothing")}, "
                                   + $"the upgrade says {def.To.Id}.");
                    unlinked++;
                }
            }

            Check("every upgrade advertises exactly what it delivers", lies == 0);
            Check("every upgrade is a tier higher and worse at nothing", sidegrades == 0);
            Check("every weapon's UpgradesTo agrees with the upgrade that performs it", unlinked == 0);

            // The other direction: a weapon claiming to become something no upgrade can reach is a
            // link that lies to the UI rather than to the transaction, and is invisible until a
            // player asks for the upgrade the tooltip promised.
            int orphans = weapons.Weapons.Count(w => w != null && w.UpgradesTo != null
                                                     && catalog.For(w) == null);
            Check("no weapon claims to become something no upgrade delivers", orphans == 0);

            var froms = new HashSet<WeaponDef>();
            int forks = catalog.Upgrades.Count(u => u != null && u.From != null && !froms.Add(u.From));
            Check("no weapon has two upgrades out of it - lines, not trees", forks == 0);

            // Fists have no bag item, so neither end of an upgrade can be them. Worth asserting
            // rather than assuming: the swap is a bag operation, and a weapon nobody carries would
            // be removed from a bag it was never in.
            Check("bare hands cannot be upgraded",
                  weapons.Fists != null && catalog.For(weapons.Fists) == null);

            // ---------------------------------------------------------------- the lines

            var targets = new HashSet<WeaponDef>(catalog.Upgrades.Where(u => u != null).Select(u => u.To));
            WeaponDef[] starts = catalog.Upgrades.Where(u => u != null && !targets.Contains(u.From))
                                        .Select(u => u.From)
                                        .Distinct()
                                        .ToArray();

            Check("there is more than one line to climb", starts.Length >= 2);

            int ends = 0;
            int threeLong = 0;

            foreach (WeaponDef start in starts)
            {
                List<WeaponDef> line = catalog.LineFrom(start);

                if (catalog.For(line[line.Count - 1]) == null) ends++;
                if (line.Count >= 3) threeLong++;

                Debug.Log($"[UpgradeTest]   line: "
                          + string.Join(" -> ", line.Select(w => $"{w.Id} t{w.Tier}")));

                // Island one to island two: the far end of a line has to be worth walking to. Damage
                // per second rather than damage, because a weapon that hits harder and half as often
                // is the sidegrade this whole issue is trying not to ship.
                WeaponDef first = line[0];
                WeaponDef last = line[line.Count - 1];

                float from = first.Hit.Damage / first.Cooldown;
                float to = last.Hit.Damage / last.Cooldown;

                Check($"{last.Id} is at least twice the weapon {first.Id} is "
                      + $"({from:F0} -> {to:F0} damage per second)", to >= from * 2f);
            }

            Check("every line ends somewhere rather than looping", ends == starts.Length);
            Check("every line runs island one to island two, three weapons deep",
                  threeLong == starts.Length);

            yield break;
        }

        // ---------------------------------------------------------------- part two: the transaction

        IEnumerator Transactions(UpgradeCatalog catalog, ItemCatalog items, Inventory bag,
                                 Wallet wallet, Upgrading upgrading, Weapon weapon,
                                 CraftingStation bench, ShopCounter counter)
        {
            UpgradeDef atBench = catalog.Upgrades.FirstOrDefault(u => u != null
                                                                     && u.Venue == UpgradeVenue.Bench);
            // Whichever trader upgrade has a magazine, if there is one. The money half of this
            // section works with any of them, but only a gun can show that the rounds you already
            // paid for survive the swap - and that is the half nobody would notice was broken.
            UpgradeDef atTrader = catalog.Upgrades.FirstOrDefault(u => u != null
                                                                      && u.Venue == UpgradeVenue.Trader
                                                                      && u.From.Magazine > 0
                                                                      && u.From.Ammo == u.To.Ammo)
                                  ?? catalog.Upgrades.FirstOrDefault(u => u != null
                                                                         && u.Venue == UpgradeVenue.Trader);

            Check("somebody at a bench can upgrade something", atBench != null);
            Check("somebody at a counter can upgrade something", atTrader != null);

            if (atBench == null || atTrader == null) yield break;

            var motor = weapon.GetComponent<PlayerMotor>();
            if (motor == null) yield break;

            Vector3 atTheBench = bench.transform.position + bench.transform.forward * 2f;
            Vector3 atTheCounter = counter.transform.position + counter.transform.forward * 2f;
            Vector3 nowhere = bench.transform.position + Vector3.right * 500f;

            // ------------------------------------------------------------ refused, out in the field

            motor.ServerTeleport(nowhere, 0f);
            yield return Settled();

            bag.ServerClear();
            Stock(bag, atBench, items, spare: 2);
            wallet.ServerSetBalance(1000);

            int before = wallet.Balance;
            int scrapBefore = bag.CountOf(items.Find("scrap_metal"));

            Check("an upgrade is refused out in the field",
                  upgrading.ServerUpgrade(atBench, out string why) == null);
            Check($"and it says why ({why})", why != null && why.Contains("workbench"));
            Check("and nothing was taken for it",
                  bag.Has(atBench.From.Item) && wallet.Balance == before
                  && bag.CountOf(items.Find("scrap_metal")) == scrapBefore);

            // ------------------------------------------------------------ refused, at the bench

            motor.ServerTeleport(atTheBench, 0f);
            yield return Settled();

            Check("standing at the bench is a venue",
                  Upgrading.InVenue(UpgradeVenue.Bench, weapon.transform.position));

            bag.ServerClear();
            bag.Add(atBench.From.Item, 1);

            Check("an upgrade with no materials is refused",
                  upgrading.ServerUpgrade(atBench, out why) == null);
            Check("and the weapon is still in the bag", bag.Has(atBench.From.Item));

            bag.ServerClear();
            Materials(bag, atBench);

            Check("an upgrade of a weapon you are not carrying is refused",
                  upgrading.ServerUpgrade(atBench, out why) == null);
            Check($"and it names the weapon ({why})",
                  why != null && why.Contains(atBench.From.Item.Id));

            Check("a trader's upgrade is refused at a bench",
                  upgrading.ServerUpgrade(atTrader, out why) == null);

            // ------------------------------------------------------------ done, at the bench

            bag.ServerClear();
            Stock(bag, atBench, items, spare: 2);
            bag.SelectSlot(0);

            yield return Settled();

            before = wallet.Balance;
            scrapBefore = bag.CountOf(items.Find("scrap_metal"));
            int scrapWanted = Wanted(atBench, items.Find("scrap_metal"));

            WeaponDef got = upgrading.ServerUpgrade(atBench, out why);

            Check($"the bench upgrade goes through ({why ?? "no complaint"})", got == atBench.To);
            Check("the old weapon is gone", !bag.Has(atBench.From.Item));
            Check("the new weapon is in the bag", bag.Has(atBench.To.Item));
            Check($"exactly the materials it listed were taken ({scrapWanted}x scrap_metal)",
                  bag.CountOf(items.Find("scrap_metal")) == scrapBefore - scrapWanted);
            Check("a bench upgrade costs no money", wallet.Balance == before);

            yield return Settled();

            Check($"and the upgraded weapon is what you are now holding "
                  + $"({(weapon.Equipped != null ? weapon.Equipped.Id : "nothing")})",
                  weapon.Equipped == atBench.To);

            // The second step of the same line, from what the first step handed back. This is the
            // one that proves a line is a line rather than a single swap: the output of one upgrade
            // is the input of the next, with nothing put back in the bag by the test in between.
            UpgradeDef second = catalog.For(atBench.To);

            if (second != null)
            {
                Materials(bag, second);
                wallet.ServerSetBalance(2000);

                WeaponDef end = upgrading.ServerUpgrade(second, out why);

                Check($"the next step of the same line goes through ({why ?? "no complaint"})",
                      end == second.To);
                Check("and the end of the line has nowhere further to go",
                      catalog.For(second.To) == null && second.To.UpgradesTo == null);
                Check("and asking anyway is refused",
                      upgrading.ServerUpgrade(atBench, out why) == null);
            }

            // ------------------------------------------------------------ the counter

            motor.ServerTeleport(atTheCounter, 0f);
            yield return Settled();

            Check("standing at the counter is a venue",
                  Upgrading.InVenue(UpgradeVenue.Trader, weapon.transform.position));
            Check("a bench upgrade is refused at a counter",
                  upgrading.ServerUpgrade(atBench, out why) == null);

            bag.ServerClear();
            Stock(bag, atTrader, items, spare: 0);
            bag.SelectSlot(0);
            wallet.ServerSetBalance(atTrader.Price - 1);

            yield return Settled();

            Check("an upgrade you cannot afford is refused",
                  upgrading.ServerUpgrade(atTrader, out why) == null);
            Check($"and it says what it costs ({why})",
                  why != null && why.Contains(atTrader.Price.ToString()));
            Check("and the money is still there", wallet.Balance == atTrader.Price - 1);
            Check("and so are the materials and the weapon",
                  bag.Has(atTrader.From.Item) && Enough(bag, atTrader));

            // A magazine that was paid for goes with the weapon. Loaded here rather than reloaded,
            // because what is under test is the swap, not #51's ammunition economy.
            const int Loaded = 7;
            if (atTrader.From.Magazine > 0) weapon.ServerLoad(atTrader.From, Loaded);

            wallet.ServerSetBalance(atTrader.Price + 40);
            int burnedBefore = Wallet.Burned;

            got = upgrading.ServerUpgrade(atTrader, out why);

            Check($"the trader's upgrade goes through ({why ?? "no complaint"})", got == atTrader.To);
            Check($"it charged exactly {atTrader.Price}", wallet.Balance == 40);
            Check("and the ledger recorded it as spent rather than vanished",
                  Wallet.Burned == burnedBefore + atTrader.Price);
            Check("the old weapon is gone and the new one is here",
                  !bag.Has(atTrader.From.Item) && bag.Has(atTrader.To.Item));

            yield return Settled();

            if (atTrader.From.Magazine > 0 && atTrader.From.Ammo == atTrader.To.Ammo)
                Check($"the {Loaded} rounds already in the magazine came with it "
                      + $"({weapon.Loaded} loaded)",
                      weapon.Equipped == atTrader.To && weapon.Loaded == Loaded);
        }

        // ---------------------------------------------------------------- part three: the curve

        IEnumerator Curve(UpgradeCatalog catalog, Inventory bag, Weapon attacker, Weapon victim,
                          Health victimHealth, StunState victimStun)
        {
            var targets = new HashSet<WeaponDef>(catalog.Upgrades.Where(u => u != null).Select(u => u.To));
            WeaponDef[] starts = catalog.Upgrades.Where(u => u != null && !targets.Contains(u.From))
                                        .Select(u => u.From)
                                        .Distinct()
                                        .ToArray();

            Vector3 lane = ClearLane(attacker, GunRange + 4f);
            int rising = 0;
            int measured = 0;

            foreach (WeaponDef start in starts)
            {
                List<WeaponDef> line = catalog.LineFrom(start);
                var dealt = new List<float>();

                foreach (WeaponDef def in line)
                {
                    float damage = 0f;

                    yield return Swing(bag, attacker, victim, victimHealth, victimStun, def, lane,
                                       result => damage = result);

                    dealt.Add(damage);
                    measured++;

                    Debug.Log($"[UpgradeTest]   {def.Id} t{def.Tier}: {damage:F0} damage a hit, "
                              + $"{1f / def.Cooldown:F1} a second, "
                              + $"{damage / def.Cooldown:F0} damage per second");
                }

                bool climbs = true;
                for (int i = 1; i < dealt.Count; i++)
                    if (dealt[i] <= dealt[i - 1]) climbs = false;

                if (climbs) rising++;

                Check($"the {start.Id} line hits harder every step "
                      + $"({string.Join(" -> ", dealt.Select(d => d.ToString("F0")))})",
                      climbs && dealt.All(d => d > 0f));
            }

            Check("every weapon in every line landed a hit worth measuring",
                  measured == starts.Sum(s => catalog.LineFrom(s).Count));
            Check("every line is a curve rather than a plateau", rising == starts.Length);
        }

        /// <summary>
        /// Equips one weapon, stands the victim up in front of it, and hits them once. The damage
        /// that actually came off is handed back.
        ///
        /// The retry loop is the same one #51 needed and for the same two reasons: a rescue grants
        /// two seconds of invulnerability, and since #50 a landed hit moves the victim far enough
        /// that the next swing misses. So the victim is healed and put back on every attempt, and the
        /// loop stops when health changed rather than when a ray connected.
        /// </summary>
        IEnumerator Swing(Inventory bag, Weapon attacker, Weapon victim, Health health, StunState stun,
                          WeaponDef def, Vector3 lane, System.Action<float> report)
        {
            if (def.Item == null)
            {
                report(0f);
                yield break;
            }

            bag.ServerClear();
            bag.Add(def.Item, 1);
            bag.SelectSlot(0);

            yield return Settled();

            if (attacker.Equipped != def)
            {
                Debug.LogError($"[UpgradeTest] Holding {def.Item.Id} equipped "
                               + $"{(attacker.Equipped != null ? attacker.Equipped.Id : "nothing")}.");
                report(0f);
                yield break;
            }

            // Every gun fires from the same spot. Damage per hit does not fall off with distance
            // here, so the distance is not a variable worth having - and the first run of this test
            // failed only because the longest-ranged pistol was stood 20m out, where the island had
            // something in the way. What is being compared is the weapon, not the terrain.
            float reach = def.Kind == WeaponKind.Hitscan ? GunRange : def.Range * 0.5f;

            float damage = 0f;

            for (int attempt = 0; attempt < 20 && damage <= 0f; attempt++)
            {
                if (def.Magazine > 0) attacker.ServerLoad(def, def.Magazine);

                Reset(health, stun);
                Stand(victim, attacker, reach, lane);

                yield return new WaitForSeconds(0.15f);

                float start = health.Current;
                int landed = attacker.ServerAttackNow(Toward(attacker, victim));
                if (landed > 0) damage = start - health.Current;
            }

            report(damage);
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Everything an upgrade needs, plus spare scrap so exact consumption is visible.</summary>
        static void Stock(Inventory bag, UpgradeDef def, ItemCatalog items, int spare)
        {
            bag.Add(def.From.Item, 1);
            Materials(bag, def);

            if (spare > 0) bag.Add(items.Find("scrap_metal"), spare);
        }

        static void Materials(Inventory bag, UpgradeDef def)
        {
            foreach (Ingredient material in def.Materials)
                bag.Add(material.Item, material.Count);
        }

        static bool Enough(Inventory bag, UpgradeDef def)
        {
            foreach (Ingredient material in def.Materials)
                if (!bag.Has(material.Item, material.Count)) return false;

            return true;
        }

        static int Wanted(UpgradeDef def, ItemDef item)
        {
            int count = 0;
            foreach (Ingredient material in def.Materials)
                if (material.Item == item) count += material.Count;

            return count;
        }

        /// <summary>A SyncVar written this frame is read next tick, not next frame. See #46.</summary>
        static WaitForSeconds Settled() => new(0.3f);

        /// <summary>
        /// Alive, healed and unstunned. Revived first, because <c>Health.Heal</c> refuses anything
        /// that is not alive and a chainsaw gets through a hundred health in two swings.
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

        /// <summary>A bearing with clear ground down it, so a launched victim has somewhere to land.</summary>
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

        void Report()
        {
            string line = $"[UpgradeTest] {_passed} passed, {_failed} failed.";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[UpgradeTest] FAILED: {what}.");
        }
    }
}
