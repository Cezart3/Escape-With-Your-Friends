using System.Collections;
using System.Collections.Generic;
using EscapeWithYourFriends.Economy;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>What a chip is riding on. The spots around the table, one kind each.</summary>
    public enum BetKind
    {
        /// <summary>One number, 35 to 1. The only bet on this table that is worth a story.</summary>
        Straight,

        Red,
        Black,
        Odd,
        Even,

        /// <summary>1-18.</summary>
        Low,

        /// <summary>19-36.</summary>
        High,

        /// <summary>1-12, 2 to 1.</summary>
        DozenLow,

        /// <summary>13-24, 2 to 1.</summary>
        DozenMid,

        /// <summary>25-36, 2 to 1.</summary>
        DozenHigh,
    }

    /// <summary>
    /// The roulette table, from #64. A single-zero wheel: 37 pockets, and the house edge is the
    /// green one.
    ///
    /// **The wheel does not decide anything.** The server rolls a number, tells everybody what it
    /// is, and the clients then animate a wheel that is already over. There is no code path in
    /// which a client's frame rate, latency or patched binary can reach the result: the roll happens
    /// inside <see cref="Spin"/> behind <c>IsServerStarted</c>, and what crosses the wire is the
    /// answer, not the seed, not the wheel speed, and not a request to roll. That is #64's
    /// acceptance, and it is the same shape as every other authority decision in the project.
    ///
    /// The animation is therefore cosmetic by construction. It eases to the pocket it was handed
    /// and stops there, and if it were wrong the only consequence would be a wheel that lies about
    /// a payout that already happened - which is exactly what the harness checks, by reading the
    /// angle back and asking which pocket is under the marker.
    ///
    /// Bets are placed by walking up to a spot and pressing a key; see <see cref="BetSpot"/> for why
    /// the table has ten small objects on it rather than one screen.
    /// </summary>
    public class RouletteWheel : NetworkBehaviour
    {
        /// <summary>
        /// The pockets in the order they sit on a real single-zero wheel, clockwise from 0. Copied
        /// rather than sorted because a wheel with the numbers in counting order looks wrong to
        /// anybody who has seen one, and an array literal is cheaper than a shrug.
        /// </summary>
        static readonly int[] Order =
        {
            0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23, 10,
            5, 24, 16, 33, 1, 20, 14, 31, 9, 22, 18, 29, 7, 28, 12, 35, 3, 26,
        };

        static readonly HashSet<int> Reds = new()
        {
            1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36,
        };

        public const int Pockets = 37;

        /// <summary>How long the table takes bets once the first one lands.</summary>
        [Min(1f)] [SerializeField] float _betWindow = 12f;

        /// <summary>How long the wheel pretends to be undecided.</summary>
        [Min(0.5f)] [SerializeField] float _spinSeconds = 5f;

        [Tooltip("Whole turns the wheel makes before it settles. Cosmetic.")]
        [Min(1)] [SerializeField] int _turns = 4;

        [SerializeField] Transform _wheel;
        [SerializeField] Transform _ball;

        /// <summary>The last number that came up, or -1 before the first spin. Everybody sees it.</summary>
        readonly SyncVar<int> _result = new(-1);

        /// <summary>True from the moment betting closes until the payouts are done.</summary>
        readonly SyncVar<bool> _spinning = new();

        /// <summary>
        /// Everything riding on this spin, replicated. <see cref="Staked"/> is the server's own list
        /// and reads zero everywhere else, which is no use to the board a player is looking at (#65).
        /// </summary>
        readonly SyncVar<int> _pot = new();

        readonly List<Bet> _bets = new();

        System.Random _rng;
        float _closesAt;
        bool _windowOpen;

        // The animation, client-side and entirely local. None of this is replicated: every peer is
        // told the same number and draws its own wheel getting there.
        float _spinFrom;
        float _spinTo;
        float _spinStarted = -1f;

        public int Result => _result.Value;
        public bool Spinning => _spinning.Value;
        public bool TakingBets => !_spinning.Value;
        public float SpinSeconds => _spinSeconds;
        public float BetWindow => _betWindow;

        /// <summary>How many chips are riding on the table right now. Server-side truth.</summary>
        public int Staked
        {
            get
            {
                int total = 0;
                foreach (Bet bet in _bets) total += bet.Chips;
                return total;
            }
        }

        public int BetCount => _bets.Count;

        /// <summary>What is on the table, as every peer sees it. See <see cref="_pot"/>.</summary>
        public int Pot => _pot.Value;

        struct Bet
        {
            public NetworkObject Owner;
            public BetKind Kind;
            public int Number;
            public int Chips;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _rng = new System.Random();
        }

        /// <summary>
        /// A player who walks in after a spin should see the wheel sitting on the last number, the
        /// way a real table does, rather than on whatever pocket the prefab was saved at. Anybody
        /// who joins mid-spin misses that round's RPC and gets the number when the next one lands.
        /// </summary>
        public override void OnStartClient()
        {
            base.OnStartClient();

            if (_wheel == null || _result.Value < 0) return;
            _wheel.localRotation = Quaternion.Euler(0f, PocketAngle(_result.Value), 0f);
        }

        // ---------------------------------------------------------------- the table of payouts

        /// <summary>
        /// What a winning bet returns, over and above the stake. 35 to 1 on a number, 2 to 1 on a
        /// dozen, evens on the rest - the standard table, which with 37 pockets leaves the house
        /// 2.7% whatever anybody bets. That number is the only reason the casino exists, and it is
        /// not a knob.
        /// </summary>
        public static int Payout(BetKind kind) => kind switch
        {
            BetKind.Straight => 35,
            BetKind.DozenLow or BetKind.DozenMid or BetKind.DozenHigh => 2,
            _ => 1,
        };

        /// <summary>
        /// Whether a bet takes the pocket. Zero loses everything except a straight bet on zero,
        /// which is the house edge stated as a rule rather than a percentage.
        /// </summary>
        public static bool Wins(BetKind kind, int number, int straightOn)
        {
            if (number < 0 || number >= Pockets) return false;

            if (kind == BetKind.Straight) return number == straightOn;
            if (number == 0) return false;

            return kind switch
            {
                BetKind.Red => Reds.Contains(number),
                BetKind.Black => !Reds.Contains(number),
                BetKind.Odd => number % 2 == 1,
                BetKind.Even => number % 2 == 0,
                BetKind.Low => number <= 18,
                BetKind.High => number >= 19,
                BetKind.DozenLow => number <= 12,
                BetKind.DozenMid => number >= 13 && number <= 24,
                BetKind.DozenHigh => number >= 25,
                _ => false,
            };
        }

        public static bool IsRed(int number) => Reds.Contains(number);

        /// <summary>Where a pocket sits on the wheel, in degrees. The inverse of what the marker reads.</summary>
        public static float PocketAngle(int number)
        {
            int slot = System.Array.IndexOf(Order, number);
            return slot < 0 ? 0f : -slot * (360f / Pockets);
        }

        /// <summary>Which pocket is under the marker at a given wheel angle. Used to read a wheel back.</summary>
        public static int PocketAt(float angle)
        {
            float step = 360f / Pockets;
            float turned = Mathf.Repeat(-angle, 360f);
            int slot = Mathf.RoundToInt(turned / step) % Pockets;

            return Order[slot];
        }

        // ---------------------------------------------------------------- taking money

        /// <summary>
        /// Puts chips on a spot. The stake leaves the wallet now, not at settlement: a bet you can
        /// walk away from is not a bet, and it also means the table never has to chase somebody who
        /// disconnected mid-spin. Returns what was actually staked, which is zero if the wheel is
        /// already turning or the wallet cannot cover it.
        /// </summary>
        [Server]
        public int ServerPlaceBet(NetworkObject actor, BetKind kind, int number, int chips)
        {
            if (_spinning.Value || actor == null || chips <= 0) return 0;

            var wallet = actor.GetComponent<Wallet>();
            if (wallet == null) return 0;

            int staked = wallet.ServerStakeChips(chips);
            if (staked <= 0) return 0;

            _bets.Add(new Bet { Owner = actor, Kind = kind, Number = number, Chips = staked });
            _pot.Value = Staked;

            Debug.Log($"[Roulette] {actor.name} put {staked} on {Describe(kind, number)}. "
                      + $"{_bets.Count} bet(s), {Staked} on the table.");

            if (!_windowOpen)
            {
                _windowOpen = true;
                _closesAt = Time.time + _betWindow;
                StartCoroutine(Round());
            }

            return staked;
        }

        /// <summary>Closes the window early. The harness uses it; a croupier NPC would too.</summary>
        [Server]
        public void ServerCallIt()
        {
            if (_windowOpen) _closesAt = Time.time;
        }

        IEnumerator Round()
        {
            while (Time.time < _closesAt) yield return null;

            _windowOpen = false;
            _spinning.Value = true;

            int result = _rng.Next(0, Pockets);
            _result.Value = result;

            RpcSpinTo(result, _spinSeconds);
            Animate(result, _spinSeconds);

            yield return new WaitForSeconds(_spinSeconds);

            Settle(result);

            _spinning.Value = false;
        }

        void Settle(int result)
        {
            int paid = 0;

            foreach (Bet bet in _bets)
            {
                if (!Wins(bet.Kind, result, bet.Number)) continue;
                if (bet.Owner == null) continue;

                var wallet = bet.Owner.GetComponent<Wallet>();
                if (wallet == null) continue;

                // Stake back plus the odds. The stake was taken when the bet was placed, so a
                // winner is handed the whole of it again, not just the winnings.
                int back = bet.Chips * (Payout(bet.Kind) + 1);
                wallet.ServerPayChips(back);
                paid += back;
            }

            Debug.Log($"[Roulette] {result} {(result == 0 ? "green" : IsRed(result) ? "red" : "black")}. "
                      + $"{Staked} staked, {paid} paid out across {_bets.Count} bet(s).");

            // Bet this spin, holding nothing now. Every winner was paid above, so a zero here is a
            // player who put the lot on the table and got none of it back. #92.
            foreach (NetworkObject owner in new HashSet<NetworkObject>(_bets.ConvertAll(b => b.Owner)))
            {
                var wallet = owner != null ? owner.GetComponent<Wallet>() : null;
                if (wallet != null && wallet.Chips == 0)
                    Net.Achievements.ServerAward(owner, Net.Achievements.LostItAll);
            }

            _bets.Clear();
            _pot.Value = 0;
        }

        public static string Describe(BetKind kind, int number)
            => kind == BetKind.Straight ? $"the {number}" : kind.ToString();

        // ---------------------------------------------------------------- the part that is a lie

        /// <summary>
        /// Everybody animates the same already-decided number, for the same length of time. The
        /// parameters are the whole payload: there is nothing here a client could answer with.
        ///
        /// The duration travels because it has to. It is a server-side field, and the moment
        /// anything changes it - the harness does, a croupier with a fast table would - a client
        /// animating for the prefab's five seconds is still turning when the server has already
        /// paid out, and its wheel is showing a number that is not the one that won. Which is
        /// exactly how the two-process run caught this: the host saw 10, the client's wheel was
        /// mid-spin on 0.
        /// </summary>
        [ObserversRpc(ExcludeServer = true)]
        void RpcSpinTo(int result, float seconds) => Animate(result, seconds);

        void Animate(int result, float seconds)
        {
            if (_wheel == null) return;

            _spinSeconds = Mathf.Max(0.1f, seconds);

            _spinFrom = _wheel.localEulerAngles.y;
            _spinTo = _spinFrom + _turns * 360f
                      + Mathf.DeltaAngle(_spinFrom, PocketAngle(result));
            _spinStarted = Time.time;
        }

        void Update()
        {
            if (_spinStarted < 0f || _wheel == null) return;

            float t = Mathf.Clamp01((Time.time - _spinStarted) / _spinSeconds);
            float eased = 1f - Mathf.Pow(1f - t, 3f);

            _wheel.localRotation = Quaternion.Euler(0f, Mathf.Lerp(_spinFrom, _spinTo, eased), 0f);

            if (_ball != null)
                _ball.localRotation = Quaternion.Euler(0f, -Mathf.Lerp(_spinFrom, _spinTo, eased) * 2f, 0f);

            if (t >= 1f) _spinStarted = -1f;
        }

        /// <summary>True while this peer's own wheel is still turning. Local, like the animation.</summary>
        public bool Animating => _spinStarted >= 0f;

        /// <summary>Where the wheel is actually pointing, as a pocket. What a camera would see.</summary>
        public int ShowingPocket() => _wheel != null ? PocketAt(_wheel.localEulerAngles.y) : -1;

        /// <summary>
        /// Shortens the round. For the harness, which wants to watch fifty spins without waiting
        /// twelve seconds for each of them; a croupier NPC would be the other caller.
        /// </summary>
        [Server]
        public void ServerSetTiming(float betWindow, float spinSeconds)
        {
            _betWindow = Mathf.Max(0.1f, betWindow);
            _spinSeconds = Mathf.Max(0.1f, spinSeconds);
        }

        /// <summary>Editor-time setup. See <c>CasinoFactory</c>.</summary>
        public void Configure(Transform wheel, Transform ball, float betWindow, float spinSeconds)
        {
            _wheel = wheel;
            _ball = ball;
            _betWindow = Mathf.Max(1f, betWindow);
            _spinSeconds = Mathf.Max(0.5f, spinSeconds);
        }
    }
}
