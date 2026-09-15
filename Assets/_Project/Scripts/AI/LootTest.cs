using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.World;
using FishNet;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.AI
{
    /// <summary>
    /// The acceptance test for #109, run inside a real session. Server side, behind <c>-lootTest</c>,
    /// and it wants <c>-scene island -noNatives -noAnimals</c>: the camps it reads are baked off the
    /// island's POIs, and a live village wandering into a controlled kill is a variable nobody asked for.
    /// The camp list survives <c>-noNatives</c> - only the topping up is switched off.
    ///
    /// The issue is one sentence - *"raiding the village to rescue a friend pays for the ammunition
    /// it cost"* - and that sentence is an arithmetic claim, not a feeling. So the harness makes it
    /// one: it takes the village's population exactly as <c>NativeFactory</c> baked it, works out how
    /// many rounds of each kind it costs to put that many bodies down with each gun in the catalog,
    /// and compares that against how many rounds those same bodies are expected to drop. Every number
    /// printed below comes off the assets; none of them is written down here.
    ///
    /// **Four things are checked, and only the last one needs the game running.**
    ///
    /// 1. **Two tables, and the village one contains the wild one.** A village body drops what it was
    ///    carrying *plus* the camp's stores. If the two tables ever became disjoint, the wandering
    ///    native would be a separate economy rather than the poor end of one.
    /// 2. **The raid is worth the walk.** The village table is worth meaningfully more than the wild
    ///    one for every role, measured at full item value.
    /// 3. **The ammunition pays for itself.** For each kind of ammunition on the island, at least one
    ///    gun that fires it comes out ahead on a village sweep, with a third of the shots missing
    ///    (<see cref="EconomyModel.MissAllowance"/>). The SMG is expected to fail this and is printed
    ///    rather than asserted: eight hundred rounds a minute is not a gun a raid can pay for.
    /// 4. **A body rolls the table its camp says it should.** Two natives are spawned and killed - one
    ///    marked as manning a stocked camp, one not - and the drops are checked against the right
    ///    table. This is the only part that can regress from a code change rather than a data one, and
    ///    it is run several times per role because the interesting lines are the ones with odds.
    ///
    /// **What is deliberately not checked here.** What the trader pays for a village sweep, and how
    /// that compares to fishing and hunting, is <c>-economyTest</c>'s job and is measured there
    /// against every other way of earning. Duplicating it would give two places to update and one of
    /// them would rot.
    /// </summary>
    public class LootTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const float WaitForNavMesh = 90f;

        /// <summary>Midnight, as <see cref="WorldClock.Normalized"/> reads it. Nothing here cares.</summary>
        const float Midnight = 0f;

        /// <summary>Bodies killed per role per table. Enough that a 35% line shows up at least once.</summary>
        const int Kills = 6;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-lootTest")) return;

            _started = true;

            var go = new GameObject("LootTest");
            DontDestroyOnLoad(go);
            go.AddComponent<LootTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            PlayerMotor motor = null;
            float deadline = Time.time + WaitForPlayer;

            while (Time.time < deadline && motor == null)
            {
                motor = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                        .FirstOrDefault(m => m != null && m.IsSpawned);

                if (motor == null) yield return new WaitForSeconds(0.5f);
            }

            if (motor == null)
            {
                Debug.LogError("[LootTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            var health = motor.GetComponent<Health>();
            var stun = motor.GetComponent<StunState>();
            var bag = motor.GetComponent<Inventory>();

            NativeCatalog natives = NativeCatalog.Active;
            ItemCatalog items = bag != null ? bag.Catalog : ItemCatalog.Active;
            WeaponCatalog guns = WeaponCatalog.Active;

            if (health == null || natives == null || items == null)
            {
                Debug.LogError("[LootTest] No player Health, native catalog or item catalog. Run "
                               + "ItemFactory.Build and NativeFactory.Build, then regenerate the island.");
                yield break;
            }

            Debug.Log($"[LootTest] {natives.Count} role(s), {items.Count} items, "
                      + $"{(guns != null ? guns.Count : 0)} weapon(s).");

            Tables(natives, items);

            NativeSpawner spawner = NativeSpawner.Instance;
            Check("the island scene carries a NativeSpawner", spawner != null);

            if (spawner == null)
            {
                Report();
                yield break;
            }

            Camps(spawner);
            Ammunition(spawner, guns);

            // Everything above is arithmetic on assets. Everything below needs somewhere to stand.
            float navDeadline = Time.time + WaitForNavMesh;
            while (Time.time < navDeadline
                   && !NavMesh.SamplePosition(motor.transform.position, out _, 25f, NavMesh.AllAreas))
                yield return new WaitForSeconds(0.5f);

            WorldClock.Freeze(Midnight);
            yield return null;

            NativeDef[] roles = natives.Natives.Where(d => d != null).ToArray();

            foreach (NativeDef def in roles)
                yield return Rolled(spawner, def, motor, health, stun, stocked: false);

            foreach (NativeDef def in roles)
                yield return Rolled(spawner, def, motor, health, stun, stocked: true);

            WorldClock.Freeze(-1f);

            Report();
        }

        // ---------------------------------------------------------------- the two tables

        /// <summary>
        /// Both tables for every role, printed side by side. The village table is a superset by
        /// construction in the factory, which is exactly why it is worth asserting here: the
        /// construction is one line in an editor script and nothing else would notice it breaking.
        /// </summary>
        void Tables(NativeCatalog natives, ItemCatalog items)
        {
            Debug.Log("[LootTest] role         wild                                         "
                      + "value   village adds");

            foreach (NativeDef def in natives.Natives)
            {
                if (def == null) continue;

                LootDrop[] wild = def.Loot;
                LootDrop[] village = def.VillageLoot;

                string extra = string.Join(", ", Stores(def).Select(Line));

                Debug.Log($"[LootTest]   {def.Id,-11}{string.Join(", ", wild.Select(Line)),-45}"
                          + $"{def.ExpectedLootValue,6:0.0}c  {extra}");

                Check($"{def.Id} has a wild table", wild.Length > 0);
                Check($"{def.Id} has a village table", village.Length > 0);

                // A superset, line for line, and matched on what the line *says* rather than on the
                // reference: the two arrays are serialized separately, so the same line in both comes
                // back as two objects after a domain reload and reference equality reads as disjoint.
                Check($"{def.Id}'s village table keeps everything the wild one drops",
                      wild.All(l => village.Any(v => Same(v, l))));

                Check($"{def.Id}'s village table adds something ({village.Length - wild.Length} line(s))",
                      village.Length > wild.Length);

                Check($"{def.Id} is worth more out of the village "
                      + $"({def.ExpectedVillageLootValue:0.0}c against {def.ExpectedLootValue:0.0}c)",
                      def.ExpectedVillageLootValue > def.ExpectedLootValue * 1.4f);

                Check($"{def.Id} carries something to eat",
                      wild.Any(l => l != null && l.Item != null && l.Item.Category == ItemCategory.Food));

                foreach (LootDrop drop in village)
                {
                    Check($"{def.Id}'s loot line has an item", drop != null && drop.Item != null);
                    if (drop == null || drop.Item == null) continue;

                    Check($"{def.Id} drops {drop.Item.Id}, which is a real item",
                          items.IndexOf(drop.Item) != 0);

                    Check($"{def.Id} drops at least one {drop.Item.Id} when it drops any", drop.Low >= 1);
                }

                // Two lines for the same item would mean two rolls of it, which reads as a bug in a
                // log and makes "the village table contains the wild one" impossible to state.
                Check($"{def.Id}'s village table names each item once",
                      village.Where(l => l != null && l.Item != null)
                             .GroupBy(l => l.Item).All(g => g.Count() == 1));
            }
        }

        // ---------------------------------------------------------------- which camps have stores

        /// <summary>Stocked is the village and only the village. The number a bad bake would change.</summary>
        void Camps(NativeSpawner spawner)
        {
            NativeSpawner.Camp[] stocked = spawner.Camps.Where(c => c != null && c.Stocked).ToArray();
            NativeSpawner.Camp[] bare = spawner.Camps.Where(c => c != null && !c.Stocked).ToArray();

            Debug.Log($"[LootTest] {stocked.Length} stocked camp(s) "
                      + $"({string.Join(", ", stocked.Select(c => c.Id))}) against {bare.Length} without "
                      + $"({string.Join(", ", bare.Select(c => c.Id))}).");

            Check($"somewhere on the island has stores ({stocked.Length} camp(s))", stocked.Length > 0);
            Check($"somewhere on the island has none ({bare.Length} camp(s))", bare.Length > 0);

            Check("every stocked camp is the village",
                  stocked.All(c => c.Id.StartsWith("village.")));

            Check("no camp outside the village has stores",
                  bare.All(c => !c.Id.StartsWith("village.")));
        }

        // ---------------------------------------------------------------- the ammunition arithmetic

        /// <summary>
        /// The issue's own sentence, as a sum. Every gun in the catalog that eats ammunition is priced
        /// against the village exactly as baked: what it costs in rounds to clear it, against what
        /// clearing it gives back.
        ///
        /// Run twice, because the village is not one place at two times of day. The night population
        /// is larger and the raid is longer, and a claim that only held at noon would be a claim about
        /// the easy half of the game.
        /// </summary>
        void Ammunition(NativeSpawner spawner, WeaponCatalog guns)
        {
            if (guns == null || guns.Count == 0)
            {
                Check("the island has weapons to price ammunition against", false);
                return;
            }

            WeaponDef[] armed = guns.Weapons.Where(w => w != null && w.Ammo != null).ToArray();
            Check($"some weapons eat ammunition ({armed.Length})", armed.Length > 0);

            Debug.Log("[LootTest] gun         ammo            day: dropped/needed        "
                      + "night: dropped/needed");

            var paid = new HashSet<ItemDef>();

            foreach (WeaponDef gun in armed)
            {
                float dayDrop = Dropped(spawner, gun.Ammo, night: 0f);
                float dayCost = Cost(spawner, gun, night: 0f);
                float nightDrop = Dropped(spawner, gun.Ammo, night: 1f);
                float nightCost = Cost(spawner, gun, night: 1f);

                bool ahead = dayDrop >= dayCost && nightDrop >= nightCost;
                if (ahead) paid.Add(gun.Ammo);

                Debug.Log($"[LootTest]   {gun.Id,-13}{gun.Ammo.Id,-14}{dayDrop,7:0.0} /{dayCost,6:0.0}"
                          + $"{nightDrop,17:0.0} /{nightCost,6:0.0}   "
                          + $"{(ahead ? "pays for itself" : "costs more than it takes")}");
            }

            // Per ammunition type rather than per gun, on purpose. A raid that fed every gun would
            // make the choice of gun free; a raid that fed none would make guns a subscription. One
            // gun per calibre coming out ahead is what makes bringing the right one a decision.
            foreach (ItemDef ammo in armed.Select(w => w.Ammo).Distinct())
                Check($"a village sweep repays {ammo.Id} for at least one gun that fires it",
                      paid.Contains(ammo));
        }

        /// <summary>Expected rounds of one ammunition off the whole village, at a given hour.</summary>
        static float Dropped(NativeSpawner spawner, ItemDef ammo, float night)
        {
            float total = 0f;

            foreach (NativeSpawner.Camp camp in spawner.Camps)
            {
                if (camp == null || camp.Role == null || !camp.Stocked) continue;

                total += camp.Wanted(night) * EconomyModel.Rounds(camp.Role.VillageLoot, ammo);
            }

            return total;
        }

        /// <summary>Rounds to clear the village with one gun, at a given hour, misses included.</summary>
        static float Cost(NativeSpawner spawner, WeaponDef gun, float night)
        {
            float total = 0f;

            foreach (NativeSpawner.Camp camp in spawner.Camps)
            {
                if (camp == null || camp.Role == null || !camp.Stocked) continue;

                total += camp.Wanted(night) * EconomyModel.RoundsToKill(camp.Role, gun);
            }

            return total;
        }

        // ---------------------------------------------------------------- what a body actually drops

        /// <summary>
        /// Kill several of one role and check every stack against the table its camp says it rolls.
        /// The claim is two-sided: nothing off the wrong table, and - for a stocked body - at least
        /// one thing that could only have come off the village half of the right one.
        /// </summary>
        IEnumerator Rolled(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                           StunState stun, bool stocked)
        {
            LootDrop[] table = def.LootFor(stocked);
            LootDrop[] stores = stocked ? Stores(def) : System.Array.Empty<LootDrop>();

            var seen = new List<ItemStack>();
            var offTable = new List<ItemStack>();
            int fromStores = 0;
            int bodies = 0;

            for (int i = 0; i < Kills; i++)
            {
                Reset(health, stun);

                Native native = Place(spawner, def, Beside(motor, 7f, i * 47f), stocked);
                if (native == null) continue;

                var body = native.GetComponent<Health>();
                if (body == null)
                {
                    Remove(native);
                    continue;
                }

                var dropped = new List<ItemStack>();
                native.Looted += (_, stacks) => dropped.AddRange(stacks);

                Check($"a {(stocked ? "village" : "wandering")} {def.Id} knows where it came from",
                      native.Stocked == stocked);

                body.TakeDamage(new DamageInfo(body.Max * 2f, DamageType.Blunt, Vector3.zero,
                                               native.transform.position, 0f, motor.NetworkObject.ObjectId));

                yield return Settled();

                bodies++;
                seen.AddRange(dropped);

                foreach (ItemStack stack in dropped)
                {
                    LootDrop line = table.FirstOrDefault(l => l != null && l.Item == stack.Def);

                    if (line == null)
                    {
                        offTable.Add(stack);
                        continue;
                    }

                    Check($"{stack.Count}x {stack.Def.Id} off a {def.Id} is within {line.Low}-{line.High}",
                          stack.Count >= line.Low && stack.Count <= line.High);

                    if (stores.Contains(line)) fromStores++;
                }

                Remove(native);
            }

            Check($"{Kills} {def.Id}(s) could be killed ({bodies})", bodies == Kills);

            Debug.Log($"[LootTest] {bodies} {(stocked ? "village" : "wandering")} {def.Id}(s) left "
                      + $"{Describe(seen)}; {fromStores} stack(s) off the camp's stores, "
                      + $"{offTable.Count} off the table.");

            Check($"nothing a {(stocked ? "village" : "wandering")} {def.Id} drops is off its table "
                  + $"({Describe(offTable)})", offTable.Count == 0);

            if (stocked)
                Check($"a village {def.Id} actually drops the camp's stores ({fromStores} stack(s) "
                      + $"in {bodies} bodies)", fromStores > 0);

            Reset(health, stun);
        }

        // ---------------------------------------------------------------- plumbing

        /// <summary>
        /// One native, standing still, close enough to hit. Nothing here walks, so the agent is
        /// switched off and the body is put exactly where it was asked for.
        /// </summary>
        static Native Place(NativeSpawner spawner, NativeDef def, Vector3 spot, bool stocked)
        {
            spot = Stand(spot);

            Native native = spawner.ServerSpawn(def, spot, spot, stocked);
            if (native == null) return null;

            var agent = native.GetComponent<NavMeshAgent>();
            if (agent != null) agent.enabled = false;

            native.transform.position = spot;

            return native;
        }

        static Vector3 Beside(PlayerMotor motor, float metres, float degrees)
        {
            Vector3 forward = motor.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;

            Vector3 direction = Quaternion.Euler(0f, degrees, 0f) * forward.normalized;

            return motor.transform.position + direction * metres;
        }

        static Vector3 Ground(Vector3 spot)
            => NavMesh.SamplePosition(spot, out NavMeshHit hit, 25f, NavMesh.AllAreas) ? hit.position : spot;

        static Vector3 Stand(Vector3 spot)
            => Physics.Raycast(spot + Vector3.up * 60f, Vector3.down, out RaycastHit hit, 200f,
                               ~0, QueryTriggerInteraction.Ignore)
                   ? hit.point + Vector3.up * 0.05f
                   : Ground(spot);

        static void Remove(Native native)
        {
            if (native == null || native.NetworkObject == null || !native.NetworkObject.IsSpawned) return;

            InstanceFinder.ServerManager.Despawn(native.NetworkObject);
        }

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

        /// <summary>A SyncVar written this frame is read next tick, not next frame. See #46.</summary>
        static WaitForSeconds Settled() => new(0.3f);

        /// <summary>The village-only half of a role's table: what the camp adds to what it carries.</summary>
        static LootDrop[] Stores(NativeDef def)
            => def.VillageLoot.Where(l => l != null && !def.Loot.Any(w => Same(w, l))).ToArray();

        /// <summary>
        /// Whether two loot lines say the same thing. Not reference equality: <see cref="LootDrop"/>
        /// is a serializable class rather than an asset, so the copy in the wild table and the copy in
        /// the village table are two objects that happen to agree.
        /// </summary>
        static bool Same(LootDrop a, LootDrop b)
            => a != null && b != null && a.Item == b.Item && a.Low == b.Low && a.High == b.High
               && Mathf.Approximately(a.Chance, b.Chance);

        static string Line(LootDrop drop)
        {
            if (drop == null || drop.Item == null) return "?";

            string count = drop.Low == drop.High ? $"{drop.Low}" : $"{drop.Low}-{drop.High}";

            return drop.Chance >= 0.999f
                   ? $"{count} {drop.Item.Id}"
                   : $"{count} {drop.Item.Id} @{drop.Chance * 100f:0}%";
        }

        static string Describe(List<ItemStack> stacks)
        {
            if (stacks.Count == 0) return "nothing";

            return string.Join(", ", stacks.GroupBy(s => s.Def)
                                           .Select(g => $"{g.Sum(s => s.Count)}x "
                                                        + $"{(g.Key != null ? g.Key.Id : "?")}"));
        }

        void Report()
        {
            string line = $"[LootTest] {_passed} passed, {_failed} failed.";

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
            Debug.LogError($"[LootTest] FAILED: {what}.");
        }
    }
}
