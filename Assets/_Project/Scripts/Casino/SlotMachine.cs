using System.Collections;
using EscapeWithYourFriends.Economy;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    /// <summary>
    /// One slot cabinet. The game itself is <see cref="SlotMath"/>; this is the part with a stake,
    /// a network and a screen.
    ///
    /// **Same authority shape as the roulette wheel.** The server rolls a seed, works the whole
    /// spin out, and only then tells anybody. What crosses the wire is that seed and the bet - a
    /// spin is decided before a single reel moves, and a client can neither ask for a seed nor
    /// answer with one. The difference from the wheel is what the seed buys: a Volcano spin with a
    /// feature is a few hundred grids, and every client rebuilds all of them from four bytes with
    /// the same arithmetic, because <see cref="SlotRng"/> is ours and not the runtime's.
    ///
    /// The stake leaves the wallet on the press and the win arrives when the animation ends, so the
    /// chip count never gives a spin away. A player who disconnects mid-spin forfeits it, exactly
    /// as at the table.
    ///
    /// Coconut Sevens adds the double-up card: after a win, whoever spun may put the whole win on
    /// red or black, up to <see cref="MaxGambles"/> times. The win is already in their stack, so a
    /// gamble is an ordinary stake of it - fifty-fifty, paid two for one, no edge either way.
    /// Spinning again is how you walk away with it.
    /// </summary>
    public class SlotMachine : NetworkBehaviour
    {
        public const int MaxGambles = 5;

        [SerializeField] SlotKind _kind;

        [Header("Screen, set by SlotFactory")]
        [SerializeField] Transform[] _cells;
        [SerializeField] MeshFilter[] _cellMeshes;

        [Tooltip("Reef's multiplier spots, one tile behind each cell. Empty on the other cabinets.")]
        [SerializeField] MeshFilter[] _spots;

        // Everything on the screen wears one atlas material and is told apart by its mesh, whose
        // UVs all sit on one colour of the atlas. A symbol is a mesh swap, never a material swap,
        // and the whole casino floor adds one material to the scene's budget.
        [SerializeField] Mesh[] _symbolMeshes;
        [SerializeField] float[] _symbolScales;
        [SerializeField] Mesh _spotMarked;
        [SerializeField] Mesh _spotHot;
        [SerializeField] float _cellSize = 0.2f;

        [Tooltip("The double-up card. Sevens only.")]
        [SerializeField] MeshFilter _card;
        [SerializeField] Mesh _cardBack;
        [SerializeField] Mesh _cardRed;
        [SerializeField] Mesh _cardBlack;

        [Tooltip("The palette's gold, for the big-win coins.")]
        [SerializeField] Material _coin;

        readonly SyncVar<int> _betIndex = new();
        readonly SyncVar<bool> _busy = new();
        readonly SyncVar<int> _lastSeed = new();
        readonly SyncVar<int> _lastBet = new();
        readonly SyncVar<int> _lastWin = new();

        /// <summary>The win on offer to the double-up card, or 0.</summary>
        readonly SyncVar<int> _gamble = new();

        /// <summary>Object id of whoever the card is offered to, or -1.</summary>
        readonly SyncVar<int> _gamblerId = new(-1);

        /// <summary>The last card turned: 0 none since the last spin, 1 red, 2 black.</summary>
        readonly SyncVar<int> _turned = new();

        /// <summary>Every spawned cabinet on this peer, for the HUD, which would otherwise search the scene each frame.</summary>
        internal static readonly System.Collections.Generic.List<SlotMachine> All = new();

        /// <summary>How long the winner has the cabinet to themselves to decide on the card.</summary>
        const float GambleHold = 15f;

        System.Random _rng;
        NetworkObject _gambler;
        int _gambles;
        float _gambleUntil;

        // The screen, local to every peer. Nothing below is replicated.
        SlotResult _playing;
        SlotFrame _settled;
        int _frame;
        float _frameStarted;
        int[] _shown;
        float _nextFlicker;
        int _reelsStopped;
        Vector3[] _home;

        public SlotKind Kind => _kind;
        public string Title => SlotMath.Title(_kind);
        public int Bet => SlotMath.Bets[Mathf.Clamp(_betIndex.Value, 0, SlotMath.Bets.Length - 1)];
        public int NextBet => SlotMath.Bets[(_betIndex.Value + 1) % SlotMath.Bets.Length];
        public bool Busy => _busy.Value;
        public int LastSeed => _lastSeed.Value;
        public int LastBet => _lastBet.Value;
        public int LastWin => _lastWin.Value;
        public int Gamble => _gamble.Value;
        public int GamblerId => _gamblerId.Value;
        public int Card => _turned.Value;
        public int Cols => SlotMath.Cols(_kind);
        public int Rows => SlotMath.Rows(_kind);

        /// <summary>Server only: the whole of the last spin, as the server worked it out.</summary>
        public SlotResult LastResult { get; private set; }

        /// <summary>True while this peer's own screen is still playing a spin.</summary>
        public bool Animating => _playing != null;

        /// <summary>The picture on this peer's screen right now, or null before the first spin.</summary>
        public SlotFrame Showing => _playing != null ? _playing.Frames[_frame] : _settled;

        /// <summary>The spin this peer's screen is playing or last played. Local.</summary>
        public SlotResult Played { get; private set; }

        /// <summary>The symbol drawn in each cell right now, what a camera would see.</summary>
        public int[] ShowingGrid() => _shown;

        public override void OnStartServer()
        {
            base.OnStartServer();
            _rng = new System.Random();
        }

        /// <summary>A player who walks in late sees the last spin's final picture, not an empty cabinet.</summary>
        public override void OnStartClient()
        {
            base.OnStartClient();
            All.Add(this);

            // Mid-spin, the last seed is the spin still running: drawing its end would give it away.
            if (_lastBet.Value <= 0 || _busy.Value) return;

            Played = SlotMath.Spin(_kind, _lastSeed.Value, _lastBet.Value);
            _settled = Played.Frames[Played.Frames.Count - 1];
            DrawStill(_settled);
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            All.Remove(this);
        }

        void Awake()
        {
            if (_coin != null) BigWin.Gold = _coin;

            _shown = new int[SlotMath.Cols(_kind) * SlotMath.Rows(_kind)];
            _home = new Vector3[_cells != null ? _cells.Length : 0];
            for (int i = 0; i < _home.Length; i++)
                if (_cells[i] != null) _home[i] = _cells[i].localPosition;
        }

        // ---------------------------------------------------------------- the buttons

        /// <summary>Stakes the bet and spins. Returns the chips staked, 0 if refused.</summary>
        [Server]
        public int ServerSpin(NetworkObject actor)
        {
            if (_busy.Value || actor == null) return 0;

            // The winner gets a moment with the card before somebody else's spin takes it away.
            if (_gamble.Value > 0 && actor != _gambler && _gambler != null && Time.time < _gambleUntil) return 0;

            var wallet = actor.GetComponent<Wallet>();
            if (wallet == null) return 0;

            int bet = Bet;
            if (wallet.ServerStakeChips(bet) <= 0) return 0;

            World.RunSummary.ServerStaked(bet);

            // Spinning again is collecting: whatever the card was offering is simply kept.
            EndOffer();
            _turned.Value = 0;

            int seed = _rng.Next();
            SlotResult result = SlotMath.Spin(_kind, seed, bet);
            LastResult = result;

            _busy.Value = true;
            _lastSeed.Value = seed;
            _lastBet.Value = bet;

            RpcPlay(seed, bet);
            Play(result);

            StartCoroutine(Settle(actor, wallet, result));

            Debug.Log($"[Slots] {actor.name} spun {Title} for {bet}: seed {seed}, "
                      + $"{result.Frames.Count} picture(s), {result.Seconds:0.0}s, wins {result.Win}"
                      + (result.FreeSpins > 0 ? $" with {result.FreeSpins} free spins." : "."));

            return bet;
        }

        IEnumerator Settle(NetworkObject actor, Wallet wallet, SlotResult result)
        {
            yield return new WaitForSeconds(result.Seconds + 0.2f);

            _lastWin.Value = result.Win;

            if (actor != null && wallet != null)
            {
                if (result.Win > 0)
                {
                    wallet.ServerPayChips(result.Win);

                    if (_kind == SlotKind.Sevens)
                    {
                        _gamble.Value = result.Win;
                        _gambler = actor;
                        _gamblerId.Value = actor.ObjectId;
                        _gambleUntil = Time.time + GambleHold;
                    }
                }
                else if (wallet.Chips == 0)
                {
                    Net.Achievements.ServerAward(actor, Net.Achievements.LostItAll);
                }
            }

            _busy.Value = false;
        }

        /// <summary>Cycles the stake. Refused mid-spin.</summary>
        [Server]
        public bool ServerNextBet()
        {
            if (_busy.Value) return false;
            _betIndex.Value = (_betIndex.Value + 1) % SlotMath.Bets.Length;
            return true;
        }

        /// <summary>Whether <paramref name="actor"/> may turn the card right now.</summary>
        public bool CanGamble(NetworkObject actor)
            => _kind == SlotKind.Sevens && !_busy.Value && _gamble.Value > 0 && actor != null
               && actor.ObjectId == _gamblerId.Value;

        /// <summary>
        /// Double or nothing on the card's colour. Returns true if it came up. The offer ends on a
        /// loss, after <see cref="MaxGambles"/> wins, or when the stack no longer holds the win.
        /// </summary>
        [Server]
        public bool ServerGamble(NetworkObject actor, bool red)
        {
            if (!CanGamble(actor) || actor != _gambler) return false;

            var wallet = actor.GetComponent<Wallet>();
            int stake = _gamble.Value;

            if (wallet == null || wallet.ServerStakeChips(stake) <= 0)
            {
                EndOffer();
                return false;
            }

            World.RunSummary.ServerStaked(stake);

            bool cameRed = _rng.Next(2) == 0;
            bool won = cameRed == red;
            _turned.Value = cameRed ? 1 : 2;

            if (won)
            {
                wallet.ServerPayChips(stake * 2);
                _gambles++;
                _gamble.Value = stake * 2;
                _gambleUntil = Time.time + GambleHold;
                if (_gambles >= MaxGambles) EndOffer();
            }
            else
            {
                EndOffer();
                if (wallet.Chips == 0) Net.Achievements.ServerAward(actor, Net.Achievements.LostItAll);
            }

            RpcCard(cameRed);
            ShowCard(cameRed);

            Debug.Log($"[Slots] {actor.name} put {stake} on {(red ? "red" : "black")}: "
                      + $"{(cameRed ? "red" : "black")}, {(won ? $"paid {stake * 2}" : "lost")}.");

            return won;
        }

        void EndOffer()
        {
            _gamble.Value = 0;
            _gamblerId.Value = -1;
            _gambler = null;
            _gambles = 0;
        }

        // ---------------------------------------------------------------- the screen

        [ObserversRpc(ExcludeServer = true)]
        void RpcPlay(int seed, int bet) => Play(SlotMath.Spin(_kind, seed, bet));

        [ObserversRpc(ExcludeServer = true)]
        void RpcCard(bool red) => ShowCard(red);

        void ShowCard(bool red)
        {
            if (_card != null) _card.sharedMesh = red ? _cardRed : _cardBlack;
            Audio.Sfx.Play(Audio.Sound.Click, transform.position);
        }

        void Play(SlotResult result)
        {
            Played = result;
            _playing = result;
            _frame = 0;
            _frameStarted = Time.time;
            _reelsStopped = 0;
            if (_card != null && _cardBack != null) _card.sharedMesh = _cardBack;
            Audio.Sfx.Play(Audio.Sound.Spin, transform.position);
        }

        void Update()
        {
            // The winner left (disconnected, or died into a ghost): nobody can take the card now,
            // and their object id may be handed to somebody else's body.
            if (IsServerStarted && _gamble.Value > 0 && _gambler == null) EndOffer();

            if (_playing == null) return;

            SlotFrame frame = _playing.Frames[_frame];

            // Frame times are added, not reset to now, so a long feature never drifts behind the
            // server's clock, which pays at the sum of them.
            while (Time.time - _frameStarted >= frame.Seconds && _frame + 1 < _playing.Frames.Count)
            {
                _frameStarted += frame.Seconds;
                _frame++;
                _reelsStopped = 0;

                SlotFrame next = _playing.Frames[_frame];
                if (next.Drop) Audio.Sfx.Play(Audio.Sound.Spin, transform.position, 0.6f);
                if (next.RunningPct > frame.RunningPct) Audio.Sfx.Play(Audio.Sound.Coin, transform.position);
                frame = next;
            }

            float t = Time.time - _frameStarted;

            // One thunk per reel as it lands; reels that land in the same frame share one.
            if (frame.Drop)
            {
                int stopped = 0;
                for (int col = 0; col < Cols; col++) if (t >= StopAt(frame, col, Cols)) stopped++;

                if (stopped > _reelsStopped)
                {
                    _reelsStopped = stopped;
                    Audio.Sfx.Play(Audio.Sound.ReelStop, transform.position, 0.8f);
                }
            }

            if (t < frame.Seconds)
            {
                Draw(frame, _frame > 0 ? _playing.Frames[_frame - 1] : null, t);
                return;
            }

            _settled = frame;
            _playing = null;
            DrawStill(frame);

            BigWin.Celebrate(transform.TransformPoint(0f, 1.5f, 0.3f), transform.position.y, Played.Win, Played.Bet);
        }

        /// <summary>How long a drop's reels spin before the last one stops.</summary>
        static float SpinTime(SlotFrame frame) => Mathf.Min(1.1f, frame.Seconds * 0.6f);

        /// <summary>When reel <paramref name="col"/> of a drop lands, left to right.</summary>
        static float StopAt(SlotFrame frame, int col, int cols)
            => (frame.Drop ? SpinTime(frame) : 0f) * (0.45f + 0.55f * col / Mathf.Max(1, cols - 1));

        void Draw(SlotFrame frame, SlotFrame previous, float t)
        {
            if (_cells == null) return;

            int rows = Rows;
            int cols = Cols;
            bool flicker = Time.time >= _nextFlicker;
            if (flicker) _nextFlicker = Time.time + 0.06f;

            // A picture that is followed by a tumble bursts its winners in its last quarter.
            bool tumbleNext = _frame + 1 < _playing.Frames.Count && !_playing.Frames[_frame + 1].Drop;

            for (int i = 0; i < _cells.Length && i < frame.Grid.Length; i++)
            {
                int col = i / rows;
                int row = i % rows;

                float stopAt = StopAt(frame, col, cols);

                if (t < stopAt)
                {
                    if (flicker) Show(i, Random.Range(0, _symbolMeshes.Length));
                    _cells[i].localPosition = _home[i] + Vector3.down * (Mathf.Repeat(t * 9f, 1f) - 0.5f) * _cellSize;
                    _cells[i].localScale = Vector3.one * Scale(_shown[i]);
                    continue;
                }

                Show(i, frame.Grid[i]);

                // A tumble: everything that had a winner under it falls into place.
                float fall = 0f;
                if (!frame.Drop && previous != null)
                {
                    int below = 0;
                    for (int r = row; r < rows; r++) if (previous.Winning[col * rows + r]) below++;
                    fall = below * _cellSize * Mathf.Pow(1f - Mathf.Clamp01(t / 0.25f), 2f);
                }

                _cells[i].localPosition = _home[i] + Vector3.up * fall;

                float size = Scale(frame.Grid[i]);
                if (frame.Winning[i] && t > stopAt + 0.1f)
                {
                    float burst = tumbleNext ? Mathf.Clamp01((frame.Seconds - t) / (frame.Seconds * 0.25f)) : 1f;
                    size *= (1f + 0.25f * Mathf.Abs(Mathf.Sin(t * 9f))) * burst;
                }

                _cells[i].localScale = Vector3.one * size;
            }

            DrawSpots(frame);
        }

        void DrawStill(SlotFrame frame)
        {
            if (_cells == null || frame == null) return;

            for (int i = 0; i < _cells.Length && i < frame.Grid.Length; i++)
            {
                Show(i, frame.Grid[i]);
                _cells[i].localPosition = _home[i];
                _cells[i].localScale = Vector3.one * Scale(frame.Grid[i]);
            }

            DrawSpots(frame);
        }

        void DrawSpots(SlotFrame frame)
        {
            if (_spots == null || _spots.Length == 0) return;

            for (int i = 0; i < _spots.Length; i++)
            {
                if (_spots[i] == null) continue;

                int spot = frame.Spots != null && i < frame.Spots.Length ? frame.Spots[i] : 0;
                _spots[i].sharedMesh = spot >= 2 ? _spotHot : spot == 1 ? _spotMarked : null;

                // A multiplier spot grows with its multiplier, x2 small and x128 filling the cell.
                float grow = spot >= 2 ? 0.6f + 0.4f * Mathf.Log(spot, 2f) / 7f : 0.6f;
                _spots[i].transform.localScale = new Vector3(_cellSize * grow, _cellSize * grow, _cellSize * 0.1f);
            }
        }

        void Show(int cell, int symbol)
        {
            _shown[cell] = symbol;

            if (symbol < 0 || symbol >= _symbolMeshes.Length) return;
            if (_cellMeshes[cell] != null) _cellMeshes[cell].sharedMesh = _symbolMeshes[symbol];
        }

        float Scale(int symbol)
            => _cellSize * (_symbolScales != null && symbol >= 0 && symbol < _symbolScales.Length
                ? _symbolScales[symbol]
                : 0.8f);

        /// <summary>Editor-time setup. See <c>SlotFactory</c>.</summary>
        public void Configure(SlotKind kind, Transform[] cells, Mesh[] meshes, float[] scales, float cellSize,
                              MeshFilter[] spots, Mesh spotMarked, Mesh spotHot,
                              MeshFilter card, Mesh cardBack, Mesh cardRed, Mesh cardBlack, Material coin)
        {
            _kind = kind;
            _cells = cells;
            _cellMeshes = System.Array.ConvertAll(cells, c => c.GetComponent<MeshFilter>());
            _symbolMeshes = meshes;
            _symbolScales = scales;
            _cellSize = cellSize;
            _spots = spots;
            _spotMarked = spotMarked;
            _spotHot = spotHot;
            _card = card;
            _cardBack = cardBack;
            _cardRed = cardRed;
            _cardBlack = cardBlack;
            _coin = coin;
        }
    }
}
