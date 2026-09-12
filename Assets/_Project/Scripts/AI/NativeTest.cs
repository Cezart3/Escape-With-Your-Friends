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
using UnityEngine.AI;

namespace EscapeWithYourFriends.AI
{
    /// <summary>
    /// The acceptance test for #55, run inside a real session. Server side, behind
    /// <c>-nativeTest</c>, and it wants <c>-scene island</c>, because a native without a NavMesh is a
    /// statue and its camps are baked off the island's POIs.
    ///
    /// The criterion is one sentence with two halves that pull against each other - *"natives are a
    /// real threat at night but not unfair in daylight"* - and the only way to check a sentence like
    /// that is to run the same experiment twice with nothing changed but the sun. So that is what
    /// this does: <see cref="WorldClock.Freeze"/> is pinned to noon, a set of measurements is taken,
    /// the clock is pinned to midnight, and the identical measurements are taken again. Every number
    /// printed below is a measured distance or a measured time, not a restatement of the asset.
    ///
    /// **The fair half, measured four ways.**
    ///
    /// 1. **They have to face you.** The sweep is run with the native turned away, and by day a
    ///    player standing well inside the notice radius is not noticed at all.
    /// 2. **Terrain is cover.** The sweep is run again with a wall dropped between the two, and by
    ///    day the wall makes the native blind - the same wall does nothing at night, which is the
    ///    single clearest statement of what the sun is worth.
    /// 3. **The leash is short.** A native pulled past its daylight leash gives up on the spot; the
    ///    same native at the same distance at night keeps coming.
    /// 4. **You can always run.** Every role's run speed is checked against the player's own sprint,
    ///    off the player's own component, because that is the promise the whole design rests on.
    ///
    /// **The threatening half, measured five ways.** The notice radius grows and stops needing sight;
    /// a spearman put in reach actually takes health off at its own stated damage after its own
    /// stated wind-up; a blowgunner lands darts and the stun that is its real weapon; a camp *shouts*
    /// - one native noticing wakes the others onto the spot where the player was; and a body that
    /// loses the fight breaks for camp rather than dying in place.
    /// </summary>
    public class NativeTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const float WaitForNavMesh = 90f;

        /// <summary>Noon and midnight, as <see cref="WorldClock.Normalized"/> reads them.</summary>
        const float Noon = 0.5f;
        const float Midnight = 0f;

        /// <summary>Seconds a placed native is watched before it has or has not noticed anybody.</summary>
        const float SenseWindow = 1.4f;

        /// <summary>Metres the notice sweep steps in as it walks the player towards the native.</summary>
        const float SweepStep = 2f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-nativeTest")) return;

            _started = true;

            var go = new GameObject("NativeTest");
            DontDestroyOnLoad(go);
            go.AddComponent<NativeTest>();
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
                Debug.LogError("[NativeTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            var health = motor.GetComponent<Health>();
            var stun = motor.GetComponent<StunState>();
            var bag = motor.GetComponent<Inventory>();

            NativeCatalog natives = NativeCatalog.Active;
            ItemCatalog items = bag != null ? bag.Catalog : ItemCatalog.Active;

            if (health == null || natives == null || items == null)
            {
                Debug.LogError("[NativeTest] No player Health, native catalog or item catalog. Run "
                               + "ItemFactory.Build and NativeFactory.Build, then regenerate the island.");
                yield break;
            }

            Debug.Log($"[NativeTest] {natives.Count} role(s), {items.Count} items.");

            Data(natives, items, motor.SprintSpeed);

            NativeSpawner spawner = NativeSpawner.Instance;
            Check("the island scene carries a NativeSpawner", spawner != null);

            if (spawner == null)
            {
                Debug.LogError("[NativeTest] No spawner, so no behaviour can be measured. Run with "
                               + "-scene island and regenerate the island after NativeFactory.Build.");
                Report();
                yield break;
            }

            Camps(spawner, motor.transform.position);

            // The NavMesh arrives with the island, which is loaded after the server starts. Rather
            // than assume an order, wait for the spawner to prove it works by putting somebody on it.
            float navDeadline = Time.time + WaitForNavMesh;
            while (Time.time < navDeadline && spawner.SpawnedTotal == 0)
                yield return new WaitForSeconds(1f);

            Check($"the camps man themselves ({spawner.SpawnedTotal} spawned)", spawner.SpawnedTotal > 0);

            NativeDef spearman = natives.Find("spearman");
            NativeDef blowgunner = natives.Find("blowgunner");
            NativeDef scout = natives.Find("scout");

            if (spearman == null || blowgunner == null || scout == null)
            {
                Check("the three roles exist", false);
                Report();
                yield break;
            }

            // ---------------------------------------------------------------- the same experiment, twice

            yield return Senses(spawner, spearman, motor, health, stun);

            // ---------------------------------------------------------------- the rest of the fair half

            yield return Leash(spawner, spearman, motor, health, stun);

            // ---------------------------------------------------------------- the threatening half

            yield return Fights(spawner, spearman, motor, health, stun);
            yield return Darts(spawner, blowgunner, motor, health, stun);
            yield return Shouts(spawner, spearman, motor, health, stun);
            yield return Breaks(spawner, scout, motor, health, stun);
            yield return Drops(spawner, spearman, motor, health, stun, items);

            WorldClock.Freeze(-1f);
            Report();
        }

