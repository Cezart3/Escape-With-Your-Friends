using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Player;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Economy
{
    /// <summary>
    /// The acceptance test for #209, run inside a real session. Server side, behind <c>-skinShopTest</c>.
    ///
    /// Solo host on the island: buys weapon skins through the same <see cref="ShopCounter.ServerBuy"/>
    /// the shop screen ends up calling, then reads the pistol in the hand back and checks it is
    /// painted. The tint is the one thing here that only exists on a client, so the host's own
    /// client half is what is read.
    /// </summary>
    public class SkinShopTest : MonoBehaviour
    {
        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-skinShopTest")) return;

            _started = true;

            var go = new GameObject("SkinShopTest");
            DontDestroyOnLoad(go);
            go.AddComponent<SkinShopTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            Inventory bag = null;
            ShopCounter counter = null;

            for (float deadline = Time.time + 30f; Time.time < deadline && (bag == null || counter == null);)
            {
                bag = FindObjectsByType<Inventory>(FindObjectsSortMode.None).FirstOrDefault(b => b != null && b.IsSpawned && b.IsOwner);
                counter = FindObjectsByType<ShopCounter>(FindObjectsSortMode.None).FirstOrDefault(c => c != null && c.IsSpawned);

                if (bag == null || counter == null) yield return new WaitForSeconds(0.5f);
            }

            if (bag == null || counter == null)
            {
                Debug.LogError("[SkinShopTest] No player or no counter; run ShopFactory.Build and -scene island. Nothing was checked.");
                yield break;
            }

            var wallet = bag.GetComponent<Wallet>();
            var locker = bag.GetComponent<SkinLocker>();
            var body = bag.GetComponent<CharacterSkin>();
            WeaponCatalog weapons = WeaponCatalog.Active;
            ItemCatalog items = bag.Catalog;

            if (wallet == null || locker == null || body == null || weapons == null)
            {
                Debug.LogError("[SkinShopTest] Missing Wallet, SkinLocker, CharacterSkin or weapon catalog; run "
                               + "PlayerPrefabBuilder.BuildPlayerPrefab.");
                yield break;
            }

            // ---------------------------------------------------------------- the catalog

            Check("the skin catalog is not empty", SkinCatalog.All.Length > 0);
            foreach (WeaponSkin skin in SkinCatalog.All)
            {
                Check($"'{skin.Id}' dresses a weapon that exists ({skin.WeaponId})", weapons.Find(skin.WeaponId) != null);
                Check($"'{skin.Id}' is on the shelf (run ItemFactory.Build, ShopFactory.Build -rebuildShop)",
                      OfferOf(counter, items.Find(skin.ItemId)) >= 0);
            }

            SkinCatalog.Find("gold", out WeaponSkin gold);
            SkinCatalog.Find("jungle", out WeaponSkin jungle);
            SkinCatalog.Find("obsidian", out WeaponSkin obsidian);
            int goldOffer = OfferOf(counter, items.Find(gold.ItemId));
            int jungleOffer = OfferOf(counter, items.Find(jungle.ItemId));
            int obsidianOffer = OfferOf(counter, items.Find(obsidian.ItemId));

            if (goldOffer < 0 || jungleOffer < 0 || obsidianOffer < 0)
            {
                Report();
                yield break;
            }

            // ---------------------------------------------------------------- buying one

            bag.GetComponent<PlayerMotor>()?.ServerTeleport(counter.transform.position + counter.transform.forward * 2f + Vector3.up * 0.5f, 0f);
            yield return new WaitForSeconds(0.3f);

            wallet.ServerSetBalance(1000);
            int bought = counter.ServerBuy(bag, wallet, goldOffer, 1, out string why);

            Check("the skin is sold", bought == 1 && why == null);
            Check("the money drops by the price", wallet.Balance == 1000 - gold.Price);
            Check("the skin is owned", locker.Owns("gold"));
            Check("and worn on the pistol", locker.WornOn("pistol") == "gold");
            Check("it never came in the bag", bag.CountOf(items.Find(gold.ItemId)) == 0);

            // ---------------------------------------------------------------- painted in the hand

            WeaponDef pistol = weapons.Find("pistol");
            bag.Add(pistol.Item, 1);
            bag.ServerSelect(Enumerable.Range(0, bag.SlotCount).First(s => bag[s].Def == pistol.Item));

            yield return WaitFor(() => body.Held != null && Painted(body.Held, gold.Tint));

            Check("the pistol in the hand carries the gold", body.Held != null && Painted(body.Held, gold.Tint));
            Check("and so does the first-person copy", body.View != null && Painted(body.View, gold.Tint));

            // ---------------------------------------------------------------- a second one switches

            wallet.ServerSetBalance(1000);
            counter.ServerBuy(bag, wallet, jungleOffer, 1, out why);
            Check("the second skin is owned and worn instead", locker.Owns("jungle") && locker.WornOn("pistol") == "jungle");
            Check("the first is still owned", locker.Owns("gold"));

            yield return WaitFor(() => body.Held != null && Painted(body.Held, jungle.Tint));
            Check("the hand copy switched to the second colour", body.Held != null && Painted(body.Held, jungle.Tint));

            // ---------------------------------------------------------------- no money

            wallet.ServerSetBalance(0);
            bought = counter.ServerBuy(bag, wallet, obsidianOffer, 1, out why);

            Check("a player without money cannot buy", bought == 0 && why != null);
            Check("and owns nothing new", !locker.Owns("obsidian") && wallet.Balance == 0);

            Report();
        }

        /// <summary>Whether every renderer under a model carries this colour in its property block.</summary>
        static bool Painted(GameObject model, Color colour)
        {
            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
            var block = new MaterialPropertyBlock();

            foreach (Renderer renderer in renderers)
            {
                renderer.GetPropertyBlock(block);
                Color got = block.GetColor("_BaseColor");
                if (Mathf.Abs(got.r - colour.r) + Mathf.Abs(got.g - colour.g) + Mathf.Abs(got.b - colour.b) > 0.02f)
                    return false;
            }

            return renderers.Length > 0;
        }

        /// <summary>A change reaches the client half a network tick later, not a frame later.</summary>
        static IEnumerator WaitFor(System.Func<bool> done)
        {
            for (float waited = 0f; !done() && waited < 3f; waited += Time.deltaTime)
                yield return null;
        }

        static int OfferOf(ShopCounter counter, ItemDef def)
        {
            for (int i = 0; i < counter.OfferCount; i++)
                if (counter.OfferAt(i).Item == def && def != null) return i;

            return -1;
        }

        void Report()
        {
            string line = $"[SkinShopTest] {_passed} passed, {_failed} failed.";

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
            Debug.LogError($"[SkinShopTest] FAILED: {what}.");
        }
    }
}
