using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// Server side. Puts a body back on the map once it has left it.
    ///
    /// A game whose central joke is throwing your friends around will drop one out of the world sooner
    /// or later: a ragdoll shoved hard enough into a surface tunnels through it, a thrown body clears
    /// the edge of the arena, a car punts someone off a cliff. Those are all the same physics that
    /// makes the game funny, and plugging each hole individually is a losing game — the island alone
    /// will have thousands of metres of coastline.
    ///
    /// So this is a net, not a fence. Nothing here prevents falling; it makes falling survivable. Past
    /// a height nothing legitimate reaches, the player is returned to a spawn point. See #110.
    /// </summary>
    public class FallGuard : NetworkBehaviour
    {
        [Tooltip("Below this world height a body is considered lost. Well under the lowest thing a "
                 + "player can legitimately stand on.")]
        [SerializeField] float _killHeight = -30f;

        [Tooltip("Seconds between checks. Terminal velocity is about 55 m/s, so this costs at most a "
                 + "few metres of extra falling before the rescue.")]
        [SerializeField] float _checkInterval = 0.25f;

        [Tooltip("How far above the spawn point the hips are placed when a limp body is recovered.")]
        [SerializeField] float _ragdollDropHeight = 1.2f;

        PlayerMotor _motor;
        RagdollController _ragdoll;

        float _nextCheckAt;

        float _dropTestAt;
        bool _dropTestDone;

        /// <summary>How many times this body has been recovered. Read by tests and, later, by stats.</summary>
        public int Rescues { get; private set; }

        void Awake()
        {
            _motor = GetComponent<PlayerMotor>();
            _ragdoll = GetComponent<RagdollController>();
        }

        public override void OnStartServer()
        {
            // -fallTest <seconds>: throw every body out of the world at this time, so an automated
            // headless run exercises the recovery path instead of waiting for a physics accident.
            // The accident it stands in for is rare by design now that the floor is solid, and a net
            // nobody has ever seen catch anything is not a net you can claim works. See #110.
            int seconds = CommandLine.GetInt("-fallTest", 0);
            if (seconds > 0) _dropTestAt = Time.time + seconds;
        }

        void Update()
        {
            // Server-only: the owner predicts its own movement, but where the world *is* is not a
            // client's decision. A client that rescued itself would be corrected back into the void by
            // the next reconcile anyway.
            if (!IsServerStarted) return;

            if (_dropTestAt > 0f && !_dropTestDone && Time.time >= _dropTestAt) DropForTest();

            if (Time.time < _nextCheckAt) return;
            _nextCheckAt = Time.time + _checkInterval;

            // A limp body is not where its root says it is: the hips are what the physics engine is
            // actually moving, and they are what falls through a floor.
            bool limp = _ragdoll != null && _ragdoll.IsRagdolled && _ragdoll.HipBone != null;
            float height = limp ? _ragdoll.HipBone.position.y : transform.position.y;

            if (height > _killHeight) return;

            Rescue(height, limp);
        }

        /// <summary>Puts this body well under the kill height so the next check has to recover it.</summary>
        void DropForTest()
        {
            _dropTestDone = true;

            Vector3 below = transform.position;
            below.y = _killHeight - 20f;

            if (_ragdoll != null && _ragdoll.IsRagdolled && _ragdoll.HipBone != null)
                _ragdoll.TeleportSkeleton(below);

            if (_motor != null) _motor.ServerTeleport(below, transform.eulerAngles.y);
            else transform.position = below;

            Debug.Log($"[FallGuard] -fallTest dropped owner {OwnerId} to {below.y:F0}m.");
        }

        void Rescue(float fellTo, bool limp)
        {
            Vector3 position = Vector3.up * 2f;
            Quaternion rotation = transform.rotation;

            if (PlayerSpawner.Instance != null)
                PlayerSpawner.Instance.GetSpawn(OwnerId, out position, out rotation);

            if (limp) _ragdoll.TeleportSkeleton(position + Vector3.up * _ragdollDropHeight);

            if (_motor != null) _motor.ServerTeleport(position, rotation.eulerAngles.y);
            else transform.SetPositionAndRotation(position, rotation);

            Rescues++;

            Debug.Log($"[FallGuard] owner {OwnerId} fell to {fellTo:F0}m"
                      + $"{(limp ? " while ragdolled" : string.Empty)}; returned to {position}. "
                      + $"Rescue {Rescues}.");
        }
    }
}
