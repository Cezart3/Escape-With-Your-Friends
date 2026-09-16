using System;
using System.Collections.Generic;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Player;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// Anything four people can sit in. The seats, the door, and the question of who is driving —
    /// everything M5 builds on top, and nothing about how the thing actually moves.
    ///
    /// The split is deliberate. A car (#58), a boat (#59) and a plane (#72) disagree about almost
    /// everything except that a player presses Interact, ends up in a seat, and stops being a
    /// pedestrian until they press it again. Writing that three times is how you get three different
    /// answers to "what happens if the driver dies at speed", so it is written once here and the
    /// physics components bolt on beside it.
    ///
    /// **Seating is a <see cref="SyncList{T}"/> of object ids, not of NetworkObjects.** A reference
    /// to a spawned object only resolves if that object already arrived, and the one case this has
    /// to survive is exactly the one where it has not: a fifth player joining a session where the
    /// buggy is already full. Ids always deserialise; <see cref="Apply"/> resolves what it can and
    /// retries the rest on the next frame, which is what stops a late joiner from seeing three empty
    /// seats and a car full of people standing on the roof.
    ///
    /// **Riders are glued, not parented.** <see cref="LateUpdate"/> writes each occupant's transform
    /// straight onto its seat anchor, on every peer, after everything else has had its turn — the
    /// motor on the owner, the NetworkTransform on a spectator. Networked parenting would be the
    /// tidier answer on paper and is a great deal more machinery to be wrong about; the glue is four
    /// lines and cannot desync, because every peer is copying a transform it can already see.
    ///
    /// The driver gets *ownership* of the vehicle, which does nothing at all in #57 — the thing is
    /// kinematic and the host moves it. It is here because #58's input has to arrive from somewhere,
    /// and deciding who owns a car is not a question worth answering twice.
    /// </summary>
    public class Vehicle : NetworkBehaviour, IInteractable, ICarryHolder
    {
        /// <summary>One place to sit and the patch of ground you are put down on when you leave it.</summary>
        [Serializable]
        public class Seat
        {
            [Tooltip("Where the rider is glued. Also which way they face.")]
            public Transform Anchor;

            [Tooltip("Where they are put down on the way out. One per seat, so four people getting "
                     + "out at once do not all land in the same square metre.")]
            public Transform Exit;
        }

        [Tooltip("Seats, in order. Index 0 drives; see Driver.")]
        [SerializeField] Seat[] _seats = Array.Empty<Seat>();

        [Tooltip("Where a carried body rides. The bed of the thing, not a seat: a corpse does not "
                 + "occupy a chair somebody else could have used.")]
        [SerializeField] Transform _cargo;

        [Tooltip("For prompts and logs. 'Buggy', 'Boat'.")]
        [SerializeField] string _label = "vehicle";

        [Tooltip("Metres of headroom searched for ground under an exit point before giving up and "
                 + "using it as-is.")]
        [SerializeField] float _exitGroundProbe = 3f;

        /// <summary>
        /// Object id per seat, 0 meaning empty. The list is sized once on the server and then only
        /// ever written by index — an add or a remove would renumber every seat behind it.
        /// </summary>
        readonly SyncList<int> _occupants = new();

        /// <summary>What <see cref="Apply"/> last managed to resolve. Indexed like the seats.</summary>
        VehicleRider[] _riders = Array.Empty<VehicleRider>();

        /// <summary>An id in the list that no spawned object answered to yet. Retried every frame.</summary>
        bool _unresolved;

        /// <summary>Which seat the local player is in, or -1. Only the crosshair reads it.</summary>
        int _localSeat = -1;

        public int SeatCount => _seats.Length;

        public string Label => _label;

        /// <summary>Where a carried body rides. <see cref="ICarryHolder"/>.</summary>
        public Transform CarrySocket => _cargo;

        /// <summary>
        /// Whoever is in seat 0, or null. The only seat the rest of M5 cares about by name: it is the
        /// one whose input moves the thing and the one whose connection owns it.
        /// </summary>
        public VehicleRider Driver => Occupant(0);

        void Awake()
        {
            _riders = new VehicleRider[_seats.Length];

            for (int i = 0; i < _seats.Length; i++)
            {
                if (_seats[i] == null || _seats[i].Anchor == null)
                    Debug.LogError($"[Vehicle] {name} seat {i} has no anchor; nobody can sit in it.");
            }
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();

            // Every peer, not just clients: the host runs this once and the glue below needs the
            // array to exist before the first seating.
            _occupants.OnChange += OnOccupantsChanged;
        }

        public override void OnStopNetwork()
        {
            _occupants.OnChange -= OnOccupantsChanged;
            base.OnStopNetwork();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Sized once, here, rather than in the prefab: the list has to match the seat array on
            // the instance, and a prefab edited in the editor would silently disagree with it.
            _occupants.Clear();
            for (int i = 0; i < _seats.Length; i++) _occupants.Add(0);
        }

        public override void OnStopServer()
        {
            // A despawning vehicle with people in it would leave four bodies frozen in mid-air with
            // their controllers switched off. Emptied by hand rather than through ServerVacate: the
            // SyncList is on its way out and writing to it here buys nothing anybody will ever read.
            for (int i = 0; i < _riders.Length; i++)
            {
                VehicleRider rider = _riders[i];
                if (rider == null) continue;

                _riders[i] = null;
                rider.Detach(this);

                var motor = rider.GetComponent<PlayerMotor>();
                if (motor != null) motor.ServerTeleport(ExitPoint(i), transform.eulerAngles.y);
            }

            base.OnStopServer();
        }

        // ---------------------------------------------------------------- interaction

        /// <summary>
        /// What the crosshair says. Computed from the local player's own seat rather than from
        /// whether the vehicle is full, because "Full" on a car you are sitting in would be a lie
        /// that matters, and "Ride" on a full one is the harmless kind this project already accepts
        /// everywhere else — the refusal is instant and says why.
        /// </summary>
        public string Prompt
        {
            get
            {
                if (_localSeat >= 0) return $"Get out of the {_label}";

                // What is in your hand decides what the key does, which is the whole of #61's
                // interface. It costs one line here and no new key, no new screen and no second
                // interactable competing for the same press.
                string service = ServiceLabel(LocalBag());
                return service ?? $"Ride the {_label}";
            }
        }

        /// <summary>Items that service a vehicle, matched by id so nothing has to be wired in the
        /// prefab. See <see cref="VehicleCondition"/>.</summary>
        const string FuelItem = "fuel";
        const string RepairItem = "scrap_metal";

        public bool ServerCanInteract(NetworkObject actor)
        {
            if (!IsServerStarted || actor == null) return false;

            // Already aboard: the press means "let me out", which is always allowed.
            if (SeatOf(actor) >= 0) return true;

            // Servicing first, because a wrecked car refuses to be boarded by nobody - it boards
            // fine, it just will not go - and somebody holding a can wants the can used.
            if (ServiceLabel(actor.GetComponent<Items.Inventory>()) != null) return true;

            return ServerCanBoard(actor, out _);
        }

        public void ServerInteract(NetworkObject actor)
        {
            if (!IsServerStarted || actor == null) return;

            int seated = SeatOf(actor);
            if (seated >= 0)
            {
                ServerExit(actor);
                return;
            }

            if (ServerService(actor)) return;

            ServerEnter(actor);
        }

        /// <summary>
        /// Server only. Spends the held item on whatever the vehicle is short of. Returns true if it
        /// did, in which case the press was the service rather than a boarding.
        /// </summary>
        bool ServerService(NetworkObject actor)
        {
            var bag = actor.GetComponent<Items.Inventory>();
            if (ServiceLabel(bag) == null) return false;

            ItemDef held = bag.Selected.Def;
            var condition = GetComponent<VehicleCondition>();
            VehicleUpgradeDef part = Part(held);

            if (bag.Remove(held, 1) <= 0) return false;

            bool serviced = part != null ? GetComponent<VehicleUpgrades>().ServerFit(part)
                          : held.Id == FuelItem ? condition.ServerRefuel()
                          : condition.ServerRepair();

            Debug.Log($"[Vehicle] {Name(actor)} spent one {held.Id} on the {_label}: "
                      + $"{(condition != null ? condition.Report() : "fitted")}.");

            return serviced;
        }

        /// <summary>
        /// What the held item would do to this vehicle, or null for "nothing". Shared by the prompt
        /// and by both server-side halves so the crosshair and the key never disagree.
        /// </summary>
        string ServiceLabel(Items.Inventory bag)
        {
            if (bag == null) return null;

            ItemDef held = bag.Selected.Def;
            if (held == null) return null;

            var condition = GetComponent<VehicleCondition>();

            if (condition != null)
            {
                if (held.Id == FuelItem && condition.NeedsFuel) return $"Refuel the {_label}";
                if (held.Id == RepairItem && condition.NeedsRepair) return $"Repair the {_label}";
            }

            // #62. A part in your hand is the third thing the key can mean. Asking the upgrades
            // component rather than listing part ids here is what lets the boat refuse tyres by
            // simply never having been given any.
            VehicleUpgradeDef part = Part(held);
            if (part != null) return $"Fit the {part.DisplayName} to the {_label}";

            return null;
        }

        /// <summary>The upgrade the held item would fit right now, or null. #62.</summary>
        VehicleUpgradeDef Part(ItemDef held)
        {
            var upgrades = GetComponent<VehicleUpgrades>();
            return upgrades != null ? upgrades.Fit(held) : null;
        }

        /// <summary>
        /// The local player's bag, for the prompt only. Same shape as <see cref="Items.Storage"/>'s,
        /// and cached per call for the same reason: a prompt is read on the frame the crosshair is
        /// on this and never in a loop.
        /// </summary>
        Items.Inventory LocalBag()
        {
            NetworkObject local = ClientManager != null && ClientManager.Connection != null
                ? ClientManager.Connection.FirstObject
                : null;

            return local != null ? local.GetComponent<Items.Inventory>() : null;
        }

        /// <summary>
        /// Server only. Puts <paramref name="actor"/> in the first free seat. Returns the seat index,
        /// or -1 if it was refused.
        /// </summary>
        public int ServerEnter(NetworkObject actor)
        {
            if (!ServerCanBoard(actor, out string why))
            {
                if (!string.IsNullOrEmpty(why))
                    Debug.Log($"[Vehicle] {_label} refused {Name(actor)}: {why}.");
                return -1;
            }

            int seat = FreeSeat();
            if (seat < 0) return -1;

            _occupants[seat] = actor.ObjectId;

            // Ownership follows the wheel. Nothing in #57 reads it — the thing is kinematic and the
            // host moves it — but #58's input has to arrive from a connection that owns something,
            // and a car that changes hands is not a decision worth making twice.
            if (seat == 0 && actor.Owner != null && actor.Owner.IsValid)
                NetworkObject.GiveOwnership(actor.Owner);

            Debug.Log($"[Vehicle] {Name(actor)} took seat {seat} of the {_label}"
                      + $" ({Occupied()}/{_seats.Length} aboard).");

            return seat;
        }

        /// <summary>Server only. Takes <paramref name="actor"/> out and puts it on the ground.</summary>
        public bool ServerExit(NetworkObject actor)
        {
            if (!IsServerStarted || actor == null) return false;

            int seat = SeatOf(actor);
            if (seat < 0) return false;

            ServerVacate(seat, teleport: true);
            return true;
        }

        /// <summary>Server only. Whether this body could board right now, and why not if it could not.</summary>
        public bool ServerCanBoard(NetworkObject actor, out string why)
        {
            why = null;

            if (!IsServerStarted || actor == null)
            {
                why = "there is no server";
                return false;
            }

            if (SeatOf(actor) >= 0)
            {
                why = "they are already aboard";
                return false;
            }

            // One vehicle at a time, checked against the rider rather than against every vehicle in
            // the world: the rider is the thing that knows, and it knows in one field.
            var rider = actor.GetComponent<VehicleRider>();
            if (rider == null)
            {
                why = "that body cannot ride anything";
                return false;
            }

            if (rider.IsSeated)
            {
                why = "they are already in something else";
                return false;
            }

            // Carried before downed, because a body on somebody's shoulder is always also a body
            // that is incapacitated, and "they are on the floor" is the wrong sentence about a
            // player who is currently over their friend's head.
            var carryable = actor.GetComponent<Carryable>();
            if (carryable != null && carryable.IsCarried)
            {
                why = "somebody is carrying them";
                return false;
            }

            var health = actor.GetComponent<Health>();
            if (health != null && health.IsIncapacitated)
            {
                why = "they are on the floor";
                return false;
            }

            var ragdoll = actor.GetComponent<RagdollController>();
            if (ragdoll != null && ragdoll.IsRagdolled)
            {
                why = "they are a heap on the ground";
                return false;
            }

            if (FreeSeat() < 0)
            {
                why = $"the {_label} is full";
                return false;
            }

            return true;
        }

        // ---------------------------------------------------------------- seating state

        /// <summary>Where seat <paramref name="seat"/> sits, or null. Read by the glue and the harness.</summary>
        public Transform SeatAnchor(int seat)
            => seat >= 0 && seat < _seats.Length && _seats[seat] != null ? _seats[seat].Anchor : null;

        /// <summary>Where seat <paramref name="seat"/> puts you down, or null.</summary>
        public Transform SeatExit(int seat)
            => seat >= 0 && seat < _seats.Length && _seats[seat] != null ? _seats[seat].Exit : null;

        /// <summary>The rider in a seat, or null. Resolved; safe on every peer.</summary>
        public VehicleRider Occupant(int seat)
            => seat >= 0 && seat < _riders.Length ? _riders[seat] : null;

        /// <summary>Which seat a body is in, or -1. Server or client.</summary>
        public int SeatOf(NetworkObject actor)
        {
            if (actor == null) return -1;

            for (int i = 0; i < _occupants.Count; i++)
                if (_occupants[i] == actor.ObjectId) return i;

            return -1;
        }

        /// <summary>How many seats are taken.</summary>
        public int Occupied()
        {
            int count = 0;
            for (int i = 0; i < _occupants.Count; i++)
                if (_occupants[i] != 0) count++;

            return count;
        }

        public bool IsFull => Occupied() >= _seats.Length;

        int FreeSeat()
        {
            for (int i = 0; i < _occupants.Count && i < _seats.Length; i++)
                if (_occupants[i] == 0) return i;

            return -1;
        }

        /// <summary>
        /// Server only. Empties a seat and, if asked, puts whoever was in it on the ground beside the
        /// vehicle. The teleport is skipped when the body is going away anyway — a despawn, a death
        /// handled elsewhere — because moving something that is about to stop existing is noise.
        /// </summary>
        void ServerVacate(int seat, bool teleport)
        {
            if (seat < 0 || seat >= _occupants.Count || _occupants[seat] == 0) return;

            VehicleRider rider = Occupant(seat);

            _occupants[seat] = 0;

            if (seat == 0) NetworkObject.RemoveOwnership();

            if (!teleport || rider == null) return;

            var motor = rider.GetComponent<PlayerMotor>();
            if (motor == null) return;

            motor.ServerTeleport(ExitPoint(seat), transform.eulerAngles.y);

            Debug.Log($"[Vehicle] {rider.name} left seat {seat} of the {_label}"
                      + $" ({Occupied()}/{_seats.Length} aboard).");
        }

        /// <summary>
        /// Where seat <paramref name="seat"/> puts you down. The anchor is at the vehicle's own
        /// floor height, which is the wrong height on any slope, so it is dropped onto whatever is
        /// under it — and left where it is if that search finds nothing, because a door hanging over
        /// a cliff is still a better answer than the middle of the chassis.
        /// </summary>
        Vector3 ExitPoint(int seat)
        {
            Transform door = seat >= 0 && seat < _seats.Length && _seats[seat] != null
                ? _seats[seat].Exit
                : null;

            Vector3 spot = door != null ? door.position : transform.position + transform.right * -2f;

            if (Physics.Raycast(spot + Vector3.up * _exitGroundProbe, Vector3.down,
                                out RaycastHit hit, _exitGroundProbe * 2f, ~0,
                                QueryTriggerInteraction.Ignore))
                spot = hit.point;

            return spot;
        }

        // ---------------------------------------------------------------- the glue

        void OnOccupantsChanged(SyncListOperation op, int index, int older, int newer, bool asServer)
        {
            // Rescanned whole rather than patched per operation. The list arrives at a late joiner as
            // a run of adds followed by a Complete, and a handler that trusted the op would have to
            // get both shapes right for no gain: four seats is not a loop worth optimising.
            Apply();
        }

        /// <summary>
        /// Brings <see cref="_riders"/> in line with <see cref="_occupants"/>, attaching and
        /// detaching as it goes. Idempotent, because it runs on every list change and every frame
        /// that left an id unresolved.
        /// </summary>
        void Apply()
        {
            if (_riders.Length != _seats.Length) _riders = new VehicleRider[_seats.Length];

            _unresolved = false;
            _localSeat = -1;

            for (int i = 0; i < _riders.Length; i++)
            {
                int id = i < _occupants.Count ? _occupants[i] : 0;
                VehicleRider was = _riders[i];

                if (id == 0)
                {
                    if (was != null)
                    {
                        _riders[i] = null;
                        was.Detach(this);
                    }

                    continue;
                }

                VehicleRider now = Resolve(id);

                if (now == null)
                {
                    // The body has not been spawned on this peer yet. Leave whatever we had, mark the
                    // frame dirty and try again: this is the late-joiner case, and giving up here is
                    // what would strand a rider outside the car it is sitting in.
                    _unresolved = true;
                    continue;
                }

                if (was != now)
                {
                    if (was != null) was.Detach(this);

                    _riders[i] = now;
                    now.Attach(this, i, _seats[i].Anchor);
                }

                if (now.IsOwner) _localSeat = i;
            }
        }

        VehicleRider Resolve(int objectId)
        {
            NetworkObject found = null;

            if (IsClientStarted && NetworkManager.ClientManager.Objects.Spawned
                                                 .TryGetValue(objectId, out NetworkObject asClient))
                found = asClient;
            else if (IsServerStarted && NetworkManager.ServerManager.Objects.Spawned
                                                      .TryGetValue(objectId, out NetworkObject asServer))
                found = asServer;

            return found != null ? found.GetComponent<VehicleRider>() : null;
        }

        void LateUpdate()
        {
            if (_unresolved) Apply();

            // After the motor, after the NetworkTransform, after anything else that thinks it owns
            // these transforms. Whoever wrote last wins, and this is last.
            for (int i = 0; i < _riders.Length; i++)
            {
                VehicleRider rider = _riders[i];
                if (rider == null || _seats[i] == null || _seats[i].Anchor == null) continue;

                Transform anchor = _seats[i].Anchor;
                rider.transform.SetPositionAndRotation(anchor.position, anchor.rotation);
            }

            if (IsServerStarted) ServerSweep();
        }

        /// <summary>
        /// Server only. Throws out anybody who stopped being able to sit up — downed, dead, or gone
        /// from the session entirely. Without it a player who bleeds out in the passenger seat rides
        /// around forever as a statue, which is funny exactly once and then is a bug report.
        /// </summary>
        void ServerSweep()
        {
            for (int i = 0; i < _occupants.Count && i < _seats.Length; i++)
            {
                if (_occupants[i] == 0) continue;

                VehicleRider rider = _riders[i];

                if (rider == null || rider.NetworkObject == null || !rider.NetworkObject.IsSpawned)
                {
                    // Unresolved is not the same as gone: Apply is still looking for it.
                    if (!_unresolved) ServerVacate(i, teleport: false);
                    continue;
                }

                var health = rider.GetComponent<Health>();
                if (health != null && health.IsIncapacitated)
                {
                    Debug.Log($"[Vehicle] {rider.name} went down in seat {i} of the {_label}; "
                              + "dumped on the ground.");
                    ServerVacate(i, teleport: true);
                }
            }
        }

        static string Name(NetworkObject actor) => actor != null ? actor.name : "nobody";

        /// <summary>Bake time only. The builder writes the seats; nothing at runtime does.</summary>
        public void Configure(Seat[] seats, Transform cargo, string label)
        {
            _seats = seats ?? Array.Empty<Seat>();
            _cargo = cargo;
            _label = label;
            _riders = new VehicleRider[_seats.Length];
        }

        /// <summary>Every vehicle currently spawned on this peer. Used by the harness and the HUD.</summary>
        public static IReadOnlyList<Vehicle> All => _all;

        static readonly List<Vehicle> _all = new();

        void OnEnable() => _all.Add(this);

        void OnDisable() => _all.Remove(this);
    }
}