        // ---------------------------------------------------------------- data

        /// <summary>
        /// The invariants that have to hold before any measured behaviour means anything. Each of
        /// these is a shape of bug that would otherwise surface four sections later as "the spearman
        /// just stands there", with nothing pointing at the cause.
        /// </summary>
        void Data(NativeCatalog natives, ItemCatalog items, float sprint)
        {
            Check($"there are roles at all ({natives.Count})", natives.Count > 0);
            Check("index 0 is nothing", natives.At(0) == null);
            Check("an unknown definition indexes to 0", natives.IndexOf(null) == 0);

            string previous = null;

            Debug.Log("[NativeTest] role         hp   dmg  dps   notice day/night  leash day/night  run");

            foreach (NativeDef def in natives.Natives)
            {
                if (def == null)
                {
                    Check("no holes in the catalog", false);
                    continue;
                }

                ushort index = natives.IndexOf(def);

                Check($"{def.Id} round-trips through its wire index ({index})", natives.At(index) == def);
                Check($"{def.Id} is findable by id", natives.Find(def.Id) == def);
                Check($"{def.Id} sorts after {previous ?? "the start"}",
                      previous == null || string.CompareOrdinal(previous, def.Id) < 0);
                previous = def.Id;

                Debug.Log($"[NativeTest]   {def.Id,-11} {def.MaxHealth,3:0}  {def.AttackDamage,3:0}  "
                          + $"{def.DamagePerSecond,4:0.0}  {def.DayNotice,4:0}/{def.NightNotice,-4:0}     "
                          + $"   {def.DayLeash,4:0}/{def.NightLeash,-4:0}    {def.RunSpeed,4:0.0}");

                // The promise the entire design rests on. Off the player's own component, so tuning
                // the player's sprint down without touching the AI fails here rather than in a
                // playtest three weeks later.
                Check($"a sprint outruns a {def.Id} ({sprint:0.0} > {def.RunSpeed:0.0} m/s)",
                      def.RunSpeed < sprint);

                Check($"{def.Id} chases faster than it patrols "
                      + $"({def.RunSpeed:0.0} > {def.WalkSpeed:0.0})", def.RunSpeed > def.WalkSpeed);

                // The sun has to move every dial in the same direction, or dusk makes the island
                // safer somewhere and the ramp stops being readable.
                Check($"{def.Id} notices further in the dark "
                      + $"({def.NightNotice:0} > {def.DayNotice:0}m)", def.NightNotice > def.DayNotice);

                Check($"{def.Id} follows further in the dark "
                      + $"({def.NightLeash:0} > {def.DayLeash:0}m)", def.NightLeash > def.DayLeash);

                Check($"{def.Id} needs its eyes at noon and not at midnight",
                      def.NeedsSight(0f) && !def.NeedsSight(1f));

                Check($"{def.Id} blends between the two ({def.NoticeRadius(0.5f):0.0}m at dusk)",
                      def.NoticeRadius(0.5f) > def.DayNotice && def.NoticeRadius(0.5f) < def.NightNotice);

                // Daylight has to leave room to walk past a camp. A notice radius the size of the
                // leash means being seen and being chased across the island are the same event.
                Check($"{def.Id} can be walked around by day ({def.DayNotice:0} < {def.DayLeash:0}m)",
                      def.DayNotice < def.DayLeash);

                Check($"{def.Id} hits for something ({def.AttackDamage:0})", def.AttackDamage > 0f);
                Check($"{def.Id} telegraphs its blow ({def.WindupSeconds:0.00}s)", def.WindupSeconds > 0f);

                // Time to kill a full-health player with nothing but this role standing on them. Under
                // four seconds is a death you cannot answer; over half a minute is scenery.
                float ttk = 100f / Mathf.Max(0.01f, def.DamagePerSecond);
                Check($"a lone {def.Id} takes {ttk:0}s to kill a full-health player",
                      ttk >= 4f && ttk <= 30f);

                if (def.IsRanged)
                {
                    Check($"{def.Id} keeps its distance ({def.Standoff:0} of {def.AttackRange:0}m)",
                          def.Standoff < def.AttackRange && def.Standoff > 4f);

                    Check($"{def.Id} can miss ({def.SpreadDegrees:0.0} degrees of spread)",
                          def.SpreadDegrees > 0f);

                    Check($"{def.Id}'s real weapon is the stun ({def.AttackStun:0.0}s)",
                          def.AttackStun >= 1f);
                }
                else
                {
                    Check($"{def.Id} has to reach you ({def.AttackRange:0.0}m)", def.AttackRange <= 4f);
                }

                Check($"{def.Id} is worth searching ({def.ExpectedLootValue:0.0}c in items)",
                      def.ExpectedLootValue > 0f);

                foreach (LootDrop drop in def.Loot)
                {
                    Check($"{def.Id}'s loot line has an item", drop != null && drop.Item != null);
                    if (drop == null || drop.Item == null) continue;

                    Check($"{def.Id} drops {drop.Item.Id}, which is a real item",
                          items.IndexOf(drop.Item) != 0);

                    Check($"{def.Id} drops at least one {drop.Item.Id} when it drops any", drop.Low >= 1);
                }
            }
        }

