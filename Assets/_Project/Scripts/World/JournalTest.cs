using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// <c>-journalTest</c>, solo, <c>-scene island2</c> (#275): Radu's five pages are items, three of
    /// them lie where Temple Isle's places are, picking one up is heard once, page one plays the
    /// flashback, and Use on a page in hand opens it to read.
    /// </summary>
    public class JournalTest : MonoBehaviour
    {
        static bool _started;
        int _passed, _failed, _heard;

        /// <summary><c>-journalShots &lt;folder&gt;</c>, windowed: pictures of the hint and an open page, for the PR.</summary>
        static string _folder;

        IEnumerator Picture(string name)
        {
            if (_folder == null) yield break;
            System.IO.Directory.CreateDirectory(_folder);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(_folder, name + ".png"));
            yield return new WaitForSeconds(0.5f);
        }

        internal static void Begin()
        {
            _folder = CommandLine.GetString("-journalShots", null);
            if (_started || (!CommandLine.HasFlag("-journalTest") && _folder == null)) return;
            _started = true;

            var go = new GameObject("JournalTest");
            DontDestroyOnLoad(go);
            go.AddComponent<JournalTest>();
        }

        void Start() => StartCoroutine(Run());

        void OnEnable() => Application.logMessageReceived += Hear;
        void OnDisable() => Application.logMessageReceived -= Hear;
        void Hear(string message, string stack, LogType type) { if (message.StartsWith("[Journal] page")) _heard++; }

        void Check(string what, bool ok)
        {
            if (ok) _passed++; else _failed++;
            Debug.Log($"[JournalTest] {(ok ? "PASS" : "FAIL")} {what}");
        }

        IEnumerator Run()
        {
            // Windowed, the prologue plays first, with the camera in the attic, too far for any island scene.
            IslandIntro.Visited = true;
            for (float t = 0f; t < 15f; t += Time.deltaTime)
            {
                if (StoryBeat.Playing) StoryBeat.Skip();
                yield return null;
            }

            Check($"five pages ({Journal.Pages.Length})", Journal.Pages.Length == 5);
            string[] missing = Journal.Pages.Select(p => p.Item).Where(id => ItemCatalog.Active?.Find(id) == null).ToArray();
            Check($"every page is an item ({string.Join(", ", missing)} missing)", missing.Length == 0);
            Check("every page has words", Journal.Pages.All(p => p.Heading.Length > 0 && p.Text.Length > 100));
            Check("page numbers", Journal.PageOf("chart_page") == 1 && Journal.PageOf("journal_page_5") == 5 && Journal.PageOf("rope") == 0);

            // Where Temple Isle's places are: the beach he landed on, the village, the cave.
            foreach ((string place, string item) in new[] { ("wreck", "journal_page_2"), ("village", "journal_page_3"), ("cave", "journal_page_4") })
            {
                Landmark at = Landmark.All.Find(l => l != null && l.Id == place);
                WorldItem lying = FindObjectsByType<WorldItem>(FindObjectsSortMode.None)
                    .FirstOrDefault(i => ItemCatalog.Active.At(i.Stack.Index)?.Id == item);
                float far = at != null && lying != null ? Vector3.Distance(at.transform.position, lying.transform.position) : -1f;
                Check($"{item} lies at the {place} ({far:0.0} m)", far >= 0f && far < 20f
                      && Mathf.Abs(lying.transform.position.y - at.transform.position.y) < 6f);
            }

            Inventory bag = FindObjectsByType<Inventory>(FindObjectsSortMode.None).FirstOrDefault(i => i.IsOwner);
            Check("the host has a bag", bag != null);
            if (bag == null) { Finish(); yield break; }

            // Found once: the first of a page into a bag is heard by everyone, the second is not.
            int was = _heard;
            bag.Add(ItemCatalog.Active.Find("journal_page_3"), 1);
            yield return new WaitForSeconds(0.5f);
            Check($"picking page 3 up is heard ({_heard - was})", _heard - was == 1);
            yield return Picture("hint");
            bag.Add(ItemCatalog.Active.Find("journal_page_3"), 1);
            yield return new WaitForSeconds(0.5f);
            Check($"and only the first time ({_heard - was})", _heard - was == 1);

            bag.Add(ItemCatalog.Active.Find("chart_page"), 1);
            yield return new WaitForSeconds(0.5f);
            Check("page one plays the flashback", StoryBeat.Played.Contains("flashback"));
            while (StoryBeat.Playing) { StoryBeat.Skip(); yield return null; }
            yield return new WaitForSeconds(1f);

            // Read: Use on a page in hand opens it, again closes it; anything else is no page.
            int slot = Enumerable.Range(0, bag.SlotCount).FirstOrDefault(i => bag[i].Def?.Id == "journal_page_3");
            bag.SelectSlot(slot);
            yield return new WaitForSeconds(0.5f);
            var use = bag.GetComponent<ItemUse>();
            bool read = use != null && use.RequestUse();
            Check($"Use on page 3 opens it (open {Journal.Open})", read && Journal.Open == 3);
            yield return null;
            Check("it stays open while held", Journal.Open == 3);
            yield return Picture("page3");
            if (_folder != null)
                foreach (int n in new[] { 1, 2, 4, 5 })
                {
                    Journal.Toggle(Journal.Pages[n - 1].Item, null);
                    // No owner: no hand to put it away from, so it stays open for its picture.
                    yield return Picture("page" + n);
                }
            Journal.Toggle("journal_page_3", bag);
            if (Journal.Open != 3) Journal.Toggle("journal_page_3", bag);
            use.RequestUse();
            Check("Use again closes it", Journal.Open == 0);
            Check("a rope is no page", !Journal.Toggle("rope", bag));

            use.RequestUse();
            // Not the next slot: the second page 3 from above went there.
            bag.SelectSlot(Enumerable.Range(0, bag.SlotCount).First(i => bag[i].Def?.Id != "journal_page_3"));
            for (float t = 0f; Journal.Open != 0 && t < 3f; t += Time.deltaTime) yield return null;
            Check("putting the page away closes it", Journal.Open == 0);

            Finish();
        }

        void Finish()
        {
            Debug.Log($"[JournalTest] {_passed} passed, {_failed} failed.");
            Application.Quit();
        }
    }
}
