using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Player;
using FishNet;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The look epic's instrument (#239): one fixed route of camera spots round the island, flown by a
    /// camera of its own, in two modes.
    ///
    /// <c>-perfRoute &lt;file.md&gt;</c> holds each spot for five seconds with VSync and the cap off,
    /// and appends one markdown table to the file: p50, p95 and worst frame time, then batches,
    /// SetPass calls, triangles and shadow casters off the render profiler counters (development
    /// builds only; a release build reports zeros). Run it once per <c>-quality</c>.
    ///
    /// <c>-beautyShots &lt;folder&gt;</c> shoots every spot at noon, dusk and night, at the monitor's
    /// native resolution: the 1280x720 playthrough shots looked pixelated fullscreen, which was the
    /// capture size and not the game.
    ///
    /// Both need a window (no <c>-nographics</c>) and a host. The player's own camera is switched off
    /// for the run, so the numbers are one camera's, and the HUD is hidden so the shots are the world.
    /// </summary>
    public class LookRoute : MonoBehaviour
    {
        const float Settle = 1.5f;
        const float Hold = 5f;

        /// <summary>Times of day for the shots. Sunset is 0.75 on WorldClock.</summary>
        static readonly (string name, float time)[] Times = { ("noon", 0.5f), ("dusk", 0.735f), ("night", 0.02f) };

        static bool _started;

        /// <summary>A route is flying; StoryBeat stays quiet for it.</summary>
        internal static bool Running => _started;

        string _perfFile;
        string _shotFolder;
        Camera _camera;

        struct Spot
        {
            public string Name;
            public Vector3 Eye;
            public Vector3 Target;

            /// <summary>Someone who walks about: the camera finds them where they are at the shot.</summary>
            public Transform Subject;

            /// <summary>An animal's longest side, which frames it instead of a person's height. 0 for a person.</summary>
            public float Size;
        }

        internal static void Begin()
        {
            string perf = CommandLine.GetString("-perfRoute", null);
            string shots = CommandLine.GetString("-beautyShots", null);
            if (_started || (string.IsNullOrEmpty(perf) && string.IsNullOrEmpty(shots))) return;
            _started = true;

            var go = new GameObject("LookRoute");
            DontDestroyOnLoad(go);
            var route = go.AddComponent<LookRoute>();
            route._perfFile = perf;
            route._shotFolder = shots;
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            if (!string.IsNullOrEmpty(_shotFolder) && !CommandLine.HasFlag("-screen-width"))
            {
                Resolution native = Screen.currentResolution;
                Screen.SetResolution(native.width, native.height, FullScreenMode.FullScreenWindow);
            }

            // The island, the player, the natives and the first shadow cascade all have to have arrived.
            PlayerMotor player = null;
            float deadline = Time.time + 90f;
            while ((player = FindAnyObjectByType<PlayerMotor>()) == null && Time.time < deadline) yield return null;
            yield return new WaitForSeconds(10f);

            List<Spot> spots = Spots(player);
            Debug.Log($"[LookRoute] {spots.Count} spots: {string.Join(", ", spots.Select(s => s.Name))}. "
                      + PerfProbe.Describe());

            BuildCamera();
            SpinSlots(player);

            // The first-person gun hangs off Camera.main, which is now ours. The carry pose is the one
            // switch that puts it away without touching the body.
            var skin = player != null ? player.GetComponentInChildren<CharacterSkin>() : null;
            if (skin != null) skin.ForceCarry = true;

            if (!string.IsNullOrEmpty(_perfFile)) yield return Measure(spots);
            if (!string.IsNullOrEmpty(_shotFolder)) yield return Shoot(spots);

            Debug.Log("[LookRoute] done.");
            Application.Quit();
        }

        // ---------------------------------------------------------------- the route

        /// <summary>
        /// The spots, from the island's own landmarks so a moved POI moves its spot with it. Missing
        /// ones are skipped and logged rather than failing the run: a route on Island2 is still a
        /// route, only shorter.
        /// </summary>
        List<Spot> Spots(PlayerMotor player)
        {
            var spots = new List<Spot>();

            if (player != null)
            {
                Vector3 eye = player.transform.position + Vector3.up * 1.7f;
                spots.Add(new Spot { Name = "spawn beach", Eye = eye, Target = eye + player.transform.forward * 20f });
            }

            Around(spots, "wreck", "wreck", 14f, 3f);
            Around(spots, "cave", "jungle (cave)", 16f, 2.5f);
            Around(spots, "village", "village with natives", 22f, 4f);
            Around(spots, "shop", "trader", 12f, 2.5f);
            Around(spots, "camp.base", "base camp", 13f, 3f);
            Around(spots, "casino", "casino front", 16f, 2.5f);

            Landmark casino = Find("casino");
            if (casino != null)
            {
                // Just inside the door, across the floor's hub to the VIP glass. The first baseline stood
                // at z 12, which is outside: the shot was of the doorway.
                Transform t = casino.transform;
                spots.Add(new Spot { Name = "casino floor", Eye = t.TransformPoint(0f, 2.2f, 8f), Target = t.TransformPoint(0f, 1.2f, -3f) });
            }

            // The blackjack table from a player's seat, the roulette wheel beyond it (#252).
            var blackjack = FindAnyObjectByType<Casino.BlackjackTable>();
            if (blackjack != null)
            {
                Transform t = blackjack.transform;
                spots.Add(new Spot { Name = "casino tables", Eye = t.TransformPoint(0f, 1.7f, 1.6f), Target = t.TransformPoint(0f, 0.9f, -0.2f) });
            }

            // The cabinets are placed props, not landmarks, so the first one found stands for the row.
            var slots = FindAnyObjectByType<Casino.SlotMachine>();
            if (slots != null)
                spots.Add(new Spot
                {
                    Name = "casino, every slot spinning",
                    Eye = slots.transform.position + slots.transform.forward * 3.5f + Vector3.up * 1.8f,
                    Target = slots.transform.position + Vector3.up * 1.2f,
                });

            // #249. The buggy and the boat are not landmarks; three quarters from the front, close.
            foreach (string label in new[] { "buggy", "boat" })
            {
                Vehicles.Vehicle v = System.Array.Find(FindObjectsByType<Vehicles.Vehicle>(FindObjectsSortMode.None),
                                                       x => x.Label == label);
                if (v == null) continue;
                Transform t = v.transform;
                spots.Add(new Spot { Name = "the " + label, Eye = t.TransformPoint(3.5f, 2.6f, 5.5f),
                                     Target = t.TransformPoint(0f, 0.9f, 0f) });
            }

            // #76. The people, face to face: the barman, the castaway and the native nearest the village.
            Subject(spots, "the barman", Array.Find(FindObjectsByType<Economy.ShopCounter>(FindObjectsSortMode.None),
                                                    c => c.name.StartsWith("Barman"))?.transform);
            Subject(spots, "the castaway", FindAnyObjectByType<AI.Castaway>()?.transform);
            Landmark village = Find("village");
            if (village != null)
                Subject(spots, "a native", FindObjectsByType<AI.Native>(FindObjectsSortMode.None)
                    .OrderBy(n => Vector3.Distance(n.transform.position, village.transform.position))
                    .FirstOrDefault()?.transform);

            // #287. One of each species alive, from the front quarter, framed by its body box.
            foreach (Data.AnimalDef def in AI.Animal.Live.Where(a => a != null && a.Def != null)
                         .Select(a => a.Def).Distinct())
            {
                AI.Animal one = AI.Animal.Live.First(a => a != null && a.Def == def);
                spots.Add(new Spot { Name = "a " + def.Id, Subject = one.transform,
                                     Size = Mathf.Max(def.BodySize.y, def.BodySize.z) });
            }

            Overlook(spots);
            Around(spots, "plane", "the plane", 18f, 5f);

            return spots;
        }

        static void Subject(List<Spot> spots, string name, Transform who)
        {
            if (who == null) { Debug.Log($"[LookRoute] nobody for '{name}'; skipped."); return; }
            spots.Add(new Spot { Name = name, Subject = who });
        }

        static Landmark Find(string id) => Landmark.All.Find(l => l != null && l.Id == id);

        void Around(List<Spot> spots, string id, string name, float back, float up)
        {
            Landmark mark = Find(id);
            if (mark == null) { Debug.Log($"[LookRoute] no landmark '{id}'; '{name}' skipped."); return; }

            Vector3 p = mark.transform.position;
            Vector3 eye = p + mark.transform.forward * back + Vector3.up * up;
            Terrain terrain = Terrain.activeTerrain;
            if (terrain != null) eye.y = Mathf.Max(eye.y, terrain.SampleHeight(eye) + terrain.transform.position.y + 1.7f);
            spots.Add(new Spot { Name = name, Eye = eye, Target = p + Vector3.up * 1.5f });
        }

        /// <summary>The highest point on the terrain, looking back at the island's middle.</summary>
        static void Overlook(List<Spot> spots)
        {
            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null) return;

            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            Vector3 best = origin;
            for (int x = 1; x < 64; x++)
            for (int z = 1; z < 64; z++)
            {
                var p = new Vector3(origin.x + size.x * x / 64f, 0f, origin.z + size.z * z / 64f);
                p.y = terrain.SampleHeight(p) + origin.y;
                if (p.y > best.y) best = p;
            }

            Vector3 middle = origin + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f);
            middle.y = terrain.SampleHeight(middle) + origin.y;
            spots.Add(new Spot { Name = "cliff overlook", Eye = best + Vector3.up * 3f, Target = middle });
        }

        /// <summary>
        /// Every cabinet's reels and lights going at once, which is the casino's worst case. Staked
        /// from the host's own wallet, topped up for it; only on the host, which owns the machines.
        /// </summary>
        static void SpinSlots(PlayerMotor player)
        {
            if (player == null || InstanceFinder.IsServerStarted == false) return;

            var wallet = player.GetComponent<Economy.Wallet>();
            if (wallet == null) return;
            wallet.ServerSetChips(1000000);

            int spun = 0;
            foreach (var machine in FindObjectsByType<Casino.SlotMachine>(FindObjectsSortMode.None))
                if (machine.ServerSpin(player.NetworkObject) > 0) spun++;
            Debug.Log($"[LookRoute] {spun} slot machines spinning.");
        }

        void BuildCamera()
        {
            Camera main = Camera.main;
            _camera = new GameObject("LookRouteCamera").AddComponent<Camera>();
            if (main != null)
            {
                _camera.CopyFrom(main);
                main.enabled = false;
            }

            // Camera.main from here on, so whatever follows the camera (the sea, the first-person
            // view) follows this one. The disabled brain is for PlayerCameraRig, which puts a live one
            // on Camera.main every frame if it has none, and that would fly this camera back to the
            // player's eye.
            _camera.gameObject.tag = "MainCamera";
            _camera.gameObject.AddComponent<Unity.Cinemachine.CinemachineBrain>().enabled = false;

            _camera.fieldOfView = GameSettings.Fov;
            UniversalAdditionalCameraData data = _camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = VideoSettings.Smaa ? AntialiasingMode.SubpixelMorphologicalAntiAliasing : AntialiasingMode.None;
            data.antialiasingQuality = AntialiasingQuality.High;

            foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None)) canvas.enabled = false;
        }

        void Place(Spot spot)
        {
            if (spot.Subject != null && spot.Size > 0f)
            {
                spot.Eye = spot.Subject.TransformPoint(new Vector3(0.8f, 0.55f, 1.5f) * spot.Size);
                spot.Target = spot.Subject.position + Vector3.up * (0.4f * spot.Size);
            }
            else if (spot.Subject != null)
            {
                spot.Eye = spot.Subject.TransformPoint(0.6f, 1.7f, 2.6f);
                spot.Target = spot.Subject.position + Vector3.up * 1.2f;
            }

            _camera.transform.SetPositionAndRotation(spot.Eye, Quaternion.LookRotation(spot.Target - spot.Eye));
        }

        // ---------------------------------------------------------------- perf

        IEnumerator Measure(List<Spot> spots)
        {
            // Measured flat out: a frame cap or VSync would report the monitor, not the GPU.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            WorldClock.Freeze(0.5f);

            using var batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            using var setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            using var triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            using var casters = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Shadow Casters Count");

            var rows = new List<string>();
            var frames = new List<float>(4096);

            foreach (Spot spot in spots)
            {
                Place(spot);
                yield return new WaitForSeconds(Settle);

                frames.Clear();
                long b = 0, s = 0, t = 0, c = 0;
                float end = Time.realtimeSinceStartup + Hold;
                while (Time.realtimeSinceStartup < end)
                {
                    yield return null;
                    frames.Add(Time.unscaledDeltaTime * 1000f);
                    b += batches.LastValue; s += setPass.LastValue; t += triangles.LastValue; c += casters.LastValue;
                }

                frames.Sort();
                int n = frames.Count;
                string row = $"| {spot.Name} | {frames[n / 2]:F1} | {frames[Mathf.Min(n - 1, n * 95 / 100)]:F1} | "
                             + $"{frames[n - 1]:F1} | {b / n} | {s / n} | {t / n / 1000}k | {c / n} |";
                rows.Add(row);
                Debug.Log($"[LookRoute] {row}");
            }

            string header =
                $"\n### {DateTime.Now:yyyy-MM-dd HH:mm}, {Tier()}, {Commit()}\n\n{PerfProbe.Describe()}\n\n"
                + "| Spot | p50 ms | p95 ms | worst ms | batches | SetPass | triangles | shadow casters |\n"
                + "|---|---|---|---|---|---|---|---|\n";
            File.AppendAllText(_perfFile, header + string.Join("\n", rows) + "\n");
            Debug.Log($"[LookRoute] {rows.Count} spots measured, appended to {_perfFile}.");
        }

        static string Tier() => $"{VideoSettings.PresetName} on '{QualitySettings.names[QualitySettings.GetQualityLevel()]}'";

        /// <summary>Passed in by whoever runs it, because a build has no git.</summary>
        static string Commit() => CommandLine.GetString("-commit", "commit ?");

        // ---------------------------------------------------------------- shots

        IEnumerator Shoot(List<Spot> spots)
        {
            string folder = Path.Combine(_shotFolder, VideoSettings.PresetName.ToLowerInvariant());
            Directory.CreateDirectory(folder);

            foreach (var (name, time) in Times)
            {
                WorldClock.Freeze(time);
                yield return new WaitForSeconds(1f);

                for (int i = 0; i < spots.Count; i++)
                {
                    Place(spots[i]);
                    yield return new WaitForSeconds(0.6f);
                    // Again: an animal walks a metre in that time, and a gull flies out of frame.
                    Place(spots[i]);
                    yield return new WaitForEndOfFrame();
                    string file = Path.Combine(folder, $"{i:00}_{spots[i].Name.Split(',')[0].Replace(' ', '_')}_{name}.png");
                    ScreenCapture.CaptureScreenshot(file);
                    yield return null;
                }
            }

            Debug.Log($"[LookRoute] {spots.Count * Times.Length} shots in {folder} at {Screen.width}x{Screen.height}.");
        }
    }
}
