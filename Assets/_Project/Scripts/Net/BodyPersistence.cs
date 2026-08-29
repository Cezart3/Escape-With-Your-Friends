using System;
using System.Collections.Generic;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using FishNet.Connection;
using FishNet.Managing.Server;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;
using UnityEngine;

namespace EscapeWithYourFriends.Net
{
    /// <summary>
    /// Keeps a downed or dead body in the world after its owner disconnects.
    ///
    /// FishNet despawns everything a connection owns the moment that connection drops. That is the
    /// right default — nobody wants an idle duplicate of a player who alt-F4'd — but it is exactly
    /// wrong for this game's death loop. A dead player is a physical object their friends have to
    /// haul to the Revive Machine and pay for, and the most common reason to be dead for a long time
    /// is that the game crashed or the connection dropped. If the corpse vanishes with the
    /// connection, the punishment for a bad connection is that your friends cannot get you back.
    ///
    /// **How the body survives.** FishNet's flag for this, <c>PreventDespawnOnDisconnect</c>, is
    /// serialized and internal: there is no way to turn it on for one body at runtime, and turning it
    /// on for every player prefab would leave a standing, ownerless mannequin behind every single
    /// disconnect. So this uses the ordering instead. On a disconnect, ServerManager sets the
    /// connection to disconnecting, raises <c>OnRemoteConnectionState</c>, and only afterwards sweeps
    /// <c>connection.Objects</c> and despawns what is left. Removing ownership inside that event
    /// takes the body out of <c>connection.Objects</c> before the sweep reads it, so the sweep never
    /// sees it. No prefab flag, no fork, no despawn-and-respawn dance that would lose the ragdoll's
    /// pose and whatever the body is currently tangled in.
    ///
    /// **Only if they were already down.** An upright player who quits takes their body with them.
    /// Leaving a standing body behind would be a free decoy and an invitation to disconnect on
    /// purpose, and there is nothing to revive.
    ///
    /// **The body is a prop now, not a player.** An abandoned body is unregistered from
    /// <see cref="NetworkPlayerRegistry"/>, so the squad list (#106) stops claiming someone is here.
    /// It stays in <see cref="Abandoned"/> instead, which is what the Revive Machine (#25) reads.
    /// <see cref="Player.PlayerMotor"/> builds no input for a body it does not own, so an ownerless body
    /// simply stands where it fell — no drift, no phantom movement.
    ///
    /// Reclaiming your own body on reconnect is deliberately not here. It needs a player key that
    /// survives a reconnect, and neither of the two available today works: FishNet reuses client ids,
    /// so keying by id hands a corpse to whoever takes the free slot, and every Tugboat test client
    /// shares the address 127.0.0.1. That is filed separately, and until it lands a returning player
    /// spawns fresh next to the corpse of who they used to be, which is funny enough to ship.
    /// </summary>
    public class BodyPersistence : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] Health _health;

        [Tooltip("Unregistered from the player registry once abandoned; the body is scenery then.")]
        [SerializeField] Player.PlayerIdentity _identity;

        readonly SyncVar<bool> _abandoned = new();

        static readonly List<BodyPersistence> _abandonedBodies = new();

        ServerManager _serverManager;
        int _spawnOwnerId = -1;

        // Headless test hooks; see the fields' use in OnStartServer.
        float _killAt = -1f;
        float _reviveAt = -1f;

        /// <summary>
        /// Every body still in the world whose owner is gone. The Revive Machine works from this
        /// list, and so does anything that wants to say "three of you are still out there".
        /// </summary>
        public static IReadOnlyList<BodyPersistence> Abandoned => _abandonedBodies;

        /// <summary>Raised on every peer when a body joins or leaves <see cref="Abandoned"/>.</summary>
        public static event Action<BodyPersistence> AbandonedChanged;

        /// <summary>True once the owner has disconnected and left this body behind.</summary>
        public bool IsAbandoned => _abandoned.Value;

        /// <summary>The connection id this body was spawned for. Survives losing ownership.</summary>
        public int SpawnOwnerId => _spawnOwnerId;

        /// <summary>The life state machine on this body, for callers that only hold the persistence.</summary>
        public Health Health => _health;

        void Awake()
        {
            if (_health == null) _health = GetComponent<Health>();
            if (_identity == null) _identity = GetComponent<Player.PlayerIdentity>();

            _abandoned.OnChange += OnAbandonedChanged;
        }

        void OnDestroy()
        {
            _abandoned.OnChange -= OnAbandonedChanged;

            // A body can be destroyed while abandoned — scene unload, session end, or the Revive
            // Machine consuming it later. The static list outlives all three.
            if (_abandonedBodies.Remove(this)) AbandonedChanged?.Invoke(this);
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Cached, because Owner is null by the time OnStopServer runs on a disconnect and the
            // NetworkBehaviour's own manager references are being torn down at the same moment.
            _spawnOwnerId = OwnerId;
            _serverManager = ServerManager;

            if (_serverManager != null)
                _serverManager.OnRemoteConnectionState += OnRemoteConnectionState;

            ScheduleTests();
        }

        public override void OnStopServer()
        {
            base.OnStopServer();

            if (_serverManager != null)
                _serverManager.OnRemoteConnectionState -= OnRemoteConnectionState;
            _serverManager = null;
        }

        /// <summary>
        /// Server only. Hands the body back to a connection — the seam a reconnect-adoption feature
        /// and the Revive Machine both need, since a revived body with no owner cannot be walked away.
        /// Returns false if the body was not abandoned or the connection is not usable.
        /// </summary>
        public bool ServerAdopt(NetworkConnection connection)
        {
            if (!IsServerStarted || !_abandoned.Value) return false;
            if (connection == null || !connection.IsActive) return false;

            _abandoned.Value = false;
            NetworkObject.GiveOwnership(connection);

            Debug.Log($"[BodyPersistence] Body of owner {_spawnOwnerId} adopted by "
                      + $"connection {connection.ClientId}.");
            return true;
        }

        void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped) return;

            // Compared against the live Owner rather than the spawn id, so a body that has already
            // changed hands follows whoever holds it now.
            if (connection == null || Owner != connection) return;
            if (_abandoned.Value) return;

            if (_health == null || _health.IsAlive)
            {
                Debug.Log($"[BodyPersistence] Owner {connection.ClientId} left on their feet; "
                          + "the body goes with them.");
                return;
            }

            LifeState state = _health.State;

            // Order matters and is the whole trick. RemoveOwnership takes this object out of
            // connection.Objects, and ServerManager only sweeps that collection after this event
            // returns, so the despawn never sees the body.
            _abandoned.Value = true;
            NetworkObject.RemoveOwnership();

            Debug.Log($"[BodyPersistence] Owner {connection.ClientId} left while {state}; "
                      + $"body kept in the world at {transform.position}. "
                      + $"{_abandonedBodies.Count} abandoned.");
        }

        void OnAbandonedChanged(bool previous, bool next, bool asServer)
        {
            // Fires on the host as well; see Health.OnHealthChanged for why there is no guard.
            if (next)
            {
                if (!_abandonedBodies.Contains(this)) _abandonedBodies.Add(this);

                // Out of the roster: this is scenery with a face, not a player who is present.
                if (_identity != null) NetworkPlayerRegistry.Unregister(_identity);
            }
            else
            {
                _abandonedBodies.Remove(this);
                if (_identity != null) NetworkPlayerRegistry.Register(_identity);
            }

            AbandonedChanged?.Invoke(this);
        }

        /// <summary>
        /// Arms the headless regression for this issue, the same way FallGuard arms -fallTest: the
        /// test lives inside the component it tests, because the alternative is a build flavour that
        /// only exists for tests and therefore is not the build anyone ships.
        ///
        ///   -deathTest 12        kill this body 12 seconds after the server starts
        ///   -deathTestOwner 1    ...but only the body owned by connection 1 (default: every body)
        ///   -reviveTest 35       revive this body at 35 seconds, if it is dead by then
        ///
        /// All three are read on the server, so only the host's command line matters.
        /// </summary>
        void ScheduleTests()
        {
            int killAfter = CommandLine.GetInt("-deathTest", 0);
            int reviveAfter = CommandLine.GetInt("-reviveTest", 0);
            int onlyOwner = CommandLine.GetInt("-deathTestOwner", -1);

            if (killAfter > 0 && (onlyOwner < 0 || onlyOwner == _spawnOwnerId))
            {
                _killAt = Time.time + killAfter;
                Debug.Log($"[BodyPersistence] -deathTest: owner {_spawnOwnerId} dies in {killAfter}s.");
            }

            if (reviveAfter > 0) _reviveAt = Time.time + reviveAfter;
        }

        void Update()
        {
            if (!IsServerStarted) return;

            if (_killAt > 0f && Time.time >= _killAt)
            {
                _killAt = -1f;
                _health.ServerKill(DamageInfo.World(0f, DamageType.Environment));
                Debug.Log($"[BodyPersistence] -deathTest: owner {_spawnOwnerId} killed, "
                          + $"state {_health.State}.");
            }

            if (_reviveAt > 0f && Time.time >= _reviveAt)
            {
                _reviveAt = -1f;
                bool revived = _health.ServerRevive();
                Debug.Log($"[BodyPersistence] -reviveTest: body of owner {_spawnOwnerId} "
                          + $"abandoned={_abandoned.Value} revive={revived} state={_health.State}.");
            }
        }
    }
}