        // ---------------------------------------------------------------- camps

        /// <summary>
        /// The camps as baked. The half of "not unfair" that no behaviour test can catch: a village
        /// patrolling the players' own fire is a broken map rather than a difficulty setting.
        /// </summary>
        void Camps(NativeSpawner spawner, Vector3 spawnPoint)
        {
            Check($"the island has camps ({spawner.Camps.Count})", spawner.Camps.Count > 0);

            int day = 0, night = 0;

            foreach (NativeSpawner.Camp camp in spawner.Camps)
            {
                if (camp == null || camp.Role == null)
                {
                    Check("no camp is missing its role", false);
                    continue;
                }

                float distance = Vector3.Distance(spawnPoint, camp.Centre);

                Debug.Log($"[NativeTest]   camp {camp.Id,-18} {camp.Population}(+{camp.NightExtra})x "
                          + $"{camp.Role.Id,-11} within {camp.Radius:0}m, {distance:0}m from spawn");

                Check($"{camp.Id} is manned", camp.Population > 0);

                // The dead zone around the players' fire, and it is measured to the edge of the camp
                // rather than to its centre plus the leash it would chase you with at night.
                float edge = distance - camp.Radius - camp.Role.NightLeash;
                Check($"{camp.Id} cannot reach the spawn at night ({edge:0}m of clear ground)", edge > 0f);

                day += camp.Population;
                night += camp.Wanted(1f);
            }

            Debug.Log($"[NativeTest] the island holds {day} natives by day and {night} at night.");
            Check($"the island gets busier after dark ({day} -> {night})", night > day);
        }

        // ---------------------------------------------------------------- senses

