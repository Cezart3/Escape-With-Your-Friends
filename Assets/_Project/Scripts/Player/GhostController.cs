using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// What a dead player does with their hands. See #26.
    ///
    /// Death already leaves the body where it fell and pulls the camera out to third person, which is
    /// enough to watch but not enough to care. A player who can only watch alt-tabs, and a co-op game
    /// where a quarter of the lobby has alt-tabbed is a co-op game that ends early. So the dead get
    /// two things: they can fly, and they can shove.
    ///
    /// The ghost is deliberately *not* a NetworkObject. Spawning one would mean a second prefab, a
    /// second spawn/despawn dance around every death and revive, and a NetworkTransform paying for
    /// twenty-five updates a second of a thing nobody can see. Instead the owner makes a bare
    /// <see cref="Transform"/> locally, flies it, and reports where it is at 10Hz into a SyncVar. The
    /// server only ever needs that position to answer one question — "were you close enough to touch
    /// that?" — and 100ms of staleness on a ghost that moves 6m/s is well inside the tolerance the
    /// shove already has to allow for latency.
    ///
    /// The root exists from <see cref="OnStartClient"/> onward, glued to the hip while the player is
    /// alive rather than created on death. That removes an ordering problem: <see cref="DeathCamera"/>
    /// and this class both listen for the same state change, and whichever runs second would otherwise
    /// find a null ghost or a ghost still sitting at the origin. Glued-and-idle costs one transform
    /// assignment per frame and makes death instant and correct from either direction.
    ///
    /// The shove is measured in newton-seconds against a skeleton that weighs about 56kg, with a 14kg
    /// pelvis and 4kg limbs. That scale matters: the throw a living player gets is 12 Ns, but it always
    /// lands on the pelvis, while a shove lands on whichever bone the cast touched — so one number
    /// would be a kick to a shin and nothing at all to a body. 25 Ns is a fast kick on a limb and about
    /// half a metre per second on the whole corpse — enough to start a body rolling on any slope, and
    /// on flat ground stopped by friction inside a couple of centimetres, which is what the word nudge
    /// means. "Not a weapon" is not enforced by the magnitude anyway — the shove has no damage path at
    /// all, at any strength.
    ///
    /// The "cannot deal damage or pick up items" half of the acceptance criteria needed no code at
    /// all: every combat verb already re-checks <see cref="Health.IsIncapacitated"/> *inside* its
    /// ServerRpc — <c>Weapon.ServerAttack</c>, <c>TaserWeapon</c>, <c>CarrySystem.ServerPickup</c>,
    /// <c>PlayerInteractor.ServerInteract</c>, <c>PlayerMotor</c> — so a dead client that calls them
    /// anyway is refused by the server, not merely by its own UI. The <c>-ghostTest</c> hook below
    /// calls all three on purpose, from a corpse, to keep that true.
    /// </summary>
    public class GhostController : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] Health _health;
        [SerializeField] RagdollController _ragdoll;
        [SerializeField] PlayerInputReader _input;

        [Header("Test targets")]
        [Tooltip("Only used by -ghostTest, which tries to use them from beyond the grave.")]
        [SerializeField] Combat.Weapon _weapon;
        [SerializeField] CarrySystem _carry;
        [SerializeField] PlayerInteractor _interactor;

        [Header("Flight")]
        [Tooltip("Metres per second. Slower than running: the ghost is a camera, not a racer.")]
        [SerializeField] float _speed = 6f;

        [Tooltip("Sprint multiplier, for crossing the island back to where your friends are.")]
        [SerializeField] float _sprintMultiplier = 2.5f;

        [Tooltip("Crouch descends at this rate. Ascending is looking up and flying forward.")]
        [SerializeField] float _verticalSpeed = 4f;

        [Tooltip("Metres from your own corpse. A leash, so the dead cannot scout the map for the living.")]
        [SerializeField] float _tether = 60f;

        [Header("Nudge")]
        [Tooltip("Reach of the shove, from the ghost, along where it is looking.")]
        [SerializeField] float _nudgeRange = 3f;

        [Tooltip("Newton-seconds. Enough to roll a body downhill, not enough to be a weapon.")]
        [SerializeField] float _nudgeImpulse = 25f;

        [SerializeField] float _nudgeCooldown = 0.6f;

        [Tooltip("Slack the server allows on the range check, covering latency and SyncVar staleness.")]
        [SerializeField] float _serverRangeTolerance = 2.5f;

        [SerializeField] LayerMask _nudgeMask = ~0;

        // Where the owner says its ghost is. Read by the server to validate a shove; kept as a SyncVar
        // rather than an RPC argument so that drawing other people's ghosts later costs nothing new.
        readonly SyncVar<Vector3> _position = new(new SyncTypeSettings(0.1f));

        Transform _root;
        float _nextReportAt;
        float _nextNudgeAt;       // Owner-side, so a held button does not spam the server.
        float _serverNextNudgeAt; // Server-side, because the owner-side one is a suggestion.

        // -ghostTest, in three beats: issue the forbidden verbs, read what the server made of them
        // and shove, then measure whether the shove moved anything.
        int _testPhase;
        float _testAt;
        Vector3 _testDriftFrom;
        Vector3 _testShoveFrom;
        float _testDrift;
        float _testRestSpeed;
        float _testPeakSpeed;

        /// <summary>The transform the dead player is flying. Never null on the owner's client.</summary>
        public Transform Root => _root;

        /// <summary>True while this player is dead and therefore actually haunting something.</summary>
        public bool IsActive => _root != null && _health != null && _health.IsDead;

        public override void OnStartClient()
        {
            base.OnStartClient();

            // No `enabled = false` for non-owners, unlike every other owner-only component here: this
            // one has to receive ObserversNudge on every peer, because the shove is applied per-peer.
            // Update() guards itself instead.
            if (!IsOwner) return;

            var go = new GameObject($"Ghost (owner {OwnerId})");
            _root = go.transform;
            _root.SetPositionAndRotation(AnchorPoint(), transform.rotation);

            int testAfter = CommandLine.GetInt("-ghostTest", 0);
            if (testAfter > 0)
            {
                _testPhase = 1;
                _testAt = Time.time + testAfter;
            }
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            if (_root != null) Destroy(_root.gameObject);
            _root = null;
        }

        void Update()
        {
            if (!IsOwner || _root == null) return;

            if (_health == null || !_health.IsDead)
            {
                // Alive: park the ghost on the body so that the frame death lands on, it is already in
                // the right place and the camera has something sane to blend to.
                _root.SetPositionAndRotation(AnchorPoint(), transform.rotation);
                return;
            }

            Fly();
            Report();
            RunTests();
        }

        /// <summary>
        /// Free flight from the input the reader already produces. No new bindings, no new action map:
        /// the ghost is the same look-and-move the living body uses, minus gravity and a collider.
        /// Clipping through the terrain is not a bug here — a ghost that can be trapped inside a rock
        /// is worse than one that can peek inside it.
        /// </summary>
        void Fly()
        {
            if (_input == null) return;

            _root.rotation = Quaternion.Euler(_input.Pitch, _input.Yaw, 0f);

            Vector2 move = _input.Move;
            float speed = _speed * (_input.Sprint ? _sprintMultiplier : 1f);
            Vector3 delta = (_root.forward * move.y + _root.right * move.x) * speed;

            // Ascending is looking up and flying forward, which falls out of the pitch already being
            // in the rotation. Only descending needs a key, and crouch is free while dead.
            if (_input.Crouch) delta += Vector3.down * _verticalSpeed;

            _root.position += delta * Time.deltaTime;

            // The leash. Clamped on the owner so flying into it feels like a wall rather than a
            // rubber band; the server re-checks the same distance before honouring a shove.
            Vector3 anchor = AnchorPoint();
            Vector3 offset = _root.position - anchor;
            if (offset.sqrMagnitude > _tether * _tether)
                _root.position = anchor + offset.normalized * _tether;
        }

        void Report()
        {
            if (Time.time < _nextReportAt) return;

            _nextReportAt = Time.time + 0.1f;
            ServerReportPosition(_root.position);
        }

        /// <summary>Hip bone while there is a skeleton to read; the body root otherwise.</summary>
        Vector3 AnchorPoint()
            => _ragdoll != null && _ragdoll.HipBone != null ? _ragdoll.HipBone.position : transform.position;

        /// <summary>
        /// Owner side of the shove. Picks what and which way; never how hard. Returns whether anything
        /// was actually aimed at, so the caller can fall through to another verb if not.
        /// </summary>
        public bool RequestNudge()
        {
            if (!IsOwner || !IsActive) return false;
            if (Time.time < _nextNudgeAt) return false;

            // Same fat sphere cast as carrying, for the same reason: a thin ray misses a body lying
            // on the ground, and missing your friend's corpse three times is not funny frustration.
            if (!Physics.SphereCast(_root.position, 0.35f, _root.forward, out RaycastHit hit,
                                    _nudgeRange, _nudgeMask, QueryTriggerInteraction.Ignore))
                return false;

            NetworkObject target = hit.collider.GetComponentInParent<NetworkObject>();
            if (target == null) return false;

            // A sphere cast that starts already overlapping its target reports distance 0 and a hit
            // point of Vector3.zero. Handing that to the server would put the shove at the world
            // origin and fail its own range check, so name a point just in front of the ghost.
            Vector3 point = hit.distance <= 0f ? _root.position + _root.forward * 0.35f : hit.point;

            _nextNudgeAt = Time.time + _nudgeCooldown;

            // Forced report before the shove rather than waiting for the 10Hz tick: both are reliable
            // RPCs from the same behaviour, so ordering is guaranteed, and the server therefore
            // validates against where the ghost is now instead of up to 100ms ago.
            ServerReportPosition(_root.position);
            ServerNudge(target, _root.forward, point);
            return true;
        }

        [ServerRpc]
        void ServerReportPosition(Vector3 position)
        {
            _position.Value = position;
        }

        [ServerRpc]
        void ServerNudge(NetworkObject target, Vector3 direction, Vector3 point)
        {
            if (target == null) return;

            // Only the dead haunt. A living client that forges this RPC gets nothing.
            if (_health == null || !_health.IsDead) return;
            if (Time.time < _serverNextNudgeAt) return;

            float maxDistance = _nudgeRange + _serverRangeTolerance;
            if ((point - _position.Value).sqrMagnitude > maxDistance * maxDistance) return;

            direction = direction.sqrMagnitude < 0.0001f ? Vector3.forward : direction.normalized;
            _serverNextNudgeAt = Time.time + _nudgeCooldown;

            // Magnitude is the server's word, always. The client said what and which way.
            ObserversNudge(target, direction * _nudgeImpulse, point);
        }

        /// <summary>
        /// Applied on every peer, not just the server, and that is not an optimisation — it is the
        /// only thing that works. Ragdoll bones are simulated locally and never replicated, so an
        /// impulse added on the server alone is a shove nobody else ever sees. Same reasoning as
        /// <c>Health.ObserversIncapacitated</c> and <c>Carryable.ObserversThrow</c>.
        /// </summary>
        [ObserversRpc(RunLocally = true)]
        void ObserversNudge(NetworkObject target, Vector3 impulse, Vector3 point)
        {
            if (target == null) return;

            var ragdoll = target.GetComponent<RagdollController>();
            if (ragdoll != null && ragdoll.IsRagdolled)
            {
                // Goes through the same call a punch does, which picks the bone nearest the hit —
                // so a ghost shoving a corpse in the ribs and a friend punching it there behave the
                // same way, and there is only one piece of ragdoll physics to tune.
                ragdoll.EnableRagdoll(impulse, point);
                Log(target, impulse, point, "ragdoll", ragdoll);
                return;
            }

            // Anything else: a crate, a barrel, a dropped weapon. A standing player's bones are
            // kinematic, so "ghosts cannot shove the living" needs no rule of its own — it falls out.
            Rigidbody body = target.GetComponentInChildren<Rigidbody>();
            if (body == null || body.isKinematic) return;

            body.AddForceAtPosition(impulse, point, ForceMode.Impulse);
            Log(target, impulse, point, $"rigidbody {body.name}", null);
        }

        void Log(NetworkObject target, Vector3 impulse, Vector3 point, string via, RagdollController ragdoll)
        {
            string bone = "";
            if (ragdoll != null)
            {
                // Which bone actually took the hit, and whether it was in a state to accept one. A
                // shove that lands on a bone still flagged kinematic goes nowhere and looks, from the
                // outside, exactly like a shove that never arrived.
                Rigidbody nearest = null;
                float best = float.MaxValue;
                foreach (Rigidbody b in ragdoll.Bones)
                {
                    float d = (b.position - point).sqrMagnitude;
                    if (d >= best) continue;
                    best = d;
                    nearest = b;
                }

                if (nearest != null)
                    bone = $" on {nearest.name} ({nearest.mass:0.#}kg, kinematic={nearest.isKinematic}, "
                           + $"sleeping={nearest.IsSleeping()}, v={nearest.linearVelocity.magnitude:0.00})";
            }

            Debug.Log($"[GhostController] Ghost of owner {OwnerId} nudged {target.name} "
                      + $"with {impulse.magnitude:0.0} Ns at {point} via {via}{bone}.");
        }

        /// <summary>
        /// -ghostTest &lt;seconds&gt;: proves both halves of #26 from a headless corpse. Beat one asks
        /// the server for a punch, a pickup and an interact, all of which it must refuse. Beat two
        /// reads back what the server actually did and shoves the one physics object every headless
        /// run is guaranteed to have — the player's own body. Beat three checks the body moved.
        /// </summary>
        void RunTests()
        {
            // Sampled every frame of the shove window, not at a fixed offset after it. The shove is a
            // round trip — ServerRpc out, ObserversRpc back — so on a 30Hz tick it lands somewhere
            // between one and three frames later, and a fixed sample either reads before it arrives or
            // after friction has eaten it. A peak over the window has neither problem.
            if (_testPhase == 3) _testPeakSpeed = Mathf.Max(_testPeakSpeed, FastestBone());

            if (_testPhase == 0 || Time.time < _testAt) return;

            if (_testPhase == 1)
            {
                ParkBehindCorpse();

                if (_weapon != null) _weapon.RequestAttack();
                if (_carry != null) _carry.RequestPickupOrDrop();
                bool interacted = _interactor != null && _interactor.RequestInteract();

                Debug.Log($"[GhostController] -ghostTest: owner {OwnerId} is dead and asked for a punch, "
                          + $"a pickup and an interact. interact={interacted} (expected False).");

                _testDriftFrom = Centroid();
                _testPhase = 2;
                _testAt = Time.time + 1.5f;
                return;
            }

            if (_testPhase == 2)
            {
                // The control. A ragdoll that has just been dropped is still settling, so measuring
                // only "did the body move after the shove" proves nothing — it moves anyway. This is
                // the same length of window with no shove in it, to subtract that away.
                _testDrift = (Centroid() - _testDriftFrom).magnitude;

                bool carrying = _carry != null && _carry.IsCarrying;

                // Park again rather than trusting the spot from beat one: over the control window the
                // corpse slid, and the cast is aimed from wherever the ghost happens to be. A real
                // ghost re-aims every frame with a mouse; the test has to do it by hand.
                ParkBehindCorpse();

                _testShoveFrom = Centroid();
                _testRestSpeed = FastestBone();
                _testPeakSpeed = 0f;
                bool nudged = RequestNudge();

                Debug.Log($"[GhostController] -ghostTest: server verdict carrying={carrying} (expected False), "
                          + $"state={_health.State}. Ghost at {_root.position}, nudged={nudged} (expected True). "
                          + $"Settling drift over the control window was {_testDrift:0.00}m, fastest bone "
                          + $"{_testRestSpeed:0.00}m/s.");

                _testPhase = 3;
                _testAt = Time.time + 1.5f;
                return;
            }

            float shoved = (Centroid() - _testShoveFrom).magnitude;
            string control = _testDrift > 0.01f
                ? $"{_testDrift:0.00}m of settling over the control window"
                : "a corpse that was already at rest";

            // Speed is the verdict, not distance. The arena floor is flat, and friction stops a 56kg
            // skeleton moving at half a metre per second inside a couple of centimetres — so a shove
            // that plainly landed still barely relocates the body. On a slope it would keep going.
            Debug.Log($"[GhostController] -ghostTest: fastest bone peaked at {_testPeakSpeed:0.00}m/s over "
                      + $"the shove window against {_testRestSpeed:0.00}m/s at rest, and the skeleton "
                      + $"moved {shoved:0.00}m against {control}.");

            _testPhase = 0;
        }

        /// <summary>
        /// Puts the ghost a step behind its own hip, facing it. Nothing binds input in batchmode, so
        /// yaw and pitch stay zero and the ghost faces +Z; sitting at -Z of the hip therefore aims the
        /// cast straight down the length of the corpse.
        /// </summary>
        void ParkBehindCorpse()
            => _root.SetPositionAndRotation(AnchorPoint() + new Vector3(0f, 0f, -1.2f), Quaternion.identity);

        /// <summary>
        /// Mean bone position. The hip alone is a bad measure of a shove — the impulse lands on
        /// whichever bone the cast touched, usually a limb, and a limb can be flung a long way while
        /// the pelvis stays put on the ground.
        /// </summary>
        /// <summary>Speed of the quickest bone. Zero on a corpse nothing has touched.</summary>
        float FastestBone()
        {
            if (_ragdoll == null) return 0f;

            float fastest = 0f;
            foreach (Rigidbody bone in _ragdoll.Bones)
                fastest = Mathf.Max(fastest, bone.linearVelocity.magnitude);

            return fastest;
        }

        Vector3 Centroid()
        {
            if (_ragdoll == null || _ragdoll.Bones.Count == 0) return AnchorPoint();

            Vector3 sum = Vector3.zero;
            foreach (Rigidbody bone in _ragdoll.Bones) sum += bone.position;
            return sum / _ragdoll.Bones.Count;
        }
    }
}
