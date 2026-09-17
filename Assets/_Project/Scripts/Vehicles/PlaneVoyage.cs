using EscapeWithYourFriends.Net;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// Flying off the edge of the map takes everybody to the other island. #73.
    ///
    /// The same crossing the boat makes, and deliberately the same rule: fly past the edge of the
    /// terrain and hold it, and <see cref="GameSceneLoader.ServerTravel"/> moves the session. The
    /// line is <see cref="BoatVoyage.OffTheMap"/> itself rather than a copy of it, so the sea is in
    /// one place and an aeroplane and a hull can never disagree about where the world ends.
    ///
    /// What it does *not* borrow is the rest of BoatVoyage. A hull has four parts to buy, a fuel
    /// tank to run dry and a mooring to be towed back to; an aeroplane has three parts that were
    /// already the gate in #71, no fuel by design (see PlaneBuilder), and no tow - it is in the air,
    /// and the recovery from putting it in the sea is that each island keeps its own airframe, which
    /// is the boat's answer too.
    ///
    /// **Altitude is the only extra condition.** Taxiing off the end of the strip is not a decision
    /// to leave; climbing out over open water is. Anything below <see cref="_minAltitude"/> is still
    /// somebody having a bad landing.
    /// </summary>
    [RequireComponent(typeof(Vehicle))]
    [RequireComponent(typeof(PlaneController))]
    public class PlaneVoyage : NetworkBehaviour
    {
        [Tooltip("Metres past the corner of the terrain where the crossing can start. Generous: "
                 + "leaving should be a decision, not a drift.")]
        [SerializeField] float _margin = 60f;

        [Tooltip("Seconds out there, flying, before it commits. Long enough that a wide circuit "
                 + "over the water does not move the whole group to another island.")]
        [SerializeField] float _holdSeconds = 4f;

        [Tooltip("Metres above sea level. Below this it is a bad landing, not a departure.")]
        [SerializeField] float _minAltitude = 25f;

        /// <summary>Set once the travel call has gone out, so it cannot go out twice.</summary>
        bool _left;

        float _outThere;

        Vehicle _vehicle;
        PlaneController _plane;

        /// <summary>Seconds held past the edge so far. For the harness and, later, for a HUD cue.</summary>
        public float Committing => _outThere;

        void Awake()
        {
            _vehicle = GetComponent<Vehicle>();
            _plane = GetComponent<PlaneController>();
        }

        void Update()
        {
            if (!IsServerStarted || _left) return;

            if (!Leaving())
            {
                _outThere = 0f;
                return;
            }

            _outThere += Time.deltaTime;
            if (_outThere < _holdSeconds) return;

            _outThere = 0f;

            // The last flight. #74. Leaving with the person you came back for is not a crossing,
            // it is the end of the run - there is no third island, and GameSceneLoader.Crossing
            // would cheerfully put everyone back on the one they just escaped from.
            if (AI.Castaway.Instance != null
                && AI.Castaway.Instance.Where == AI.Castaway.Stage.Aboard)
            {
                _left = true;

                Debug.Log($"[PlaneVoyage] {_vehicle.Occupied()} aboard at {transform.position.y:0}m "
                          + "with the one they went back for; that is the run.");

                World.RunSummary.ServerEnd();

                if (_plane != null && _plane.Touchdowns == 0)
                    for (int seat = 0; seat < _vehicle.SeatCount; seat++)
                    {
                        VehicleRider rider = _vehicle.Occupant(seat);
                        if (rider != null) Achievements.ServerAward(rider.NetworkObject, Achievements.FirstTry);
                    }

                return;
            }

            GameSceneLoader loader = GameSceneLoader.Instance;
            string there = GameSceneLoader.Crossing;
            if (loader == null || there == null) return;

            Debug.Log($"[PlaneVoyage] {_vehicle.Occupied()} aboard at {transform.position.y:0}m and "
                      + $"{_holdSeconds:0}s past the edge of {GameSceneLoader.Current}; "
                      + $"making for {there}.");

            _left = loader.ServerTravel(there);
        }

        /// <summary>Somebody is flying it, it is whole, it is high, and it is past the line.</summary>
        bool Leaving()
            => _vehicle.Driver != null
               && _plane.Flyable
               && _plane.IsAirborne
               && transform.position.y >= _minAltitude
               && BoatVoyage.OffTheMap(transform.position, _margin);
    }
}