        /// <summary>
        /// The acceptance criterion itself: the same three sweeps at noon and at midnight.
        ///
        /// A sweep walks the player in from beyond the night radius and reports the first distance at
        /// which the native reacted, which is a measurement rather than an assertion - if the cone,
        /// the raycast or the blend ever silently stops working, the number moves and the check that
        /// reads it fails.
        /// </summary>
        IEnumerator Senses(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                           StunState stun)
        {
            float ceiling = def.NightNotice + 6f;
            float floor = def.Earshot + 2f;

            var results = new Dictionary<string, float>();

            foreach ((string label, float sun) in new[] { ("noon", Noon), ("midnight", Midnight) })
            {
                WorldClock.Freeze(sun);
                yield return null;

                Debug.Log($"[NativeTest] --- {label}, night01 = {WorldClock.Night01:F2} ---");

                foreach ((string what, bool facing, bool wall) in new[]
                         {
                             ("in the open", true, false),
                             ("turned away", false, false),
                             ("behind a wall", true, true),
                         })
                {
                    float found = 0f;
                    yield return Sweep(spawner, def, motor, health, stun, ceiling, floor, facing, wall,
                                       metres => found = metres);

                    results[$"{label}/{what}"] = found;

                    Debug.Log($"[NativeTest] {def.Id} {what} at {label}: "
                              + (found > 0f ? $"noticed at {found:0}m." : "never noticed."));
                }
            }

            float dayOpen = results["noon/in the open"];
            float nightOpen = results["midnight/in the open"];

            // The one line the whole issue is about.
            Debug.Log($"[NativeTest] the sun is worth {nightOpen - dayOpen:0}m of notice: a {def.Id} sees "
                      + $"{dayOpen:0}m at noon and {nightOpen:0}m at midnight.");

            Check($"a {def.Id} notices a player in daylight ({dayOpen:0}m)", dayOpen > 0f);

            Check($"daylight notice matches the asset ({dayOpen:0} vs {def.DayNotice:0}m)",
                  dayOpen <= def.DayNotice + SweepStep && dayOpen >= def.DayNotice - SweepStep * 2f);

            Check($"the dark is worse ({nightOpen:0}m vs {dayOpen:0}m)", nightOpen > dayOpen + SweepStep);

            Check($"night notice matches the asset ({nightOpen:0} vs {def.NightNotice:0}m)",
                  nightOpen <= def.NightNotice + SweepStep && nightOpen >= def.NightNotice - SweepStep * 2f);

            // Fair half, one: by day, behind them is behind them.
            Check($"by day you can walk up behind a {def.Id} ({results["noon/turned away"]:0}m vs "
                  + $"{dayOpen:0}m in the open)", results["noon/turned away"] < dayOpen - SweepStep);

            // Fair half, two: by day, terrain is cover.
            Check($"by day a wall blinds a {def.Id} ({results["noon/behind a wall"]:0}m)",
                  results["noon/behind a wall"] <= 0f);

            // Threatening half: at night neither trick is worth anything, because they are listening.
            Check($"at night a {def.Id} does not care where it is facing "
                  + $"({results["midnight/turned away"]:0}m)",
                  results["midnight/turned away"] >= nightOpen - SweepStep);

            Check($"at night a wall is not cover ({results["midnight/behind a wall"]:0}m)",
                  results["midnight/behind a wall"] >= nightOpen - SweepStep);
        }

        /// <summary>
        /// Walks a player in from <paramref name="ceiling"/> to <paramref name="floor"/> and reports
        /// the first distance the native reacted at, or 0 if it never did.
        ///
        /// From far to near rather than near to far, so the answer is the *boundary* and the sweep
        /// stops the moment it has one. A fresh native each step, because a native that has already
        /// noticed you remembers for nine seconds and would report a radius it never had.
        /// </summary>
        IEnumerator Sweep(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                          StunState stun, float ceiling, float floor, bool facing, bool blocked,
                          System.Action<float> result)
        {
            result(0f);

            for (float metres = ceiling; metres >= floor; metres -= SweepStep)
            {
                Reset(health, stun);

                Vector3 spot = Ahead(motor, metres);
                Native native = Place(spawner, def, spot, motor, facing);

                if (native == null)
                {
                    Check($"a {def.Id} can be placed {metres:0}m away", false);
                    yield break;
                }

                GameObject wall = blocked ? Wall(motor.transform.position, native.transform.position) : null;

                if (wall != null)
                {
                    // The slab was born at the origin one statement ago and the physics world only
                    // catches up on FixedUpdate - without this the native looks straight through a
                    // wall that is, as far as PhysX is concerned, still sitting on the map origin.
                    Physics.SyncTransforms();

                    // Silent when the cover works. It speaks up only when the ray the native is
                    // about to cast would reach the player anyway, which means the test is now
                    // measuring something other than what it says it measures.
                    Vector3 eye = native.transform.position + Vector3.up * (def.BodyHeight * 0.9f);
                    Vector3 span = motor.transform.position + Vector3.up * 1.2f - eye;

                    if (!Physics.Raycast(eye, span.normalized, span.magnitude, ~0,
                                         QueryTriggerInteraction.Ignore))
                        Debug.LogWarning($"[NativeTest] the wall {metres:0}m out is not in the way: "
                                         + $"{wall.transform.position} does not cross {eye} -> "
                                         + $"{motor.transform.position}.");
                }

                float until = Time.time + SenseWindow;
                bool noticed = false;

                while (Time.time < until && !noticed)
                {
                    noticed = native.Target != null;
                    yield return null;
                }

                // The distance as placed rather than as asked for: the spot was pulled onto the
                // NavMesh, and a boundary reported from the number we wanted would be fiction.
                float actual = Vector3.Distance(motor.transform.position, native.transform.position);

                Remove(native);
                if (wall != null) Destroy(wall);

                if (!noticed) continue;

                result(actual);
                yield break;
            }
        }

