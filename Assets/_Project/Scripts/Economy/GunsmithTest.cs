using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Player;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Economy
{
    /// <summary>
    /// The acceptance test for weapon mods, behind <c>-gunsmithTest</c> with <c>-scene island</c>,
    /// solo: the price table climbs the way the design says, a trader sells the next level of each
    /// track for the gun in your hand and refuses everything it should, the bought levels change the
    /// numbers the weapon and the camera read, and they survive a save.
    /// </summary>
    public class GunsmithTest : MonoBehaviour
    {
        static bool _started;
        int _passed, _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-gunsmithTest")) return;
            _started = true;

            var go = new GameObject("GunsmithTest");
            DontDestroyOnLoad(go);
            go.AddComponent<GunsmithTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            Weapon weapon = null;
            for (float deadline = Time.time + 60f; weapon == null && Time.time < deadline;)
            {
                weapon = FindObjectsByType<Weapon>(FindObjectsSortMode.None).FirstOrDefault(w => w != null && w.IsSpawned && w.IsOwner);
                if (weapon == null) yield return new WaitForSeconds(0.5f);
            }

            var mods = weapon != null ? weapon.GetComponent<WeaponMods>() : null;
            var bag = weapon != null ? weapon.GetComponent<Inventory>() : null;
            var wallet = weapon != null ? weapon.GetComponent<Wallet>() : null;
            var motor = weapon != null ? weapon.GetComponent<PlayerMotor>() : null;
            Check("a player with WeaponMods spawned (rebuild the player prefab if not)", mods != null && bag != null && wallet != null);
            if (mods == null || bag == null || wallet == null || motor == null)
            {
                Report();
                yield break;
            }

            // ------------------------------------------------------------ the table

            Check("firepower 1 costs a quarter of the weapon", WeaponMods.Price(1000, ModTrack.Firepower, 1) == 250);
            Check("firepower 5 costs five times the weapon", WeaponMods.Price(1000, ModTrack.Firepower, 5) == 5000);
            Check("the scope starts at half and tops out at five times", WeaponMods.Price(1000, ModTrack.Sight, 1) == 500
                                                                        && WeaponMods.Price(1000, ModTrack.Sight, 4) == 5000);
            Check("the grip costs three times the weapon", WeaponMods.Price(1000, ModTrack.Grip, 1) == 3000);
            Check("red dot and flashlight are one purchase each", WeaponMods.Levels(ModTrack.RedDot) == 1 && WeaponMods.Levels(ModTrack.Flashlight) == 1);
            bool climbs = true;
            foreach (ModTrack track in new[] { ModTrack.Firepower, ModTrack.Sight })
                for (int level = 2; level <= WeaponMods.Levels(track); level++)
                    climbs &= WeaponMods.Price(250000, track, level) > WeaponMods.Price(250000, track, level - 1);
            Check("every level costs more than the one before", climbs);
            Check("each sight level zooms further", WeaponMods.Zoom(0) > 1f && WeaponMods.Zoom(1) > WeaponMods.Zoom(0) && WeaponMods.Zoom(4) > WeaponMods.Zoom(3));

            // ------------------------------------------------------------ the trader

            WeaponDef pistol = WeaponCatalog.Active != null ? WeaponCatalog.Active.Weapons.FirstOrDefault(w => w != null && w.Id == "pistol") : null;
            WeaponDef knife = WeaponCatalog.Active != null ? WeaponCatalog.Active.Weapons.FirstOrDefault(w => w != null && w.Id == "knife") : null;
            ShopCounter trader = FindObjectsByType<ShopCounter>(FindObjectsSortMode.None)
                                 .FirstOrDefault(c => c != null && WeaponMods.WeaponPrice(c.Shop, pistol) > 0);
            Check("a counter sells the pistol", trader != null);
            Check("the knife takes firepower and nothing else", WeaponMods.Fits(knife, ModTrack.Firepower) && !WeaponMods.Fits(knife, ModTrack.Sight));
            if (trader == null || pistol == null)
            {
                Report();
                yield break;
            }

            int price = WeaponMods.WeaponPrice(trader.Shop, pistol);
            mods.ServerRestore(null);
            bag.ServerClear();
            bag.Add(pistol.Item, 1);
            bag.SelectSlot(0);

            motor.ServerTeleport(trader.transform.position + Vector3.right * 500f, 0f);
            wallet.ServerSetBalance(1000000);
            yield return Settled();
            Check("refused away from a trader", !mods.ServerBuy(ModTrack.Firepower, out _));

            motor.ServerTeleport(trader.transform.position + trader.transform.forward * 2f, 0f);
            yield return Settled();
            Check($"holding the pistol ({(weapon.Equipped != null ? weapon.Equipped.Id : "nothing")})", weapon.Equipped == pistol);

            wallet.ServerSetBalance(WeaponMods.Price(price, ModTrack.Firepower, 1) - 1);
            Check("refused a coin short", !mods.ServerBuy(ModTrack.Firepower, out string why) && mods.Level(pistol, ModTrack.Firepower) == 0);
            Debug.Log($"[GunsmithTest] a coin short: {why}.");

            wallet.ServerSetBalance(1000000);
            int before = wallet.Balance;
            Check("firepower 1 bought", mods.ServerBuy(ModTrack.Firepower, out why));
            Check("and it cost exactly the table price", before - wallet.Balance == WeaponMods.Price(price, ModTrack.Firepower, 1));
            while (mods.Level(pistol, ModTrack.Firepower) < WeaponMods.Levels(ModTrack.Firepower) && mods.ServerBuy(ModTrack.Firepower, out why)) { }
            Check("firepower climbs to 5 and stops", mods.Level(pistol, ModTrack.Firepower) == 5 && !mods.ServerBuy(ModTrack.Firepower, out _));
            Check("the red dot is bought once", mods.ServerBuy(ModTrack.RedDot, out _) && !mods.ServerBuy(ModTrack.RedDot, out _));
            Check("grip, flashlight and a scope bought", mods.ServerBuy(ModTrack.Grip, out _) && mods.ServerBuy(ModTrack.Flashlight, out _)
                                                          && mods.ServerBuy(ModTrack.Sight, out _));

            // ------------------------------------------------------------ what it does

            Check($"firepower 5 hits {mods.DamageScale(pistol):0.00}x", Mathf.Approximately(mods.DamageScale(pistol), 1f + 5f * WeaponMods.DamagePerLevel));
            Check("the grip cuts the kick to a third", mods.RecoilScale(pistol) < 0.4f);
            Check("aiming with a red dot scatters a quarter", Mathf.Approximately(mods.SpreadScale(pistol, true), 0.25f)
                                                              && Mathf.Approximately(mods.SpreadScale(pistol, false), 1f));
            Check("the mods are the pistol's, not the knife's", mods.Level(knife, ModTrack.Firepower) == 0);

            bag.ServerClear();
            yield return Settled();
            Check("empty-handed buys nothing", !mods.ServerBuy(ModTrack.Firepower, out why));

            // ------------------------------------------------------------ saving

            var saved = mods.Saved();
            mods.ServerRestore(null);
            Check("cleared", mods.Level(pistol, ModTrack.Firepower) == 0);
            mods.ServerRestore(saved);
            Check($"restored from {saved.Count} saved line(s)", mods.Level(pistol, ModTrack.Firepower) == 5 && mods.Has(pistol, ModTrack.Grip)
                                                              && mods.Level(pistol, ModTrack.Sight) == 1);

            yield return Settled();
            Report();
        }

        static WaitForSeconds Settled() => new(0.3f);

        void Check(string what, bool passed)
        {
            if (passed) _passed++;
            else
            {
                _failed++;
                Debug.LogError($"[GunsmithTest] FAILED: {what}.");
            }
        }

        void Report()
        {
            string line = $"[GunsmithTest] {_passed} passed, {_failed} failed.";
            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }
    }
}
