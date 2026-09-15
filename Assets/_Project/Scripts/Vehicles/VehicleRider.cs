using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// The passenger half of <see cref="Vehicle"/>: what happens to a body when it stops being a
    /// pedestrian.
    ///
    /// It holds no networked state of its own, deliberately. Who is sitting where is one fact, and
    /// two SyncVars claiming it — one on the seat and one on the body — is two facts that disagree
    /// the first time a packet is lost. <see cref="Vehicle"/> owns the list; this component is told
    /// what it is, on every peer, by <see cref="Vehicle.Attach"/>.
    ///
    /// Sitting down means three things:
    ///
    /// * **The CharacterController switches off.** It owns its own position and will drag a body
    ///   straight back out of a moving car otherwise — the same fact that makes
    ///   <see cref="Player.PlayerMotor.ServerTeleport"/> switch it off before writing a transform.
    /// * **The motor stops steering.** <see cref="Player.PlayerMotor"/> already refuses to run with
    ///   the controller disabled, so this is only about the reconcile, which would keep writing a
    ///   world position a round trip behind the car.
    /// * **Collisions with the vehicle are ignored**, so the chassis cannot punt its own passengers.
    ///
    /// Standing up puts all three back exactly as they were, and the server teleports the body to the
    /// seat's door on the way out.
    /// </summary>
    public class VehicleRider : NetworkBehaviour
    {
        Vehicle _vehicle;
        int _seat = -1;

        CharacterController _controller;
        bool _controllerWasEnabled;

        /// <summary>The engine of whatever we are sitting in, if it has one. Cached on the way in.</summary>
        CarController _car;

        readonly List<Collider> _ignoredWith = new();

        /// <summary>The vehicle this body is sitting in, or null.</summary>
        public Vehicle Vehicle => _vehicle;

        /// <summary>Which seat, or -1.</summary>
        public int Seat => _seat;

        public bool IsSeated => _vehicle != null;

        /// <summary>Seat 0. The one whose input moves the thing.</summary>
        public bool IsDriving => _vehicle != null && _seat == 0;

        /// <summary>
        /// Owner side, every frame while driving. Forwarded rather than read here, because
        /// <see cref="Player.PlayerCombatInput"/> is the one file that decides which button means
        /// which verb and a second opinion on that is how a remap goes half-applied.
        ///
        /// Steering a vehicle that has no engine is not an error: a boat and a plane will answer the
        /// same two axes with their own components, and a trailer will answer with nothing.
        /// </summary>
        public void Drive(Vector2 move, bool handbrake)
        {
            if (_car == null || !IsDriving) return;

            _car.OwnerDrive(move.y, move.x, handbrake);
        }

        void Awake() => _controller = GetComponent<CharacterController>();

        /// <summary>
        /// Owner side. Asks to get out. Returns whether the request went anywhere, so the input
        /// component can fall through to the rest of its list when we are not in anything.
        /// </summary>
        public bool RequestExit()
        {
            if (!IsOwner || !IsSeated) return false;

            ServerRequestExit();
            return true;
        }

        /// <summary>
        /// Nothing is sent but the ask. The server already knows which vehicle this body is in — it
        /// is the one holding the id — so a client naming one would only be a chance to name the
        /// wrong one.
        /// </summary>
        [ServerRpc]
        void ServerRequestExit()
        {
            if (_vehicle == null) return;

            _vehicle.ServerExit(NetworkObject);
        }

        /// <summary>
        /// Called by <see cref="Vehicle"/> on every peer. Not public: a body does not decide what it
        /// is sitting in, and anything that could call this without going through the seat list would
        /// be inventing a second source of truth.
        /// </summary>
        internal void Attach(Vehicle vehicle, int seat, Transform anchor)
        {
            if (vehicle == null || anchor == null) return;

            _vehicle = vehicle;
            _seat = seat;
            _car = vehicle.GetComponent<CarController>();

            if (_controller != null)
            {
                _controllerWasEnabled = _controller.enabled;
                _controller.enabled = false;
            }

            transform.SetPositionAndRotation(anchor.position, anchor.rotation);

            IgnoreVehicle(vehicle, true);

            // autoSyncTransforms is off in this project, so until this runs every physics query still
            // believes the body is standing where it was when it pressed the key. That includes the
            // ignore pairs above, which is how a passenger ends up being shot by a bullet aimed at a
            // patch of grass forty metres back.
            Physics.SyncTransforms();
        }

        /// <summary>Called by <see cref="Vehicle"/> on every peer. Puts the body back the way it was.</summary>
        internal void Detach(Vehicle vehicle)
        {
            // Left a different vehicle than the one we are in: a stale detach from a seat that has
            // already been taken over by something else. Ignoring it is what keeps the swap safe.
            if (_vehicle != vehicle) return;

            _vehicle = null;
            _seat = -1;
            _car = null;

            IgnoreVehicle(vehicle, false);

            // Before the controller comes back, for the same reason the carry code syncs before it
            // goes dynamic: a controller re-enabled at a stale physics position depenetrates out of
            // whatever PhysX still thinks it is standing in.
            Physics.SyncTransforms();

            if (_controller != null) _controller.enabled = _controllerWasEnabled;
        }

        void IgnoreVehicle(Vehicle vehicle, bool ignore)
        {
            if (!ignore)
            {
                foreach (Collider other in _ignoredWith)
                    if (other != null) SetIgnore(other, false);

                _ignoredWith.Clear();
                return;
            }

            foreach (Collider other in vehicle.GetComponentsInChildren<Collider>())
            {
                if (other == null || other.isTrigger) continue;

                SetIgnore(other, true);
                _ignoredWith.Add(other);
            }
        }

        void SetIgnore(Collider other, bool ignore)
        {
            foreach (Collider mine in GetComponentsInChildren<Collider>())
            {
                if (mine == null || mine.isTrigger) continue;

                // The CharacterController is switched off for the whole ride, and Unity logs an
                // error rather than shrugging when asked to ignore a disabled collider. There is
                // nothing to ignore either way: a disabled controller collides with nothing.
                if (mine is CharacterController) continue;

                Physics.IgnoreCollision(mine, other, ignore);
            }
        }
    }
}