        // ---------------------------------------------------------------- leash

        /// <summary>
        /// The other half of daylight fairness: a chase that ends. The native is walked out past its
        /// own daylight leash with the player still on top of it, and by day it has to let go where
        /// it stands. The same distance at night is well inside the night leash, so the identical
        /// setup has to produce the opposite answer - which is the point, because a leash that never
        /// releases and a leash that always releases are both bugs.
        /// </summary>
        IEnumerator Leash(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                          StunState stun)
        {
            float outside = def.DayLeash * 1.2f;

            Check($"{outside:0}m is past a {def.Id}'s daylight leash but inside its night leash",
                  outside > def.DayLeash && outside < def.NightLeash);

            foreach ((string label, float sun, bool shouldHold) in new[]
                     {
                         ("noon", Noon, false),
                         ("midnight", Midnight, true),
                     })
            {
                WorldClock.Freeze(sun);
                yield return null;

                Reset(health, stun);

                // Camp where the player is standing, native right next to the player so it notices
                // immediately whatever the sun is doing - Earshot ignores both the cone and the ray.
                Vector3 camp = motor.transform.position;
                Native native = Place(spawner, def, Ahead(motor, 3f), motor, facing: true);

                if (native == null)
                {
                    Check($"a {def.Id} can be placed for the leash test", false);
                    continue;
                }

                yield return Until(() => native.Target != null, 3f);

                bool caught = native.Target != null;
                Check($"a {def.Id} standing on you notices at {label}", caught);

                if (!caught)
                {
                    Remove(native);
                    continue;
                }

                // Drag both of them out to the far end of the leash together. The distance that
                // matters is the native's own distance from its camp, so the player follows.
                Vector3 far = camp + (Ahead(motor, 1f) - camp).normalized * outside;

                native.transform.position = far;
                motor.ServerTeleport(far + (camp - far).normalized * 3f, 0f);

                yield return new WaitForSeconds(1.2f);

                bool held = native.Target != null;
                float fromCamp = Vector3.Distance(native.transform.position, native.Camp);

                Debug.Log($"[NativeTest] {def.Id} dragged {fromCamp:0}m from camp at {label} "
                          + $"(leash {def.LeashRange(WorldClock.Night01):0}m): "
                          + (held ? "still hunting." : "gave up."));

                Check(shouldHold
                          ? $"at {label} a {def.Id} follows you {fromCamp:0}m from camp"
                          : $"at {label} a {def.Id} lets go {fromCamp:0}m from camp",
                      held == shouldHold);

                Remove(native);
                motor.ServerTeleport(camp, 0f);

                yield return Settled();
            }
        }

        // ---------------------------------------------------------------- the fight

        /// <summary>A spearman put in reach has to take health off, at its own number, after its own tell.</summary>
        IEnumerator Fights(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                           StunState stun)
        {
            WorldClock.Freeze(Midnight);
            yield return null;

            Reset(health, stun);

            Native native = Place(spawner, def, Ahead(motor, def.AttackRange * 0.6f), motor, facing: true);

            if (native == null)
            {
                Check($"a {def.Id} can be placed in reach", false);
                yield break;
            }

            var blows = new List<float>();
            float noticedAt = 0f;
            float firstBlow = 0f;

            native.Noticed += (_, __) => { if (noticedAt <= 0f) noticedAt = Time.time; };
            native.Struck += (_, victim, damage) =>
            {
                if (victim != health) return;

                if (firstBlow <= 0f) firstBlow = Time.time;
                blows.Add(damage);
            };

            float before = health.Current;
            bool everStunned = false;
            bool everAttacking = false;

            float until = Time.time + def.AttackInterval * 3f + 2f;
            while (Time.time < until && health.IsAlive)
            {
                if (stun != null && stun.IsStunned) everStunned = true;
                if (native.State == NativeState.Attack) everAttacking = true;

                yield return null;
            }

            float lost = before - health.Current;

            Debug.Log($"[NativeTest] a {def.Id} in reach: {blows.Count} blow(s), {lost:0} health, "
                      + $"first one {firstBlow - noticedAt:0.00}s after it noticed "
                      + $"(wind-up {def.WindupSeconds:0.00}s).");

            Check($"a {def.Id} gets into its attack state", everAttacking);
            Check($"a {def.Id} lands blows ({blows.Count})", blows.Count > 0);
            Check($"a {def.Id} takes real health ({lost:0} of 100)", lost > 0f);

            Check($"every blow is a {def.Id}'s own {def.AttackDamage:0} damage",
                  blows.All(d => Mathf.Abs(d - def.AttackDamage) < 0.01f));

            Check($"a {def.Id} stuns what it hits", everStunned);

            // The tell. A blow that lands the instant it decides to swing is a blow you can only
            // lose to, so the first one has to arrive no sooner than the wind-up says.
            if (noticedAt > 0f && firstBlow > 0f)
                Check($"a {def.Id} winds up before it lands one "
                      + $"({firstBlow - noticedAt:0.00}s >= {def.WindupSeconds:0.00}s)",
                      firstBlow - noticedAt >= def.WindupSeconds - 0.05f);

            Remove(native);
            Reset(health, stun);
        }

