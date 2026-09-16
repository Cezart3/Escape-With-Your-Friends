using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Player;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>
    /// The acceptance test for #66, run inside a real session. Server side, behind <c>-drunkTest</c>.
    ///
    /// The acceptance is two judgements - "the buff is genuinely tempting" and "the drunk vision is
    /// genuinely a handicap" - and neither can be asserted directly. What can be asserted is the
    /// arithmetic underneath each of them, and both halves are measured the same way: the same hit
    /// landed sober and drunk, and the same gun fired sober and drunk, with the numbers printed.
    ///
    /// **The handicap is measured, not declared.** The cone is not read off the asset; sixty shots
    /// are fired straight up and the angle of each one is taken off the <c>Fired</c> event, which is
    /// the same event that draws the tracer. A test that asserted <c>AimWobble > 0</c> would pass on
    /// a build where nothing added it to the spread, which is exactly the bug worth catching - #63
    /// shipped a check that passed because the player was standing too far away, and this is the same
    /// mistake wearing a different hat.
    ///
    /// The blur itself has no screen here, so what is checked is the part that decides what the blur
    /// does: the lean, which is pure and static, and the weight it drives.
    /// </summary>
    public class DrunkTest : MonoBehaviour
    {
        const int Shots = 60;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-drunkTest")) return;

            _started = true;

            var go = new GameObject("DrunkTest");
            DontDestroyOnLoad(go);
            go.AddComponent<DrunkTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            BuffState buffs = null;
            float deadline = Time.time + 20f;

            while (Time.time < deadline && buffs == null)
            {
                buffs = FindObjectsByType<BuffState>(FindObjectsSortMode.None)
                        .FirstOrDefault(b => b != null && b.IsSpawned);

                if (buffs == null) yield return new WaitForSeconds(0.5f);
            }

            ShopCounter barman = null;

            while (Time.time < deadline && barman == null)
            {
                barman = FindObjectsByType<ShopCounter>(FindObjectsSortMode.None)
                         .FirstOrDefault(c => c != null && c.IsSpawned && c.Shop != null
                                              && c.Shop.Id == "casino_bar");

                if (barman == null) yield return new WaitForSeconds(0.5f);
            }

            if (buffs == null || barman == null)
            {
                Debug.LogError($"[DrunkTest] Nothing to check: player={buffs != null}, "
                               + $"barman={barman != null}. Run PlayerPrefabBuilder, "
                               + "CasinoFactory.Build, then TerrainGenerator.GenerateIsland -rebuildPois.");
                yield break;
            }

            var bag = buffs.GetComponent<Inventory>();
            var wallet = buffs.GetComponent<Wallet>();
            var use = buffs.GetComponent<ItemUse>();
            var health = buffs.GetComponent<Health>();
            var weapon = buffs.GetComponent<Weapon>();
            var motor = buffs.GetComponent<PlayerMotor>();

            BuffDef drunk = buffs.Catalog != null ? buffs.Catalog.Find("drunk") : null;
            ItemDef grog = bag != null && bag.Catalog != null ? bag.Catalog.Find("grog") : null;
            ItemDef empty = bag != null && bag.Catalog != null ? bag.Catalog.Find("empty_bottle") : null;

            if (bag == null || wallet == null || use == null || health == null || weapon == null
                || motor == null || drunk == null || grog == null)
            {
                Debug.LogError("[DrunkTest] The player or the catalogs are missing something; run "
                               + "ItemFactory.Build then BuffFactory.Build.");
                yield break;
            }

            // ---------------------------------------------------------------- wiring

            Check("there is grog to drink and it is consumable", grog.Consumable);
            Check($"drinking it makes you drunk ({(grog.Effect != null ? grog.Effect.Id : "nothing")})",
                  grog.Effect == drunk);
            Check("and leaves the bottle behind", empty != null && grog.LeavesBehind == empty);

            Check($"the barman sells exactly one thing ({barman.Describe()})", barman.OfferCount == 1);

            ShopDef.Offer offer = barman.OfferAt(0);

            Check("and it is grog", offer.Item == grog);
            Check($"he never runs out ({barman.Remaining(0)})", offer.Unlimited);
            Check($"at a markup over what it is worth ({offer.Price} against {grog.Value})",
                  offer.Price > grog.Value);

            RouletteWheel table = FindObjectsByType<RouletteWheel>(FindObjectsSortMode.None)
                                  .FirstOrDefault(t => t != null && t.IsSpawned);

            if (table != null)
            {
                float toTable = Vector3.Distance(barman.transform.position, table.transform.position);
                Debug.Log($"[DrunkTest] the barman is standing {toTable:0.0}m from the wheel.");

                // He is in the room rather than out on the sand. The building is placed by the
                // greybox builder and the barman by the POI catalogue, which is the same pair of
                // systems that have never met that #65's table check exists for.
                Check($"the barman is inside the casino ({toTable:0.0}m from the table)", toTable < 6f);
            }

            // ---------------------------------------------------------------- he takes money

            motor.ServerTeleport(barman.transform.position + barman.transform.forward * 1.6f, 0f);
            bag.ServerClear();
            wallet.ServerSetBalance(0);
            wallet.ServerSetChips(5000);

            yield return new WaitForSeconds(0.3f);

            // #63's lesson, applied before the check rather than after it: a refusal from four
            // hundred metres away proves nothing about what the barman takes.
            Check("the player is standing at the bar", barman.InReach(buffs.transform.position));

            int bought = barman.ServerBuy(bag, wallet, 0, 1, out string why);

            Check($"a pocket full of chips buys no drink ({why})", bought == 0);
            Check("and the refusal is about the money, not the distance", !why.Contains("counter"));
            Check($"the stack is untouched ({wallet.Chips} chips)", wallet.Chips == 5000);

            wallet.ServerSetBalance(offer.Price * 3);

            yield return new WaitForSeconds(0.2f);

            int paid = barman.ServerBuy(bag, wallet, 0, 1, out string reason);

            Check($"but money does ({paid} bought, {reason})", paid == 1);
            Check("and the bottle is in the bag", bag.CountOf(grog) == 1);
            Check($"the chips never moved ({wallet.Describe()})", wallet.Chips == 5000);

            // ---------------------------------------------------------------- drinking it

            int slot = SlotOf(bag, grog);

            Check("the drink is somewhere the hand can reach it", slot >= 0);
            if (slot < 0) yield break;

            Check("drinking starts", use.ServerBeginUse(slot));

            yield return new WaitForSeconds(grog.UseSeconds + 0.5f);

            Check("the drink is gone", bag.CountOf(grog) == 0);
            Check("the bottle is not", empty == null || bag.CountOf(empty) == 1);
            Check($"and the player is drunk ({buffs.Describe()})", buffs.Has(drunk));
            Check($"for a minute and a half ({buffs.Remaining(drunk):0}s)", buffs.Remaining(drunk) > 60f);

            // ---------------------------------------------------------------- tempting

            // The same hit, sober and drunk, through the same Health. This is the half of the trade
            // the player is buying, and if it is not measurable the drink is a pure downside.
            buffs.Clear(drunk);

            yield return new WaitForSeconds(0.2f);

            float sober = Landed(health, 20f);

            buffs.Apply(drunk);
            float drunkTaken = Landed(health, 20f);

            Check($"a hit that costs {sober:0.0} sober costs {drunkTaken:0.0} drunk", drunkTaken < sober);
            Check($"which is worth having (x{drunk.DamageTakenMultiplier:0.00})",
                  drunk.DamageTakenMultiplier <= 0.8f);
            Check($"and it is not free: slower (x{drunk.SpeedMultiplier:0.00})",
                  drunk.SpeedMultiplier < 1f);

            // ---------------------------------------------------------------- the handicap

            WeaponDef pistol = WeaponCatalog.Active != null ? WeaponCatalog.Active.Find("pistol") : null;

            if (pistol == null || pistol.Item == null)
            {
                Debug.LogError("[DrunkTest] No pistol in the catalog; run WeaponFactory.Build.");
            }
            else
            {
                bag.ServerClear();
                bag.Add(pistol.Item, 1);
                bag.SelectSlot(0);

                yield return new WaitForSeconds(0.4f);

                if (weapon.Equipped != pistol)
                {
                    Debug.LogError($"[DrunkTest] holding a pistol equipped "
                                   + $"{(weapon.Equipped != null ? weapon.Equipped.Id : "nothing")}.");
                }
                else
                {
                    buffs.Clear(drunk);

                    yield return new WaitForSeconds(0.2f);

                    Cone soberCone = null;
                    yield return Measure(weapon, pistol, c => soberCone = c);

                    buffs.Apply(drunk);

                    yield return new WaitForSeconds(0.2f);

                    Cone drunkCone = null;
                    yield return Measure(weapon, pistol, c => drunkCone = c);

                    Debug.Log($"[DrunkTest] {Shots} shots sober: {soberCone}. Drunk: {drunkCone}.");

                    Check($"a sober pistol shoots where it is pointed (worst {soberCone.Worst:0.0}°)",
                          soberCone.Worst <= pistol.Spread + 0.2f);

                    Check($"a drunk one does not (average {soberCone.Average:0.0}° -> "
                          + $"{drunkCone.Average:0.0}°)", drunkCone.Average > soberCone.Average * 3f);

                    Check($"by about the wobble on the buff, not more ({drunkCone.Worst:0.0}° against "
                          + $"{pistol.Spread + drunk.AimWobble:0.0}° of cone)",
                          drunkCone.Worst <= pistol.Spread + drunk.AimWobble + 0.2f);

                    // The number that says the handicap is a real one rather than a nudge: a drunk
                    // pistol scatters wider than a sober shotgun.
                    Check($"a drunk pistol is wider than a sober shotgun's cone "
                          + $"({drunkCone.Worst:0.0}° against 6.5°)", drunkCone.Worst > 6.5f);

                    Check($"and the server is the one rolling it ({buffs.AimWobble:0.0}°)",
                          Mathf.Approximately(buffs.AimWobble, drunk.AimWobble));
                }
            }

            // ---------------------------------------------------------------- the lean

            Check("a sober camera does not lean", DrunkVision.Sway(0f, 12f) == Vector3.zero);

            Vector3 lean = DrunkVision.Sway(0.5f, 12f);
            Vector3 bound = DrunkVision.MaxSway(0.5f);

            Check($"a drunk one does ({lean.z:0.0}° of roll)", lean.sqrMagnitude > 0.01f);
            Check("within what it promises", Mathf.Abs(lean.x) <= bound.x + 0.001f
                                             && Mathf.Abs(lean.y) <= bound.y + 0.001f
                                             && Mathf.Abs(lean.z) <= bound.z + 0.001f);

            Check("twice as drunk leans twice as far",
                  Mathf.Abs(DrunkVision.Sway(1f, 12f).z - lean.z * 2f) < 0.001f);

            // Roll is the one that matters: a tipping horizon reads as drunk, a shaking one reads as
            // an explosion. Checked against the other two so a future tweak cannot quietly swap them.
            Check($"and it tips rather than shakes ({bound.z:0.0}° of roll against {bound.x:0.0}° "
                  + $"of pitch)", bound.z > bound.x * 2f && bound.z > bound.y * 2f);

            // Never the full weight: at 1 the depth of field is a wall and the player cannot find the
            // door, which is annoying rather than funny.
            Check($"the blur never closes completely (x{DrunkVision.MaxWeight:0.00})",
                  DrunkVision.MaxWeight > 0.5f && DrunkVision.MaxWeight < 1f);

            bool moves = false;
            for (float t = 0f; t < 20f && !moves; t += 0.25f)
                moves = Vector3.Distance(DrunkVision.Sway(0.5f, 12f), DrunkVision.Sway(0.5f, 12f + t)) > 1f;

            Check("and it never settles", moves);

            Debug.Log($"[DrunkTest] {_passed} passed, {_failed} failed.");
        }

        // ---------------------------------------------------------------- measuring

        /// <summary>What sixty shots did, in degrees off where the gun was pointed.</summary>
        class Cone
        {
            public float Average;
            public float Worst;
            public int Samples;

            public override string ToString() => $"{Samples} shots, {Average:0.00}° average, "
                                                 + $"{Worst:0.00}° worst";
        }

        /// <summary>
        /// Fires up. Straight up, because a ray into the sky hits nothing and comes back at full
        /// range, and a shot into the hillside the player happens to be standing on would still give
        /// the right angle but a much shorter arm to measure it with.
        ///
        /// The magazine is refilled between shots rather than once: the harness door spends a round
        /// like anything else does, and a pistol holds twelve.
        /// </summary>
        IEnumerator Measure(Weapon weapon, WeaponDef gun, System.Action<Cone> done)
        {
            var angles = new List<float>();
            Vector3 aim = Vector3.up;

            void OnFired(Vector3 origin, Vector3[] ends)
            {
                foreach (Vector3 end in ends)
                {
                    Vector3 shot = end - origin;
                    if (shot.sqrMagnitude < 0.0001f) continue;

                    angles.Add(Vector3.Angle(aim, shot));
                }
            }

            weapon.Fired += OnFired;

            for (int i = 0; i < Shots; i++)
            {
                weapon.ServerLoad(gun, 5);
                weapon.ServerAttackNow(aim);

                if (i % 10 == 9) yield return null;
            }

            yield return null;

            weapon.Fired -= OnFired;

            var cone = new Cone { Samples = angles.Count };

            foreach (float angle in angles)
            {
                cone.Average += angle;
                cone.Worst = Mathf.Max(cone.Worst, angle);
            }

            if (angles.Count > 0) cone.Average /= angles.Count;

            done(cone);
        }

        static float Landed(Health health, float amount)
        {
            float before = health.Current;
            health.TakeDamage(new DamageInfo(amount, DamageType.Blunt));

            return before - health.Current;
        }

        static int SlotOf(Inventory bag, ItemDef item)
        {
            for (int i = 0; i < bag.SlotCount; i++)
                if (bag[i].Def == item) return i;

            return -1;
        }

        void Check(string what, bool passed)
        {
            if (passed)
            {
                _passed++;
                return;
            }

            _failed++;
            Debug.LogError($"[DrunkTest] FAILED: {what}.");
        }
    }
}
