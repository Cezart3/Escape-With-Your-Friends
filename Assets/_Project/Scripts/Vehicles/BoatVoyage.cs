using System.Linq;
using EscapeWithYourFriends.Net;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// The three things #69 asks of a boat: it does not work until it is paid for, it is how you
    /// leave, and losing it is survivable.
    ///
    /// **The gate is four parts, each from a different kind of play (#274), and they belong to the
    /// group rather than to the hull.** Planks crafted at the bench, the outboard bought from the
    /// Trader, fuel taken from the cult village and the chart from the end of the cave. A part in
    /// your hand is already a thing <see cref="Vehicle"/> knows how to spend - it is the same press
    /// that pours a can of fuel in - so fitting one needs no new key, no new screen and no second
    /// interactable competing for the same button. What is fitted is counted in a static, because
    /// the hull at the far mooring is a different <c>NetworkObject</c> in a different scene and it
    /// would be absurd to make a group that has already bought a boat buy the second island's one
    /// as well.
    ///
    /// **Travel is a scene swap, not streaming.** The islands are two scenes a kilometre of sea
    /// apart, and nothing on one is ever visible from the other, so loading both to slide between
    /// them would be paying for a view nobody gets. Sail past the edge of the map and hold it for a
    /// few seconds and <see cref="GameSceneLoader.ServerTravel"/> puts everybody on the other one.
    ///
    /// **The boat does not come with you, and that is the recovery.** A scene object cannot be moved
    /// between scenes in FishNet, and it should not be: each island keeps its own hull at its own
    /// mooring, so arriving anywhere always means there is something moored to leave on. The far
    /// hull is seaworthy on arrival because the group already owns the parts.
    ///
    /// **A dead boat is towed home.** Wrecked on a rock or run dry halfway across, with four people
    /// standing on it, it waits <see cref="_recoverSeconds"/> and then reappears at its mooring -
    /// riders still aboard, because they are glued to their anchors and a teleport takes them with
    /// it - repaired and fuelled. That costs the crossing, which is the punishment, rather than the
    /// run, which would be a soft lock on an island with no shop.
    /// </summary>
    [RequireComponent(typeof(Vehicle))]
    public class BoatVoyage : NetworkBehaviour
    {
        /// <summary>The four parts, matched by item id like fuel and scrap, and where each is found.</summary>
        public static readonly (string Item, string Name, string Where)[] Parts =
        {
            ("hull_planks", "hull planks", "craft them at the bench"),
            ("outboard", "the outboard", "the Trader sells it"),
            ("fuel_drum", "fuel", "the cult village"),
            ("chart_page", "the chart", "the end of the cave"),
        };

        const int All = (1 << 4) - 1;

        [Tooltip("Metres past the corner of the terrain where the open sea starts. Generous: the "
                 + "point is to leave deliberately, not to be swept off by a wave.")]
        [SerializeField] float _margin = 60f;

        [Tooltip("Seconds out there before the crossing commits, so drifting over the line while "
                 + "fishing does not move the whole group to another island.")]
        [SerializeField] float _holdSeconds = 4f;

        [Tooltip("Seconds a hull that cannot drive takes to turn up at its mooring again.")]
        [SerializeField] float _recoverSeconds = 45f;

        /// <summary>
        /// Parts this hull has, one bit per entry of <see cref="Parts"/>, mirrored to clients for the crosshair. The count is the group's -
        /// see <see cref="_owned"/> - and this is the copy of it that the boat in front of you shows.
        /// </summary>
        readonly SyncVar<int> _fitted = new();

        /// <summary>
        /// ponytail: parts are the group's and the group is the process. A host that quits to the
        /// menu and hosts again in the same process keeps its boat. Move this into the save file
        /// when #75 gives one a home.
        /// </summary>
        static int _owned;

        /// <summary>
        /// False while a harness is deliberately standing the hull somewhere it could never sail to,
        /// which is what <see cref="BoatTest"/> does two kilometres out to measure it in clean water.
        /// </summary>
        public static bool Sailing = true;

        Vehicle _vehicle;
        VehicleCondition _condition;
        BoatController _boat;
        Rigidbody _body;

        Vector3 _mooring;
        Quaternion _moored;

        float _offTheMap;
        float _adrift;
        bool _left;

        public bool Seaworthy => _fitted.Value == All;
        public int Fitted => Parts.Count(p => (_fitted.Value & Bit(p.Item)) != 0);
        public int Needed => Parts.Length;
        public int Missing => Needed - Fitted;

        static int Bit(string item) => 1 << System.Array.FindIndex(Parts, p => p.Item == item);

        /// <summary>Whether this item is a part the boat still lacks.</summary>
        public bool Wants(string item)
            => System.Array.FindIndex(Parts, p => p.Item == item) >= 0 && (_fitted.Value & Bit(item)) == 0;

        /// <summary>What is still missing and where it is, for the objective line.</summary>
        public string MissingLine()
            => string.Join(", ", Parts.Where(p => (_fitted.Value & Bit(p.Item)) == 0).Select(p => $"{p.Name} ({p.Where})"));

        /// <summary>Where this hull ties up, which is wherever the POI placed it.</summary>
        public Vector3 Mooring => _mooring;

        void Awake()
        {
            _vehicle = GetComponent<Vehicle>();
            _condition = GetComponent<VehicleCondition>();
            _boat = GetComponent<BoatController>();
            _body = GetComponent<Rigidbody>();
        }

        public override void OnStartServer()
        {
            _mooring = transform.position;
            _moored = transform.rotation;

            // A group that has bought a boat has bought every boat. This is what makes the far
            // island's hull drivable the moment they walk down onto its beach.
            _fitted.Value = _owned;
        }

        /// <summary>Server only. Fits this part. Returns false if it is not one, or already fitted.</summary>
        public bool ServerFit(string item)
        {
            if (!IsServerStarted || !Wants(item)) return false;

            _fitted.Value |= Bit(item);
            _owned |= _fitted.Value;

            Debug.Log($"[BoatVoyage] {item}, part {Fitted} of {Needed}, fitted"
                      + (Seaworthy ? " - she floats, and she goes." : "."));

            return true;
        }

        /// <summary>Server only, for the harness and for anything that hands a group a boat outright.</summary>
        public void ServerGrant()
        {
            if (!IsServerStarted) return;
            _owned = All;
            _fitted.Value = All;
        }

        /// <summary>Forgets what the group owns. The harness calls it to start from nothing.</summary>
        public static void ServerForget() => _owned = 0;

        /// <summary>Server only, for the harness. Forty-five seconds is the right wait for a player
        /// and the wrong one for a suite that has three more things to check.</summary>
        public void ServerRecoverIn(float seconds)
        {
            if (IsServerStarted) _recoverSeconds = Mathf.Max(0.5f, seconds);
        }

        void Update()
        {
            if (!IsServerStarted || _left) return;

            float dt = Time.deltaTime;

            if (_condition != null && !_condition.CanDrive) { Adrift(dt); return; }

            _adrift = 0f;
            Crossing(dt);
        }

        /// <summary>
        /// A hull that cannot drive is a hull somebody is standing on in the middle of the sea. Wait,
        /// then put it back where it came from with everybody still on it.
        /// </summary>
        void Adrift(float dt)
        {
            _offTheMap = 0f;
            _adrift += dt;
            if (_adrift < _recoverSeconds) return;

            _adrift = 0f;
            ServerTow();
        }

        /// <summary>Server only. Puts the hull back at its mooring, whole and fuelled.</summary>
        public void ServerTow()
        {
            if (!IsServerStarted) return;

            if (_body != null)
            {
                _body.linearVelocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }

            transform.SetPositionAndRotation(_mooring, _moored);

            // Loops rather than a restore method: one press of scrap is 30 integrity and one can is
            // 25 litres, and repeating the thing that already exists is smaller than a second way of
            // setting the same two fields.
            if (_condition != null)
            {
                while (_condition.ServerRepair()) { }
                while (_condition.ServerRefuel()) { }
            }

            Debug.Log($"[BoatVoyage] the {_vehicle.Label} drifted home to its mooring, repaired"
                      + $" and fuelled, with {_vehicle.Occupied()} aboard.");
        }

        /// <summary>Counts the seconds spent out beyond the map with somebody at the wheel.</summary>
        void Crossing(float dt)
        {
            if (!Sailing || !Seaworthy || _vehicle.Driver == null
                || (_boat != null && !_boat.IsAfloat) || !OffTheMap(transform.position, _margin))
            {
                _offTheMap = 0f;
                return;
            }

            _offTheMap += dt;
            if (_offTheMap < _holdSeconds) return;

            _offTheMap = 0f;

            GameSceneLoader loader = GameSceneLoader.Instance;
            string there = GameSceneLoader.Crossing;
            if (loader == null || there == null) return;

            Debug.Log($"[BoatVoyage] {_vehicle.Occupied()} aboard and {_holdSeconds:0}s past the "
                      + $"edge of {GameSceneLoader.Current}; making for {there}.");

            _left = loader.ServerTravel(there);
        }

        /// <summary>
        /// Whether a point is off the edge of whatever island is loaded. The terrain is the map, so
        /// its own extents are the coastline plus everything the island could grow into - no marker
        /// to place, and the second island gets the smaller crossing for free because it is smaller.
        /// </summary>
        public static bool OffTheMap(Vector3 position, float margin)
        {
            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null || terrain.terrainData == null) return false;

            Vector3 size = terrain.terrainData.size;
            Vector3 centre = terrain.transform.position + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f);

            float flat = Vector2.Distance(new Vector2(position.x, position.z),
                                          new Vector2(centre.x, centre.z));

            return flat > Mathf.Max(size.x, size.z) * 0.5f + margin;
        }

        /// <summary>Metres from here to the line, negative once it is behind you. For the harness.</summary>
        public static float ToTheEdge(Vector3 position, float margin = 60f)
        {
            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null || terrain.terrainData == null) return float.NaN;

            Vector3 size = terrain.terrainData.size;
            Vector3 centre = terrain.transform.position + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f);

            float flat = Vector2.Distance(new Vector2(position.x, position.z),
                                          new Vector2(centre.x, centre.z));

            return Mathf.Max(size.x, size.z) * 0.5f + margin - flat;
        }

        public string Report()
            => Seaworthy ? "seaworthy" : $"{Fitted}/{Needed} parts, missing {MissingLine()}";
    }
}