        /// <summary>A blowgunner has to actually fire, sometimes miss, and stun what it hits.</summary>
        IEnumerator Darts(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                          StunState stun)
        {
            WorldClock.Freeze(Midnight);
            yield return null;

            Reset(health, stun);

            // Inside the standoff it would back off; well outside it the spread turns the test into a
            // coin flip. Eight metres is a shot it takes and mostly makes.
            Native native = Place(spawner, def, Ahead(motor, 8f), motor, facing: true);

            if (native == null)
            {
                Check($"a {def.Id} can be placed at range", false);
                yield break;
            }

            int fired = 0;
            int landed = 0;

            native.DartFired += (_, __) => fired++;
            native.Struck += (_, victim, ___) => { if (victim == health) landed++; };

            float before = health.Current;
            bool everStunned = false;

            // Measured now rather than after: the first dart that lands knocks the player about, and
            // the distance that matters is the one the shots were actually taken at.
            float range = Vector3.Distance(motor.transform.position, native.transform.position);

            // Ten shots rather than five: a four degree cone at eight metres misses about a third of
            // the time on purpose, and a test that fails on a run of bad luck is worse than no test.
            float until = Time.time + def.AttackInterval * 10f + 2f;
            while (Time.time < until && health.IsAlive)
            {
                if (stun != null && stun.IsStunned) everStunned = true;
                yield return null;
            }

            float lost = before - health.Current;

            Debug.Log($"[NativeTest] a {def.Id} at {range:0.0}m: {fired} dart(s), {landed} hit, "
                      + $"{lost:0} health and {(everStunned ? "a" : "no")} stun.");

            // The spread is a real miss chance, so the distance has to be the one the tuning assumes
            // or the hit count means nothing.
            Check($"the {def.Id} stands where the test put it ({range:0.0}m of 8)",
                  Mathf.Abs(range - 8f) < 1.5f);

            Check($"a {def.Id} fires ({fired} darts)", fired > 0);
            Check($"a {def.Id} hits from range ({landed} of {fired})", landed > 0);
            Check($"a {def.Id} barely hurts ({lost:0} health over {fired} darts)",
                  lost <= def.AttackDamage * fired + 0.01f);

            // The whole reason the role exists. Nine damage is nothing; being held still next to two
            // spearmen is the thing that kills you.
            Check($"a {def.Id}'s dart stuns", everStunned);

            Remove(native);
            Reset(health, stun);
        }

        // ---------------------------------------------------------------- the camp

