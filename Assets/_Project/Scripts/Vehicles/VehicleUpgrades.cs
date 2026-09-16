using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Data;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// #62: what is bolted to this vehicle, and what that does to it.
    ///
    /// **The fitting interaction already existed.** #61 taught the vehicle to look at what is in your
    /// hand and do something with it - petrol refuels, scrap repairs - so a part is just a third
    /// answer to that same question. Nothing here adds a screen, a key, an RPC or a workbench. You
    /// buy the part off the trader's shelf like any other item, carry it to the vehicle, and press
    /// the interact key standing next to it.
    ///
    /// **What a vehicle accepts is a list on the vehicle, not a global catalog.** The buggy takes
    /// tyres and the boat does not, and one serialized array per prefab says so without a single
    /// branch - fitting tyres to a hull is impossible because the hull has never heard of them. This
    /// is also the whole reason there is no <c>VehicleUpgradeCatalog</c>: nothing crosses the wire
    /// but the tier numbers below, so there is no index to agree on and no static to keep alive.
    ///
    /// **Effects are recomputed whole, never accumulated.** <see cref="Apply"/> reads the fitted
    /// tiers and writes absolute numbers over the stock ones every time, so fitting a part twice,
    /// applying after a late join, or replaying the list in any order all land on the same vehicle.
    /// The stock numbers are captured by each controller in its own Awake, which is the only place
    /// they are guaranteed to still be what the prefab was baked with.
    ///
    /// Only the server ever applies anything: the chassis is simulated there and replicated out, so
    /// a client that never hears about the engine is a client that never needed to.
    /// </summary>
    [RequireComponent(typeof(Vehicle))]
    public class VehicleUpgrades : NetworkBehaviour
    {
        /// <summary>One per <see cref="VehiclePart"/>.</summary>
        public const int Slots = 4;

        [Tooltip("Every part this vehicle will accept, in any order. A part not in here cannot be "
                 + "fitted, which is how a boat refuses tyres.")]
        [SerializeField] VehicleUpgradeDef[] _fits = new VehicleUpgradeDef[0];

        /// <summary>
        /// Fitted tier per part, indexed by <c>(int)VehiclePart</c>, 0 meaning stock. Replicated
        /// because the crosshair prompt on every client has to know whether the thing in your hand
        /// is still worth fitting.
        /// </summary>
        readonly SyncList<int> _tiers = new();

        CarController _car;
        BoatController _boat;
        VehicleCondition _condition;

        public IReadOnlyList<VehicleUpgradeDef> Fits => _fits;

        void Awake()
        {
            _car = GetComponent<CarController>();
            _boat = GetComponent<BoatController>();
            _condition = GetComponent<VehicleCondition>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            _tiers.Clear();
            for (int i = 0; i < Slots; i++) _tiers.Add(0);
        }

        public int TierOf(VehiclePart part)
        {
            int slot = (int)part;
            return slot >= 0 && slot < _tiers.Count ? _tiers[slot] : 0;
        }

        /// <summary>
        /// The part this item would fit right now, or null. Null covers all three ways an item is
        /// not a part: this vehicle does not take it, it is not a part at all, or the next tier up
        /// is already bolted on.
        /// </summary>
        public VehicleUpgradeDef Fit(ItemDef held)
        {
            if (held == null) return null;

            foreach (VehicleUpgradeDef def in _fits)
            {
                if (def == null || def.ItemId != held.Id) continue;
                if (def.Tier != TierOf(def.Part) + 1) continue;

                return def;
            }

            return null;
        }

        /// <summary>Server only. Bolts it on and rewrites the vehicle's numbers.</summary>
        public bool ServerFit(VehicleUpgradeDef def)
        {
            if (!IsServerStarted || def == null) return false;
            if (def.Tier != TierOf(def.Part) + 1) return false;
            if (!_fits.Contains(def)) return false;

            _tiers[(int)def.Part] = def.Tier;
            Apply();

            Debug.Log($"[VehicleUpgrades] {name}: fitted {def.DisplayName} "
                      + $"({def.Part} tier {def.Tier}, x{def.Multiplier:0.00}). {Report()}");

            return true;
        }

        /// <summary>The absolute multiplier over stock for a part, 1 for a part nobody has fitted.</summary>
        public float Multiplier(VehiclePart part)
        {
            int tier = TierOf(part);
            if (tier <= 0) return 1f;

            foreach (VehicleUpgradeDef def in _fits)
                if (def != null && def.Part == part && def.Tier == tier) return def.Multiplier;

            return 1f;
        }

        /// <summary>
        /// Server only. Writes every fitted number onto the vehicle from scratch. Cheap enough to
        /// call on every fit, and calling it that way is what keeps the tiers the single source of
        /// truth rather than a history of what has been applied.
        /// </summary>
        void Apply()
        {
            if (_car != null) _car.ServerTune(Multiplier(VehiclePart.Engine),
                                              Multiplier(VehiclePart.Tyres));

            if (_boat != null) _boat.ServerTune(Multiplier(VehiclePart.Engine));

            if (_condition != null) _condition.ServerTune(Multiplier(VehiclePart.Tank),
                                                          Multiplier(VehiclePart.Armour));
        }

        public string Report()
        {
            string[] parts = new string[Slots];

            for (int i = 0; i < Slots; i++)
            {
                var part = (VehiclePart)i;
                int tier = TierOf(part);

                parts[i] = tier <= 0 ? $"{part} stock"
                                     : $"{part} tier {tier} (x{Multiplier(part):0.00})";
            }

            return string.Join(", ", parts) + ".";
        }

        /// <summary>Editor-time setup. See <c>VehicleBuilder</c>.</summary>
        public void Configure(VehicleUpgradeDef[] fits) => _fits = fits ?? new VehicleUpgradeDef[0];
    }
}
