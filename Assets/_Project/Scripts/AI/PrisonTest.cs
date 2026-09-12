using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.World;
using FishNet;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.AI
{
    /// <summary>
    /// The acceptance test for #108, run inside a real session. Server side, behind
    /// <c>-prisonTest</c>, and it wants <c>-scene island -noNatives -noAnimals</c>: a real village
    /// with real hooks in it, and nobody wandering about except the bodies this test puts there.
    ///
    /// #107 ended with a native walking your friend out of sight. #108 is what is at the other end
    /// of that walk, and the four things it has to get right are all about *consequence*:
    ///
    /// 1. **The body goes on a hook**, in a fixed place, in the open, where it can be found. A haul
    ///    that ended with a body dumped somewhere in a village would be a disappearance, not a
    ///    kidnapping.
    /// 2. **Cutting somebody down does not fix them.** They fall, still downed, still on the timer,
    ///    and now somebody has to carry them out at carry speed.
    /// 3. **The camp guards what it is holding.** A village with a prisoner in it behaves as if it
    ///    were midnight whatever the sun is doing - so a rescue is a raid rather than a stealth run,
    ///    and #55's daylight contract is untouched for a village holding nobody.
    /// 4. **If the timer runs out up there, they die up there** and the corpse stays on the hook.
    ///    That is the worst outcome in the game, and it has to be visible from across the village
    ///    rather than being a silent despawn.
    ///
    /// The test drives real hooks from the island's own POI bake rather than building its own, and
    /// walks the player and the natives to them, because "there is a prison in the village" is one of
    /// the things worth checking.
    /// </summary>
    public class PrisonTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;
        const float WaitForNavMesh = 90f;
        const float WaitForHooks = 60f;

        /// <summary>Noon, as <see cref="WorldClock.Normalized"/> reads it. Everything here is at noon:
        /// the claim under test is that a guarded village stops caring what time it is.</summary>
        const float Noon = 0.5f;

        /// <summary>Seconds allowed for a native to walk to a body and get it off the ground.</summary>
        const float GrabWindow = 20f;

        /// <summary>Seconds allowed for a haul to reach its delivery point once the body is up.</summary>
        const float HaulWindow = 25f;

        /// <summary>Seconds a placed native is watched before it has or has not noticed anybody.</summary>
        const float SenseWindow = 1.4f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-prisonTest")) return;

            _started = true;

            var go = new GameObject("PrisonTest");
            DontDestroyOnLoad(go);
            go.AddComponent<PrisonTest>();
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
                Debug.LogError("[PrisonTest] No player ever spawned. Nothing was checked.");
                yield break;
            }

            var health = motor.GetComponent<Health>();
            var stun = motor.GetComponent<StunState>();
            var body = motor.GetComponent<Carryable>();
            var ragdoll = motor.GetComponent<RagdollController>();

            NativeCatalog natives = NativeCatalog.Active;
            NativeSpawner spawner = NativeSpawner.Instance;
            NativeDef spearman = natives != null ? natives.Find("spearman") : null;

            if (health == null || body == null || ragdoll == null || spawner == null || spearman == null)
            {
                Debug.LogError("[PrisonTest] The player has no Health, Carryable or ragdoll, or there "
                               + "is no NativeSpawner or spearman role. Run with -scene island after "
                               + "NativeFactory.Build.");
                yield break;
            }

            // The POI spawner puts the hooks in after the server starts, and the island's NavMesh
            // arrives on its own schedule. Both are worth waiting for rather than racing.
            float hookDeadline = Time.time + WaitForHooks;
            HangPoint[] hooks = Hooks();

            while (Time.time < hookDeadline && hooks.Length == 0)
            {
                yield return new WaitForSeconds(0.5f);
                hooks = Hooks();
            }

            float navDeadline = Time.time + WaitForNavMesh;
            while (Time.time < navDeadline
                   && !NavMesh.SamplePosition(motor.transform.position, out _, 25f, NavMesh.AllAreas))
                yield return new WaitForSeconds(1f);

            WorldClock.Freeze(Noon);

            Village(hooks);

            if (hooks.Length == 0)
            {
                Debug.LogError("[PrisonTest] The island has no hang points at all. Re-bake the POIs.");
                Report();
                yield break;
            }

            yield return Hung(spawner, spearman, motor, health, stun, body, ragdoll, hooks);
            yield return CutDown(spawner, spearman, motor, health, stun, body, ragdoll, hooks);
            yield return Guards(spawner, spearman, motor, health, stun, body, ragdoll, hooks);
            yield return Expires(spawner, spearman, motor, health, stun, body, ragdoll, hooks);
            yield return Taken(spawner, spearman, motor, health, stun, body, ragdoll, hooks);

            WorldClock.Freeze(-1f);
            Report();
        }

        // ---------------------------------------------------------------- the village has a prison

        /// <summary>
        /// The geometry, before any behaviour means anything. Three hooks, in the village rather than
        /// wherever the bake happened to put them, all free, and none of them offering a prompt to
        /// somebody walking past an empty prison.
        /// </summary>
        void Village(HangPoint[] hooks)
        {
            Check($"the island has hang points in it ({hooks.Length})", hooks.Length > 0);

            if (hooks.Length == 0) return;

            Check($"there is more than one, so a second body is not dropped on the floor ({hooks.Length})",
                  hooks.Length >= 2);

            Landmark village = FindObjectsByType<Landmark>(FindObjectsSortMode.None)
                               .FirstOrDefault(l => l != null && l.Id == "NativeVillage");

            if (village != null)
            {
                float worst = hooks.Max(h => Vector3.Distance(h.transform.position,
                                                              village.transform.position));

                Debug.Log($"[PrisonTest] {hooks.Length} hook(s), the furthest {worst:0.0}m from the "
                          + $"middle of the village at {village.transform.position}.");

                Check($"the prison is inside the village ({worst:0.0}m from the middle)",
                      worst <= village.Radius);
            }

            Check("every hook starts empty", hooks.All(h => !h.IsOccupied));

            // Interact is a shared key. A free hook that answered would swallow every other gesture
            // anybody made while standing in the prison.
            Check("an empty hook offers nothing to the crosshair",
                  hooks.All(h => string.IsNullOrEmpty(h.Prompt)));

            float spread = hooks.Length < 2
                           ? 0f
                           : hooks.Max(a => hooks.Max(b => Vector3.Distance(a.transform.position,
                                                                           b.transform.position)));

            Check($"the hooks are not all in the same spot ({spread:0.0}m apart at the widest)",
                  hooks.Length < 2 || spread > 1f);
        }

        // ---------------------------------------------------------------- the haul ends on a hook

        /// <summary>
        /// The join between #107 and #108: a native told to deliver next to a hook puts the body on
        /// it rather than on the floor, and the player up there is still downed and still on a
        /// running clock.
        /// </summary>
        IEnumerator Hung(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                         StunState stun, Carryable body, RagdollController ragdoll, HangPoint[] hooks)
        {
            Reset(health, stun, hooks);
            yield return null;

            HangPoint hook = hooks[0];
            Vector3 camp = Ground(hook.transform.position);
            Vector3 origin = Ground(Beside(camp, 12f, 0f));

            motor.ServerTeleport(origin, 0f);
            yield return Settled();

            Native native = Place(spawner, def, Beside(origin, 5f, 270f), camp);
            if (native == null)
            {
                Check("a native can be placed next to the prison", false);
                yield break;
            }

            yield return Settled();

            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));

            yield return Until(() => native == null || native.IsHauling, GrabWindow);
            yield return Until(() => Holding(hooks, motor) != null || native == null || !native.IsHauling,
                               HaulWindow);
            yield return Settled();

            HangPoint used = Holding(hooks, motor);
            float drop = used == null
                         ? -1f
                         : Vector3.Distance(ragdoll.HipBone.position, used.CarrySocket.position);

            Debug.Log($"[PrisonTest] a haul ended at the prison: the body went to "
                      + $"{(used != null ? used.name : "the floor")}, hips {drop:0.00}m from its "
                      + $"socket, and the player has {health.BleedOutRemaining:0}s left on the timer.");

            Check("the body ends up on a hook rather than on the floor", used != null);
            Check("on the hook nearest the delivery point", used == hook);
            Check($"hanging from it rather than near it ({drop:0.00}m)", used != null && drop < 0.2f);
            Check("the carrier hands it over and goes back to work",
                  native == null || native.State != NativeState.Abduct);
            Check("the player is still downed rather than dead", health.IsDowned);
            Check("and still on a running clock", health.BleedOutRemaining > 0f);

            // Being strung up is not a rescue and not a death, so the prompt over the body is still
            // the one that matters most to whoever gets there.
            Check("a hung player is still something a friend can help up",
                  !string.IsNullOrEmpty(motor.GetComponent<Rescuable>()?.Prompt));

            Check("and the hook now offers a way to get them down",
                  used != null && used.Prompt == "Cut down");

            Clear(native);
            Reset(health, stun, hooks);
        }

        // ---------------------------------------------------------------- cutting somebody down

        /// <summary>
        /// The rescue interaction, and the thing it deliberately does not do. Cutting a body down
        /// frees it and nothing else: it falls, it is still downed, it is still on the clock, and the
        /// person who cut it down now has to carry it out of a village that is fully awake.
        /// </summary>
        IEnumerator CutDown(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                            StunState stun, Carryable body, RagdollController ragdoll, HangPoint[] hooks)
        {
            Reset(health, stun, hooks);
            yield return null;

            HangPoint hook = hooks[0];
            Vector3 camp = Ground(hook.transform.position);
            motor.ServerTeleport(Ground(Beside(camp, 4f, 0f)), 0f);
            yield return Settled();

            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));
            yield return Settled();

            Check("a body can be strung up to begin with", hook.ServerHang(body));
            yield return Settled();

            float before = health.BleedOutRemaining;

            Check("a hung body refuses a second tenant",
                  !hook.ServerHang(body) && hook.Occupant == motor.NetworkObject);

            // Nobody can cut themselves down: the victim is ragdolled on a hook, and if they could
            // reach the interaction the whole mechanic would be a two-second inconvenience.
            Check("the person hanging there cannot cut themselves down",
                  !hook.ServerCanInteract(motor.NetworkObject));

            hook.ServerCutDown(motor.NetworkObject, "the test cut them down");
            yield return Settled();

            Debug.Log($"[PrisonTest] a body was cut down with {health.BleedOutRemaining:0}s left "
                      + $"(it had {before:0}s on the hook): carried={body.IsCarried}, "
                      + $"downed={health.IsDowned}, the hook is free={!hook.IsOccupied}.");

            Check("cutting somebody down lets go of them", !body.IsCarried);
            Check("and frees the hook for the next one", !hook.IsOccupied);
            Check("and is not a rescue - they are still on the floor", health.IsDowned);
            Check("and does not stop the clock either", health.BleedOutRemaining > 0f
                                                        && health.BleedOutRemaining < before);

            Check("the freed hook goes quiet again", string.IsNullOrEmpty(hook.Prompt));

            Reset(health, stun, hooks);
        }

        // ---------------------------------------------------------------- a camp that is guarding

        /// <summary>
        /// The raid. A village with somebody hanging in it behaves as if it were midnight whatever
        /// the sun is doing, which is the difference between a rescue and a stealth run.
        ///
        /// What is measured is <see cref="Native.Alertness"/> - the number the sense sweep actually
        /// uses - and the two notice radii it produces, rather than whether one particular native
        /// spotted one particular body. That is deliberate: the final half of the sweep is a vision
        /// cone and a raycast that <c>-nativeTest</c> already measures against the same radius, and
        /// there is only ever one player body in a single-process harness - it cannot be the
        /// prisoner and the rescuer at the same time. So this checks the lever, and #55 checks what
        /// the lever moves.
        /// </summary>
        IEnumerator Guards(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                           StunState stun, Carryable body, RagdollController ragdoll, HangPoint[] hooks)
        {
            Reset(health, stun, hooks);
            WorldClock.Freeze(Noon);
            yield return null;

            float day = def.NoticeRadius(0f);
            float night = def.NoticeRadius(1f);

            Check($"a guarded camp has a wider net than a sleepy one ({day:0}m by day, {night:0}m at night)",
                  night > day + 4f);

            HangPoint hook = hooks[0];
            Vector3 camp = Ground(hook.transform.position);
            Vector3 post = Ground(Beside(camp, 3f, 180f));

            // Out of the way, so the native has nobody to be distracted by while this is measured.
            motor.ServerTeleport(Ground(Beside(camp, 4f, 0f)), 0f);
            yield return Settled();

            Native guard = Place(spawner, def, post, camp);
            if (guard == null)
            {
                Check("a native can be placed to stand guard", false);
                yield break;
            }

            // Long enough for at least one sense tick, which is where the guard flag is recomputed.
            yield return new WaitForSeconds(SenseWindow);

            float idle = guard.Alertness;
            bool guardingIdle = guard.Guarding;

            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));
            yield return Settled();

            Check("a prisoner can be put on the hook for this case", hook.ServerHang(body));
            yield return new WaitForSeconds(SenseWindow);

            float guarding = guard.Alertness;
            bool guardingHeld = guard.Guarding;

            hook.ServerCutDown(null, "the test took the prisoner back");
            yield return new WaitForSeconds(SenseWindow);

            float after = guard.Alertness;

            Debug.Log($"[PrisonTest] at noon a native at the prison went from alertness {idle:0.00} "
                      + $"(notice {def.NoticeRadius(idle):0}m, sight "
                      + $"{(def.NeedsSight(idle) ? "required" : "not needed")}) to {guarding:0.00} "
                      + $"(notice {def.NoticeRadius(guarding):0}m, sight "
                      + $"{(def.NeedsSight(guarding) ? "required" : "not needed")}) with somebody on "
                      + $"the hook, and back to {after:0.00} once they were cut down.");

            Check("an empty village at noon is not guarding anything", !guardingIdle);
            Check($"and behaves like the daylight it is (alertness {idle:0.00})",
                  Mathf.Approximately(idle, 0f));

            Check("a village holding somebody knows it", guardingHeld);
            Check($"and goes to full alert at noon (alertness {guarding:0.00})",
                  Mathf.Approximately(guarding, 1f));

            Check($"which is a real difference in reach "
                  + $"({def.NoticeRadius(idle):0}m -> {def.NoticeRadius(guarding):0}m)",
                  def.NoticeRadius(guarding) > def.NoticeRadius(idle) + 4f);

            Check("and means a rescuer no longer has to be seen to be noticed",
                  def.NeedsSight(idle) && !def.NeedsSight(guarding));

            Check("cutting the prisoner down lets the village go back to sleep",
                  !guard.Guarding && Mathf.Approximately(after, 0f));

            Clear(guard);
            Reset(health, stun, hooks);
        }

        // ---------------------------------------------------------------- the clock runs out

        /// <summary>
        /// The worst outcome, and the reason the haul is worth racing. Bleeding out while strung up
        /// kills you where you are hanging, and nothing lets go: the corpse stays on the hook, in the
        /// middle of a native camp, and getting it back is now a carry out plus a Revive Machine bill.
        /// </summary>
        IEnumerator Expires(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                            StunState stun, Carryable body, RagdollController ragdoll, HangPoint[] hooks)
        {
            Reset(health, stun, hooks);
            yield return null;

            HangPoint hook = hooks[0];
            Vector3 camp = Ground(hook.transform.position);
            motor.ServerTeleport(Ground(Beside(camp, 4f, 0f)), 0f);
            yield return Settled();

            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));
            yield return Settled();

            Check("a body can be hung for the timer to run out on", hook.ServerHang(body));
            yield return Settled();

            // Killing outright rather than waiting out a forty-five second bleed: the thing being
            // checked is what death does to the hook, not that Health can count.
            health.ServerKill(new DamageInfo(0f, DamageType.Blunt));
            yield return Settled();

            float drop = Vector3.Distance(ragdoll.HipBone.position, hook.CarrySocket.position);

            Debug.Log($"[PrisonTest] the clock ran out on a hung player: dead={health.IsDead}, "
                      + $"still on the hook={hook.IsOccupied}, hips {drop:0.00}m from the socket, "
                      + $"death {health.Deaths}.");

            Check("dying up there does not cut you down", hook.IsOccupied);
            Check("the corpse is still hanging where it died", drop < 0.2f);
            Check("and is still the hook's problem", hook.Occupant == motor.NetworkObject);
            Check("somebody can still come and get it", hook.Prompt == "Cut down");
            Check("and it is a corpse, so it is the Revive Machine's bill now", health.IsDead);

            hook.ServerCutDown(null, "the test finished with it");
            yield return Settled();

            Check("a corpse can be cut down like anything else", !hook.IsOccupied && !body.IsCarried);

            Reset(health, stun, hooks);
        }

        // ---------------------------------------------------------------- one hook, one body

        /// <summary>
        /// Occupancy, from the delivering native's side. A hook holds one body; a second haul arriving
        /// at a village whose first hook is taken goes to the next one along, and to the ground when
        /// there is no next one. A village that stacked two bodies on a hook would be the same bug as
        /// two natives carrying one player, seen from the other end.
        ///
        /// This is checked through <see cref="HangPoint.ServerFree"/>, which is the whole of the
        /// decision, rather than by staging a second abduction - there is only one player body in a
        /// single-process harness, and it is already hanging up.
        /// </summary>
        IEnumerator Taken(NativeSpawner spawner, NativeDef def, PlayerMotor motor, Health health,
                          StunState stun, Carryable body, RagdollController ragdoll, HangPoint[] hooks)
        {
            Reset(health, stun, hooks);
            yield return null;

            HangPoint first = hooks[0];
            Vector3 camp = Ground(first.transform.position);

            motor.ServerTeleport(Ground(Beside(camp, 4f, 0f)), 0f);
            yield return Settled();

            HangPoint before = HangPoint.ServerFree(camp);
            Check("a native arriving at an empty prison finds a hook", before != null);

            health.ServerDown(new DamageInfo(0f, DamageType.Blunt));
            yield return Settled();

            Check("and can hang somebody on it", before != null && before.ServerHang(body));
            yield return Settled();

            HangPoint next = HangPoint.ServerFree(camp);

            int reachable = hooks.Count(h => Vector3.Distance(h.transform.position, camp) <= h.ClaimRadius);

            Debug.Log($"[PrisonTest] {reachable} hook(s) are within reach of this delivery point; "
                      + $"with one of them full the next haul is offered "
                      + $"{(next != null ? next.name : "the ground")}.");

            Check("a full hook is never offered twice", next != before);
            Check("the second body goes to the hook next to it", next != null && !next.IsOccupied);
            Check("and the first one is still holding the first body",
                  before != null && before.Occupant == motor.NetworkObject);

            // A camp with no prison in it - the cave outpost, and every native who goes down somewhere
            // improvised. Nothing within reach means the body is put on the ground, which is an
            // outcome rather than an error; -abductTest measures that end of it in full.
            Vector3 nowhere = camp + Vector3.right * 500f;
            Check("a camp with no prison in it is offered nothing rather than a hook far away",
                  HangPoint.ServerFree(nowhere) == null);

            Free(before);
            yield return Settled();

            Check("cutting the prisoner down puts the hook back in the pool",
                  HangPoint.ServerFree(camp) != null);

            Reset(health, stun, hooks);
        }

        // ---------------------------------------------------------------- helpers

        static HangPoint[] Hooks()
            => FindObjectsByType<HangPoint>(FindObjectsSortMode.None)
               .Where(h => h != null && h.IsSpawned)
               .OrderBy(h => h.name)
               .ToArray();

        static void Free(HangPoint hook)
        {
            if (hook != null && hook.IsOccupied) hook.ServerCutDown(null, "the test cleaned up");
        }

        /// <summary>Whichever hook is holding this body, or null.</summary>
        static HangPoint Holding(HangPoint[] hooks, PlayerMotor motor)
            => hooks.FirstOrDefault(h => h != null && h.Occupant == motor.NetworkObject);

        /// <summary>
        /// A native on the NavMesh at a spot, with its camp somewhere it can actually walk to. Mobile
        /// by definition: everything here is about walking.
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

        /// <summary>
        /// Alive, healed, unstunned and off every hook. The whole prison is cleared rather than the
        /// one hook a case was watching: the first run of this test left a body on the hook next door,
        /// and every case after it measured a village that was still holding somebody.
        /// </summary>
        static void Reset(Health health, StunState stun, HangPoint[] hooks)
        {
            if (hooks != null)
                foreach (HangPoint hook in hooks) Free(hook);

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
            string line = $"[PrisonTest] {_passed} passed, {_failed} failed.";

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
            Debug.LogError($"[PrisonTest] FAILED: {what}.");
        }
    }
}
