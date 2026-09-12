using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.AI
{
    /// <summary>
    /// The acceptance test for #53, run inside a real session. Server side, behind <c>-animalTest</c>,
    /// and it needs <c>-scene island</c>: an animal without a NavMesh is a statue, and a hunt without
    /// a trader is a pile of meat.
    ///
    /// The criterion is one sentence - "hunting is a viable early money source" - and it contains
    /// three claims that fail independently, so they are checked independently.
    ///
    /// 1. **Viable.** A boar has to be catchable and killable with what a player owns on day one. So
    ///    the fight is measured rather than asserted: a hatchet swing lands on an animal for the
    ///    damage the hatchet's own asset states, and the boar's health divided by that number is the
    ///    number of swings printed in the log.
    /// 2. **Money.** The loot has to be worth carrying. That is arithmetic and it is done in full:
    ///    every species' expected drop, what the trader pays for it, and - the number that actually
    ///    decides it - **what it is worth per kilogram**, because twenty slots and a forty-kilo limit
    ///    mean weight is the real constraint on a trip back to the counter. Hunted goods have to beat
    ///    scrap metal, the money source that already existed, by a wide margin or hunting is a
    ///    worse use of the same walk.
    /// 3. **Early.** An income source you meet an hour inland is not early. So the spawner's own
    ///    baked zones are read back and one of them has to hold boars within a couple of hundred
    ///    metres of where players spawn.
    ///
    /// And underneath all three, the thing that makes it a hunt rather than a kill counter: **the
    /// loot lands on the ground.** The test kills an animal, counts the <see cref="WorldItem"/>s that
    /// appear around the carcass, carries them to the counter and sells them, and checks the money
    /// that arrives against the money the table promised. Every step of that chain is a place the
    /// feature could be silently fake.
    /// </summary>
    public class AnimalTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const float WaitForNavMesh = 90f;

        /// <summary>Metres in front of the player a test subject is placed. Inside every react radius.</summary>
        const float SpawnAhead = 12f;

        /// <summary>Metres in front for the ones that are meant to be hit rather than watched.</summary>
        const float MeleeAhead = 1.6f;

        /// <summary>Seconds a flee or a charge is watched before it has to have gone somewhere.</summary>
        const float WatchSeconds = 4f;

        /// <summary>The money source hunting has to beat. Torn off the wreck, sold by the kilo.</summary>
        const string Baseline = "scrap_metal";

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-animalTest")) return;

            _started = true;

            var go = new GameObject("AnimalTest");
            DontDestroyOnLoad(go);
            go.AddComponent<AnimalTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            // ---------------------------------------------------------------- the cast

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
                Debug.LogError("[AnimalTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            var bag = motor.GetComponent<Inventory>();
            var wallet = motor.GetComponent<Wallet>();
            var weapon = motor.GetComponent<Weapon>();
            var health = motor.GetComponent<Health>();
            var stun = motor.GetComponent<StunState>();

            if (bag == null || wallet == null || weapon == null || health == null)
            {
                Debug.LogError("[AnimalTest] The player is missing Inventory, Wallet, Weapon or Health; "
                               + "run PlayerPrefabBuilder.BuildPlayerPrefab.");
                yield break;
            }

            AnimalCatalog animals = AnimalCatalog.Active;
            ItemCatalog items = bag.Catalog;
            WeaponCatalog weapons = WeaponCatalog.Active;

            if (animals == null || items == null || weapons == null)
            {
                Debug.LogError("[AnimalTest] No animal, item or weapon catalog. Run ItemFactory.Build, "
                               + "WeaponFactory.Build and AnimalFactory.Build, then regenerate the island.");
                yield break;
            }

            Debug.Log($"[AnimalTest] {animals.Count} species, {items.Count} items, {weapons.Count} weapons.");

            // ---------------------------------------------------------------- the data

            Data(animals, items, motor.SprintSpeed);

            // ---------------------------------------------------------------- the arithmetic

            ShopCounter counter = FindObjectsByType<ShopCounter>(FindObjectsSortMode.None)
                                  .FirstOrDefault(c => c != null && c.IsSpawned && c.Shop != null);

            if (counter == null)
            {
                Debug.LogError("[AnimalTest] This scene has no trader, so nothing hunted can be sold and "
                               + "the acceptance criterion cannot be checked. Run with -scene island.");
                Report();
                yield break;
            }

            Economics(animals, items, counter.Shop);

            // ---------------------------------------------------------------- the island

            AnimalSpawner spawner = AnimalSpawner.Instance;
            Check("the island scene carries an AnimalSpawner", spawner != null);

            if (spawner == null)
            {
                Report();
                yield break;
            }

            Populations(spawner, motor.transform.position);

            // The NavMesh arrives with the island, which is loaded after the server starts. Rather
            // than assume an order, wait for the spawner to prove it works by putting something on it.
            float navDeadline = Time.time + WaitForNavMesh;
            while (Time.time < navDeadline && spawner.SpawnedTotal == 0)
                yield return new WaitForSeconds(1f);

            Check($"the zones populate themselves ({spawner.SpawnedTotal} spawned)", spawner.SpawnedTotal > 0);

            if (spawner.SpawnedTotal == 0)
            {
                Debug.LogError("[AnimalTest] Nothing was ever spawned, so no behaviour can be measured. "
                               + "Either there is no NavMesh or the zones are empty.");
                Report();
                yield break;
            }

            // ---------------------------------------------------------------- the behaviour

            yield return Flees(spawner, animals.Find("deer"), motor, health, stun);
            yield return Charges(spawner, animals.Find("boar"), motor, health, stun);

            // ---------------------------------------------------------------- the hunt

            yield return Hunt(spawner, animals.Find("boar"), motor, weapon, bag, wallet, items, weapons,
                              health, stun, counter);

            Report();
        }

        // ---------------------------------------------------------------- data

        /// <summary>
        /// The invariants a species has to hold before any of its behaviour means anything. Every one
        /// of these is a shape of bug that would otherwise show up as "the deer just stands there"
        /// three test sections later, with nothing pointing at the cause.
        /// </summary>
        void Data(AnimalCatalog animals, ItemCatalog items, float sprint)
        {
            Check($"there are species at all ({animals.Count})", animals.Count > 0);
            Check("index 0 is nothing", animals.At(0) == null);
            Check("an unknown definition indexes to 0", animals.IndexOf(null) == 0);

            string previous = null;

            foreach (AnimalDef def in animals.Animals)
            {
                if (def == null)
                {
                    Check("no holes in the catalog", false);
                    continue;
                }

                ushort index = animals.IndexOf(def);

                Check($"{def.Id} round-trips through its wire index ({index})", animals.At(index) == def);
                Check($"{def.Id} is findable by id", animals.Find(def.Id) == def);

                Check($"{def.Id} sorts after {previous ?? "the start"}",
                      previous == null || string.CompareOrdinal(previous, def.Id) < 0);
                previous = def.Id;

                // A run speed at or below the walk means an animal that flees at grazing pace, which
                // reads as broken rather than as calm.
                Check($"{def.Id} runs faster than it walks ({def.RunSpeed:0.0} > {def.WalkSpeed:0.0})",
                      def.RunSpeed > def.WalkSpeed);

                // React inside sense is definitional: an animal cannot bolt from something it has not
                // noticed, and the clamp in AnimalDef exists so a bad asset cannot say otherwise.
                Check($"{def.Id} reacts inside what it senses "
                      + $"({def.ReactRadius:0} <= {def.SenseRadius:0})", def.ReactRadius <= def.SenseRadius);

                // Both prey species have to be catchable by a sprinting player (7.5 m/s), or hunting
                // is gated behind the firearms in #51 and stops being an *early* money source.
                if (!def.IsAggressive)
                    Check($"{def.Id} can be run down by a sprint ({def.RunSpeed:0.0} < "
                          + $"{sprint:0.0})", def.RunSpeed < sprint);

                Check($"{def.Id} is {(def.IsAggressive ? "armed because it is aggressive" : "harmless because it flees")}",
                      def.IsAggressive ? def.AttackDamage > 0f : def.AttackDamage <= 0f);

                Check($"{def.Id} is worth killing ({def.ExpectedLootValue:0.0} in items)",
                      def.ExpectedLootValue > 0f);

                foreach (LootDrop drop in def.Loot)
                {
                    Check($"{def.Id}'s loot line has an item", drop != null && drop.Item != null);
                    if (drop == null || drop.Item == null) continue;

                    Check($"{def.Id} drops {drop.Item.Id}, which is a real item",
                          items.IndexOf(drop.Item) != 0);

                    Check($"{def.Id} drops at least one {drop.Item.Id} when it drops any", drop.Low >= 1);
                    Check($"{def.Id}'s {drop.Item.Id} has a chance worth having", drop.Chance > 0f);
                }
            }
        }

        // ---------------------------------------------------------------- arithmetic

        /// <summary>
        /// The acceptance criterion, stated as money.
        ///
        /// The comparison is per kilogram and not per kill, and that is the whole argument. A player
        /// has twenty slots and forty kilos, so the question "is hunting worth it" is really "is a
        /// kilo of this better than a kilo of what I would otherwise be carrying". Scrap metal is
        /// what they would otherwise be carrying.
        /// </summary>
        void Economics(AnimalCatalog animals, ItemCatalog items, ShopDef shop)
        {
            ItemDef scrap = items.Find(Baseline);
            ItemDef raw = items.Find("meat_raw");
            ItemDef cooked = items.Find("meat_cooked");

            if (scrap == null || raw == null || cooked == null)
            {
                Check("the economy has scrap, raw meat and roast meat to compare", false);
                return;
            }

            float scrapPerKg = shop.PriceFor(scrap) / Mathf.Max(0.01f, scrap.Weight);
            float rawPerKg = shop.PriceFor(raw) / Mathf.Max(0.01f, raw.Weight);
            float cookedPerKg = shop.PriceFor(cooked) / Mathf.Max(0.01f, cooked.Weight);

            Debug.Log($"[AnimalTest] the trader pays {shop.PriceFor(raw)} for raw meat, "
                      + $"{shop.PriceFor(cooked)} for roast, and {shop.PriceFor(scrap)} for scrap.");
            Debug.Log("[AnimalTest] species        kill  kg    c/kg   cooked  kg    c/kg");

            foreach (AnimalDef def in animals.Animals)
            {
                if (def == null) continue;

                float money = 0f, kilos = 0f, roastMoney = 0f, roastKilos = 0f;

                foreach (LootDrop drop in def.Loot)
                {
                    if (drop == null || drop.Item == null) continue;

                    float count = drop.Expected;

                    money += count * shop.PriceFor(drop.Item);
                    kilos += count * drop.Item.Weight;

                    // Cooking is a one-for-one swap at a fire, so the roast column is the same kill
                    // with every raw mouthful turned over. It is the number that decides whether the
                    // campfire is worth the walk back.
                    bool meat = drop.Item == raw;

                    roastMoney += count * shop.PriceFor(meat ? cooked : drop.Item);
                    roastKilos += count * (meat ? cooked.Weight : drop.Item.Weight);
                }

                float perKg = money / Mathf.Max(0.01f, kilos);
                float roastPerKg = roastMoney / Mathf.Max(0.01f, roastKilos);

                Debug.Log($"[AnimalTest]   {def.Id,-12} {money,4:0}  {kilos,4:0.0}  {perKg,5:0.0}   "
                          + $"{roastMoney,4:0}  {roastKilos,4:0.0}  {roastPerKg,5:0.0}");

                Check($"a {def.Id} is worth more per kilo than scrap "
                      + $"({perKg:0.0} vs {scrapPerKg:0.0} c/kg)", perKg > scrapPerKg);

                // A full trip, which is what a player actually plans. Forty kilos of this species
                // against forty kilos of the alternative.
                float trip = perKg * 40f;
                float scrapTrip = scrapPerKg * 40f;

                Check($"a full carry of {def.Id} beats a full carry of scrap by 2x "
                      + $"({trip:0} vs {scrapTrip:0})", trip > scrapTrip * 2f);
            }

            Check($"cooking meat is worth doing ({cookedPerKg:0.0} vs {rawPerKg:0.0} c/kg)",
                  cookedPerKg > rawPerKg);

            // What it is all for. Four parts at whatever the trader charges, over four players.
            ShopDef.Offer boat = shop.Offers.FirstOrDefault(o => o.Item != null && o.Item.Id == "boat_part");
            AnimalDef boar = animals.Find("boar");

            if (boat.Item != null && boar != null)
            {
                float perBoar = boar.Loot.Where(l => l != null && l.Item != null)
                                         .Sum(l => l.Expected * shop.PriceFor(l.Item));

                float parts = Mathf.Max(1, boat.Stock);
                float total = boat.Price * parts;
                float kills = total / Mathf.Max(1f, perBoar);

                Debug.Log($"[AnimalTest] the boat costs {total:0} ({parts:0} parts at {boat.Price}); "
                          + $"a boar is worth {perBoar:0}, so {kills:0} kills - "
                          + $"{kills / 4f:0} each across four players.");

                // #56 repriced the boat against every way of earning at once, so a boar count on its
                // own is no longer the measure - nobody buys a boat with venison alone. What hunting
                // still has to be is a credible way to spend an evening: four people who did nothing
                // but hunt should get there in a few hours of actual hunting, which is long enough to
                // want the other activities and short enough that the choice to hunt is not a
                // mistake.
                float hours = boat.Price * parts
                              / Mathf.Max(0.01f, Economy.EconomyModel.Hunting(animals, shop, 40f).CoinsPerMinute
                                                 * Economy.EconomyModel.Players
                                                 * Economy.EconomyModel.Attention * 60f);

                Debug.Log($"[AnimalTest] four people hunting and nothing else reach it in {hours:0.0} hours.");

                Check($"hunting alone would get there eventually ({hours:0.0} hours)",
                      hours >= 1.5f && hours <= 6f);
            }
        }

        // ---------------------------------------------------------------- populations

        /// <summary>
        /// The zones as baked. The "early" half of the acceptance criterion lives here: a money
        /// source you have to walk an hour inland for is not an early money source.
        /// </summary>
        void Populations(AnimalSpawner spawner, Vector3 spawnPoint)
        {
            Check($"the island has zones ({spawner.Zones.Count})", spawner.Zones.Count > 0);

            bool nearby = false;

            foreach (AnimalSpawner.Zone zone in spawner.Zones)
            {
                if (zone == null || zone.Species == null)
                {
                    Check("no zone is missing its species", false);
                    continue;
                }

                float distance = Vector3.Distance(spawnPoint, zone.Centre);

                Debug.Log($"[AnimalTest]   zone {zone.Id,-14} {zone.Population}x {zone.Species.Id} "
                          + $"within {zone.Radius:0}m, {distance:0}m from spawn");

                Check($"{zone.Id} keeps somebody alive", zone.Population > 0);
                Check($"{zone.Id} is big enough to hold {zone.Population}", zone.Radius >= 20f);

                // Aggressive, because the boar is the species carrying the money, and near, because
                // that is what makes it the *first* thing a player does rather than the fifth.
                if (zone.Species.IsAggressive && distance - zone.Radius <= 250f) nearby = true;
            }

            Check("there is huntable game within reach of the spawn", nearby);
        }

        // ---------------------------------------------------------------- behaviour

        /// <summary>A skittish species put in somebody's face has to end up further away.</summary>
        IEnumerator Flees(AnimalSpawner spawner, AnimalDef def, PlayerMotor motor, Health health,
                          StunState stun)
        {
            if (def == null)
            {
                Check("there is a skittish species to watch", false);
                yield break;
            }

            Reset(health, stun);

            Animal animal = spawner.ServerSpawn(def, Ahead(motor, SpawnAhead));
            if (animal == null)
            {
                Check($"a {def.Id} can be placed near the player", false);
                yield break;
            }

            float start = Distance(animal, motor);
            float furthest = start;
            AnimalState seen = animal.State;

            float until = Time.time + WatchSeconds;
            while (Time.time < until && Alive(animal))
            {
                furthest = Mathf.Max(furthest, Distance(animal, motor));
                if (animal.State == AnimalState.Flee) seen = AnimalState.Flee;

                yield return null;
            }

            Debug.Log($"[AnimalTest] the {def.Id} started {start:0.0}m away and got to {furthest:0.0}m "
                      + $"in {WatchSeconds:0}s.");

            Check($"a {def.Id} notices a player {start:0}m away", seen == AnimalState.Flee);
            Check($"a {def.Id} runs away ({furthest:0.0}m > {start:0.0}m)", furthest > start + 4f);

            Remove(animal);
        }

        /// <summary>An aggressive species put in somebody's face has to close, and then hurt.</summary>
        IEnumerator Charges(AnimalSpawner spawner, AnimalDef def, PlayerMotor motor, Health health,
                            StunState stun)
        {
            if (def == null)
            {
                Check("there is an aggressive species to watch", false);
                yield break;
            }

            Reset(health, stun);

            Animal animal = spawner.ServerSpawn(def, Ahead(motor, SpawnAhead));
            if (animal == null)
            {
                Check($"a {def.Id} can be placed near the player", false);
                yield break;
            }

            float start = Distance(animal, motor);
            float closest = start;
            float before = health.Current;
            bool attacked = false;

            // Longer than a flee: it has to close the distance first, and the charge is deliberately
            // slower than a sprint.
            float until = Time.time + WatchSeconds * 3f;
            while (Time.time < until && Alive(animal))
            {
                closest = Mathf.Min(closest, Distance(animal, motor));
                if (animal.State == AnimalState.Attack) attacked = true;

                yield return null;
            }

            float lost = before - health.Current;

            Debug.Log($"[AnimalTest] the {def.Id} started {start:0.0}m away, closed to {closest:0.0}m, "
                      + $"and took {lost:0} health.");

            Check($"a {def.Id} charges ({closest:0.0}m < {start:0.0}m)", closest < start - 4f);
            Check($"a {def.Id} gets into reach", attacked);
            Check($"a {def.Id} hits for something ({lost:0} lost)", lost > 0f);

            // Every hit it landed was one of its own, at its own number - a boar cannot do rifle
            // damage because it went through the same Health door everything else does.
            Check($"a {def.Id} hits for its own damage ({def.AttackDamage:0} a time)",
                  lost <= def.AttackDamage * 12f + 0.01f);

            Remove(animal);
            Reset(health, stun);
        }

        // ---------------------------------------------------------------- the hunt

        /// <summary>
        /// The whole chain, once, on the species the criterion is about: swing at it, kill it, watch
        /// the loot hit the ground, carry it to the trader, and count the money.
        /// </summary>
        IEnumerator Hunt(AnimalSpawner spawner, AnimalDef def, PlayerMotor motor, Weapon weapon,
                         Inventory bag, Wallet wallet, ItemCatalog items, WeaponCatalog weapons,
                         Health health, StunState stun, ShopCounter counter)
        {
            if (def == null)
            {
                Check("there is a species to hunt", false);
                yield break;
            }

            Reset(health, stun);

            WeaponDef hatchet = weapons.Find("hatchet");
            if (hatchet == null || hatchet.Item == null)
            {
                Check("there is a hatchet to hunt with", false);
                yield break;
            }

            bag.ServerClear();
            bag.Add(hatchet.Item, 1);
            bag.SelectSlot(0);

            yield return Settled();

            if (weapon.Equipped != hatchet)
            {
                Check($"the hatchet equips (holding "
                      + $"{(weapon.Equipped != null ? weapon.Equipped.Id : "nothing")})", false);
                yield break;
            }

            Animal animal = spawner.ServerSpawn(def, Ahead(motor, MeleeAhead));
            if (animal == null)
            {
                Check($"a {def.Id} can be placed in front of the player", false);
                yield break;
            }

            var prey = animal.GetComponent<Health>();
            Check($"a {def.Id} has {prey.Max:0} health, so a hatchet needs "
                  + $"{Mathf.CeilToInt(prey.Max / hatchet.Hit.Damage)} swings",
                  prey.Max > 0f && prey.Max <= hatchet.Hit.Damage * 6f);

            // ---------------------------------------------------------------- it can be hit

            float before = prey.Current;
            int hits = weapon.ServerAttackNow(Toward(motor, animal));

            yield return Settled();

            float dealt = before - prey.Current;

            Debug.Log($"[AnimalTest] one hatchet swing at a {def.Id}: {hits} hit(s), {dealt:0} damage.");

            Check($"a weapon hits an animal at all ({hits} hit)", hits > 0);
            Check($"a hatchet does its own {hatchet.Hit.Damage:0} to a {def.Id} ({dealt:0})",
                  Mathf.Abs(dealt - hatchet.Hit.Damage) < 1f);

            // ---------------------------------------------------------------- it dies and it drops

            var dropped = new List<ItemStack>();
            animal.Butchered += (_, stacks) => dropped.AddRange(stacks);

            Vector3 carcass = animal.transform.position;
            var already = new HashSet<WorldItem>(FindObjectsByType<WorldItem>(FindObjectsSortMode.None));

            prey.TakeDamage(new DamageInfo(prey.Max * 2f, DamageType.Blunt, Vector3.zero, carcass, 0f,
                                           motor.NetworkObject.ObjectId));

            yield return Settled();

            Check($"a {def.Id} dies outright rather than going down "
                  + $"({prey.State})", prey.IsDead && animal.State == AnimalState.Dead);

            Check($"a dead {def.Id} leaves something ({Describe(dropped)})", dropped.Count > 0);

            foreach (ItemStack stack in dropped)
            {
                ItemDef item = stack.Def;
                LootDrop line = def.Loot.FirstOrDefault(l => l != null && l.Item == item);

                Check($"a {def.Id} dropping {stack.Count}x {(item != null ? item.Id : "?")} is on its table",
                      line != null);

                if (line == null) continue;

                Check($"{stack.Count}x {item.Id} is within {line.Low}-{line.High}",
                      stack.Count >= line.Low && stack.Count <= line.High);
            }

            // On the ground and not in a bag. This is the line between hunting and a kill counter.
            WorldItem[] fresh = FindObjectsByType<WorldItem>(FindObjectsSortMode.None)
                                .Where(w => w != null && !already.Contains(w)
                                            && Vector3.Distance(w.transform.position, carcass) < 6f)
                                .ToArray();

            Debug.Log($"[AnimalTest] {fresh.Length} item(s) on the ground around the carcass.");

            Check($"the loot lands on the ground ({fresh.Length} of {dropped.Count})",
                  fresh.Length >= dropped.Count);

            // ---------------------------------------------------------------- it is worth money

            int predicted = dropped.Sum(s => s.Def != null ? counter.Shop.PriceFor(s.Def) * s.Count : 0);

            bag.ServerClear();
            foreach (ItemStack stack in dropped)
                if (stack.Def != null) bag.Add(stack.Def, stack.Count);

            motor.ServerTeleport(counter.transform.position + Vector3.up * 0.5f, 0f);
            wallet.ServerSetBalance(0);

            yield return Settled();

            int earned = 0;
            for (int slot = 0; slot < bag.SlotCount; slot++)
            {
                if (bag[slot].IsEmpty) continue;

                earned += counter.ServerSell(bag, wallet, slot, int.MaxValue, out string why);
                if (why != null) Debug.Log($"[AnimalTest] slot {slot}: {why}");
            }

            yield return Settled();

            Debug.Log($"[AnimalTest] one {def.Id} sold for {earned}; the table said {predicted}.");

            Check($"the trader pays what the table promised ({earned} vs {predicted})", earned == predicted);
            Check($"the money arrives in the wallet ({wallet.Balance})", wallet.Balance == earned);
            Check($"one {def.Id} is real money ({earned})", earned > 0);

            Remove(animal);
        }

        // ---------------------------------------------------------------- plumbing

        static Vector3 Ahead(PlayerMotor motor, float metres)
        {
            Vector3 forward = motor.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;

            return motor.transform.position + forward.normalized * metres;
        }

        static Vector3 Toward(PlayerMotor motor, Animal animal)
        {
            Vector3 d = animal.transform.position - motor.transform.position;
            d.y = 0f;

            return d.sqrMagnitude > 0.001f ? d.normalized : motor.transform.forward;
        }

        static float Distance(Animal animal, PlayerMotor motor)
            => Vector3.Distance(animal.transform.position, motor.transform.position);

        static bool Alive(Animal animal)
            => animal != null && animal.NetworkObject != null && animal.NetworkObject.IsSpawned;

        static void Remove(Animal animal)
        {
            if (animal == null || animal.NetworkObject == null || !animal.NetworkObject.IsSpawned) return;

            InstanceFinder.ServerManager.Despawn(animal.NetworkObject);
        }

        static string Describe(List<ItemStack> stacks)
            => stacks.Count == 0
                ? "nothing"
                : string.Join(", ", stacks.Select(s => $"{s.Count}x {(s.Def != null ? s.Def.Id : "?")}"));

        /// <summary>Alive, healed and unstunned. A stunned player cannot swing at anything.</summary>
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

        void Report()
        {
            string line = $"[AnimalTest] {_passed} passed, {_failed} failed.";

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
            Debug.LogError($"[AnimalTest] FAILED: {what}.");
        }
    }
}
