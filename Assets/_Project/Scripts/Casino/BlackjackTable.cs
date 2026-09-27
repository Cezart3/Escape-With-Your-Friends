using System.Collections;
using System.Collections.Generic;
using EscapeWithYourFriends.Economy;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Casino
{
    public enum BlackjackPhase
    {
        /// <summary>Seats take bets. The first bet starts the window; when it closes, the cards go out.</summary>
        Betting,

        /// <summary>Hands act in seat order, one at a time.</summary>
        Playing,

        /// <summary>The hole card turns and the dealer draws.</summary>
        Dealer,

        /// <summary>Paid. The cards stay on the felt a moment so everybody can see why.</summary>
        Done,
    }

    /// <summary>
    /// The blackjack table: four seats against the house, paid in the chips the roulette wheel and
    /// the slot cabinets take. The rules are <see cref="BlackjackRound"/>'s; this is the part with a
    /// clock, the wallets and the wire.
    ///
    /// **Authority is the wheel's, and so is the stake.** A press on a seat's bet button takes the
    /// chips there and then; the first one opens a betting window, and when it closes the server
    /// shuffles a shoe from a seed nobody else sees and deals. A double or a split takes its stake
    /// the moment it is pressed. Winnings are paid when the dealer is done.
    ///
    /// **What crosses the wire is the cards as they land**, not the seed: a seed would tell every
    /// client the hole card and the next card in the shoe. The round keeps a log of what has been
    /// shown, the hole card going in only when it turns over, and that log is a
    /// <see cref="SyncList{T}"/> every peer rebuilds the hands from with
    /// <see cref="BlackjackMath.Rebuild"/>. Nothing a client sends can reach the shoe; its only
    /// words are "bet" and "hit / stand / double / split on my own hand, on my turn".
    ///
    /// A player who walks off, dies or disconnects on their turn is stood after
    /// <see cref="_turnSeconds"/>, and a hand whose owner is gone is paid to nobody - the slot
    /// cabinet's rule.
    /// </summary>
    public class BlackjackTable : NetworkBehaviour
    {
        /// <summary>Chips per press of a bet button.</summary>
        public const int Chunk = 50;

        /// <summary>Most a seat may put on one hand before doubles and splits.</summary>
        public const int MaxBet = 500;

        /// <summary>Cards drawn per hand on the felt. A ninth card still counts; it just is not drawn.</summary>
        public const int CardsShown = 8;

        [Min(0.1f)] [SerializeField] float _betWindow = 10f;
        [Min(1f)] [SerializeField] float _turnSeconds = 20f;
        [Min(0.05f)] [SerializeField] float _dealSeconds = 0.8f;
        [Min(0.1f)] [SerializeField] float _resultSeconds = 5f;

        [Header("The felt, set by BlackjackFactory")]
        [Tooltip("CardsShown per hand, hands in BlackjackMath order, the dealer's last.")]
        [SerializeField] Renderer[] _cards;

        [Tooltip("A face plate on each card, drawn with CardFaces.")]
        [SerializeField] Renderer[] _faces;
        [SerializeField] Material _face;
        [SerializeField] Material _back;

        readonly SyncVar<int> _phase = new();
        readonly SyncVar<int> _turn = new(-1);
        readonly SyncVar<bool> _holeDown = new();

        /// <summary>Bumped every time the felt is cleared, so a peer redraws even when the log length comes back the same.</summary>
        readonly SyncVar<int> _round = new();

        /// <summary>Object id seated at each seat, or -1.</summary>
        readonly SyncList<int> _owners = new();

        /// <summary>Chips on each hand, <see cref="BlackjackMath.Hands"/> of them.</summary>
        readonly SyncList<int> _bets = new();

        /// <summary>What each hand was handed back at the end, once the dealer is done.</summary>
        readonly SyncList<int> _paid = new();

        /// <summary>The round's log, as far as the table has shown it. See the class summary.</summary>
        readonly SyncList<int> _log = new();

        /// <summary>Every spawned table on this peer, for the HUD.</summary>
        internal static readonly List<BlackjackTable> All = new();

        readonly NetworkObject[] _players = new NetworkObject[BlackjackMath.Seats];

        System.Random _rng;
        BlackjackRound _dealt;
        bool _windowOpen;
        float _closesAt;
        float _turnUntil;
        int _timedTurn = -1;

        // The felt, local to every peer.
        int _rebuiltRound = -1;
        int _rebuiltCount = -1;
        int _feltRound = -1;
        int _feltCount = -1;
        bool _feltHole;
        MeshFilter[] _faceMeshes;
        BlackjackPhase _heardPhase;
        List<int>[] _hands = BlackjackMath.Rebuild(new List<int>());

        public BlackjackPhase Phase => (BlackjackPhase)_phase.Value;
        public int Turn => _turn.Value;
        public bool HoleDown => _holeDown.Value;
        public int Owner(int seat) => seat >= 0 && seat < _owners.Count ? _owners[seat] : -1;
        public int BetOn(int hand) => hand >= 0 && hand < _bets.Count ? _bets[hand] : 0;
        public int PaidOn(int hand) => hand >= 0 && hand < _paid.Count ? _paid[hand] : 0;
        public IReadOnlyList<int> Log => _log;

        /// <summary>The hands as this peer has been shown them. Rebuilt from the log whenever it changes.</summary>
        public List<int>[] Hands
        {
            get
            {
                Refresh();
                return _hands;
            }
        }

        /// <summary>Server only: the round being played, with the hole card in it. Null between rounds.</summary>
        public BlackjackRound Dealt => _dealt;

        /// <summary>Server only: the seed of the last shoe.</summary>
        public int LastSeed { get; private set; }

        /// <summary>Harness only: the next round deals from this shoe instead of a shuffled one, once.</summary>
        internal int[] RiggedShoe;

        public override void OnStartServer()
        {
            base.OnStartServer();
            _rng = new System.Random();

            _owners.Clear();
            for (int i = 0; i < BlackjackMath.Seats; i++) _owners.Add(-1);

            _bets.Clear();
            _paid.Clear();
            for (int i = 0; i < BlackjackMath.Hands; i++)
            {
                _bets.Add(0);
                _paid.Add(0);
            }
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            All.Add(this);

            // A player who walks in on a paid table did not see it paid.
            _heardPhase = Phase;
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            All.Remove(this);
        }

        // ---------------------------------------------------------------- what anybody may ask

        /// <summary>The seat <paramref name="objectId"/> sits at, or -1.</summary>
        public int SeatOf(int objectId)
        {
            for (int seat = 0; seat < BlackjackMath.Seats; seat++)
                if (objectId >= 0 && Owner(seat) == objectId) return seat;

            return -1;
        }

        /// <summary>Whether <paramref name="objectId"/>, holding <paramref name="chips"/>, may put another chunk down at <paramref name="seat"/>.</summary>
        public bool CanBet(int objectId, int seat, int chips)
        {
            if (Phase != BlackjackPhase.Betting || seat < 0 || seat >= BlackjackMath.Seats || objectId < 0) return false;

            int owner = Owner(seat);
            if (owner >= 0 && owner != objectId) return false;
            if (owner < 0 && SeatOf(objectId) >= 0) return false;

            return chips >= Chunk && BetOn(seat * 2) + Chunk <= MaxBet;
        }

        /// <summary>Whether <paramref name="objectId"/> may play <paramref name="action"/> at <paramref name="seat"/> right now.</summary>
        public bool CanAct(int objectId, int seat, BlackjackAction action, int chips)
        {
            int hand = Turn;
            if (Phase != BlackjackPhase.Playing || hand < 0 || hand / 2 != seat || Owner(seat) != objectId) return false;

            bool staking = action == BlackjackAction.Double || action == BlackjackAction.Split;
            if (staking && chips < BetOn(hand)) return false;

            return BlackjackRound.Allowed(action, hand, Hands[hand], BetOn(seat * 2 + 1) > 0);
        }

        // ---------------------------------------------------------------- the buttons

        /// <summary>Puts a chunk on <paramref name="seat"/>, sitting there if it is free. Returns the chips staked, 0 if refused.</summary>
        [Server]
        public int ServerBet(NetworkObject actor, int seat)
        {
            var wallet = actor != null ? actor.GetComponent<Wallet>() : null;
            if (wallet == null || !CanBet(actor.ObjectId, seat, wallet.Chips)) return 0;
            if (wallet.ServerStakeChips(Chunk) <= 0) return 0;

            World.RunSummary.ServerStaked(Chunk);

            _players[seat] = actor;
            _owners[seat] = actor.ObjectId;
            _bets[seat * 2] += Chunk;

            Debug.Log($"[Blackjack] {actor.name} bet {Chunk} at seat {seat + 1}, {_bets[seat * 2]} on it.");

            if (!_windowOpen)
            {
                _windowOpen = true;
                _closesAt = Time.time + _betWindow;
                StartCoroutine(Round());
            }

            return Chunk;
        }

        /// <summary>Plays <paramref name="action"/> on the seat's hand if it is that seat's turn and <paramref name="actor"/> sits there.</summary>
        [Server]
        public bool ServerAct(NetworkObject actor, int seat, BlackjackAction action)
        {
            if (_dealt == null || actor == null || seat < 0 || seat >= BlackjackMath.Seats || _players[seat] != actor)
                return false;

            var wallet = actor.GetComponent<Wallet>();
            if (wallet == null || !CanAct(actor.ObjectId, seat, action, wallet.Chips) || !_dealt.Can(action)) return false;

            int cost = _dealt.Cost(action);
            if (cost > 0)
            {
                if (wallet.ServerStakeChips(cost) <= 0) return false;
                World.RunSummary.ServerStaked(cost);
            }

            int hand = _dealt.Turn;
            _dealt.Act(action);
            Mirror();

            Debug.Log($"[Blackjack] {actor.name} {action} on hand {hand}: "
                      + BlackjackMath.Describe(_dealt.Cards[hand]) + $", bet {_dealt.Bet[hand]}.");

            return true;
        }

        /// <summary>Closes the betting window now. The harness uses it.</summary>
        [Server]
        public void ServerCallIt()
        {
            if (_windowOpen) _closesAt = Time.time;
        }

        /// <summary>Shortens everything. For the harness.</summary>
        [Server]
        public void ServerSetTiming(float betWindow, float turnSeconds, float dealSeconds, float resultSeconds)
        {
            _betWindow = Mathf.Max(0.1f, betWindow);
            _turnSeconds = Mathf.Max(0.2f, turnSeconds);
            _dealSeconds = Mathf.Max(0.02f, dealSeconds);
            _resultSeconds = Mathf.Max(0.1f, resultSeconds);
        }

        // ---------------------------------------------------------------- the round

        IEnumerator Round()
        {
            while (Time.time < _closesAt) yield return null;
            _windowOpen = false;

            var seatBets = new int[BlackjackMath.Seats];
            for (int seat = 0; seat < seatBets.Length; seat++) seatBets[seat] = _bets[seat * 2];

            LastSeed = _rng.Next();
            int[] shoe = RiggedShoe ?? BlackjackMath.Shoe(LastSeed);
            RiggedShoe = null;

            _dealt = new BlackjackRound(shoe, seatBets);
            _holeDown.Value = !_dealt.HoleShown;
            _phase.Value = (int)BlackjackPhase.Playing;
            Mirror();

            Audio.Sfx.Play(Audio.Sound.Click, transform.position);
            Debug.Log($"[Blackjack] Dealt from seed {LastSeed}: dealer shows "
                      + BlackjackMath.Name(_dealt.Cards[BlackjackMath.Dealer][0]) + ".");

            // Update stands whoever runs out of time; this only waits for the last of them.
            while (_dealt.Turn >= 0) yield return null;

            _phase.Value = (int)BlackjackPhase.Dealer;
            _dealt.PlayDealer();

            // The dealer's cards one at a time, the hole card first, so a table of friends gets to groan.
            while (_log.Count < _dealt.Log.Count)
            {
                yield return new WaitForSeconds(_dealSeconds);
                _log.Add(_dealt.Log[_log.Count]);
                _holeDown.Value = false;
            }

            Settle();
            _phase.Value = (int)BlackjackPhase.Done;

            yield return new WaitForSeconds(_resultSeconds);

            Clear();
        }

        void Settle()
        {
            int staked = 0, paid = 0;

            for (int hand = 0; hand < BlackjackMath.Hands; hand++)
            {
                staked += _dealt.Bet[hand];
                int back = _dealt.Returned(hand);
                _paid[hand] = back;

                NetworkObject owner = _players[hand / 2];
                Wallet wallet = owner != null ? owner.GetComponent<Wallet>() : null;
                if (back <= 0 || wallet == null) continue;

                wallet.ServerPayChips(back);
                paid += back;
            }

            Debug.Log($"[Blackjack] Dealer {BlackjackMath.Describe(_dealt.Cards[BlackjackMath.Dealer])}. "
                      + $"{staked} staked, {paid} paid.");

            foreach (NetworkObject owner in _players)
            {
                var wallet = owner != null ? owner.GetComponent<Wallet>() : null;
                if (wallet != null && wallet.Chips == 0) Net.Achievements.ServerAward(owner, Net.Achievements.LostItAll);
            }

        }

        void Clear()
        {
            _dealt = null;
            _timedTurn = -1;
            _log.Clear();

            for (int seat = 0; seat < BlackjackMath.Seats; seat++)
            {
                _players[seat] = null;
                _owners[seat] = -1;
            }

            for (int hand = 0; hand < BlackjackMath.Hands; hand++)
            {
                _bets[hand] = 0;
                _paid[hand] = 0;
            }

            _turn.Value = -1;
            _holeDown.Value = false;
            _round.Value++;
            _phase.Value = (int)BlackjackPhase.Betting;
        }

        /// <summary>
        /// Copies the round's state into what replicates. Only called while players act: the
        /// dealer's draws are not in the round's log until <see cref="Round"/> plays them, and it
        /// paces them out itself.
        /// </summary>
        void Mirror()
        {
            while (_log.Count < _dealt.Log.Count)
            {
                _log.Add(_dealt.Log[_log.Count]);
                if (_dealt.HoleShown) _holeDown.Value = false;
            }

            for (int hand = 0; hand < BlackjackMath.Hands; hand++)
                if (_bets[hand] != _dealt.Bet[hand]) _bets[hand] = _dealt.Bet[hand];

            _turn.Value = _dealt.Turn;
        }

        void Update()
        {
            if (IsServerStarted) Timeouts();
            Draw();

            // Every peer rings its own table when the hands are paid; the server's Settle is heard by nobody else.
            if (Phase == _heardPhase) return;
            _heardPhase = Phase;
            if (Phase != BlackjackPhase.Done) return;

            bool anyWin = false;
            for (int hand = 0; hand < BlackjackMath.Hands; hand++) anyWin |= PaidOn(hand) > BetOn(hand);
            if (anyWin) Audio.Sfx.Play(Audio.Sound.Win, transform.position);
        }

        /// <summary>Stands a hand whose player has run out of time or is no longer there.</summary>
        void Timeouts()
        {
            if (_dealt == null || _dealt.Turn < 0 || _phase.Value != (int)BlackjackPhase.Playing) return;

            if (_dealt.Turn != _timedTurn)
            {
                _timedTurn = _dealt.Turn;
                _turnUntil = Time.time + _turnSeconds;
            }

            if (_players[_dealt.Turn / 2] != null && Time.time < _turnUntil) return;

            Debug.Log($"[Blackjack] Hand {_dealt.Turn} stood for its player.");
            _dealt.Act(BlackjackAction.Stand);
            Mirror();
        }

        // ---------------------------------------------------------------- the felt

        void Refresh()
        {
            if (_rebuiltRound == _round.Value && _rebuiltCount == _log.Count) return;

            _hands = BlackjackMath.Rebuild(_log);
            _rebuiltRound = _round.Value;
            _rebuiltCount = _log.Count;
        }

        /// <summary>
        /// Cards face up with their <see cref="CardFaces"/> face, the hole card face down, the rest
        /// hidden. A new card or the hole card turning snaps; a cleared felt does not.
        /// </summary>
        void Draw()
        {
            if (_cards == null || _cards.Length == 0) return;

            if (_feltRound == _round.Value && _feltCount == _log.Count && _feltHole == _holeDown.Value) return;

            if (_feltRound == _round.Value && (_log.Count > _feltCount || (_feltHole && !_holeDown.Value)))
                Audio.Sfx.Play(Audio.Sound.Deal, transform.position);

            _faceMeshes ??= System.Array.ConvertAll(_faces ?? new Renderer[0], f => f != null ? f.GetComponent<MeshFilter>() : null);

            _feltRound = _round.Value;
            _feltCount = _log.Count;
            _feltHole = _holeDown.Value;
            Refresh();

            for (int hand = 0; hand <= BlackjackMath.Dealer; hand++)
            {
                List<int> cards = _hands[hand];
                bool dealer = hand == BlackjackMath.Dealer;

                // The dealer's face-down card is not in the log, but it is on the felt.
                int shown = cards.Count + (dealer && _holeDown.Value && cards.Count == 1 ? 1 : 0);

                for (int i = 0; i < CardsShown; i++)
                {
                    int at = hand * CardsShown + i;
                    if (at >= _cards.Length || _cards[at] == null) continue;

                    bool on = i < shown;
                    bool faceDown = i >= cards.Count;

                    _cards[at].enabled = on;
                    if (on) _cards[at].sharedMaterial = faceDown ? _back : _face;

                    if (_faces == null || at >= _faces.Length || _faces[at] == null) continue;

                    _faces[at].enabled = on && !faceDown;
                    if (!on || faceDown) continue;

                    _faces[at].sharedMaterial = CardFaces.Material(_face);
                    if (_faceMeshes[at] != null) _faceMeshes[at].sharedMesh = CardFaces.Mesh(cards[i]);
                }
            }
        }

        /// <summary>Editor-time setup. See <c>BlackjackFactory</c>.</summary>
        public void Configure(Renderer[] cards, Renderer[] faces, Material face, Material back)
        {
            _cards = cards;
            _faces = faces;
            _face = face;
            _back = back;
        }
    }
}
