using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// #61: a vehicle runs on fuel, breaks when you crash it, and is put back together with scrap.
    ///
    /// The issue's acceptance is the whole design brief - "wrecking a vehicle costs something but is
    /// never run-ending" - and both halves of that sentence are load-bearing. A wreck has to hurt, or
    /// driving badly is free and the trader has nothing to sell. A wreck must never be terminal, so
    /// **every failure state is repaired in place, by hand, with an item the island gives away.**
    /// Nothing here tows, despawns or respawns a vehicle, because a buggy you cannot fix where it
    /// stands is a run that ended when you hit a tree.
    ///
    /// Both numbers live on the same component because they fail the same way and are fixed the same
    /// way: the engine stops, and somebody walks over with something in their hands. Two components
    /// would be two SyncVar sets, two interaction branches and two harnesses, for no gain.
    ///
    /// **Fuel burns per metre travelled, and only with somebody at the wheel.** Time-based burn makes
    /// a parked car a liability nobody asked for, and distance is the number a player can actually
    /// reason about: a full tank is so many trips across the island. A driverless car rolling down a
    /// hill costs nothing, which is correct - the engine is not doing it.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Vehicle))]
    public class VehicleCondition : NetworkBehaviour
    {
        /// <summary>Litres. Set per vehicle by the builders; a boat carries more because the sea has
        /// no walking home.</summary>
        [SerializeField] float _tank = 60f;

        /// <summary>Litres burned per kilometre driven.</summary>
        [SerializeField] float _burnPerKm = 12f;

        [SerializeField] float _integrityMax = 100f;

        /// <summary>
        /// Metres per second below which hitting something costs nothing. Kerbs, ruts and the landing
        /// after a small jump all raise contacts, and a vehicle that loses integrity to those is one
        /// nobody dares drive - which is the opposite of what this game is for.
        /// </summary>
        [SerializeField] float _crashSpeed = 6f;

        /// <summary>Integrity lost per metre per second of impact above <see cref="_crashSpeed"/>.</summary>
        [SerializeField] float _crashDamage = 4f;

        /// <summary>Litres a single can puts in.</summary>
        [SerializeField] float _fuelPerCan = 25f;

        /// <summary>Integrity one piece of scrap metal puts back.</summary>
        [SerializeField] float _repairPerPart = 30f;

        readonly SyncVar<float> _fuel = new();
        readonly SyncVar<float> _integrity = new();

        Rigidbody _body;
        Vehicle _vehicle;

        /// <summary>Pre-solve speed, for the same reason <see cref="VehicleImpact"/> caches one: read
        /// inside a collision callback, <c>linearVelocity</c> is a post-solve number and it spikes.</summary>
        float _speed;

        public float Fuel => _fuel.Value;
        public float Tank => _tank;
        public float Integrity => _integrity.Value;
        public float IntegrityMax => _integrityMax;

        public bool IsDry => _fuel.Value <= 0f;
        public bool IsWrecked => _integrity.Value <= 0f;

        /// <summary>What the input relay asks before passing the throttle on.</summary>
        public bool CanDrive => !IsDry && !IsWrecked;

        public bool NeedsFuel => _fuel.Value < _tank - 0.01f;
        public bool NeedsRepair => _integrity.Value < _integrityMax - 0.01f;

        void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _vehicle = GetComponent<Vehicle>();

            // #62. The baked capacities, so a fitted part is a multiple of the vehicle as found.
            _stockTank = _tank;
            _stockIntegrity = _integrityMax;
        }

        float _stockTank;
        float _stockIntegrity;

        /// <summary>
        /// Server only. Bigger tank, thicker panels, from #62.
        ///
        /// Armour hands the extra integrity over as well as raising the ceiling: a player who bolts
        /// plate to a dented car has made it tougher, and a version of this that only moved the
        /// maximum would leave them looking at a car that got *more* broken the moment they paid.
        /// Fuel is not given away the same way - the tank gets bigger, filling it is still the
        /// trader's business.
        /// </summary>
        public void ServerTune(float tank, float armour)
        {
            if (!IsServerStarted) return;

            _tank = _stockTank * tank;

            float wasMax = _integrityMax;
            _integrityMax = _stockIntegrity * armour;

            _fuel.Value = Mathf.Min(_fuel.Value, _tank);
            _integrity.Value = Mathf.Min(_integrityMax,
                                         _integrity.Value + Mathf.Max(0f, _integrityMax - wasMax));
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Found with a full tank and no dents. A vehicle that spawns needing service is a puzzle
            // nobody set, and the island only bakes one of each.
            _fuel.Value = _tank;
            _integrity.Value = _integrityMax;
        }

        void FixedUpdate()
        {
            _speed = _body.linearVelocity.magnitude;

            if (!IsServerStarted || _fuel.Value <= 0f) return;
            if (_vehicle == null || _vehicle.Driver == null) return;

            _fuel.Value = Mathf.Max(0f, _fuel.Value
                                        - _speed * Time.fixedDeltaTime * _burnPerKm / 1000f);
        }

        void OnCollisionEnter(Collision other)
        {
            if (!IsServerStarted || _integrity.Value <= 0f) return;

            // People do not dent cars. They are the softest thing in the world and #60 already
            // decided what happens to them.
            if (other.collider.GetComponentInParent<StunState>() != null) return;

            if (_speed < _crashSpeed) return;

            ServerDamage((_speed - _crashSpeed) * _crashDamage, other.collider.name);
        }

        public void ServerDamage(float amount, string cause = "something")
        {
            if (!IsServerStarted || amount <= 0f) return;

            float was = _integrity.Value;
            _integrity.Value = Mathf.Max(0f, was - amount);

            if (CommandLine.HasFlag("-vehicleLog"))
                Debug.Log($"[VehicleCondition] {name} hit {cause} at {_speed:F1} m/s: "
                          + $"-{amount:F0} integrity, {_integrity.Value:F0}/{_integrityMax:F0} left"
                          + (IsWrecked ? " - WRECKED." : "."));
        }

        /// <summary>Server only. One can. Returns false if the tank was already full.</summary>
        public bool ServerRefuel()
        {
            if (!IsServerStarted || !NeedsFuel) return false;

            _fuel.Value = Mathf.Min(_tank, _fuel.Value + _fuelPerCan);
            return true;
        }

        /// <summary>Server only. One piece of scrap. Returns false if nothing was bent.</summary>
        public bool ServerRepair()
        {
            if (!IsServerStarted || !NeedsRepair) return false;

            _integrity.Value = Mathf.Min(_integrityMax, _integrity.Value + _repairPerPart);
            return true;
        }

        /// <summary>Server only, for the harness and for whatever wants to break one on purpose.</summary>
        public void ServerSetFuel(float litres)
        {
            if (IsServerStarted) _fuel.Value = Mathf.Clamp(litres, 0f, _tank);
        }

        public string Report()
            => $"{_fuel.Value:F1}/{_tank:F0}L, {_integrity.Value:F0}/{_integrityMax:F0} integrity"
               + (CanDrive ? "" : IsWrecked ? ", WRECKED" : ", DRY");

        /// <summary>Editor-time setup. See <see cref="VehicleBuilder"/>.</summary>
        public void Configure(float tank, float integrity)
        {
            _tank = tank;
            _integrityMax = integrity;
        }
    }
}
