using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The expensive way back. Drag a corpse into the bay, press Interact, pay, watch the machine
    /// eat your friend and spit out a living one.
    ///
    /// This is the far end of the death loop and the reason the loop has stakes at all. Being downed
    /// costs your friends a walk. Being dead costs them money — money that was going to buy the boat.
    /// The whole point is that letting the bleed-out timer expire is a bill somebody has to pay, and
    /// that the person who pays it is standing right next to the person who let it happen.
    ///
    /// **Only the dead.** <see cref="Health.ServerRevive"/> refuses anything but
    /// <see cref="LifeState.Dead"/>, and nothing here works around that. A downed player carried to
    /// the machine is not a customer: they get picked up off the floor for free, wherever they are.
    /// So the machine cannot become the fast path, and hauling someone here can never be the
    /// *cheaper* option.
    ///
    /// **The cost is the content.** Base price plus a surcharge for every previous death this run,
    /// read from <see cref="Health.Deaths"/> on the body itself — so the friend who keeps dying gets
    /// more expensive, which is exactly the argument the game wants people to have. Validated
    /// server-side against the payer's <see cref="Wallet"/>; the client's balance is a display.
    ///
    /// **How the swallowing works.** The machine is an <see cref="ICarryHolder"/>, so eating a body
    /// is the same code path as a player picking it up: <see cref="Carryable"/> parents the hips to
    /// the intake socket on every peer and freezes the bones. The machine then drags that socket
    /// into its own housing over the cycle. No new attach path, no replicated animation — the intake
    /// moves identically on every machine because it is driven by a tick both sides already agree
    /// on. See <see cref="ICarryHolder"/> for why that interface exists.
    ///
    /// **An abandoned body is refused.** A corpse whose owner disconnected (see
    /// <see cref="BodyPersistence"/>) has nobody to walk out of the machine, and charging for that
    /// would be taking money for nothing. It is refused, unpaid, until reconnect adoption lands
    /// (#111) and hands the body back to whoever comes back for it.
    /// </summary>
    public class ReviveMachine : NetworkBehaviour, IInteractable, ICarryHolder
    {
        [Header("Geometry")]
        [Tooltip("Where a body has to be lying, or be held, for the machine to see it.")]
        [SerializeField] Transform _bay;

        [Tooltip("How far from the bay a corpse still counts as loaded.")]
        [SerializeField] float _bayRadius = 2.2f;

        [Tooltip("Socket the body hangs from while it is being swallowed.")]
        [SerializeField] Transform _intake;

        [Tooltip("Where the living player is put down afterwards.")]
        [SerializeField] Transform _exit;

        [Tooltip("Spins while the machine works. Cosmetic; may be null.")]
        [SerializeField] Transform _rotor;

        [Header("Animation")]
        [Tooltip("Local offset the intake travels over one cycle — in and up, into the housing.")]
        [SerializeField] Vector3 _intakeTravel = new(0f, 0.9f, -1.3f);

        [Tooltip("Degrees per second at full speed. Ramps up with the cycle, because it is funnier.")]
        [SerializeField] float _rotorSpeed = 720f;

        [Header("Rules")]
        [Tooltip("Price of a first death.")]
        [SerializeField] int _baseCost = 250;

        [Tooltip("Added for every previous death this run. Dying twice is not twice as bad, it is worse.")]
        [SerializeField] int _costPerDeath = 200;

        [Tooltip("Seconds the machine takes. Long enough to be an event, short enough to not be a wait.")]
        [SerializeField] float _cycleSeconds = 4f;

        [Tooltip("Health the revived player walks out with. Not full: the machine is not a hospital.")]
        [Range(0.05f, 1f)]
        [SerializeField] float _reviveHealthFraction = 0.5f;

        /// <summary>The body currently inside. Null when the machine is idle.</summary>
        readonly SyncVar<NetworkObject> _occupant = new();

        /// <summary>Tick the current cycle finishes on. Meaningless when idle.</summary>
        readonly SyncVar<uint> _cycleEndTick = new();

        /// <summary>Length of the current cycle in ticks, so clients can work out a progress bar.</summary>
        readonly SyncVar<uint> _cycleTicks = new();

        // Server-side only: who is paying, and what they paid, so a cancelled cycle can refund.
        Wallet _payer;
        int _price;

        Vector3 _intakeRest;

        // Headless test hook; see ScheduleTests.
        float _testAt = -1f;
        int _testStage;
        int _testBalance;

        /// <summary>True while a body is being processed.</summary>
        public bool IsBusy => _occupant.Value != null;

        /// <summary>0 to 1 across the current cycle. 0 when idle.</summary>
        public float Progress
        {
            get
            {
                if (!IsBusy || TimeManager == null || _cycleTicks.Value == 0) return 0f;

                uint now = TimeManager.Tick;
                uint end = _cycleEndTick.Value;
                if (now >= end) return 1f;

                return 1f - (end - now) / (float)_cycleTicks.Value;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// The prompt does not know whether you can afford it. That answer lives on the server and
        /// asking every frame would cost a round trip per frame; the refusal is instant and says why.
        /// </remarks>
        public string Prompt => IsBusy ? "Revive Machine (working)" : "Revive Machine";

        /// <inheritdoc />
        public Transform CarrySocket => _intake;

        void Awake()
        {
            if (_intake != null) _intakeRest = _intake.localPosition;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            ScheduleTests();
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            // Do not leave a corpse welded to a despawning machine's socket.
            if (IsBusy) Cancel("the machine shut down");
        }

        /// <inheritdoc />
        public bool ServerCanInteract(NetworkObject actor)
        {
            if (!IsServerStarted || actor == null) return false;
            if (IsBusy) return false;

            return FindLoadedBody() != null;
        }

        /// <inheritdoc />
        public void ServerInteract(NetworkObject actor)
        {
            if (!IsServerStarted || actor == null || IsBusy) return;

            Health body = FindLoadedBody();
            if (body == null) return;

            var persistence = body.GetComponent<BodyPersistence>();
            if (persistence != null && persistence.IsAbandoned)
            {
                Debug.Log($"[ReviveMachine] Refused: the body of owner {persistence.SpawnOwnerId} has "
                          + "no player behind it. Nothing to hand back until they reconnect (#111).");
                return;
            }

            int cost = PriceFor(body);

            var wallet = actor.GetComponent<Wallet>();
            if (wallet == null)
            {
                Debug.LogWarning($"[ReviveMachine] {actor.name} has no Wallet; nobody can be billed.");
                return;
            }

            if (!wallet.ServerTrySpend(cost))
            {
                Debug.Log($"[ReviveMachine] Refused: owner {actor.OwnerId} has {wallet.Balance} and "
                          + $"the cycle costs {cost}. Death {body.Deaths} is not free.");
                return;
            }

            _payer = wallet;
            _price = cost;

            Swallow(body);

            Debug.Log($"[ReviveMachine] Owner {actor.OwnerId} paid {cost} to revive owner "
                      + $"{body.OwnerId} (death {body.Deaths}). {wallet.Balance} left. "
                      + $"Cycle runs {_cycleSeconds}s.");
        }

        /// <summary>
        /// What reviving this body costs right now. Public because the shop UI and the HUD both want
        /// to show a number before anyone commits to it.
        /// </summary>
        public int PriceFor(Health body)
        {
            if (body == null) return _baseCost;

            // Deaths is already incremented by the time a corpse is lying here, so the first death
            // costs exactly the base price.
            int previous = Mathf.Max(0, body.Deaths - 1);
            return _baseCost + _costPerDeath * previous;
        }

        /// <summary>
        /// Server only. The dead body in the bay, if there is exactly one worth taking. Carried
        /// bodies count: walking up to the machine holding your friend is the intended gesture, and
        /// their hips are at your shoulder, which is inside the bay when you are standing in it.
        /// </summary>
        Health FindLoadedBody()
        {
            Transform bay = _bay != null ? _bay : transform;

            Collider[] hits = Physics.OverlapSphere(bay.position, _bayRadius, ~0,
                                                    QueryTriggerInteraction.Ignore);

            foreach (Collider hit in hits)
            {
                // In parents: what the sphere actually touches is a shin bone.
                var health = hit.GetComponentInParent<Health>();
                if (health == null || !health.IsDead) continue;

                // No body, no ride. A corpse with no ragdoll cannot be parented to the intake.
                if (health.GetComponent<Carryable>() == null) continue;

                return health;
            }

            return null;
        }

        /// <summary>Server only. Takes the body off whoever is holding it and hangs it in the intake.</summary>
        void Swallow(Health body)
        {
            var carryable = body.GetComponent<Carryable>();

            // Through the carrier rather than straight to the Carryable: a CarrySystem tracks what it
            // is holding in its own SyncVar, and detaching behind its back leaves it convinced it
            // still has a corpse in its arms.
            if (carryable.IsCarried)
            {
                var holder = carryable.Carrier.GetComponent<CarrySystem>();
                if (holder != null) holder.ServerForceDrop();
                else carryable.ServerDetach();
            }

            carryable.ServerAttach(NetworkObject);

            uint ticks = TimeManager != null ? TimeManager.TimeToTicks(_cycleSeconds) : 1u;
            _cycleTicks.Value = ticks;
            _cycleEndTick.Value = (TimeManager != null ? TimeManager.Tick : 0u) + ticks;
            _occupant.Value = body.NetworkObject;
        }

        void Update()
        {
            Animate();

            if (!IsServerStarted) return;

            RunTests();

            if (!IsBusy) return;

            // A body can leave mid-cycle: its owner disconnects and it is despawned, or the session
            // is coming down. Charging for that and keeping the money would be theft.
            if (!_occupant.Value.IsSpawned)
            {
                Cancel("the body left mid-cycle");
                return;
            }

            if (TimeManager == null || TimeManager.Tick < _cycleEndTick.Value) return;

            Complete();
        }

        /// <summary>
        /// Runs on every peer. The intake carries the body in and the rotor spins up, both driven by
        /// <see cref="Progress"/>, which is derived from a replicated tick — so this is the same
        /// animation everywhere without a single byte of animation traffic.
        /// </summary>
        void Animate()
        {
            float progress = Progress;

            if (_intake != null)
                _intake.localPosition = _intakeRest + _intakeTravel * progress;

            if (_rotor != null && IsBusy)
                _rotor.Rotate(Vector3.up, _rotorSpeed * progress * Time.deltaTime, Space.Self);
        }

        /// <summary>Server only. Hands back a living player at the exit.</summary>
        void Complete()
        {
            NetworkObject body = _occupant.Value;
            var health = body.GetComponent<Health>();
            var carryable = body.GetComponent<Carryable>();
            var ragdoll = body.GetComponent<RagdollController>();
            var motor = body.GetComponent<PlayerMotor>();

            // Off the socket first, so the hips are back under their own body before the life state
            // change tells every peer to stand up.
            if (carryable != null) carryable.ServerDetach();

            bool revived = health != null && health.ServerRevive(_reviveHealthFraction);

            Transform exit = _exit != null ? _exit : transform;
            if (ragdoll != null && ragdoll.HipBone != null)
                ragdoll.TeleportSkeleton(exit.position + Vector3.up * 1.1f);
            if (motor != null) motor.ServerTeleport(exit.position, exit.eulerAngles.y);

            Debug.Log($"[ReviveMachine] Cycle finished: owner {body.OwnerId} revived={revived} "
                      + $"state={(health != null ? health.State.ToString() : "?")} at {exit.position}. "
                      + $"Paid by owner {(_payer != null ? _payer.OwnerId : -1)}.");

            _occupant.Value = null;
            _payer = null;
            _price = 0;
        }

        /// <summary>Server only. Aborts a cycle and gives the money back.</summary>
        void Cancel(string why)
        {
            NetworkObject body = _occupant.Value;

            if (body != null && body.IsSpawned)
            {
                var carryable = body.GetComponent<Carryable>();
                if (carryable != null) carryable.ServerDetach();
            }

            if (_payer != null && _price > 0) _payer.ServerAdd(_price);

            Debug.Log($"[ReviveMachine] Cycle cancelled because {why}; refunded {_price}.");

            _occupant.Value = null;
            _payer = null;
            _price = 0;
        }

        /// <summary>
        /// Arms the headless regression, the same way FallGuard arms -fallTest and BodyPersistence
        /// arms -deathTest: the test lives inside the thing it tests, because a build flavour that
        /// only exists for tests is not the build anyone ships.
        ///
        ///   -machineTest 30      at 30s, drag a dead body to the bay and run the machine
        ///
        /// It runs the refusal and the sale in one pass. At T the payer's wallet is emptied and the
        /// machine is asked to work, which must fail; three seconds later the balance is restored and
        /// it is asked again, which must succeed. Proving both in one process is worth the small
        /// amount of stage-managing, and neither half is convincing without the other.
        /// </summary>
        void ScheduleTests()
        {
            int seconds = CommandLine.GetInt("-machineTest", 0);
            if (seconds <= 0) return;

            _testAt = Time.time + seconds;
            Debug.Log($"[ReviveMachine] -machineTest: first attempt in {seconds}s.");
        }

        void RunTests()
        {
            if (_testAt < 0f || Time.time < _testAt) return;

            Health body = FindAnyDeadBody();
            Wallet payer = FindPayer(body);

            if (body == null || payer == null)
            {
                Debug.Log($"[ReviveMachine] -machineTest: nothing to test with "
                          + $"(body={(body != null ? body.OwnerId.ToString() : "none")}, "
                          + $"payer={(payer != null ? payer.OwnerId.ToString() : "none")}).");
                _testAt = -1f;
                return;
            }

            Transform bay = _bay != null ? _bay : transform;

            if (_testStage == 0)
            {
                // Drag the corpse over. A test player has no hands and the machine only reads the bay.
                var ragdoll = body.GetComponent<RagdollController>();
                if (ragdoll != null && ragdoll.HipBone != null)
                    ragdoll.TeleportSkeleton(bay.position + Vector3.up * 0.4f);

                _testBalance = payer.Balance;
                payer.ServerSetBalance(0);

                Debug.Log($"[ReviveMachine] -machineTest: owner {payer.OwnerId} emptied, "
                          + $"attempting to revive owner {body.OwnerId} at price {PriceFor(body)}.");

                ServerInteract(payer.NetworkObject);

                Debug.Log($"[ReviveMachine] -machineTest: broke attempt busy={IsBusy} "
                          + $"(expected False), body state {body.State}.");

                _testStage = 1;
                _testAt = Time.time + 3f;
                return;
            }

            payer.ServerSetBalance(_testBalance);
            Debug.Log($"[ReviveMachine] -machineTest: owner {payer.OwnerId} refunded to "
                      + $"{payer.Balance}, attempting again.");

            ServerInteract(payer.NetworkObject);

            Debug.Log($"[ReviveMachine] -machineTest: funded attempt busy={IsBusy} (expected True), "
                      + $"balance now {payer.Balance}.");

            _testAt = -1f;
        }

        /// <summary>Server only. Any dead body with a player still behind it.</summary>
        static Health FindAnyDeadBody()
        {
            foreach (Health health in FindObjectsByType<Health>(FindObjectsSortMode.None))
            {
                if (!health.IsDead) continue;

                var persistence = health.GetComponent<BodyPersistence>();
                if (persistence != null && persistence.IsAbandoned) continue;

                return health;
            }

            return null;
        }

        /// <summary>Server only. Someone alive who is not the corpse, and therefore can be billed.</summary>
        static Wallet FindPayer(Health body)
        {
            foreach (Wallet wallet in FindObjectsByType<Wallet>(FindObjectsSortMode.None))
            {
                if (body != null && wallet.gameObject == body.gameObject) continue;

                var health = wallet.GetComponent<Health>();
                if (health == null || !health.IsAlive) continue;

                return wallet;
            }

            return null;
        }
    }
}
