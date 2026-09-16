using System.Collections;
using System.Linq;
using EscapeWithYourFriends.AI;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Net;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The acceptance test for #68, run inside a real session. Server side, behind
    /// <c>-island2Test</c>, and it wants <c>-scene island2</c> - there is nothing to measure on the
    /// first island and every check here would fail there, which is the point.
    ///
    /// The acceptance is "visibly and mechanically more hostile than island 1 within 30 seconds of
    /// landing", and none of those three words is assertable on its own. What is assertable is the
    /// arithmetic each one rests on, measured off the island that actually got baked rather than off
    /// the numbers that were asked for:
    ///
    /// * **Visibly** is the terrain and the weather. Half the size, most of it bare rock, almost no
    ///   sand, and fog the camera is actually rendering at twice the density of the shared climate -
    ///   read back from <c>RenderSettings</c>, not from the profile that asked for it, because a
    ///   scale nothing multiplies through is exactly the bug worth catching.
    /// * **Mechanically** is who lives there. The headhunter is compared against the spearman *in
    ///   the same catalog*, so there is no number in this file to go stale: if somebody makes the
    ///   spearman as tough as the headhunter, this fails, and it should.
    /// * **Within 30 seconds** is distance. The nearest camp to the beachhead, in metres, against
    ///   what a player covers in half a minute over rock.
    ///
    /// The one thing it cannot check is whether it *feels* worse. That is the playtest.
    /// </summary>
    public class Island2Test : MonoBehaviour
    {
        const float WaitForWorld = 90f;

        /// <summary>
        /// Metres a player covers in thirty seconds of the walking-and-looking that follows a
        /// landing. Deliberately not the 7.5 m/s sprint: nobody sprints into an island they have
        /// never seen, and a claim built on the sprint would be true of the first island too.
        /// </summary>
        const float ThirtySeconds = 150f;

        /// <summary>
        /// How far the nearest camp may be and still count as "within thirty seconds". The catalog
        /// asks for the village at 110-220m and the cave at 90-200m, so this is the worst placement
        /// those wishes allow; it fails if a future edit pushes the camps out of reach.
        /// </summary>
        const float NearestCampLimit = 220f;

        /// <summary>Grid of samples taken across the terrain to measure slope and cover.</summary>
        const int Samples = 96;

        /// <summary>exp(-(density * d)^2) = 0.02: where fog has swallowed 98% of what is behind it.</summary>
        const float Opaque = 1.978f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-island2Test")) return;

            _started = true;

            var go = new GameObject("Island2Test");
            DontDestroyOnLoad(go);
            go.AddComponent<Island2Test>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            Terrain terrain = null;
            float deadline = Time.time + WaitForWorld;

            while (Time.time < deadline
                   && (terrain == null || POISpawner.Instance == null || NativeSpawner.Instance == null))
            {
                terrain = FindFirstObjectByType<Terrain>();
                if (terrain == null || POISpawner.Instance == null || NativeSpawner.Instance == null)
                    yield return new WaitForSeconds(0.5f);
            }

            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogError("[Island2Test] No terrain in the scene. Run with -scene island2.");
                _failed++;
                Report();
                yield break;
            }

            if (!string.Equals(GameSceneLoader.Current, "Island2", System.StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError($"[Island2Test] This is '{GameSceneLoader.Current}', not the second "
                               + "island. Every check below measures the second island. Run with "
                               + "-scene island2.");
                _failed++;
                Report();
                yield break;
            }

            Shape(terrain);
            yield return null;

            Weather();
            yield return null;

            Who();
            yield return null;

            Worth();
            yield return null;

            Near();

            Report();
        }

        /// <summary>Half the island, and most of what is left standing on end.</summary>
        void Shape(Terrain terrain)
        {
            TerrainData data = terrain.terrainData;

            Check($"the island is 512m square, not 1024 (it is {data.size.x:0}x{data.size.z:0})",
                  Mathf.Abs(data.size.x - 512f) < 1f && Mathf.Abs(data.size.z - 512f) < 1f);

            int land = 0;
            float slopeSum = 0f;
            float sandSum = 0f;
            float rockSum = 0f;

            float[,,] alpha = data.GetAlphamaps(0, 0, data.alphamapWidth, data.alphamapHeight);
            int layers = alpha.GetLength(2);

            for (int i = 0; i < Samples; i++)
            {
                for (int j = 0; j < Samples; j++)
                {
                    float u = (i + 0.5f) / Samples;
                    float v = (j + 0.5f) / Samples;

                    // Interpolated height is normalised; the terrain object sits SeabedDepth below
                    // sea level, so anything above the water is a height above that offset.
                    float height = data.GetInterpolatedHeight(u, v) + terrain.transform.position.y;
                    if (height <= 0.5f) continue;

                    land++;
                    slopeSum += Vector3.Angle(data.GetInterpolatedNormal(u, v), Vector3.up);

                    int x = Mathf.Clamp(Mathf.RoundToInt(u * (data.alphamapWidth - 1)), 0, data.alphamapWidth - 1);
                    int z = Mathf.Clamp(Mathf.RoundToInt(v * (data.alphamapHeight - 1)), 0, data.alphamapHeight - 1);

                    // Layer order is the one TerrainGenerator paints in: sand, grass, rock, dirt.
                    sandSum += alpha[z, x, 0];
                    if (layers > 2) rockSum += alpha[z, x, 2];
                }
            }

            if (land == 0)
            {
                Debug.LogError("[Island2Test] No land above the waterline. The island is a sea.");
                _failed++;
                return;
            }

            float slope = slopeSum / land;
            float sand = sandSum / land;
            float rock = rockSum / land;

            Debug.Log($"[Island2Test] {land} of {Samples * Samples} samples are dry land: "
                      + $"{slope:0.0}° average slope, {sand * 100f:0}% sand, {rock * 100f:0}% bare rock.");

            // Twenty degrees is a hillside rather than a beach. The first island averages about
            // twelve; this one is the same noise with more height over less ground.
            Check($"the ground is steep on average ({slope:0.0}°, wanted 18° or more)", slope >= 18f);

            // BeachBand 1.6m and SandTop 1.2m: sand only where the water actually reaches.
            Check($"there is almost no beach ({sand * 100f:0}% sand, wanted 10% or less)", sand <= 0.10f);

            // RockSlope 0.45 and RockHeight 40m: bare from 24 degrees up, and from 40m up regardless.
            Check($"most of it is bare rock ({rock * 100f:0}%, wanted 30% or more)", rock >= 0.30f);
        }

        /// <summary>The fog the camera is rendering, not the fog the profile asked for.</summary>
        void Weather()
        {
            DayNightCycle cycle = FindFirstObjectByType<DayNightCycle>();
            if (cycle == null || cycle.Profile == null)
            {
                Debug.LogError("[Island2Test] No DayNightCycle with a profile; the weather is whatever "
                               + "the last scene left behind.");
                _failed++;
                return;
            }

            Check($"the island asks for thicker fog than the climate (scale {cycle.FogScale:0.00})",
                  cycle.FogScale >= 1.9f);

            float climate = Mathf.Max(0.00001f, cycle.Profile.FogDensity.Evaluate(cycle.TimeOfDay));
            float actual = RenderSettings.fogDensity;
            float ratio = actual / climate;

            Debug.Log($"[Island2Test] fog {actual:0.0000} against the climate's {climate:0.0000} "
                      + $"({ratio:0.00}x), so you can see about {Opaque / Mathf.Max(0.00001f, actual):0}m "
                      + $"instead of {Opaque / climate:0}m.");

            // The multiplier has to reach RenderSettings, which is the only place the camera reads.
            // Asserting the field on the component would pass on a build where nothing multiplies it.
            Check($"the fog the camera renders is scaled by it ({ratio:0.00}x, wanted {cycle.FogScale:0.00}x)",
                  Mathf.Abs(ratio - cycle.FogScale) < 0.1f);

            Check($"you cannot see the far side of the island ({Opaque / Mathf.Max(0.00001f, actual):0}m "
                  + "of sight against 512m of island)", Opaque / Mathf.Max(0.00001f, actual) < 512f);
        }

        /// <summary>Who is waiting, and how they compare to the people on the first island.</summary>
        void Who()
        {
            NativeSpawner spawner = NativeSpawner.Instance;
            var camps = spawner.Camps.Where(c => c != null && c.Role != null).ToList();

            int day = camps.Sum(c => c.Population);
            int night = camps.Sum(c => c.Wanted(1f));

            Debug.Log($"[Island2Test] {camps.Count} camp line(s): {day} by day, {night} at night, "
                      + $"roles {string.Join(", ", camps.Select(c => c.Role.Id).Distinct())}.");

            // The first island bakes five by day. More bodies on a quarter of the ground.
            Check($"there are more of them than on the first island ({day} by day, wanted 8 or more)",
                  day >= 8);

            NativeDef headhunter = camps.Select(c => c.Role).FirstOrDefault(r => r.Id == "headhunter");
            Check("a headhunter camp exists", headhunter != null);
            if (headhunter == null) return;

            NativeDef spearman = NativeCatalog.Active != null ? NativeCatalog.Active.Find("spearman") : null;
            if (spearman == null)
            {
                Debug.LogError("[Island2Test] No spearman in the catalog to compare against.");
                _failed++;
                return;
            }

            Debug.Log($"[Island2Test] headhunter {headhunter.MaxHealth:0}hp / {headhunter.AttackDamage:0} "
                      + $"damage / flees at {headhunter.FleeHealth:0.00}, against the spearman's "
                      + $"{spearman.MaxHealth:0}hp / {spearman.AttackDamage:0} / {spearman.FleeHealth:0.00}.");

            // Nothing here is a number typed into this file. If the spearman is ever made as tough as
            // the headhunter, these fail, which is the correct answer rather than a stale test.
            Check($"it takes more killing than a spearman ({headhunter.MaxHealth:0} vs {spearman.MaxHealth:0})",
                  headhunter.MaxHealth > spearman.MaxHealth * 1.5f);

            Check($"it hits harder than a spearman ({headhunter.AttackDamage:0} vs {spearman.AttackDamage:0})",
                  headhunter.AttackDamage > spearman.AttackDamage * 1.4f);

            Check("it does not run away when hurt", headhunter.FleeHealth <= 0.01f);

            Check($"it sees further than a spearman ({headhunter.DayNotice:0}m vs {spearman.DayNotice:0}m)",
                  headhunter.DayNotice > spearman.DayNotice);

            Check("it will carry a downed player off like the rest of them", headhunter.Abducts);
        }

        /// <summary>What a body is worth, in the coins the trader would pay for it.</summary>
        void Worth()
        {
            NativeDef headhunter = NativeCatalog.Active != null ? NativeCatalog.Active.Find("headhunter") : null;
            NativeDef spearman = NativeCatalog.Active != null ? NativeCatalog.Active.Find("spearman") : null;

            if (headhunter == null || spearman == null)
            {
                Debug.LogError("[Island2Test] Cannot price a body without both roles in the catalog.");
                _failed++;
                return;
            }

            float rich = Expected(headhunter);
            float poor = Expected(spearman);

            Debug.Log($"[Island2Test] a headhunter carries about {rich:0.0} coins' worth, "
                      + $"a spearman about {poor:0.0}.");

            Check($"the trip pays better than the first island ({rich:0.0} against {poor:0.0} coins)",
                  rich > poor);

            Check("a headhunter can be carrying a pearl",
                  headhunter.Loot != null
                  && headhunter.Loot.Any(d => d != null && d.Item != null && d.Item.Id == "pearl"));
        }

        /// <summary>Expected value of one body: every line's chance times its average times its price.</summary>
        static float Expected(NativeDef def)
        {
            if (def.Loot == null) return 0f;

            float total = 0f;
            foreach (LootDrop drop in def.Loot)
            {
                if (drop == null || drop.Item == null) continue;
                total += drop.Chance * (drop.Low + drop.High) * 0.5f * drop.Item.Value;
            }

            return total;
        }

        /// <summary>How far the trouble is from where the boat ties up.</summary>
        void Near()
        {
            POISpawner pois = POISpawner.Instance;
            Vector3 landing = pois.PositionOf("camp.base");

            Check($"there are five points of interest on it ({pois.Placements.Count})",
                  pois.Placements.Count == 5);

            Check("there is a revive machine ashore",
                  pois.Placements.Any(p => p != null && p.Id == "camp.revive"));

            var camps = NativeSpawner.Instance.Camps.Where(c => c != null && c.Role != null).ToList();
            if (camps.Count == 0) return;

            NativeSpawner.Camp nearest = camps
                .OrderBy(c => Vector3.Distance(new Vector3(c.Centre.x, 0f, c.Centre.z),
                                               new Vector3(landing.x, 0f, landing.z)))
                .First();

            float distance = Vector3.Distance(new Vector3(nearest.Centre.x, 0f, nearest.Centre.z),
                                              new Vector3(landing.x, 0f, landing.z));

            int close = camps
                .Where(c => Vector3.Distance(new Vector3(c.Centre.x, 0f, c.Centre.z),
                                             new Vector3(landing.x, 0f, landing.z)) <= NearestCampLimit)
                .Sum(c => c.Population);

            Debug.Log($"[Island2Test] the nearest camp is {nearest.Id} ({nearest.Role.Id}) {distance:0}m "
                      + $"from the landing; {close} native(s) live within {NearestCampLimit:0}m of it, "
                      + $"which is {distance / ThirtySeconds * 30f:0} seconds of walking.");

            Check($"the first camp is close enough to be a problem ({distance:0}m, limit {NearestCampLimit:0}m)",
                  distance <= NearestCampLimit);

            Check($"there is more than one of them nearby ({close} within {NearestCampLimit:0}m)", close >= 3);
        }

        void Report()
        {
            string line = $"[Island2Test] {_passed} passed, {_failed} failed.";
            if (_failed == 0) Debug.Log(line);
            else Debug.LogError(line);
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[Island2Test] FAILED: {what}.");
        }
    }
}
