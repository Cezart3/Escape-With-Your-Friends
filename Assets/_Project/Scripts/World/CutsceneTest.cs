using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Audio;
using EscapeWithYourFriends.Core;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// <c>-cutsceneTest</c> (#287): every scene played through headless, at four times speed. Solo, on
    /// either island:
    ///
    ///     -host -scene island -noNatives -noAnimals -cutsceneTest -quitAfter 260
    ///
    /// What a terminal can settle about a film: the cast loads and every actor is a humanoid; every
    /// clip a scene asks for is in the library; each scene runs to its end by itself inside 40 s,
    /// with actors that actually move and more than one shot; no lens on the island ends up inside
    /// the scenery; and afterwards nothing is left behind, the players are visible again, and the
    /// controls are back. Skipping and queueing are checked too. How it looks is the screenshots'.
    /// </summary>
    public class CutsceneTest : MonoBehaviour
    {
        const float Speed = 4f;

        static readonly string[] ClipsUsed =
        {
            "Idle_Loop", "Walk_Loop", "Fixing_Kneeling", "Idle_Torch_Loop", "Push_Loop", "PickUp_Table", "Interact",
            "Idle_Talking_Loop", "Crouch_Idle_Loop", "Death01", "Sitting_Idle_Loop", "Sitting_Exit", "Sitting_Talking_Loop",
            "Dance_Loop",
        };

        static readonly string[] SetsUsed = { "Attic", "TinBox", "TinLid", "Journal", "Radio", "Photo", "Marisol", "Reef", "Crate" };

        static bool _started;

        /// <summary>
        /// <c>-cutsceneShots &lt;folder&gt;</c>, windowed: the same run at normal speed, a picture a second
        /// into every shot. For the PR, and for whoever wants to see one without playing to it.
        /// </summary>
        static string _folder;
        int _passed, _failed, _missingClips;

        internal static void Begin()
        {
            _folder = CommandLine.GetString("-cutsceneShots", null);
            if (_started || (!CommandLine.HasFlag("-cutsceneTest") && _folder == null)) return;
            _started = true;

            var go = new GameObject("CutsceneTest");
            DontDestroyOnLoad(go);
            go.AddComponent<CutsceneTest>();
        }

        void OnEnable()
        {
            Application.logMessageReceived += Count;
            StartCoroutine(Run());
        }

        void OnDisable() => Application.logMessageReceived -= Count;

        void Count(string message, string stack, LogType type)
        {
            if (message.StartsWith("[Puppet]")) _missingClips++;
        }

        IEnumerator Run()
        {
            yield return new WaitForSeconds(12f);

            CutsceneCast cast = CutsceneCast.Load();
            Check("the cast is in Resources", cast != null);
            if (cast == null) { Finish(); yield break; }

            Check($"four friends ({cast.Friends.Length})", cast.Friends.Length == 4 && cast.Friends.All(Humanoid));
            Check("Bogdan is a humanoid", Humanoid(cast.Bogdan));
            string[] missing = ClipsUsed.Where(c => cast.Clip(c) == null).ToArray();
            Check($"every clip a scene uses is in the library ({string.Join(", ", missing)} missing)", missing.Length == 0);
            string[] noSet = SetsUsed.Where(s => cast.Set(s) == null).ToArray();
            Check($"every set is built ({string.Join(", ", noSet)} missing)", noSet.Length == 0 && cast.Atlas != null);
            Check("the storm and the radio have sounds",
                  Sfx.Clip(Sound.Thunder) != null && Sfx.Clip(Sound.Rain) != null && Sfx.Clip(Sound.Radio) != null);

            Transform wreck = Landmark.All.FirstOrDefault(l => l != null && l.Id.ToLowerInvariant() == "wreck")?.transform;
            Transform castaway = FindAnyObjectByType<AI.Castaway>()?.transform;
            Transform plane = FindAnyObjectByType<PlaneAssembly>()?.transform;
            Check("there is a wreck to arrive at", wreck != null);

            // The island's own arrival beat retries until it plays; out of reach of the wreck it would
            // queue behind the first rehearsal and shift every scene after it by one.
            // Windowed, the real prologue is already playing (and the arrival queued behind it).
            IslandIntro.Visited = true;
            while (StoryBeat.Playing) { StoryBeat.Skip(); yield return null; }
            StoryBeat.Rehearse = true;
            Time.timeScale = _folder == null ? Speed : 1f;
            if (_folder != null) System.IO.Directory.CreateDirectory(_folder);

            var scenes = new (string Id, Transform Focus, int Actors)[]
            {
                ("prologue", null, 5), ("arrive:island", wreck, 4), ("arrive:island2", wreck, 4),
                ("castaway", castaway != null ? castaway : wreck, 5), ("plane", plane != null ? plane : wreck, 4),
                ("ending", null, 5), ("radio", null, 0),
            };

            foreach ((string id, Transform focus, int actors) in scenes)
                yield return Scene(id, focus, actors);

            // Skipping ends it and clears up at once.
            StoryBeat.Forget("castaway");
            StoryBeat.Play("castaway", "Found him", "He waited. Barely.", castaway != null ? castaway : wreck);
            yield return new WaitForSeconds(1f * Speed);
            StoryBeat.Skip();
            for (int i = 0; i < 5; i++) yield return null;
            Check("a skipped scene ends", !StoryBeat.Playing);
            Check("and leaves no actors", FindObjectsByType<Puppet>(FindObjectsSortMode.None).Length == 0);

            // #272. Skipping is a vote: the server's count, driven directly as two and four players.
            SkipVote.Count(1, new SkipWish { Id = "t", Watching = true });
            SkipVote.Count(2, new SkipWish { Id = "t", Watching = true });
            SkipTally one = SkipVote.Count(1, new SkipWish { Id = "t", Watching = true, Skip = true });
            Check($"of two watchers one vote is not enough ({one.Votes} of {one.Needed})", !one.Carried && one.Needed == 2);
            Check("the second carries it", SkipVote.Count(2, new SkipWish { Id = "t", Watching = true, Skip = true }).Carried);
            for (int c = 1; c <= 4; c++) SkipVote.Count(c, new SkipWish { Id = "u", Watching = true });
            SkipVote.Count(1, new SkipWish { Id = "u", Watching = true, Skip = true });
            SkipTally two = SkipVote.Count(2, new SkipWish { Id = "u", Watching = true, Skip = true });
            SkipTally left = SkipVote.Count(4, new SkipWish { Id = "u", Watching = false });
            Check($"of four, two votes wait ({two.Votes} of {two.Needed}); one leaving carries it ({left.Votes} of {left.Needed})",
                  !two.Carried && left.Carried);

            // And over the wire: the host alone watching, its own vote ends the scene.
            StoryBeat.Forget("castaway");
            StoryBeat.Play("castaway", "Found him", "He waited. Barely.", castaway != null ? castaway : wreck);
            yield return new WaitForSeconds(1f * Speed);
            SkipVote.Want("castaway");
            for (float t = Time.realtimeSinceStartup; StoryBeat.Playing && Time.realtimeSinceStartup - t < 3f;) yield return null;
            Check("a vote from the only watcher ends it", !StoryBeat.Playing);

            // A scene asked for while another plays waits its turn.
            StoryBeat.Forget("radio");
            StoryBeat.Forget("arrive:island");
            StoryBeat.Play("radio", "", "", null);
            yield return null;
            StoryBeat.Play("arrive:island", "Day one", "", wreck);
            yield return null;
            Check("the second waits", StoryBeat.Current != null && StoryBeat.Current.Id == "radio");
            StoryBeat.Skip();
            for (int i = 0; i < 5; i++) yield return null;
            Check("and plays after the first", StoryBeat.Current != null && StoryBeat.Current.Id == "arrive:island");
            StoryBeat.Skip();
            for (int i = 0; i < 5; i++) yield return null;

            Time.timeScale = 1f;
            Check($"no scene asked for a clip that is not there ({_missingClips})", _missingClips == 0);
            Finish();
        }

        IEnumerator Scene(string id, Transform focus, int actors)
        {
            StoryBeat.Forget(id);
            bool asked = StoryBeat.Play(id, id, "the test", focus);
            yield return null;
            StoryBeat beat = StoryBeat.Current;
            Check($"{id}: plays", asked && beat != null && beat.Id == id);
            if (beat == null) yield break;

            float began = Time.time;
            int most = 0, shots = 0;
            float pictureAt = -1f;
            bool moved = false;
            var before = new Dictionary<Transform, Quaternion>();
            List<Vector3> stood = beat.Stood;

            // A destroyed beat compares equal to null, so "Current == beat" stays true after it ends.
            while (beat != null && Time.time - began < 60f)
            {
                most = Mathf.Max(most, beat.Actors.Count(a => a != null));
                if (beat.Shots != shots) pictureAt = Time.time + 1.2f;
                shots = beat.Shots;
                if (_folder != null && pictureAt > 0f && Time.time >= pictureAt)
                {
                    pictureAt = -1f;
                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(_folder, $"{id.Replace(':', '_')}_{shots}.png"));
                }

                // The bones, half a second apart: a puppet whose graph is not driving it stands in a T.
                if (!moved && Time.time - began > 6f)
                {
                    foreach (Puppet actor in beat.Actors)
                    {
                        Transform arm = actor != null ? actor.Bone(HumanBodyBones.RightUpperArm) : null;
                        if (arm == null) continue;
                        if (before.TryGetValue(arm, out Quaternion was) && Quaternion.Angle(was, arm.localRotation) > 0.5f) moved = true;
                        before[arm] = arm.localRotation;
                    }

                    yield return new WaitForSeconds(0.5f);
                    continue;
                }

                yield return null;
            }

            float length = Time.time - began;
            Check($"{id}: ends by itself, in {length:F1} s", length < 41f && beat == null);
            Check($"{id}: {most} actors (wants {actors})", most >= actors);
            Check($"{id}: cut, not one long take ({shots} shots)", shots >= (actors > 0 ? 3 : 1));
            if (actors > 0) Check($"{id}: the actors move", moved);

            for (int i = 0; i < 3; i++) yield return null;

            if (focus != null)
            {
                int buried = stood.Count(at => Physics.OverlapSphere(at, 0.15f, ~0, QueryTriggerInteraction.Ignore)
                                                      .Any(c => c.GetComponentInParent<Player.PlayerMotor>() == null));
                Check($"{id}: no lens inside the scenery ({buried} of {stood.Count})", buried == 0);
            }

            Check($"{id}: nothing left behind", FindObjectsByType<Puppet>(FindObjectsSortMode.None).Length == 0
                                                && GameObject.Find("Attic") == null && GameObject.Find("Marisol") == null);
            Check($"{id}: the players are back", FindObjectsByType<Player.PlayerMotor>(FindObjectsSortMode.None)
                                                    .SelectMany(m => m.GetComponentsInChildren<Renderer>())
                                                    .All(r => !r.forceRenderingOff));
            Debug.Log($"[CutsceneTest] {id}: {length:F1} s, {most} actors, {stood.Count} lens positions.");
        }

        static bool Humanoid(GameObject body)
        {
            Animator animator = body != null ? body.GetComponent<Animator>() : null;
            return animator != null && animator.avatar != null && animator.avatar.isHuman;
        }

        void Finish()
        {
            StoryBeat.Rehearse = false;
            Debug.Log($"[CutsceneTest] {_passed} passed, {_failed} failed.");
        }

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }
            _failed++;
            Debug.LogError($"[CutsceneTest] FAIL: {what}");
        }
    }
}
