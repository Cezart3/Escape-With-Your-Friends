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
    /// **Reclaiming your own body on reconnect (#111).** Each body remembers the
    /// <see cref="PlayerKey"/> of whoever it was spawned for, in <see cref="OwnerKey"/>. That key
    /// outlives the connection — which is the whole point, since a client id is reused by FishNet and
    /// every Tugboat test client shares one address — so a returning player is matched to the body
    /// they left rather than to the slot they happen to land in. <see cref="PlayerSpawner"/> does the
    /// matching through <see cref="FindAbandoned"/> and hands the body over with
    /// <see cref="ServerAdopt"/> instead of spawning a fresh one.
    ///
    /// **No expiry.** An unclaimed body sits there until the Revive Machine consumes it or the session
    /// ends. A timer that cleaned bodies up would delete the content: the corpse is the thing your
    /// friends have to go and fetch, and the longer it has been lying there the more that matters.
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

        /// <summary>
        /// Server-only, and deliberately not a SyncVar. Only the server ever asks who a body belongs
        /// to, and replicating it would put every player's Steam id on every other player's machine
        /// for no gameplay reason at all.
        /// </summary>
        string _ownerKey = string.Empty;

        // Headless test hooks; see the fields' use in OnStartServer.
        float _killAt = -1f;
        float _reviveAt = -1f;
        string _killKey;

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

        /// <summary>
        /// The <see cref="PlayerKey"/> of the player this body belongs to, or empty if it was spawned
        /// before anyone asked. Unlike <see cref="SpawnOwnerId"/> this survives the player leaving and
        /// coming back, which is the only reason it exists.
        /// </summary>
        public string OwnerKey => _ownerKey;

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
        /// Server only. Stamps this body with the key of the player it was spawned for. Called once,
        /// by <see cref="PlayerSpawner"/>, at spawn.
        /// </summary>
        public void ServerSetOwnerKey(string key)
        {
            if (!IsServerStarted || string.IsNullOrEmpty(key)) return;
            _ownerKey = key;
        }

        /// <summary>
        /// The most recently abandoned body belonging to <paramref name="key"/>, or null.
        ///
        /// Searched newest first. One player should never have two bodies waiting — adopting the
        /// first one back is what stops a second from being created — but if a session ever produces
        /// two, the one they left last is the one they remember dying in.
        /// </summary>
        public static BodyPersistence FindAbandoned(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            for (int i = _abandonedBodies.Count - 1; i >= 0; i--)
            {
                BodyPersistence body = _abandonedBodies[i];
                if (body != null && body._ownerKey == key) return body;
            }

            return null;
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
        ///   -deathTestKey ALPHA  ...or only the body belonging to that player key
        ///   -reviveTest 35       revive this body at 35 seconds, if it is dead by then
        ///
        /// All four are read on the server, so only the host's command line matters.
        ///
        /// Prefer <c>-deathTestKey</c> over <c>-deathTestOwner</c> in anything with more than two
        /// processes. Connection ids are handed out in the order the transport accepts sockets, and
        /// a host's own client is not reliably first: a client process that finished booting while
        /// the host was still loading the scene takes connection 0, and the run then kills the wrong
        /// body while still exiting green. That is exactly how the first #111 run passed for the
        /// wrong reason.
        /// </summary>
        void ScheduleTests()
        {
            int killAfter = CommandLine.GetInt("-deathTest", 0);
            int reviveAfter = CommandLine.GetInt("-reviveTest", 0);
            int onlyOwner = CommandLine.GetInt("-deathTestOwner", -1);

            if (killAfter > 0 && (onlyOwner < 0 || onlyOwner == _spawnOwnerId))
            {
                _killAt = Time.time + killAfter;

                // Matched at fire time rather than here: PlayerSpawner stamps the key immediately
                // after the spawn, which is immediately after this runs, so there is nothing to
                // compare against yet.
                _killKey = CommandLine.GetString("-deathTestKey", null);

                Debug.Log($"[BodyPersistence] -deathTest: owner {_spawnOwnerId} dies in {killAfter}s"
                          + $"{(string.IsNullOrEmpty(_killKey) ? "" : $", if its key is {_killKey}")}.");
            }

            if (reviveAfter > 0) _reviveAt = Time.time + reviveAfter;
        }

        void Update()
        {
            if (!IsServerStarted) return;

            if (_killAt > 0f && Time.time >= _killAt)
            {
                _killAt = -1f;

                if (!string.IsNullOrEmpty(_killKey) && _ownerKey != _killKey)
                {
                    Debug.Log($"[BodyPersistence] -deathTest: owner {_spawnOwnerId} spared, key "
                              + $"{PlayerKey.Short(_ownerKey)} is not {_killKey}.");
                    return;
                }

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