        /// <summary>
        /// The shout. Two natives turned away at noon - so neither can see anything - and a third
        /// with the player standing on it. The two that saw nothing have to come looking, and they
        /// have to come looking at the spot where the player *was*, not wherever the player is now.
        /// </summary>
        IEnumerator Shouts(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                           StunState stun)
        {
            // Noon deliberately: at midnight the listeners would notice on their own and the test
            // would pass without the alarm ever firing.
            WorldClock.Freeze(Noon);
            yield return null;

            Reset(health, stun);

            Vector3 where = motor.transform.position;

            Native shouter = Place(spawner, def, Ahead(motor, 2.5f), motor, facing: true);
            if (shouter == null)
            {
                Check($"a {def.Id} can be placed to shout", false);
                yield break;
            }

            // Inside the alarm radius, outside the daylight notice radius, and facing away, so the
            // only thing that can wake them is the shout.
            float out1 = Mathf.Min(def.AlarmRadius * 0.6f, def.DayNotice + 6f);
            float out2 = Mathf.Min(def.AlarmRadius * 0.85f, def.DayNotice + 10f);

            // Mobile, unlike everywhere else here: Investigate is a walk to a place, and a native
            // with nowhere to walk falls straight back to Idle without the state ever being visible.
            Native a = Place(spawner, def, Beside(motor, out1, 90f), motor, facing: false, mobile: true);
            Native b = Place(spawner, def, Beside(motor, out2, -90f), motor, facing: false, mobile: true);

            if (a == null || b == null)
            {
                Check($"two more {def.Id} can be placed to hear it", false);
                Remove(shouter);
                Remove(a);
                Remove(b);
                yield break;
            }

            yield return Until(() => shouter.Target != null, 3f);

            Check($"the {def.Id} standing on the player notices", shouter.Target != null);

            yield return new WaitForSeconds(1f);

            int woken = new[] { a, b }.Count(n => n.State == NativeState.Investigate
                                                  || n.State == NativeState.Chase
                                                  || n.State == NativeState.Attack);

            float missA = Vector3.Distance(a.Suspect, where);

            Debug.Log($"[NativeTest] one {def.Id} shouted at noon: {woken} of 2 out of earshot came "
                      + $"looking, within {missA:0.0}m of where the player actually was.");

            Check($"a camp answers a shout ({woken} of 2)", woken == 2);
            Check("the listeners knew where to look", a.HasSuspect && b.HasSuspect);

            // Where the player *was*. A native that walks to your live position is a native reading
            // your mind, and that is the difference between a camp that reacts and one that cheats.
            Check($"they head for where the player was, not where they are ({missA:0.0}m)", missA < 3f);

            Remove(shouter);
            Remove(a);
            Remove(b);
        }

        /// <summary>A losing native runs for camp instead of dying where it stands.</summary>
        IEnumerator Breaks(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                           StunState stun)
        {
            WorldClock.Freeze(Midnight);
            yield return null;

            Reset(health, stun);

            // Its camp is put forty metres off and the agent is left on, because breaking and running
            // is a run: a native whose camp is under its feet has arrived before it has started, and
            // one that cannot path anywhere drops back to Idle instead.
            Vector3 spot = Ahead(motor, 6f);
            Native native = Place(spawner, def, spot, motor, facing: true, mobile: true,
                                  camp: Beside(motor, 40f, 140f));

            if (native == null)
            {
                Check($"a {def.Id} can be placed to break", false);
                yield break;
            }

            var body = native.GetComponent<Health>();

            yield return Until(() => native.Target != null, 3f);

            // Down to just under the threshold in one blow, from the player, so the retaliation path
            // and the flee path are both exercised by the same hit.
            float leave = body.Max * def.FleeHealth * 0.9f;

            body.TakeDamage(new DamageInfo(body.Current - leave, DamageType.Blunt, Vector3.zero,
                                           native.transform.position, 0f, motor.NetworkObject.ObjectId));

            yield return Settled();

            Debug.Log($"[NativeTest] a {def.Id} at {body.Current:0}/{body.Max:0} hp "
                      + $"(breaks under {def.FleeHealth * 100f:0}%) is {native.State}.");

            Check($"a beaten {def.Id} runs ({native.State})", native.State == NativeState.Flee);
            Check($"a fleeing {def.Id} stops hunting", native.Target == null);
            Check($"a fleeing {def.Id} is still alive ({body.Current:0} hp)", body.IsAlive);

            Remove(native);
            Reset(health, stun);
        }

        /// <summary>A body leaves what it was carrying on the ground, where four people can argue over it.</summary>
        IEnumerator Drops(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                          StunState stun, ItemCatalog items)
        {
            WorldClock.Freeze(Midnight);
            yield return null;

            Reset(health, stun);

            Native native = Place(spawner, def, Ahead(motor, 6f), motor, facing: true);
            if (native == null)
            {
                Check($"a {def.Id} can be placed to kill", false);
                yield break;
            }

            var body = native.GetComponent<Health>();
            var dropped = new List<ItemStack>();

            native.Looted += (_, stacks) => dropped.AddRange(stacks);

            Vector3 corpse = native.transform.position;
            var already = new HashSet<WorldItem>(FindObjectsByType<WorldItem>(FindObjectsSortMode.None));

            body.TakeDamage(new DamageInfo(body.Max * 2f, DamageType.Blunt, Vector3.zero, corpse, 0f,
                                           motor.NetworkObject.ObjectId));

            yield return Settled();

            Check($"a {def.Id} dies outright rather than going down ({body.State})",
                  body.IsDead && native.State == NativeState.Dead);

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

            WorldItem[] fresh = FindObjectsByType<WorldItem>(FindObjectsSortMode.None)
                                .Where(w => w != null && !already.Contains(w)
                                            && Vector3.Distance(w.transform.position, corpse) < 6f)
                                .ToArray();

            Debug.Log($"[NativeTest] a dead {def.Id} left {Describe(dropped)}; "
                      + $"{fresh.Length} of them are on the ground.");

            Check($"the loot lands on the ground ({fresh.Length} of {dropped.Count})",
                  fresh.Length >= dropped.Count);

            Reset(health, stun);
        }

