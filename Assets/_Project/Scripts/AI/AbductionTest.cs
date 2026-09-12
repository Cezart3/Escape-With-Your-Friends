using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Player;
using FishNet;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.AI
{
    /// <summary>
    /// The acceptance test for #107, run inside a real session. Server side, behind
    /// <c>-abductTest</c>, and it wants <c>-scene island -noNatives -noAnimals</c>: a NavMesh to walk
    /// on, and nobody on it except the bodies this test puts there.
    ///
    /// What #107 asks for is one sentence - *when a player goes down, nearby natives break off and
    /// carry the body back to their village* - and almost all of the difficulty is in the four ways
    /// it has to be able to end. A haul that cannot be stopped is not a mechanic, it is a cutscene
    /// with a timer on it, so the counter-play is what most of this measures:
    ///
    /// 1. **Kill the carrier** and the body drops where the fight happened, not where the camp is.
    /// 2. **Break the carrier** - hurt it past the flee threshold - and it drops your friend and runs.
    /// 3. **Get there first**, because nobody can take a body somebody else is already holding.
    /// 4. **Pick your friend up off the floor** and the parcel stands up mid-journey.
    ///
    /// Two more things are measured because they are the difference between a threat and a
    /// punishment: **the timer keeps running** the whole way, so being carried off is not a reprieve
    /// and not a stay of execution either, and **the haul is slow** - half a run, well under a sprint
    /// - so catching a kidnapper is never in doubt. What it costs you is the distance back.
    ///
    /// Every number printed below is measured. The distances are metres between transforms after the
    /// fact, the times are stopwatch readings, and the one claim the test makes about the data
    /// (which roles take prisoners) is read off the catalog rather than restated here.
    /// </summary>
    public class AbductionTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const float WaitForNavMesh = 90f;

        /// <summary>Seconds allowed for a native to walk to a body and get it off the ground.</summary>
        const float GrabWindow = 20f;

        /// <summary>Seconds allowed for a haul to reach the camp once the body is up.</summary>
        const float HaulWindow = 25f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-abductTest")) return;

            _started = true;

            var go = new GameObject("AbductionTest");
            DontDestroyOnLoad(go);
            go.AddComponent<AbductionTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

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
                Debug.LogError("[AbductionTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            var health = motor.GetComponent<Health>();
            var stun = motor.GetComponent<StunState>();
            var body = motor.GetComponent<Carryable>();
            var ragdoll = motor.GetComponent<RagdollController>();

            NativeCatalog natives = NativeCatalog.Active;
            NativeSpawner spawner = NativeSpawner.Instance;

            if (health == null || body == null || ragdoll == null || natives == null)
            {
                Debug.LogError("[AbductionTest] The player has no Health, Carryable or ragdoll, or there "
                               + "is no native catalog. Run NativeFactory.Build and rebuild the player "
                               + "prefab.");
                yield break;
            }

            if (spawner == null)
            {
                Debug.LogError("[AbductionTest] No NativeSpawner, so nothing can be placed. Run with "
                               + "-scene island.");
                yield break;
            }

            NativeDef spearman = natives.Find("spearman");
            NativeDef blowgunner = natives.Find("blowgunner");

            if (spearman == null || blowgunner == null)
            {
                Check("the roles this test needs exist", false);
                Report();
                yield break;
            }

            // The island arrives after the server starts, and a native without a NavMesh is a statue.
            float navDeadline = Time.time + WaitForNavMesh;
            while (Time.time < navDeadline
                   && !NavMesh.SamplePosition(motor.transform.position, out _, 25f, NavMesh.AllAreas))
                yield return new WaitForSeconds(1f);

            Check("there is a NavMesh under the player",
                  NavMesh.SamplePosition(motor.transform.position, out _, 25f, NavMesh.AllAreas));

            Data(natives, motor.SprintSpeed);

            yield return Claims(spawner, spearman, blowgunner, motor, health, stun, body, ragdoll);
            yield return Journey(spawner, spearman, motor, health, stun, body, ragdoll);
            yield return Killed(spawner, spearman, motor, health, stun, body, ragdoll);
            yield return Broke(spawner, spearman, motor, health, stun, body, ragdoll);
            yield return Rescued(spawner, spearman, motor, health, stun, body, ragdoll);
            yield return Delivered(spawner, spearman, motor, health, stun, body, ragdoll);

            Report();
        }

        // ---------------------------------------------------------------- data

        /// <summary>
        /// The two promises the asset has to keep before any of the behaviour below means anything:
        /// that this is a role rather than a global setting, and that a kidnapper can be caught.
        /// </summary>
        void Data(NativeCatalog natives, float sprint)
        {
            int haulers = 0;

            for (ushort i = 1; i <= natives.Count; i++)
            {
                NativeDef def = natives.At(i);
                if (def == null) continue;

                if (!def.Abducts)
                {
                    Check($"{def.Id} does not come for bodies, and has no radius for it",
                          Mathf.Approximately(def.AbductRadius, 0f));

                    continue;
                }

                haulers++;

                Check($"{def.Id} will come for a body from a real distance ({def.AbductRadius:0}m)",
                      def.AbductRadius >= 10f);

                // The fairness lever, and the same one the chase rests on: if a haul could outrun a
                // sprint, every abduction would be a death and the other three players would be
                // spectators to it.
                Check($"{def.Id} hauls at {def.HaulSpeed:0.0} m/s, under a {sprint:0.0} m/s sprint",
                      def.HaulSpeed < sprint - 1f);

                Check($"{def.Id} hauls slower than it runs ({def.HaulSpeed:0.0} < {def.RunSpeed:0.0})",
                      def.HaulSpeed < def.RunSpeed);
            }

            Check($"somebody on this island takes prisoners ({haulers} role(s))", haulers > 0);
            Check($"not everybody does ({haulers} of {natives.Count})", haulers < natives.Count);
        }

        // ---------------------------------------------------------------- who comes, and how many

        /// <summary>
        /// Three spearmen and a blowgunner watch somebody go down. Exactly one spearman claims the
        /// body - the nearest - and the blowgunner claims nothing, ever.
        /// </summary>
        IEnumerator Claims(NativeSpawner spawner, NativeDef spearman, NativeDef blowgunner,
                           PlayerMotor motor, Health health, StunState stun, Carryable body,
                           RagdollController ragdoll)
        {
            Reset(health, stun);
            yield return null;

            Vector3 origin = motor.transform.position;
            Vector3 camp = Ground(origin + motor.transform.forward * 40f);

            Native near = Place(spawner, spearman, Beside(origin, 8f, 0f), camp);
            Native middle = Place(spawner, spearman, Beside(origin, 14f, 120f), camp);
            Native far = Place(spawner, spearman, Beside(origin, 19f, 240f), camp);
            Native shooter = Place(spawner, blowgunner, Beside(origin, 6f, 60f), camp);

            if (near == null || middle == null || far == null || shooter == null)
            {
                Check("four natives can be placed around a player", false);
                Clear(near, middle, far, shooter);
                yield break;
            }

            yield return Settled();

            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));

            // The claim is made inside ServerDown, on the same frame, from the server-side state
            // event - so one frame of slack is generous rather than hopeful.
            yield return null;

            int claims = Native.Live.Count(n => n != null && n.Haul == body);

            Debug.Log($"[AbductionTest] a player went down with four natives watching: {claims} "
                      + $"claimed the body; the one {Vector3.Distance(near.transform.position, origin):0}m "
                      + "away got it.");

            Check($"somebody comes for a downed player ({claims} claim(s))", claims >= 1);
            Check($"exactly one native claims a body ({claims})", claims == 1);
            Check("the nearest one is the one that claims it", near.Haul == body);
            Check("a blowgunner never claims a body", shooter.Haul == null);
            Check("the claim is a state, not a flag", near.State == NativeState.Abduct);

            // And it has to actually get there. This is the first time the NavMesh is involved.
            yield return Until(() => near == null || near.IsHauling, GrabWindow);

            Check("the claimant walks over and picks the body up", near != null && near.IsHauling);

            if (near != null && near.IsHauling)
            {
                Check("the body knows who is carrying it",
                      body.IsCarried && body.Carrier == near.NetworkObject);

                Check("a native is a carry holder", near.CarrySocket != null);

                float toSocket = Vector3.Distance(ragdoll.HipBone.position, near.CarrySocket.position);
                Check($"the body is on the shoulder, not near it ({toSocket:0.00}m)", toSocket < 0.1f);
            }

            Clear(near, middle, far, shooter);
            Reset(health, stun);
        }

        // ---------------------------------------------------------------- the journey

        /// <summary>
        /// Being carried off is not a reprieve: the bleed-out timer runs the whole way, and the body
        /// measurably travels towards the camp while it does.
        /// </summary>
        IEnumerator Journey(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                            StunState stun, Carryable body, RagdollController ragdoll)
        {
            Reset(health, stun);
            yield return null;

            Vector3 origin = motor.transform.position;
            Vector3 camp = Ground(origin + motor.transform.forward * 45f);

            Native native = Place(spawner, def, Beside(origin, 6f, 0f), camp);
            if (native == null)
            {
                Check("a native can be placed for the journey", false);
                yield break;
            }

            yield return Settled();

            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));
            yield return Until(() => native == null || native.IsHauling, GrabWindow);

            if (native == null || !native.IsHauling)
            {
                Check("the body gets picked up for the journey", false);
                Clear(native);
                Reset(health, stun);
                yield break;
            }

            float bleedAtPickup = health.BleedOutRemaining;
            float distanceAtPickup = Vector3.Distance(ragdoll.HipBone.position, camp);

            float watched = 4f;
            yield return new WaitForSeconds(watched);

            float bleedNow = health.BleedOutRemaining;
            float distanceNow = Vector3.Distance(ragdoll.HipBone.position, camp);

            float travelled = distanceAtPickup - distanceNow;
            float spent = bleedAtPickup - bleedNow;

            Debug.Log($"[AbductionTest] {watched:0}s of being carried: {travelled:0.0}m closer to camp "
                      + $"({distanceAtPickup:0}m -> {distanceNow:0}m) and {spent:0.0}s of bleed-out "
                      + $"gone ({bleedAtPickup:0}s -> {bleedNow:0}s left).");

            // The whole point of hauling somebody to a village: the body has to move, and the ragdoll
            // has to move with it rather than being left behind by a root transform that walks alone.
            Check($"the body is carried towards the camp ({travelled:0.0}m in {watched:0}s)",
                  travelled > 1f);

            // Being carried off is not a reprieve. It is also not a punishment - the timer runs at
            // exactly the rate it always did, so the rescue window is the one the game promised.
            Check($"the bleed-out timer keeps running while carried ({spent:0.0}s of {watched:0}s)",
                  spent >= watched - 1f && spent <= watched + 1f);

            Check("the carrier is still holding on", native.IsHauling && health.IsDowned);

            // Slow enough to catch. Measured, rather than read off the asset a second time.
            float speed = travelled / watched;
            Debug.Log($"[AbductionTest] the haul moved at {speed:0.0} m/s against a "
                      + $"{motor.SprintSpeed:0.0} m/s sprint.");

            Check($"a kidnapper can be run down ({speed:0.0} m/s vs {motor.SprintSpeed:0.0})",
                  speed < motor.SprintSpeed);

            Clear(native);
            Reset(health, stun);
        }

        // ---------------------------------------------------------------- killing the carrier

        /// <summary>Shoot the kidnapper and your friend lands at your feet, not at the village.</summary>
        IEnumerator Killed(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                           StunState stun, Carryable body, RagdollController ragdoll)
        {
            Reset(health, stun);
            yield return null;

            Vector3 origin = motor.transform.position;
            Vector3 camp = Ground(origin + motor.transform.forward * 60f);

            Native native = Place(spawner, def, Beside(origin, 6f, 180f), camp);
            if (native == null)
            {
                Check("a native can be placed to be killed", false);
                yield break;
            }

            yield return Settled();

            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));
            yield return Until(() => native == null || native.IsHauling, GrabWindow);

            if (native == null || !native.IsHauling)
            {
                Check("the body gets picked up before the carrier is killed", false);
                Clear(native);
                Reset(health, stun);
                yield break;
            }

            // Let it get properly under way, so "dropped where it fell" is a different place from
            // "dropped where it started".
            yield return new WaitForSeconds(2f);

            Vector3 where = native.transform.position;
            float fromCamp = Vector3.Distance(where, camp);

            native.GetComponent<Health>().ServerKill(new DamageInfo(0f, DamageType.Blunt));
            yield return null;

            float drift = Vector3.Distance(ragdoll.HipBone.position, where);

            Debug.Log($"[AbductionTest] the carrier was killed {fromCamp:0}m short of camp at {where}; "
                      + $"the body landed {drift:0.0}m from there, at {ragdoll.HipBone.position}.");

            Check("killing the carrier drops the body", !body.IsCarried);
            Check("the carrier is not still holding a claim", native.Haul == null);
            Check($"the body is left where the fight was ({drift:0.0}m)", drift < 3f);
            Check($"and nowhere near the camp ({fromCamp:0}m away)", fromCamp > 10f);
            Check("the player is still downed rather than rescued by it", health.IsDowned);

            Clear(native);
            Reset(health, stun);
        }

        // ---------------------------------------------------------------- breaking the carrier

        /// <summary>
        /// The cheap rescue: hurt the kidnapper past the point where it is willing to keep doing
        /// this, and it drops your friend and runs for camp. Cheaper than killing it, and the only
        /// counter-play that costs a wounded native rather than a dead one - which matters, because
        /// the camp will refill a dead one.
        /// </summary>
        IEnumerator Broke(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                          StunState stun, Carryable body, RagdollController ragdoll)
        {
            Reset(health, stun);
            yield return null;

            Vector3 origin = motor.transform.position;
            Vector3 camp = Ground(origin + motor.transform.forward * 60f);

            Native native = Place(spawner, def, Beside(origin, 6f, 30f), camp);
            if (native == null)
            {
                Check("a native can be placed to be broken", false);
                yield break;
            }

            yield return Settled();

            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));
            yield return Until(() => native == null || native.IsHauling, GrabWindow);

            if (native == null || !native.IsHauling)
            {
                Check("the body gets picked up before the carrier is broken", false);
                Clear(native);
                Reset(health, stun);
                yield break;
            }

            var hurt = native.GetComponent<Health>();

            // Just past the threshold the role itself declares, rather than a number typed here: one
            // hit point below "this is where it breaks" is the whole experiment.
            float leave = def.MaxHealth * def.FleeHealth - 1f;
            hurt.TakeDamage(new DamageInfo(hurt.Current - leave, DamageType.Blunt));

            yield return Until(() => native == null || native.Haul == null, 2f);

            Debug.Log($"[AbductionTest] the carrier was hurt to {hurt.Current:0}/{hurt.Max:0} hp "
                      + $"(it breaks under {def.FleeHealth:P0}): carried={body.IsCarried}, "
                      + $"it is now {native.State}.");

            Check("a broken carrier drops the body", !body.IsCarried);
            Check("and gives up the claim with it", native.Haul == null);
            Check("and runs rather than standing there", native.State == NativeState.Flee);
            Check("the player is still downed, just no longer luggage", health.IsDowned);

            Clear(native);
            Reset(health, stun);
        }

        // ---------------------------------------------------------------- taking them back

        /// <summary>Helping your friend up mid-journey works, and the kidnapper notices.</summary>
        IEnumerator Rescued(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                            StunState stun, Carryable body, RagdollController ragdoll)
        {
            Reset(health, stun);
            yield return null;

            Vector3 origin = motor.transform.position;
            Vector3 camp = Ground(origin + motor.transform.forward * 60f);

            Native native = Place(spawner, def, Beside(origin, 6f, 90f), camp);
            if (native == null)
            {
                Check("a native can be placed for the rescue", false);
                yield break;
            }

            yield return Settled();

            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));
            yield return Until(() => native == null || native.IsHauling, GrabWindow);

            if (native == null || !native.IsHauling)
            {
                Check("the body gets picked up before the rescue", false);
                Clear(native);
                Reset(health, stun);
                yield break;
            }

            health.ServerRescue();
            yield return Until(() => native == null || native.Haul == null, 2f);

            Debug.Log("[AbductionTest] a player helped up mid-haul: "
                      + $"carried={body.IsCarried}, the native is now {native.State}.");

            Check("helping somebody up takes them off the shoulder", !body.IsCarried);
            Check("the kidnapper lets go of the claim", native.Haul == null);
            Check("and is not left standing in an abduction it is not doing",
                  native.State != NativeState.Abduct);

            Check("the rescued player is on their feet", health.IsAlive);

            Clear(native);
            Reset(health, stun);
        }

        // ---------------------------------------------------------------- arriving

        /// <summary>
        /// Nobody intervenes. The body reaches the camp, the native puts it down there, and the
        /// player is still on the timer when it arrives - which is what #108 hangs off.
        /// </summary>
        IEnumerator Delivered(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                              StunState stun, Carryable body, RagdollController ragdoll)
        {
            Reset(health, stun);
            yield return null;

            Vector3 origin = motor.transform.position;

            // Close, because this case is about arriving rather than about walking. The journey is
            // measured above.
            Vector3 camp = Ground(origin + motor.transform.forward * 12f);

            Native native = Place(spawner, def, Beside(origin, 5f, 270f), camp);
            if (native == null)
            {
                Check("a native can be placed for a delivery", false);
                yield break;
            }

            yield return Settled();

            float start = Time.time;
            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));

            yield return Until(() => native == null || native.IsHauling, GrabWindow);
            yield return Until(() => native == null || !native.IsHauling, HaulWindow);

            if (native == null)
            {
                Check("the carrier survives its own delivery", false);
                Reset(health, stun);
                yield break;
            }

            float took = Time.time - start;
            float fromCamp = Vector3.Distance(ragdoll.HipBone.position, camp);

            Debug.Log($"[AbductionTest] a body was carried to the camp in {took:0.0}s and put down "
                      + $"{fromCamp:0.0}m from the middle of it at {ragdoll.HipBone.position} "
                      + $"(the carrier stopped at {native.transform.position}), with "
                      + $"{health.BleedOutRemaining:0}s left on the timer.");

            Check($"the body reaches the camp ({fromCamp:0.0}m from the centre)", fromCamp < 6f);
            Check("and is put down rather than carried around forever", !body.IsCarried);
            Check("the carrier goes back to its day job", native.State != NativeState.Abduct);
            Check("the player is still alive to be rescued at the other end", health.IsDowned);

            Clear(native);
            Reset(health, stun);
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// A native on the NavMesh at a spot, facing the middle of the test, with its camp somewhere
        /// it can actually walk to. Mobile by definition: everything here is about walking.
        /// </summary>
        Native Place(NativeSpawner spawner, NativeDef def, Vector3 spot, Vector3 camp)
        {
            Native native = spawner.ServerSpawn(def, Ground(spot), camp);
            if (native == null) return null;

            var agent = native.GetComponent<NavMeshAgent>();
            if (agent != null && agent.enabled) agent.Warp(Ground(spot));

            return native;
        }

        static Vector3 Ground(Vector3 spot)
            => NavMesh.SamplePosition(spot, out NavMeshHit hit, 25f, NavMesh.AllAreas) ? hit.position : spot;

        /// <summary>A point some metres from the origin, at a bearing, so bodies are not in a line.</summary>
        static Vector3 Beside(Vector3 origin, float metres, float degrees)
            => origin + Quaternion.Euler(0f, degrees, 0f) * Vector3.forward * metres;

        static void Clear(params Native[] natives)
        {
            foreach (Native native in natives)
            {
                if (native == null || native.NetworkObject == null || !native.NetworkObject.IsSpawned)
                    continue;

                InstanceFinder.ServerManager.Despawn(native.NetworkObject);
            }
        }

        static IEnumerator Until(System.Func<bool> done, float seconds)
        {
            float deadline = Time.time + seconds;
            while (Time.time < deadline && !done()) yield return null;
        }

        /// <summary>Alive, healed and unstunned. Every case starts from the same player.</summary>
        static void Reset(Health health, StunState stun)
        {
            if (health != null)
            {
                if (health.IsDead) health.ServerRevive(1f);
                else if (health.IsDowned) health.ServerRescue();
            }

            if (stun != null) stun.ServerClearStun();
            if (health != null) health.Heal(health.Max);
        }

        /// <summary>A SyncVar written this frame is read next tick, not next frame. See #46.</summary>
        static WaitForSeconds Settled() => new(0.3f);

        void Report()
        {
            string line = $"[AbductionTest] {_passed} passed, {_failed} failed.";

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
            Debug.LogError($"[AbductionTest] FAILED: {what}.");
        }
    }
}
