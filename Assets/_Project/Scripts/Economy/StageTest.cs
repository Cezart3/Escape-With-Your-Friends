using System.Collections;
using System.Linq;
using EscapeWithYourFriends.AI;
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
    /// The acceptance test for the game stage and the wild zone, behind <c>-stageTest</c> with
    /// <c>-scene island -noAnimals -save -savePath X</c>, solo: holding a better gun raises the stage,
    /// the stage multiplies what drops sell for and nothing else, it survives a save, and the far side
    /// of the island has animals worth the trip.
    /// </summary>
    public class StageTest : MonoBehaviour
    {
        static bool _started;
        int _passed, _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-stageTest")) return;
            _started = true;

            var go = new GameObject("StageTest");
            DontDestroyOnLoad(go);
            go.AddComponent<StageTest>();
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

            var bag = weapon != null ? weapon.GetComponent<Inventory>() : null;
            Check("a player spawned", bag != null);
            Check("the run save is armed (pass -save)", RunSave.Armed);
            if (bag == null)
            {
                Report();
                yield break;
            }

            // ------------------------------------------------------------ the ladder

            Check("a pistol earns nothing", GameStage.StageFor(300) == 0);
            Check("the shotgun is stage 1, the rifle 3, the sniper 5",
                  GameStage.StageFor(2500) == 1 && GameStage.StageFor(25000) == 3 && GameStage.StageFor(250000) == 5);
            Check("each stage pays half again", Mathf.Approximately(GameStage.Multiplier(0), 1f)
                                                 && Mathf.Approximately(GameStage.Multiplier(2), 2.25f));

            // ------------------------------------------------------------ holding a gun raises it

            GameStage.Reset();
            WeaponDef rifle = WeaponCatalog.Active != null ? WeaponCatalog.Active.Weapons.FirstOrDefault(w => w != null && w.Id == "rifle") : null;
            WeaponDef pistol = WeaponCatalog.Active != null ? WeaponCatalog.Active.Weapons.FirstOrDefault(w => w != null && w.Id == "pistol") : null;
            ShopCounter trader = FindObjectsByType<ShopCounter>(FindObjectsSortMode.None)
                                 .FirstOrDefault(c => c != null && WeaponMods.WeaponPrice(c.Shop, rifle) > 0);
            Check("a counter sells the rifle", trader != null && pistol != null);
            if (trader == null || pistol == null)
            {
                Report();
                yield break;
            }

            bag.ServerClear();
            bag.Add(pistol.Item, 1);
            bag.SelectSlot(0);
            yield return Settled();
            Check($"the pistol leaves it at 0 (stage {GameStage.Stage})", GameStage.Stage == 0);

            bag.ServerClear();
            bag.Add(rifle.Item, 1);
            bag.SelectSlot(0);
            yield return Settled();
            Check($"the rifle raises it to 3 (stage {GameStage.Stage})", GameStage.Stage == 3);

            bag.ServerClear();
            bag.Add(pistol.Item, 1);
            bag.SelectSlot(0);
            yield return Settled();
            Check("and it never falls", GameStage.Stage == 3);
            Check($"the counter shows it to clients (stage {trader.Stage})", trader.Stage == 3);

            // ------------------------------------------------------------ what it pays

            ItemDef pelt = ItemCatalog.Active.Find("jaguar_pelt"), hide = ItemCatalog.Active.Find("hide");
            ItemDef stock = trader.Shop.Offers.Select(o => o.Item).FirstOrDefault(i => i != null && trader.Shop.PriceFor(i) > 0);
            Check("pelts and hides are drops, stock is not", GameStage.IsDrop(pelt) && GameStage.IsDrop(hide) && !GameStage.IsDrop(stock));

            int flat = trader.Shop.PriceFor(pelt);
            Check($"a pelt sells for {GameStage.SellPrice(trader.Shop, pelt, 3)} at stage 3, {flat} flat",
                  flat > 0 && GameStage.SellPrice(trader.Shop, pelt, 3) == Mathf.RoundToInt(flat * 3.375f));
            Check("stock sells back flat at any stage", stock != null
                                                        && GameStage.SellPrice(trader.Shop, stock, 5) == trader.Shop.PriceFor(stock));

            // ------------------------------------------------------------ the save

            RunSave.ServerSave();
            GameStage.Reset();
            Check("reset", GameStage.Stage == 0);
            RunSave.Reload();
            Check($"the save brings stage 3 back (stage {GameStage.Stage})", GameStage.Stage == 3);

            // ------------------------------------------------------------ the wild zone

            AnimalSpawner spawner = AnimalSpawner.Instance;
            AnimalSpawner.Zone wild = spawner != null ? spawner.Zones.FirstOrDefault(z => z.Id == "jaguar.wild") : null;
            AnimalSpawner.Zone camp = spawner != null ? spawner.Zones.FirstOrDefault(z => z.Id == "boar.camp") : null;
            Check("the island has a jaguar zone (run AnimalFactory.Rezone)", wild != null && camp != null && spawner.Zones.Any(z => z.Id == "stag.wild"));
            if (wild != null && camp != null)
            {
                float far = Vector3.Distance(wild.Centre, camp.Centre);
                Check($"the wild is {far:0} m from camp", far > 300f);
                Check("jaguars live nowhere else", spawner.Zones.Count(z => z.Species == wild.Species) == 1);

                Animal jaguar = spawner.ServerSpawn(wild.Species, wild.Centre);
                Check("a jaguar stands on the wild zone's ground", jaguar != null
                                                                   && Vector3.Distance(jaguar.transform.position, wild.Centre) < 30f);
                if (jaguar != null) InstanceFinder.ServerManager.Despawn(jaguar.NetworkObject);

                AnimalDef boar = camp.Species;
                Check($"a jaguar is worth {Worth(wild.Species)} to a boar's {Worth(boar)}", Worth(wild.Species) > 5 * Worth(boar));
            }

            GameStage.Reset();
            yield return Settled();
            Report();
        }

        /// <summary>The average value of one kill's loot.</summary>
        static int Worth(AnimalDef animal)
            => animal == null || animal.Loot == null ? 0
             : Mathf.RoundToInt(animal.Loot.Where(d => d != null && d.Item != null).Sum(d => d.Chance * (d.Min + d.Max) * 0.5f * d.Item.Value));

        static WaitForSeconds Settled() => new(0.3f);

        void Check(string what, bool passed)
        {
            if (passed) _passed++;
            else
            {
                _failed++;
                Debug.LogError($"[StageTest] FAILED: {what}.");
            }
            if (passed) Debug.Log($"[StageTest] ok: {what}.");
        }

        void Report()
        {
            string line = $"[StageTest] {_passed} passed, {_failed} failed.";
            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }
    }
}
