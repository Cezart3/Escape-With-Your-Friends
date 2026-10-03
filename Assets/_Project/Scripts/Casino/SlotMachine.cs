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
    ///
    /// Volcano and Reef sell their free spins outright: <see cref="SlotMath.BuyPrice"/> bets, and the
    /// first drop is forced to trigger the feature. Same seed, same replay, one more bool on the wire.
    /// Volcano also has the ante: a quarter more per spin, twice the feature, no buying while it is on.
    ///
    /// **Lagoon Catch can be left to farm** (<see cref="ServerAutoplay"/>). One player owns the
    /// autoplay; each spin is an ordinary <see cref="ServerSpin"/> staked from their wallet, the next
    /// one as soon as the last has paid. It stops when the spins run out, when a stake bounces, when
    /// the owner's body is gone, or on the feature, which then plays out like any other spin. Nobody
    /// else can spin the cabinet meanwhile; they see whose it is and how many spins are left.
    ///
    /// **The jackpot is one pot for the whole floor.** Every stake at any cabinet feeds it
    /// <see cref="SlotMath.JackpotPct"/>%; every spin may drop it, decided by the seed on the server
    /// like everything else, and the amount goes to clients with the seed. It lives on the server in
    /// statics and every cabinet replicates the same number, so friends at different cabinets
    /// watch one ticker climb. A dropped pot keeps showing until the winner's screen settles, so
    /// the ticker never gives a spin away.
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

        [Header("Lights, set by SlotFactory")]
        [Tooltip("The cabinet's bulbs in three interleaved groups, chased round in turn.")]
        [SerializeField] MeshRenderer[] _bulbs;
        [SerializeField] Material _bulbOn;
        [SerializeField] Material _bulbOff;

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

        /// <summary>The shared jackpot as this cabinet shows it, in chips. Server-written, the same on every cabinet.</summary>
        readonly SyncVar<int> _jackpot = new();

        /// <summary>The last spin's jackpot, 0 if it did not drop.</summary>
        readonly SyncVar<int> _lastJackpot = new();

        /// <summary>The last spin bought its feature.</summary>
        readonly SyncVar<bool> _lastBuy = new();

        /// <summary>The ante is on: every spin stakes a quarter more for twice the feature. Volcano only.</summary>
        readonly SyncVar<bool> _ante = new();

        /// <summary>The last spin was played with the ante.</summary>
        readonly SyncVar<bool> _lastAnte = new();

        /// <summary>Autoplay spins still to go, 0 when none is running.</summary>
        readonly SyncVar<int> _autoLeft = new();

        /// <summary>Object id of whoever the autoplay is spending, or -1.</summary>
        readonly SyncVar<int> _autoOwnerId = new(-1);

        /// <summary>Their name, for everybody else's prompt.</summary>
        readonly SyncVar<string> _autoOwnerName = new();

        /// <summary>The autoplay lengths. The first press starts the first; each press by the owner raises it to the next, and past the last stops it.</summary>
        public static readonly int[] AutoSpins = { 10, 25, 50, 100 };

        /// <summary>Harness only: seeds the server spins with, in order, before it goes back to rolling its own.</summary>
        internal static readonly System.Collections.Generic.Queue<int> Seeds = new();

        /// <summary>Server: the pot, in hundredths of a chip so a 10-chip stake still adds to it.</summary>
        static long _pot = SlotMath.JackpotSeed * 100L;

        /// <summary>Server: dropped jackpots still on their winners' screens, shown on top of the pot.</summary>
        static int _owed;

        /// <summary>Server: every cabinet this server runs, to publish the pot to.</summary>
        static readonly System.Collections.Generic.List<SlotMachine> Served = new();

        /// <summary>Harness only: the next spin drops the jackpot whatever its seed says.</summary>
        internal static bool ForceJackpot;

        /// <summary>Server: the pot in hundredths of a chip. For the harness.</summary>
        internal static long ServerPot => _pot;

        /// <summary>Every spawned cabinet on this peer, for the HUD, which would otherwise search the scene each frame.</summary>
        internal static readonly System.Collections.Generic.List<SlotMachine> All = new();

        /// <summary>How long the winner has the cabinet to themselves to decide on the card.</summary>
        const float GambleHold = 15f;

        System.Random _rng;

        /// <summary>Server: this cabinet's dropped jackpot still counted in <see cref="_owed"/>.</summary>
        int _owing;
        NetworkObject _gambler;
        int _gambles;
        float _gambleUntil;
        NetworkObject _autoOwner;

        /// <summary>Server: true while the autoplay itself is pressing spin, the only spin it lets through.</summary>
        bool _autoTurn;

        // The screen, local to every peer. Nothing below is replicated.
        SlotResult _playing;
        SlotFrame _settled;
        int _frame;
        float _frameStarted;
        int[] _shown;
        int _reelsStopped;
        Vector3[] _home;

        /// <summary>When each reel of the frame on screen lands, and the first one held back for the scatter, or -1.</summary>
        float[] _stops;
        int _tease = -1;
        bool _teaseHeard;

        int _lit = -1;
        float _flashUntil;

        public SlotKind Kind => _kind;
        public string Title => SlotMath.Title(_kind);
        public int Bet => CasinoDays.Scaled(SlotMath.Bets[Mathf.Clamp(_betIndex.Value, 0, SlotMath.Bets.Length - 1)]);
        public int NextBet => CasinoDays.Scaled(SlotMath.Bets[(_betIndex.Value + 1) % SlotMath.Bets.Length]);
        public bool Open => CasinoDays.IsOpen(CasinoDays.Of(_kind));
        public bool Busy => _busy.Value;
        public int LastSeed => _lastSeed.Value;
        public int LastBet => _lastBet.Value;
        public int LastWin => _lastWin.Value;
        public int Jackpot => _jackpot.Value;
        public int LastJackpot => _lastJackpot.Value;

        /// <summary>What the feature costs at the current bet, 0 on a cabinet without one or with the ante on.</summary>
        public int BuyCost => Ante ? 0 : Bet * SlotMath.BuyPrice(_kind);

        public bool Ante => _ante.Value;

        /// <summary>What one spin takes from the wallet: the bet, or a quarter more with the ante.</summary>
        public int Stake => Ante ? SlotMath.AnteStake(Bet) : Bet;
        public int AutoLeft => _autoLeft.Value;
        public int AutoOwnerId => _autoOwnerId.Value;
        public string AutoOwnerName => _autoOwnerName.Value;

        /// <summary>What the owner's next press of the autoplay button raises it to, or 0 when it would stop it.</summary>
        public int NextAuto
        {
            get
            {
                foreach (int spins in AutoSpins) if (spins > _autoLeft.Value) return spins;
                return 0;
            }
        }

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

        /// <summary>The jackpot that came with <see cref="Played"/>, 0 if none. Local.</summary>
        public int PlayedJackpot { get; private set; }

        /// <summary>The symbol drawn in each cell right now, what a camera would see.</summary>
        public int[] ShowingGrid() => _shown;

        /// <summary>Harness: the bulb groups, the reels' symbol meshes, and how far the cells are off their resting places.</summary>
        internal MeshRenderer[] Bulbs => _bulbs;
        internal Mesh[] Symbols => _symbolMeshes;

        internal float Displacement()
        {
            float off = 0f;
            for (int i = 0; i < _home.Length; i++)
                if (_cells[i] != null) off += (_cells[i].localPosition - _home[i]).magnitude;
            return off;
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            _rng = new System.Random();

            // Statics outlive a session; a fresh server starts a fresh pot.
            if (Served.Count == 0)
            {
                _pot = SlotMath.JackpotSeed * 100L;
                _owed = 0;
            }

            Served.Add(this);
            Publish();
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            Served.Remove(this);

            // Despawned mid-spin: Settle will never take its drop off the ticker.
            _owed -= _owing;
            _owing = 0;
            Publish();
        }

        static void Publish()
        {
            int shown = (int)(_pot / 100) + _owed;
            foreach (SlotMachine machine in Served) machine._jackpot.Value = shown;
        }

        /// <summary>A player who walks in late sees the last spin's final picture, not an empty cabinet.</summary>
        public override void OnStartClient()
        {
            base.OnStartClient();
            All.Add(this);

            // Mid-spin, the last seed is the spin still running: drawing its end would give it away.
            if (_lastBet.Value <= 0 || _busy.Value) return;

            Played = SlotMath.Spin(_kind, _lastSeed.Value, _lastBet.Value, _lastBuy.Value, _lastAnte.Value);
            PlayedJackpot = _lastJackpot.Value;
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

        /// <summary>Stakes the bet, or the feature's price when <paramref name="buy"/>, and spins. Returns the chips staked, 0 if refused.</summary>
        [Server]
        public int ServerSpin(NetworkObject actor, bool buy = false)
        {
            if (_busy.Value || actor == null) return 0;
            if (buy && BuyCost <= 0) return 0;
            if (_autoLeft.Value > 0 && !_autoTurn) return 0;

            // The winner gets a moment with the card before somebody else's spin takes it away.
            if (_gamble.Value > 0 && actor != _gambler && _gambler != null && Time.time < _gambleUntil) return 0;

            var wallet = actor.GetComponent<Wallet>();
            if (wallet == null) return 0;

            int bet = Bet;
            bool ante = !buy && Ante;
            int stake = buy ? BuyCost : Stake;
            if (wallet.ServerStakeChips(stake) <= 0) return 0;

            World.RunSummary.ServerStaked(stake);

            // Spinning again is collecting: whatever the card was offering is simply kept.
            EndOffer();
            _turned.Value = 0;

            int seed = Seeds.Count > 0 ? Seeds.Dequeue() : _rng.Next();
            SlotResult result = SlotMath.Spin(_kind, seed, bet, buy, ante);
            LastResult = result;

            _pot += (long)stake * SlotMath.JackpotPct;

            int jackpot = 0;
            // By the stake, so a bonus buy has its price's worth of chances.
            if (ForceJackpot || SlotMath.JackpotHit(seed, stake))
            {
                ForceJackpot = false;
                jackpot = (int)(_pot / 100);
                _pot = SlotMath.JackpotSeed * 100L;
                _owed += jackpot;
                _owing = jackpot;
            }

            Publish();

            _busy.Value = true;
            _lastSeed.Value = seed;
            _lastBet.Value = bet;
            _lastBuy.Value = buy;
            _lastAnte.Value = ante;
            _lastJackpot.Value = jackpot;

            RpcPlay(seed, bet, buy, ante, jackpot);
            Play(result, jackpot);

            StartCoroutine(Settle(actor, wallet, result, jackpot));

            Debug.Log($"[Slots] {actor.name} {(buy ? "bought the feature on" : "spun")} {Title} for {stake}: seed {seed}, "
                      + $"{result.Frames.Count} picture(s), {result.Seconds:0.0}s, wins {result.Win}"
                      + (result.FreeSpins > 0 ? $" with {result.FreeSpins} free spins" : "")
                      + (jackpot > 0 ? $" and the JACKPOT of {jackpot}." : "."));

            return stake;
        }

        IEnumerator Settle(NetworkObject actor, Wallet wallet, SlotResult result, int jackpot)
        {
            yield return new WaitForSeconds(result.Seconds + 0.2f);

            int win = result.Win + jackpot;
            _lastWin.Value = win;

            // Paid or forfeited, the dropped pot is off the ticker now.
            if (jackpot > 0)
            {
                _owed -= _owing;
                _owing = 0;
                Publish();
            }

            if (actor != null && wallet != null)
            {
                if (win > 0)
                {
                    wallet.ServerPayChips(win);

                    if (_kind == SlotKind.Sevens)
                    {
                        _gamble.Value = win;
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

        /// <summary>
        /// The autoplay button. Starts <see cref="AutoSpins"/>[0] spins for <paramref name="actor"/>;
        /// pressed again by them, raises it to <see cref="NextAuto"/>, or stops it past the last.
        /// Refused to anybody else while it runs, and on a cabinet without autoplay.
        /// </summary>
        [Server]
        public bool ServerAutoplay(NetworkObject actor)
        {
            if (!SlotMath.HasAutoplay(_kind) || actor == null) return false;

            if (_autoLeft.Value > 0)
            {
                if (actor != _autoOwner) return false;

                int next = NextAuto;
                if (next > 0) _autoLeft.Value = next;
                else EndAuto();
                return true;
            }

            if (_busy.Value) return false;

            var identity = actor.GetComponent<Player.PlayerIdentity>();
            _autoOwner = actor;
            _autoOwnerId.Value = actor.ObjectId;
            _autoOwnerName.Value = identity != null ? identity.DisplayName : actor.name;
            _autoLeft.Value = AutoSpins[0];
            StartCoroutine(Autoplay());

            Debug.Log($"[Slots] {actor.name} started autoplay on {Title}.");
            return true;
        }

        IEnumerator Autoplay()
        {
            string why = "its spins ran out";

            while (_autoLeft.Value > 0)
            {
                while (_busy.Value) yield return null;
                if (_autoLeft.Value <= 0) { why = "its owner stopped it"; break; }
                if (_autoOwner == null || !_autoOwner.IsSpawned) { why = "its owner left"; break; }

                _autoTurn = true;
                int staked = ServerSpin(_autoOwner);
                _autoTurn = false;
                if (staked <= 0) { why = "the stake bounced"; break; }

                _autoLeft.Value--;
                if (LastResult.FreeSpins > 0) { why = "it hooked the feature"; break; }
            }

            Debug.Log($"[Slots] Autoplay on {Title} stopped: {why}.");
            EndAuto();
        }

        void EndAuto()
        {
            _autoLeft.Value = 0;
            _autoOwnerId.Value = -1;
            _autoOwner = null;
        }

        /// <summary>Cycles the stake. Refused mid-spin.</summary>
        [Server]
        public bool ServerNextBet()
        {
            if (_busy.Value) return false;
            _betIndex.Value = (_betIndex.Value + 1) % SlotMath.Bets.Length;
            return true;
        }

        /// <summary>Flips the ante. Refused mid-spin and on a cabinet without one.</summary>
        [Server]
        public bool ServerToggleAnte()
        {
            if (_busy.Value || !SlotMath.HasAnte(_kind)) return false;
            _ante.Value = !_ante.Value;
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
        void RpcPlay(int seed, int bet, bool buy, bool ante, int jackpot) => Play(SlotMath.Spin(_kind, seed, bet, buy, ante), jackpot);

        [ObserversRpc(ExcludeServer = true)]
        void RpcCard(bool red) => ShowCard(red);

        void ShowCard(bool red)
        {
            if (_card != null) _card.sharedMesh = red ? _cardRed : _cardBlack;
            Audio.Sfx.Play(Audio.Sound.Click, transform.position);
        }

        void Play(SlotResult result, int jackpot)
        {
            Played = result;
            PlayedJackpot = jackpot;
            _playing = result;
            _frame = 0;
            _frameStarted = Time.time;
            Plan(result.Frames[0]);
            if (_card != null && _cardBack != null) _card.sharedMesh = _cardBack;
            Audio.Sfx.Play(Audio.Sound.Spin, transform.position);
        }

        void Update()
        {
            // The winner left (disconnected, or died into a ghost): nobody can take the card now,
            // and their object id may be handed to somebody else's body.
            if (IsServerStarted && _gamble.Value > 0 && _gambler == null) EndOffer();

            Lights();

            if (_playing == null) return;

            SlotFrame frame = _playing.Frames[_frame];

            // Frame times are added, not reset to now, so a long feature never drifts behind the
            // server's clock, which pays at the sum of them.
            while (Time.time - _frameStarted >= frame.Seconds && _frame + 1 < _playing.Frames.Count)
            {
                _frameStarted += frame.Seconds;
                _frame++;

                SlotFrame next = _playing.Frames[_frame];
                if (next.Drop) Audio.Sfx.Play(Audio.Sound.Spin, transform.position, 0.6f);
                if (next.RunningPct > frame.RunningPct) Audio.Sfx.Play(Audio.Sound.Coin, transform.position);
                frame = next;
                Plan(frame);
            }

            float t = Time.time - _frameStarted;

            // One thunk per reel as it lands; reels that land in the same frame share one. A held
            // reel lands lower and louder, and the hold itself starts on a rising whirr.
            if (frame.Drop)
            {
                int stopped = 0;
                for (int col = 0; col < Cols; col++) if (t >= _stops[col]) stopped++;

                if (stopped > _reelsStopped)
                {
                    bool held = _tease >= 0 && stopped > _tease;
                    _reelsStopped = stopped;
                    Audio.Sfx.Play(Audio.Sound.ReelStop, transform.position, held ? 1f : 0.8f, held ? 0.8f : 1f);
                }

                if (_tease >= 0 && !_teaseHeard && stopped >= _tease)
                {
                    _teaseHeard = true;
                    Audio.Sfx.Play(Audio.Sound.Spin, transform.position, 0.7f, 1.35f);
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

            if (Played.Win > 0) _flashUntil = Time.time + (Played.Win >= Played.Bet * 10 ? 5f : 2.5f);
            BigWin.Celebrate(transform.TransformPoint(0f, 1.5f, 0.3f), transform.position.y, Played.Win, Played.Bet, PlayedJackpot);
        }

        // ---------------------------------------------------------------- the reels

        /// <summary>Cells a second a reel runs at, and a held reel's crawl.</summary>
        const float Speed = 15f, Crawl = 5f;

        /// <summary>The kick back up before a reel goes, and the drop-and-settle when it lands.</summary>
        const float WindUp = 0.14f, Bounce = 0.32f;

        /// <summary>Half the window spans this much of the drum.</summary>
        const float HalfArc = Mathf.PI / 3f;

        /// <summary>
        /// When each reel lands, left to right over the first sixty-odd per cent of the frame. A
        /// teased drop's held reels share the extra time SlotMath gave it, each a slow beat apart.
        /// </summary>
        void Plan(SlotFrame frame)
        {
            int cols = Cols;
            if (_stops == null || _stops.Length != cols) _stops = new float[cols];

            _reelsStopped = 0;
            _teaseHeard = false;
            _tease = SlotMath.Tease(_kind, frame);

            float plain = frame.Seconds - (_tease >= 0 ? SlotMath.TeaseSeconds : 0f);
            float spin = frame.Drop ? Mathf.Min(1.5f, plain * 0.62f) : 0f;
            for (int c = 0; c < cols; c++) _stops[c] = spin * (0.4f + 0.6f * c / Mathf.Max(1, cols - 1));

            if (_tease < 0) return;
            int held = cols - _tease;
            for (int c = _tease; c < cols; c++) _stops[c] += SlotMath.TeaseSeconds * (c - _tease + 1) / held;
        }

        /// <summary>How many cells reel <paramref name="col"/> has run by <paramref name="t"/>: a kick back, full speed, a crawl once it is held.</summary>
        float Travel(int col, float t)
        {
            if (t < WindUp) return -0.3f * Mathf.Sin(Mathf.PI * 0.5f * t / WindUp);

            float run = t - WindUp;
            if (_tease < 0 || col < _tease) return -0.3f + Speed * run;

            float fast = Mathf.Max(0f, _stops[_tease - 1] - WindUp);
            return -0.3f + Speed * Mathf.Min(run, fast) + Crawl * Mathf.Max(0f, run - fast);
        }

        /// <summary>The reel strip: a fixed shuffle per reel, the same on every peer and every spin.</summary>
        int Strip(int col, int slot)
        {
            uint h = (uint)(col * 7919 + slot * 104729 + (int)_kind * 31337);
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return (int)(h % (uint)_symbolMeshes.Length);
        }

        static float BackOut(float u)
        {
            const float k = 2.2f;
            u -= 1f;
            return 1f + u * u * ((k + 1f) * u + k);
        }

        /// <summary>
        /// Puts cell <paramref name="i"/> at <paramref name="row"/> (fractional, 0 the top) on a drum,
        /// <paramref name="curve"/> of the way from flat: rows away from the middle roll back and
        /// foreshorten, and past the window's edge they sink behind the glass.
        /// </summary>
        void Place(int i, float row, float curve, float size, float stretch)
        {
            float up = (Rows - 1) * 0.5f - row;
            float arc = HalfArc / (Rows * 0.5f);
            float a = Mathf.Clamp(up * arc, -Mathf.PI * 0.5f, Mathf.PI * 0.5f);
            float radius = _cellSize / arc;

            // _home is the cell's own row; the reel's middle is where the flat grid has y = 0.
            Vector3 home = _home[i];
            float middle = home.y - ((Rows - 1) * 0.5f - i % Rows) * _cellSize;
            _cells[i].localPosition = new Vector3(
                home.x,
                middle + Mathf.Lerp(up * _cellSize, radius * Mathf.Sin(a), curve),
                home.z + curve * radius * (Mathf.Cos(a) - 1f));
            _cells[i].localRotation = Quaternion.Euler(-Mathf.Rad2Deg * a * curve, 0f, 0f);
            _cells[i].localScale = new Vector3(size, size * stretch, size);
        }

        void Draw(SlotFrame frame, SlotFrame previous, float t)
        {
            if (_cells == null) return;

            int rows = Rows;
            (int scatter, _) = SlotMath.Scatter(_kind);
            float last = _stops[_stops.Length - 1];
            bool anyWin = false;
            for (int i = 0; i < frame.Winning.Length; i++) anyWin |= frame.Winning[i];

            // A picture that is followed by a tumble bursts its winners in its last quarter.
            bool tumbleNext = _frame + 1 < _playing.Frames.Count && !_playing.Frames[_frame + 1].Drop;

            for (int i = 0; i < _cells.Length && i < frame.Grid.Length; i++)
            {
                int col = i / rows;
                int row = i % rows;
                float stop = _stops[col];

                if (frame.Drop && t < stop)
                {
                    // Spinning: each cell rides the strip down and wraps to the top with a new symbol.
                    float s = row + Travel(col, t) + 0.5f;
                    int lap = Mathf.FloorToInt(s / rows);
                    Show(i, Strip(col, lap * rows - row));

                    bool held = _tease >= 0 && col >= _tease && t > _stops[_tease - 1];
                    Place(i, s - lap * rows - 0.5f, 1f, Scale(_shown[i]), held ? 1.1f : 1.3f);
                    if (held) _cells[i].localPosition += Vector3.right * (Mathf.Sin(t * 70f + col) * 0.03f * _cellSize);
                    continue;
                }

                Show(i, frame.Grid[i]);
                float size = Scale(frame.Grid[i]);

                if (frame.Drop && t < stop + Bounce)
                {
                    // Landing: dropped in from a little above, past home, and back.
                    float u = (t - stop) / Bounce;
                    Place(i, row - 0.6f * (1f - BackOut(u)), 1f - u, size, 1f);
                    continue;
                }

                // A tumble: everything that had a winner under it falls into place.
                float fall = 0f;
                if (!frame.Drop && previous != null)
                {
                    int below = 0;
                    for (int r = row; r < rows; r++) if (previous.Winning[col * rows + r]) below++;
                    fall = below * _cellSize * Mathf.Pow(1f - Mathf.Clamp01(t / 0.25f), 2f);
                }

                Vector3 at = _home[i] + Vector3.up * fall;
                Quaternion turn = Quaternion.identity;
                float settled = t - last - Bounce;

                if (frame.Winning[i] && settled > 0f)
                {
                    // Winners dance: a beat of growth, a hop and a wag, the wag out of step reel to reel.
                    float beat = Mathf.Abs(Mathf.Sin(settled * 8f));
                    float burst = tumbleNext ? Mathf.Clamp01((frame.Seconds - t) / (frame.Seconds * 0.25f)) : 1f;
                    size *= (1f + 0.22f * beat) * burst;
                    at += Vector3.up * (0.08f * _cellSize * beat) + Vector3.forward * (0.15f * _cellSize);
                    turn = Quaternion.Euler(0f, 0f, 10f * Mathf.Sin(settled * 11f + col));
                }
                else if (anyWin && settled > 0f)
                {
                    size *= 0.8f;
                }
                else if (_tease >= 0 && t < last && frame.Grid[i] == scatter)
                {
                    // While the held reels crawl, the scatters already down throb.
                    size *= 1f + 0.2f * Mathf.Abs(Mathf.Sin(t * 10f));
                    at += Vector3.forward * (0.15f * _cellSize);
                }

                _cells[i].localPosition = at;
                _cells[i].localRotation = turn;
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
                _cells[i].localRotation = Quaternion.identity;
                _cells[i].localScale = Vector3.one * Scale(frame.Grid[i]);
            }

            DrawSpots(frame);
        }

        /// <summary>
        /// The bulbs: a slow chase while the cabinet waits, a fast one while it spins, faster still
        /// on a held reel, and all of them flashing together after a win. Materials are swapped only
        /// when the pattern moves on.
        /// </summary>
        void Lights()
        {
            if (_bulbs == null || _bulbs.Length == 0 || _bulbOn == null) return;

            float now = Time.time + (int)_kind * 0.37f;
            int lit;
            if (_playing != null) lit = (int)(now * (_tease >= 0 ? 24f : 12f)) % 3;
            else if (Time.time < _flashUntil) lit = (int)(now * 6f) % 2 == 0 ? 3 : 4;
            else lit = (int)(now * 2.5f) % 3;

            if (lit == _lit) return;
            _lit = lit;

            for (int g = 0; g < _bulbs.Length; g++)
                if (_bulbs[g] != null) _bulbs[g].sharedMaterial = lit == 3 || lit == g % 3 ? _bulbOn : _bulbOff;
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
                              MeshFilter card, Mesh cardBack, Mesh cardRed, Mesh cardBlack, Material coin,
                              MeshRenderer[] bulbs, Material bulbOn, Material bulbOff)
        {
            _bulbs = bulbs;
            _bulbOn = bulbOn;
            _bulbOff = bulbOff;
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
