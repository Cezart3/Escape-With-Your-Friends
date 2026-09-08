using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Player;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Items
{
    /// <summary>
    /// The acceptance test for #54, run inside a real session. Server side, behind <c>-fishTest</c>.
    ///
    /// The criterion is three adjectives - "relaxing, slightly stupid, and profitable" - and an
    /// adjective is not a test, so each one is turned into something that can fail on its own.
    ///
    /// 1. **Relaxing.** Nothing in the loop may be a reflex check. Every hook window is long enough to
    ///    react to, every fight is winnable by the rhythm the tuning claims, and - the part that
    ///    actually decides it - **holding the button is a losing strategy on a big fish and a winning
    ///    one on a small fish**. Both are played out for real here, on the server, at real time: the
    ///    sardine is landed with one unbroken pull and the tuna snaps the line, and the same tuna is
    ///    then landed by reeling in the gaps between its runs. If those three ever agree with each
    ///    other, the minigame has stopped being a minigame.
    /// 2. **Slightly stupid.** A quarter of the table has to be rubbish, and the rubbish has to be
    ///    genuinely worthless rather than a small prize - a boot the trader pays one coin for is a
    ///    consolation, and a boot they pay nothing for is a joke.
    /// 3. **Profitable.** Measured per kilogram, like the hunt in #53, because twenty slots and forty
    ///    kilos mean weight is the real constraint on the walk to the counter. Fish has to beat scrap
    ///    metal, cooking has to beat not cooking, and one cast has to be worth more than the fifteen
    ///    seconds it takes. The last one is arithmetic over the whole table and it is printed in full.
    ///
    /// Underneath all three is the chain that could be silently fake at every link: a cast that finds
    /// water, a bite that arrives, a strike that hooks, a fight that resolves, an item that lands in
    /// the bag, and a trader who pays for it. Every one of those is exercised.
    /// </summary>
    public class FishTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;

        /// <summary>Rolls used to check the table's odds are the odds it advertises.</summary>
        const int Rolls = 20000;

        /// <summary>How far an observed frequency may drift from the advertised one.</summary>
        const float RollTolerance = 0.02f;

        /// <summary>Seconds of grace on top of a species' own longest wait before a bite is late.</summary>
        const float BiteGrace = 3f;

        /// <summary>Seconds of grace on top of a perfect fight before it counts as unwinnable.</summary>
        const float FightGrace = 8f;

        /// <summary>The money source fishing has to beat, same baseline as the hunt in #53.</summary>
        const string Baseline = "scrap_metal";

        /// <summary>
        /// How far ahead of itself the rhythm looks. A player watching a fish sees the run start; a
        /// coroutine counting its own seconds is up to a frame out of step with the server's fight
        /// clock, so it lets go slightly early rather than slightly late.
        /// </summary>
        const float Lookahead = 0.25f;

        static bool _started;

        int _passed;
        int _failed;

        // Filled by the events rather than polled: a catch and a loss both happen and are immediately
        // over, so there is no frame in which polling the state would see either one.
        FishDef _caught;
        int _caughtCount;
        string _lost;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-fishTest")) return;

            _started = true;

            var go = new GameObject("FishTest");
            DontDestroyOnLoad(go);
            go.AddComponent<FishTest>();
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
                Debug.LogError("[FishTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            var fishing = motor.GetComponent<Fishing>();
            var bag = motor.GetComponent<Inventory>();
            var wallet = motor.GetComponent<Wallet>();

            if (fishing == null || bag == null || wallet == null)
            {
                Debug.LogError("[FishTest] The player has no Fishing, Inventory or Wallet; run "
                               + "PlayerPrefabBuilder.BuildPlayerPrefab.");
                yield break;
            }

            FishCatalog fish = fishing.Catalog;
            ItemCatalog items = bag.Catalog;

            if (fish == null || items == null)
            {
                Debug.LogError("[FishTest] No fish or item catalog. Run FishFactory.Build and "
                               + "ItemFactory.Build.");
                yield break;
            }

            // ---------------------------------------------------------------- the table

            Data(fish, items);
            Odds(fish);
            Relaxing(fish);
            Stupid(fish);

            ShopCounter counter = FindObjectsByType<ShopCounter>(FindObjectsSortMode.None)
                                  .FirstOrDefault(c => c != null && c.IsSpawned && c.Shop != null);

            if (counter != null) Economics(fish, items, counter.Shop);
            else Debug.LogWarning("[FishTest] No trader in this scene, so 'profitable' was checked in "
                                  + "item value only. Run with -scene island for the real prices.");

            // ---------------------------------------------------------------- the water

            fishing.Caught += OnCaught;
            fishing.Lost += OnLost;

            ItemDef rod = fish.Rod;

            if (rod == null)
            {
                Debug.LogError("[FishTest] The catalog has no rod, so nothing can open the minigame.");
                Report();
                yield break;
            }

            bag.ServerClear();
            bag.Add(rod, 1);
            SelectItem(bag, rod);

            yield return Settled();

            Check("holding the rod is what makes Attack mean 'cast'", fishing.HasRod);

            yield return Shoreline(motor, fishing);

            // ---------------------------------------------------------------- the fights

            yield return Fight(fishing, bag, fish.Find("sardine"),
                               rhythm: true, expect: true,
                               why: "a sardine lands in one unbroken pull");

            yield return Fight(fishing, bag, fish.Find("boot"),
                               rhythm: false, expect: true,
                               why: "a boot does not fight, so holding the button lands it");

            yield return Fight(fishing, bag, fish.Find("tuna"),
                               rhythm: false, expect: false,
                               why: "a tuna snaps the line if you just hold the button");

            yield return Fight(fishing, bag, fish.Find("tuna"),
                               rhythm: true, expect: true,
                               why: "the same tuna lands if you reel between its runs");

            yield return Fight(fishing, bag, fish.Find("oyster"),
                               rhythm: true, expect: true,
                               why: "the rarest row is winnable, or the payoff is a lie");

            yield return Missed(fishing, fish.Find("boot"));

            // ---------------------------------------------------------------- the money

            if (counter != null) yield return Sale(motor, bag, wallet, counter, fish);

            fishing.Caught -= OnCaught;
            fishing.Lost -= OnLost;

            Report();
        }

        // ---------------------------------------------------------------- the table

        /// <summary>Catalog doctrine, the same seven checks every catalog in the game has passed.</summary>
        void Data(FishCatalog fish, ItemCatalog items)
        {
            Check($"there is something in the sea ({fish.Count} row(s))", fish.Count > 0);
            Check("index 0 is nothing", fish.At(0) == null);
            Check("an unknown definition indexes to 0", fish.IndexOf(null) == 0);

            string previous = null;

            foreach (FishDef def in fish.Fish)
            {
                if (def == null)
                {
                    Check("no holes in the catalog", false);
                    continue;
                }

                ushort index = fish.IndexOf(def);

                Check($"{def.Id} round-trips through its wire index ({index})", fish.At(index) == def);
                Check($"{def.Id} is findable by id", fish.Find(def.Id) == def);
                Check($"{def.Id} sorts after {previous ?? "the start"}",
                      previous == null || string.CompareOrdinal(previous, def.Id) < 0);

                previous = def.Id;

                Check($"{def.Id} can actually come up (rarity {def.Rarity})", def.Rarity > 0);
                Check($"{def.Id} turns into something", def.Catch != null);
                Check($"{def.Id} gives at least one of it ({def.Low}-{def.High})", def.Low >= 1);
                Check($"{def.Id}'s catch is a real item",
                      def.Catch == null || items.IndexOf(def.Catch) != 0);
            }

            ItemDef rod = fish.Rod;

            Check("there is a rod", rod != null);
            Check("the rod is a real item", rod == null || items.IndexOf(rod) != 0);
        }

        /// <summary>
        /// The table's odds are the odds it prints. Rolled rather than reasoned about: the weighted
        /// pick walks a running total, and an off-by-one there would bias the last row in a way no
        /// amount of reading the method catches.
        /// </summary>
        void Odds(FishCatalog fish)
        {
            var seen = new Dictionary<FishDef, int>();
            int nulls = 0;

            for (int i = 0; i < Rolls; i++)
            {
                FishDef rolled = fish.Roll(Random.value);

                if (rolled == null)
                {
                    nulls++;
                    continue;
                }

                seen.TryGetValue(rolled, out int count);
                seen[rolled] = count + 1;
            }

            Check($"every roll lands on something ({nulls} miss(es))", nulls == 0);
            Check("the extremes are covered", fish.Roll(0f) != null && fish.Roll(1f) != null);

            foreach (FishDef def in fish.Fish)
            {
                if (def == null) continue;

                seen.TryGetValue(def, out int count);

                float observed = count / (float)Rolls;
                float advertised = fish.ChanceOf(def);

                Debug.Log($"[FishTest]   {def.Id,-8} rolled {observed * 100f,5:0.0}%, "
                          + $"table says {advertised * 100f,5:0.0}%");

                Check($"{def.Id} comes up as often as the table says "
                      + $"({observed * 100f:0.0}% vs {advertised * 100f:0.0}%)",
                      Mathf.Abs(observed - advertised) <= RollTolerance);
            }
        }

        /// <summary>
        /// Nothing here may be a reflex check, and nothing may be unwinnable. Both of those are
        /// properties of the numbers rather than of the play, so they are read straight off the table.
        /// </summary>
        void Relaxing(FishCatalog fish)
        {
            bool anyRestful = false;
            float longestWait = 0f;

            foreach (FishDef def in fish.Fish)
            {
                if (def == null) continue;

                Check($"{def.Id} gives you time to strike ({def.HookSeconds:0.0}s)",
                      def.HookSeconds >= 0.8f);

                Check($"{def.Id}'s wait is a wait and not a delay ({def.BiteSeconds.x:0.0}s min)",
                      def.BiteSeconds.x >= 0.5f);

                Check($"{def.Id} can be beaten by perfect play ({def.PerfectFightSeconds:0.0}s)",
                      def.PerfectFightSeconds <= 20f);

                Check($"{def.Id}'s fight is long enough to be a fight ({def.PerfectFightSeconds:0.0}s)",
                      def.PerfectFightSeconds >= 0.5f);

                Check($"{def.Id} rests between runs ({def.RunSeconds:0.0}s of {def.StruggleSeconds:0.0}s)",
                      def.RunSeconds < def.StruggleSeconds);

                if (def.RunSeconds <= 0f) anyRestful = true;

                longestWait = Mathf.Max(longestWait, def.BiteSeconds.y);
            }

            Check("something in the sea does not fight at all", anyRestful);
            Check($"the longest wait is a wait and not a punishment ({longestWait:0.0}s)",
                  longestWait <= 20f);
        }

        /// <summary>A quarter of the table is rubbish, and the rubbish is worth nothing.</summary>
        void Stupid(FishCatalog fish)
        {
            float junk = 0f;

            foreach (FishDef def in fish.Fish)
            {
                if (def == null || def.Catch == null) continue;

                // Rubbish, defined by what it is worth rather than by a flag on the asset: anything
                // whose whole catch is worth less than a single unit of raw fish is a joke.
                if (def.ExpectedValue <= 5f) junk += fish.ChanceOf(def);
            }

            Debug.Log($"[FishTest] {junk * 100f:0.0}% of casts pull up rubbish.");

            Check($"a decent share of the sea is rubbish ({junk * 100f:0.0}%)", junk >= 0.2f);
            Check($"it is not mostly rubbish ({junk * 100f:0.0}%)", junk <= 0.45f);
        }

        /// <summary>
        /// Profitable, measured the way #53 established: per kilogram, against scrap metal, with the
        /// cooking step priced separately because the walk back to the fire has to be worth making.
        /// </summary>
        void Economics(FishCatalog fish, ItemCatalog items, ShopDef shop)
        {
            ItemDef scrap = items.Find(Baseline);
            ItemDef raw = items.Find("fish_raw");
            ItemDef cooked = items.Find("fish_cooked");
            ItemDef boot = items.Find("boot");

            if (scrap == null || raw == null || cooked == null)
            {
                Check("the economy has scrap and fish to compare", false);
                return;
            }

            float scrapPerKg = shop.PriceFor(scrap) / Mathf.Max(0.01f, scrap.Weight);
            float rawPerKg = shop.PriceFor(raw) / Mathf.Max(0.01f, raw.Weight);
            float cookedPerKg = shop.PriceFor(cooked) / Mathf.Max(0.01f, cooked.Weight);

            Debug.Log($"[FishTest] per kilogram at the counter: scrap {scrapPerKg:0.0}, "
                      + $"raw fish {rawPerKg:0.0}, cooked fish {cookedPerKg:0.0}.");

            Check($"raw fish beats scrap by the kilo ({rawPerKg:0.0} vs {scrapPerKg:0.0} c/kg)",
                  rawPerKg > scrapPerKg);

            Check($"cooking is worth the walk ({cookedPerKg:0.0} vs {rawPerKg:0.0} c/kg)",
                  cookedPerKg > rawPerKg * 1.5f);

            if (boot != null)
                Check($"a boot is worth nothing, which is the joke ({shop.PriceFor(boot)})",
                      shop.PriceFor(boot) == 0);

            // What a cast is actually worth, in coins rather than in item value, and what that comes
            // to per minute once the wait and the fight are paid for.
            float coins = 0f;
            float seconds = 0f;

            foreach (FishDef def in fish.Fish)
            {
                if (def == null || def.Catch == null) continue;

                float chance = fish.ChanceOf(def);
                float wait = (def.BiteSeconds.x + def.BiteSeconds.y) * 0.5f;

                coins += chance * def.Expected * shop.PriceFor(def.Catch);
                seconds += chance * (wait + def.PerfectFightSeconds + 1.5f);

                Debug.Log($"[FishTest]   {def.Id,-8} {chance * 100f,5:0.0}%  "
                          + $"{def.Expected,4:0.0}x {def.Catch.Id,-12} "
                          + $"{def.Expected * shop.PriceFor(def.Catch),6:0.0}c  "
                          + $"in {wait + def.PerfectFightSeconds:0.0}s");
            }

            float perMinute = seconds > 0f ? coins / seconds * 60f : 0f;

            Debug.Log($"[FishTest] a cast is worth {coins:0.0}c and takes {seconds:0.0}s: "
                      + $"{perMinute:0} coins per minute of fishing.");

            Check($"a cast is worth casting ({coins:0.0}c)", coins >= 4f);
            Check($"a cast is not a career in itself ({seconds:0.0}s)", seconds <= 30f);
            Check($"fishing pays a real wage ({perMinute:0} c/min)", perMinute >= 20f);
        }

        // ---------------------------------------------------------------- the water

        /// <summary>
        /// Finds somewhere on this island you can actually fish from, stands there, and casts for
        /// real - through <see cref="Fishing.RequestAttack"/>, so the owner-to-server chain that the
        /// keyboard uses is the chain under test rather than the server method underneath it.
        ///
        /// Skipped with a warning rather than a failure in a scene with no terrain: the arena has no
        /// sea, and a test that fails because the map is the wrong map is noise.
        /// </summary>
        IEnumerator Shoreline(PlayerMotor motor, Fishing fishing)
        {
            Vector3 start = motor.transform.position;

            if (!FindShore(start, motor, fishing, out Vector3 stand, out Vector3 aim, out Vector3 water))
            {
                Debug.LogWarning("[FishTest] No fishable shoreline near the spawn, so the cast itself "
                                 + "was not checked. Run with -scene island.");
                yield break;
            }

            Debug.Log($"[FishTest] fishing from {stand:F1} at water {water:F1}, "
                      + $"{Vector3.Distance(stand, water):0.0}m out.");

            // A headless host has no mouse, so the rig that normally points the head from the camera
            // pitch would hold it dead level and every cast would come back "aim at the water". It is
            // switched off and the head is aimed by hand, which is the only way the *keyboard* path -
            // Attack key to owner RPC to server - can be tested at all rather than skipped in favour
            // of calling the server method underneath it.
            var rig = motor.GetComponent<PlayerCameraRig>();
            if (rig != null) rig.enabled = false;

            Transform head = motor.transform.Find("AimOrigin");
            if (head != null) head.rotation = Quaternion.LookRotation(aim);

            Check("the body has a head to aim with", head != null);

            // Refusals first, while the line is still in. Each one is a different mistake with a
            // different fix, and a single "you cannot fish here" would tell a player none of that.
            Check("aiming at the sky is not a cast",
                  !fishing.ServerWaterHit(Vector3.up, out _, out string skyWhy) && skyWhy != null);

            Debug.Log($"[FishTest] aiming up says: {skyWhy}.");

            bool cast = fishing.RequestAttack();
            yield return Settled();

            Check("the Attack key casts, all the way from the owner to the server",
                  cast && fishing.State == FishingState.Waiting);
            Check("the float lands on the water",
                  Mathf.Abs(fishing.Bobber.y - World.WaterSurface.SeaLevel) < 0.01f);

            Check("a second press does not cast twice",
                  !fishing.ServerTryCast(aim, out string busyWhy) && busyWhy != null);

            Debug.Log($"[FishTest] casting twice says: {busyWhy}.");

            // The same key again, on a float nothing has taken. Second press winds it in, which is
            // the whole of what a player does with a spot that is not producing.
            fishing.RequestAttack();
            yield return Settled();

            Check("a second press winds an empty line back in", fishing.State == FishingState.Idle);

            if (rig != null) rig.enabled = true;
        }

        /// <summary>
        /// Walks outward from the spawn in every direction until it finds water deep enough to fish
        /// and dry land close enough to fish it from.
        ///
        /// The oracle is <see cref="Fishing.ServerWaterHit"/> itself rather than a second copy of its
        /// rules: the body is teleported to each candidate and the real check is asked. That costs a
        /// few teleports and means the test cannot drift away from the thing it is testing.
        /// </summary>
        bool FindShore(Vector3 start, PlayerMotor motor, Fishing fishing,
                       out Vector3 stand, out Vector3 aim, out Vector3 water)
        {
            stand = start;
            aim = Vector3.forward;
            water = default;

            const float Step = 4f;
            const float Reach = 600f;
            const int Spokes = 24;

            for (int spoke = 0; spoke < Spokes; spoke++)
            {
                float angle = spoke * (360f / Spokes);
                Vector3 direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;

                Vector3 land = start;
                bool hasLand = false;

                for (float distance = 0f; distance < Reach; distance += Step)
                {
                    Vector3 probe = start + direction * distance;

                    if (!Ground(probe, out float height)) break;

                    if (height > World.WaterSurface.SeaLevel + 0.4f)
                    {
                        land = new Vector3(probe.x, height, probe.z);
                        hasLand = true;
                        continue;
                    }

                    // Below the waterline. Keep walking out until it is deep enough to hold a fish,
                    // then ask the real check whether the last dry point can reach it.
                    if (!hasLand) continue;
                    if (height > World.WaterSurface.SeaLevel - 3.5f) continue;

                    var target = new Vector3(probe.x, World.WaterSurface.SeaLevel, probe.z);

                    if (Vector3.Distance(land, target) > 26f) break;

                    float yaw = Quaternion.LookRotation(target - land).eulerAngles.y;
                    motor.ServerTeleport(land + Vector3.up * 0.2f, yaw);

                    Vector3 eye = motor.transform.position + Vector3.up * 1.55f;
                    Vector3 look = (target - eye).normalized;

                    if (!fishing.ServerWaterHit(look, out Vector3 point, out string why))
                    {
                        if (Application.isBatchMode)
                            Debug.Log($"[FishTest] {land:F0} -> {target:F0} refused: {why}.");

                        break;
                    }

                    stand = motor.transform.position;
                    aim = look;
                    water = point;

                    return true;
                }
            }

            motor.ServerTeleport(start, motor.transform.eulerAngles.y);
            return false;
        }

        /// <summary>Ground height under a point, or false where there is no ground at all.</summary>
        static bool Ground(Vector3 at, out float height)
        {
            height = 0f;

            var from = new Vector3(at.x, 400f, at.z);

            if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit, 800f, ~0,
                                 QueryTriggerInteraction.Ignore))
                return false;

            height = hit.point.y;
            return true;
        }

        // ---------------------------------------------------------------- the fights

        /// <summary>
        /// One whole cast, played out at real time on the server with a decided species.
        ///
        /// <paramref name="rhythm"/> is the difference between the two ways of playing: true reels
        /// through the calm and lets go through the runs, false holds the button down from the strike
        /// to whatever happens. <paramref name="expect"/> is which of those is supposed to work.
        /// </summary>
        IEnumerator Fight(Fishing fishing, Inventory bag, FishDef def,
                          bool rhythm, bool expect, string why)
        {
            if (def == null)
            {
                Check($"the table has the species this checks ({why})", false);
                yield break;
            }

            _caught = null;
            _caughtCount = 0;
            _lost = null;

            int before = def.Catch != null ? bag.CountOf(def.Catch) : 0;

            // Straight down at the angler's own feet rather than out in front. A forced cast skips
            // the water check entirely, so the only thing the point still decides is the leash - and
            // a float directly below you cannot be walked away from wherever this scene put you.
            if (!fishing.ServerCastAt(fishing.transform.position, def, out string castWhy))
            {
                Check($"a {def.Id} can be cast for ({castWhy})", false);
                yield break;
            }

            // ------------------------------------------------------------ the wait

            float waited = 0f;
            float patience = def.BiteSeconds.y + BiteGrace;

            while (fishing.State == FishingState.Waiting && waited < patience)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Check($"a {def.Id} bites within its own window ({waited:0.0}s of {def.BiteSeconds.y:0.0}s)",
                  fishing.State == FishingState.Biting);

            if (fishing.State != FishingState.Biting) yield break;

            // ------------------------------------------------------------ the strike

            Check($"striking a {def.Id} sets the hook", fishing.ServerStrike());
            Check($"a hooked {def.Id} is a fight", fishing.State == FishingState.Fighting);
            Check($"the fight starts with a full line ({fishing.Line:0.00})", fishing.Line > 0.99f);
            Check($"the fight starts slack ({fishing.Tension:0.00})", fishing.Tension < 0.01f);
            Check("everybody can see what is on the line", fishing.Hooked == def);

            // ------------------------------------------------------------ the fight

            float fought = 0f;
            float budget = def.PerfectFightSeconds + FightGrace;
            float peakTension = 0f;
            float lineWhenLost = 1f;

            while (fishing.State == FishingState.Fighting && fought < budget)
            {
                // The rhythm, played the way the tuning says it should be: pull while it is calm,
                // rest while it runs. The lookahead is the coroutine admitting it is a frame behind
                // the server's own fight clock.
                bool reel = !rhythm || !(def.Running(fought) || def.Running(fought + Lookahead));

                fishing.ServerReel(reel);

                peakTension = Mathf.Max(peakTension, fishing.Tension);
                lineWhenLost = fishing.Line;

                fought += Time.deltaTime;
                yield return null;
            }

            fishing.ServerReel(false);
            yield return Settled();

            bool landed = _caught != null;

            Debug.Log($"[FishTest] {def.Id} {(rhythm ? "with the rhythm" : "held down")}: "
                      + $"{(landed ? $"landed {_caughtCount}x" : $"lost - {_lost}")} after {fought:0.0}s, "
                      + $"peak tension {peakTension:0.00}.");

            Check(why, landed == expect);
            Check($"the rod is free again after a {def.Id}", fishing.State == FishingState.Idle);

            if (expect)
            {
                Check($"a landed {def.Id} is the {def.Id} that was hooked", _caught == def);
                Check($"a landed {def.Id} is worth having ({_caughtCount})",
                      _caughtCount >= def.Low && _caughtCount <= def.High);

                if (def.Catch != null)
                    Check($"a {def.Id} arrives in the bag as {def.Catch.Id}",
                          bag.CountOf(def.Catch) == before + _caughtCount);

                Check($"landing a {def.Id} inside the budget ({fought:0.0}s of {budget:0.0}s)",
                      fought < budget);
            }
            else
            {
                Check($"losing a {def.Id} says why ({_lost})", !string.IsNullOrEmpty(_lost));
                Check($"a {def.Id} lost to the line is lost with line still out ({lineWhenLost:0.00})",
                      lineWhenLost > 0.4f);
                Check($"nothing arrives in the bag from a lost {def.Id}",
                      def.Catch == null || bag.CountOf(def.Catch) == before);
            }
        }

        /// <summary>
        /// The other way to lose: doing nothing at all. The hook window has to close on its own, or
        /// the bite is a prompt that waits forever and "strike" is not a verb.
        /// </summary>
        IEnumerator Missed(Fishing fishing, FishDef def)
        {
            if (def == null) yield break;

            _caught = null;
            _lost = null;

            if (!fishing.ServerCastAt(fishing.transform.position, def, out string _))
                yield break;

            float waited = 0f;
            float patience = def.BiteSeconds.y + BiteGrace;

            while (fishing.State == FishingState.Waiting && waited < patience)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            if (fishing.State != FishingState.Biting)
            {
                Check("a missed strike can be tested at all", false);
                yield break;
            }

            yield return new WaitForSeconds(def.HookSeconds + 0.6f);

            Debug.Log($"[FishTest] ignoring the bite: {_lost ?? "nothing happened"}.");

            Check("ignoring the bite loses it", fishing.State == FishingState.Idle && _caught == null);
            Check($"and it says so ({_lost})", !string.IsNullOrEmpty(_lost));
        }

        // ---------------------------------------------------------------- the money

        /// <summary>
        /// Everything caught so far, carried to the counter and sold. The last link in the chain, and
        /// the only one that turns "fishing works" into "fishing is worth doing".
        /// </summary>
        IEnumerator Sale(PlayerMotor motor, Inventory bag, Wallet wallet, ShopCounter counter,
                         FishCatalog fish)
        {
            ItemDef rod = fish.Rod;

            int predicted = 0;
            for (int slot = 0; slot < bag.SlotCount; slot++)
            {
                ItemStack stack = bag[slot];
                if (stack.IsEmpty || stack.Def == null || stack.Def == rod) continue;

                predicted += counter.Shop.PriceFor(stack.Def) * stack.Count;
            }

            motor.ServerTeleport(counter.transform.position + Vector3.up * 0.5f, 0f);
            wallet.ServerSetBalance(0);

            yield return Settled();

            int earned = 0;
            for (int slot = 0; slot < bag.SlotCount; slot++)
            {
                ItemStack stack = bag[slot];
                if (stack.IsEmpty || stack.Def == rod) continue;

                earned += counter.ServerSell(bag, wallet, slot, int.MaxValue, out string why);
                if (why != null) Debug.Log($"[FishTest] slot {slot}: {why}.");
            }

            yield return Settled();

            Debug.Log($"[FishTest] the morning's catch sold for {earned}; the table said {predicted}.");

            Check($"the trader pays what the table promised ({earned} vs {predicted})",
                  earned == predicted);

            Check($"the money arrives in the wallet ({wallet.Balance})", wallet.Balance == earned);
            Check($"a morning of fishing is real money ({earned})", earned > 0);
            Check("the rod is not sold with the fish", rod == null || bag.CountOf(rod) == 1);
        }

        // ---------------------------------------------------------------- plumbing

        void OnCaught(FishDef def, int count)
        {
            _caught = def;
            _caughtCount = count;
        }

        void OnLost(string why) => _lost = why;

        /// <summary>Puts the item in a hotbar slot and selects it.</summary>
        static void SelectItem(Inventory bag, ItemDef def)
        {
            for (int slot = 0; slot < bag.SlotCount; slot++)
            {
                if (bag[slot].Def != def) continue;

                bag.ServerSelect(slot);
                return;
            }
        }

        /// <summary>One network tick of slack. A SyncVar written this frame is read the next one.</summary>
        static WaitForSeconds Settled() => new(0.3f);

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[FishTest] FAILED: {what}.");
        }

        void Report()
        {
            string line = $"[FishTest] {_passed} passed, {_failed} failed.";

            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }
    }
}