        // ---------------------------------------------------------------- plumbing

        /// <summary>
        /// Puts a native exactly where the test wants it, rather than where a NavMesh sample felt
        /// like putting it, and points it at or away from the player.
        ///
        /// The agent is switched off first. A test about senses is not a test about pathing, and an
        /// enabled agent would drag the body off the mark being measured within a frame.
        /// </summary>
        Native Place(NativeSpawner spawner, NativeDef def, Vector3 spot, PlayerMotor motor, bool facing,
                     bool mobile = false, Vector3? camp = null)
        {
            // A body that will not be walking anywhere does not need to stand on the NavMesh, and
            // asking for the nearest navigable point drags it as much as twenty metres sideways -
            // which is fatal to a test whose whole output is a distance.
            spot = mobile ? Ground(spot) : Stand(spot);

            Native native = spawner.ServerSpawn(def, spot, camp.HasValue ? Ground(camp.Value) : spot);
            if (native == null) return null;

            var agent = native.GetComponent<NavMeshAgent>();

            if (agent == null || !mobile)
            {
                if (agent != null) agent.enabled = false;
                native.transform.position = spot;
            }
            else
            {
                // Warp rather than an assignment: an enabled agent owns the transform, and writing
                // to it behind the agent's back leaves the two disagreeing about where the body is.
                agent.Warp(spot);
            }

            Vector3 to = motor.transform.position - spot;
            to.y = 0f;

            if (to.sqrMagnitude > 0.001f)
                native.transform.rotation = Quaternion.LookRotation(facing ? to.normalized : -to.normalized);

            return native;
        }

        /// <summary>The nearest standable point. A body floating over a hill sees over it.</summary>
        static Vector3 Ground(Vector3 spot)
            => NavMesh.SamplePosition(spot, out NavMeshHit hit, 25f, NavMesh.AllAreas) ? hit.position : spot;

        /// <summary>
        /// The ground directly under a spot, which is what an immobile body wants: straight down
        /// keeps the x and z the test asked for, and the test's numbers are all distances in x and z.
        /// </summary>
        static Vector3 Stand(Vector3 spot)
            => Physics.Raycast(spot + Vector3.up * 60f, Vector3.down, out RaycastHit hit, 200f,
                               ~0, QueryTriggerInteraction.Ignore)
                   ? hit.point + Vector3.up * 0.05f
                   : Ground(spot);

        /// <summary>A slab of world between two points, for proving that terrain is cover.</summary>
        static GameObject Wall(Vector3 from, Vector3 to)
        {
            Vector3 middle = (from + to) * 0.5f;
            Vector3 along = to - from;
            along.y = 0f;

            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "NativeTestWall";
            wall.transform.position = middle + Vector3.up * 2f;
            wall.transform.rotation = along.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(along.normalized)
                : Quaternion.identity;

            wall.transform.localScale = new Vector3(14f, 8f, 0.5f);

            return wall;
        }

        static Vector3 Ahead(PlayerMotor motor, float metres) => Beside(motor, metres, 0f);

        static Vector3 Beside(PlayerMotor motor, float metres, float degrees)
        {
            Vector3 forward = motor.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;

            Vector3 direction = Quaternion.Euler(0f, degrees, 0f) * forward.normalized;

            return motor.transform.position + direction * metres;
        }

        static void Remove(Native native)
        {
            if (native == null || native.NetworkObject == null || !native.NetworkObject.IsSpawned) return;

            InstanceFinder.ServerManager.Despawn(native.NetworkObject);
        }

        static IEnumerator Until(System.Func<bool> done, float seconds)
        {
            float deadline = Time.time + seconds;
            while (Time.time < deadline && !done()) yield return null;
        }

        static string Describe(List<ItemStack> stacks)
            => stacks.Count == 0
                ? "nothing"
                : string.Join(", ", stacks.Select(s => $"{s.Count}x {(s.Def != null ? s.Def.Id : "?")}"));

        /// <summary>Alive, healed and unstunned. Every measurement starts from the same player.</summary>
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
            string line = $"[NativeTest] {_passed} passed, {_failed} failed.";

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
            Debug.LogError($"[NativeTest] FAILED: {what}.");
        }
    }
}
