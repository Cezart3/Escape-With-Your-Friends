using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// The rescuer's half of the rescue: a hold timer that the server owns.
    ///
    /// **The hold is timed on the server, not the client.** A client-side countdown ending in one
    /// "I rescued them" message is a single number a modified build sets to zero, and unlike a
    /// mistimed punch this one undoes a death. So the client only ever sends two things — the press,
    /// which arrives through <see cref="PlayerInteractor"/> and gets the same range validation every
    /// other interaction does, and the release. Everything between them is the server watching.
    ///
    /// What the server re-checks every frame, and what each check is actually for:
    /// <list type="bullet">
    /// <item>the rescuer is still alive and unstunned — a punch to the helper stops the help;</item>
    /// <item>the rescuer has not taken damage since the hold began — this is the interrupt the whole
    /// mechanic is built around, and it is why a firefight is a bad place to pick someone up;</item>
    /// <item>the target is still downed — dead, helped up by someone else, or abducted all end it;</item>
    /// <item>the two are still within <see cref="_breakDistance"/> — walking away cancels, and no
    /// message from the client is needed to notice.</item>
    /// </list>
    ///
    /// Progress is not stored here. It is written onto the victim's <see cref="Rescuable"/>, because
    /// that is the body the HUD is already drawing a marker over.
    /// </summary>
    public class RescueSystem : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] Health _health;
        [SerializeField] StunState _stun;

        [Header("Rules")]
        [Tooltip("Seconds of held Interact needed to get a teammate off the floor.")]
        [SerializeField] float _rescueSeconds = 3.5f;

        [Tooltip("How far the two can drift apart before the hold breaks. Interact reach plus slack.")]
        [SerializeField] float _breakDistance = 5f;

        Rescuable _target;
        float _progress;

        /// <summary>
        /// Health the rescuer had when the hold started. Compared rather than hooked to an event:
        /// one number read per frame is cheaper than the subscribe/unsubscribe bookkeeping, and it
        /// also catches damage that arrived in the same frame as the press.
        /// </summary>
        float _healthAtStart;

        // -rescueTest, host only. See RunTest.
        static bool _testClaimed;
        bool _testOwner;
        int _testPhase;
        float _testAt;
        Rescuable _testVictim;
        bool _testInterrupted;

        /// <summary>Who this player is currently helping up, or null. Server-side truth.</summary>
        public Rescuable Target => _target;

        /// <summary>Hold progress from 0 to 1. Server-side; clients read the victim's copy.</summary>
        public float Progress => _progress;

        /// <summary>Seconds of holding a full rescue takes. Read by the HUD to size its bar.</summary>
        public float RescueSeconds => _rescueSeconds;

        void Awake()
        {
            if (_health == null) _health = GetComponent<Health>();
            if (_stun == null) _stun = GetComponent<StunState>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            float delay = CommandLine.GetFloat("-rescueTest", -1f);
            if (delay <= 0f) return;

            // Every player body on the server carries this component, so without a claim the flag
            // arms one test per player and they all knock each other down at once — leaving nobody
            // upright to do any helping, and a run that fails for a reason the mechanic never had.
            // One body takes the test; the rest are what it looks for.
            if (_testClaimed) return;

            _testClaimed = true;
            _testOwner = true;
            _testPhase = 1;
            _testAt = Time.time + delay;
        }

        public override void OnStopServer()
        {
            ServerCancel();

            if (_testOwner)
            {
                _testClaimed = false;
                _testOwner = false;
            }

            base.OnStopServer();
        }

        /// <summary>Server only. Whether this player is in a state to be helping anyone.</summary>
        public bool ServerCanRescue()
        {
            if (!IsServerStarted) return false;
            if (_health != null && !_health.IsAlive) return false;
            if (_stun != null && _stun.IsStunned) return false;
            return true;
        }

        /// <summary>
        /// Server only. Begins the hold. Called by <see cref="Rescuable.ServerInteract"/>, which has
        /// already had the actor's range checked by <see cref="PlayerInteractor"/>.
        /// </summary>
        public void ServerBegin(Rescuable target)
        {
            if (!IsServerStarted || target == null || !ServerCanRescue()) return;

            // Re-pressing on the same body continues rather than restarts. Interact is buffered as a
            // press, so a client whose frame rate stutters mid-hold can easily send a second one, and
            // losing three seconds of progress to that would read as the mechanic being broken.
            if (_target == target) return;

            ServerCancel();

            _target = target;
            _progress = 0f;
            _healthAtStart = _health != null ? _health.Current : 0f;

            target.ServerSetRescue(NetworkObject, 0f);
        }

        /// <summary>Server only. Ends the hold without helping anyone up.</summary>
        public void ServerCancel()
        {
            if (_target == null) return;

            // Guarded: the victim clears its own state when it stops being downed, and calls back
            // here to do the same. Without the null-out first this would bounce between the two.
            Rescuable target = _target;
            _target = null;
            _progress = 0f;

            if (target.Rescuer == NetworkObject) target.ServerSetRescue(null, 0f);
        }

        /// <summary>Owner side. Interact was released, so stop holding.</summary>
        public void NotifyReleased()
        {
            if (IsOwner) ServerRelease();
        }

        [ServerRpc]
        void ServerRelease() => ServerCancel();

        void Update()
        {
            if (!IsServerStarted) return;

            RunTest();

            if (_target == null) return;

            if (!ServerCanRescue()
                || _target.Health == null
                || !_target.Health.IsDowned
                || (_health != null && _health.Current < _healthAtStart - 0.01f)
                || (_target.transform.position - transform.position).sqrMagnitude
                   > _breakDistance * _breakDistance)
            {
                if (_testPhase > 0) _testInterrupted = true;
                ServerCancel();
                return;
            }

            _progress += _rescueSeconds > 0f ? Time.deltaTime / _rescueSeconds : 1f;
            _target.ServerSetRescue(NetworkObject, _progress);

            if (_progress < 1f) return;

            Rescuable rescued = _target;
            ServerCancel();
            rescued.Health.ServerRescue();
        }

        /// <summary>
        /// <c>-rescueTest &lt;seconds&gt;</c>, host only. Drives the server half of the rescue with no
        /// keyboard and no camera, and proves both outcomes rather than only the happy one.
        ///
        /// The sphere cast is the one part that cannot be tested headlessly — it needs a camera
        /// pointed at a body — so the test starts where <see cref="Rescuable.ServerInteract"/> starts,
        /// after the aim has already resolved. Everything the mechanic actually guards is downstream
        /// of that: the timing, the distance, the damage interrupt and the state change.
        ///
        /// Phase 1 downs a teammate and drags it to the rescuer's feet. Phase 2 begins a hold and,
        /// halfway through, punches the rescuer — the run passes only if that cancels. Phase 3 begins
        /// again and lets it finish, which must put the victim back on <see cref="LifeState.Alive"/>.
        /// </summary>
        void RunTest()
        {
            if (_testPhase == 0 || Time.time < _testAt) return;

            switch (_testPhase)
            {
                case 1:
                {
                    _testVictim = FindVictim();
                    if (_testVictim == null)
                    {
                        Debug.LogWarning("[RescueSystem] -rescueTest: no other player to down; skipped.");
                        _testPhase = 0;
                        return;
                    }

                    _testVictim.Health.ServerDown(new DamageInfo(0f, DamageType.Blunt));

                    // Dropped in front of the rescuer, because distance is one of the things the hold
                    // checks and a bot that wandered off would fail the test for the wrong reason.
                    Vector3 spot = transform.position + transform.forward * 1.5f + Vector3.up * 0.5f;
                    var ragdoll = _testVictim.GetComponent<RagdollController>();
                    if (ragdoll != null) ragdoll.TeleportSkeleton(spot);

                    var motor = _testVictim.GetComponent<PlayerMotor>();
                    if (motor != null) motor.ServerTeleport(spot, transform.eulerAngles.y);
                    else _testVictim.transform.position = spot;

                    Debug.Log($"[RescueSystem] -rescueTest: downed owner {_testVictim.OwnerId} at "
                              + $"{spot}, {_testVictim.Health.BleedOutRemaining:0.0}s to bleed out.");

                    // Three seconds on the floor before anyone reaches for them, not half a second.
                    // The gap is what the HUD test needs: a window where the victim is simply down,
                    // long enough for a once-a-second sample on both peers to land inside it and
                    // print the bleed-out countdown. Without it every sample lands mid-rescue and
                    // the number the HUD exists to show is never observed.
                    _testPhase = 2;
                    _testAt = Time.time + 3f;
                    return;
                }

                case 2:
                {
                    ServerBegin(_testVictim);
                    _testInterrupted = false;

                    _testPhase = 3;
                    _testAt = Time.time + _rescueSeconds * 0.5f;
                    return;
                }

                case 3:
                {
                    float before = _progress;

                    // A single point of damage. The interrupt is not about how hard you were hit; it
                    // is about whether anyone is shooting at you at all.
                    _health.TakeDamage(new DamageInfo(1f, DamageType.Blunt));

                    Debug.Log($"[RescueSystem] -rescueTest: rescuer took 1 damage at "
                              + $"{before * 100f:0}% of the hold.");

                    _testPhase = 4;
                    _testAt = Time.time + 0.2f;
                    return;
                }

                case 4:
                {
                    Debug.Log($"[RescueSystem] -rescueTest: after damage the hold is "
                              + $"{(_target == null ? "cancelled" : "STILL RUNNING")}, victim is "
                              + $"{_testVictim.Health.State}, interrupt flag {_testInterrupted}.");

                    // Health changed, so the next hold has to bank the new number or it would cancel
                    // itself immediately. ServerBegin does that; this is just the second attempt.
                    ServerBegin(_testVictim);

                    _testPhase = 5;
                    _testAt = Time.time + _rescueSeconds + 0.5f;
                    return;
                }

                default:
                {
                    Debug.Log($"[RescueSystem] -rescueTest: uninterrupted hold finished with victim "
                              + $"{_testVictim.OwnerId} at {_testVictim.Health.State}, "
                              + $"{_testVictim.Health.Current:0}/{_testVictim.Health.Max:0} health.");

                    _testPhase = 0;
                    return;
                }
            }
        }

        Rescuable FindVictim()
        {
            foreach (NetworkPlayerRegistry.PlayerBody body in NetworkPlayerRegistry.Players)
            {
                if (!body.IsValid || body.Object == NetworkObject) continue;

                var rescuable = body.Object.GetComponent<Rescuable>();
                if (rescuable != null && rescuable.Health != null && rescuable.Health.IsAlive)
                    return rescuable;
            }

            return null;
        }
    }
}
