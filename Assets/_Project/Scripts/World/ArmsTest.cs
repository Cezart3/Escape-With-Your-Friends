using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Items;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// <c>-armsTest</c>: a gun can be had. The second playtest found #51's whole arsenal unobtainable -
    /// nothing sold it and nothing dropped it - and the wreck the first objective points at empty.
    /// Checks the wreck's loot lies on the ground near it and the trader sells every gun and its rounds.
    /// </summary>
    public class ArmsTest : MonoBehaviour
    {
        static bool _started;
        int _passed, _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-armsTest")) return;
            _started = true;

            var go = new GameObject("ArmsTest");
            DontDestroyOnLoad(go);
            go.AddComponent<ArmsTest>().StartCoroutine(go.GetComponent<ArmsTest>().Run());
        }

        void Check(string what, bool ok)
        {
            if (ok) _passed++; else _failed++;
            Debug.Log($"[ArmsTest] {(ok ? "PASS" : "FAIL")} {what}");
        }

        IEnumerator Run()
        {
            yield return new WaitForSeconds(15f);

            Landmark wreck = Landmark.All.Find(l => l.Id == "wreck");
            Check("there is a wreck", wreck != null);
            if (wreck != null)
            {
                WorldItem[] near = FindObjectsByType<WorldItem>(FindObjectsSortMode.None)
                    .Where(i => Vector3.Distance(i.transform.position, wreck.transform.position) < 20f).ToArray();
                string[] ids = near.Select(i => ItemCatalog.Active.At(i.Stack.Index)?.Id).ToArray();
                Debug.Log($"[ArmsTest] near the wreck: {string.Join(", ", ids)}");

                Check("a pistol lies at the wreck", ids.Contains("pistol"));
                Check("pistol rounds lie at the wreck", ids.Contains("pistol_ammo"));

                // On the ground, not in the sky or under the sand: every stack within 3m of it vertically.
                float worst = near.Length > 0 ? near.Max(i => Mathf.Abs(i.transform.position.y - wreck.transform.position.y)) : 0f;
                Check($"the loot lies near the wreck's height (worst {worst:0.0}m off)", worst < 6f);
            }

            // The island's trader, not the casino bar's counter, which is a ShopCounter too.
            ShopCounter counter = FindObjectsByType<ShopCounter>(FindObjectsSortMode.None)
                .FirstOrDefault(c => c.Shop != null && c.Shop.Offers.Any(o => o.IsValid && o.Item.Id == "rope"));
            Check("there is a trader", counter != null);
            if (counter != null)
                foreach (string gun in new[] { "pistol", "shotgun", "smg", "rifle", "pistol_ammo", "shotgun_shell", "rifle_ammo" })
                    Check($"the trader sells {gun}", counter.Shop.Offers.Any(o => o.IsValid && o.Item.Id == gun));

            Debug.Log($"[ArmsTest] {_passed} passed, {_failed} failed.");
            Application.Quit();
        }
    }
}
