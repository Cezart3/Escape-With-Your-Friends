using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The airframe the three parts from #70 go into, and the whole of #71.
    ///
    /// The acceptance is *"progress is legible at a glance and replicated to all players"*, and the
    /// laziest honest reading of that is: **the plane itself is the progress bar**. A wreck with no
    /// engine, no wing and no propeller stands at the beachhead with three holes in it, and each part
    /// you carry home fills one of them in, on everybody's screen at once. No meter, no percentage,
    /// no UI at all - if you have to read a number to know how far along the plane is, the plane is
    /// not legible.
    ///
    /// **How the holes are wired.** Every child called <c>Fitted.something</c> is a missing piece,
    /// hidden at <see cref="Awake"/>, and <c>something</c> is the <see cref="PlanePart.Label"/> that
    /// fills it. There is no serialized table and nothing to wire in an inspector: adding a fourth
    /// part later means adding a fourth child in <c>PlaneBuilder</c> and a fourth entry in
    /// <c>PlanePartBuilder</c>, and this file does not change.
    ///
    /// What is fitted is a bitmask rather than a count, because fitting the wing first and seeing an
    /// engine appear is the kind of small lie that makes a player stop trusting the model. A count
    /// would have been one character shorter and wrong.
    /// </summary>
    public class PlaneAssembly : NetworkBehaviour, IInteractable
    {
        [Tooltip("Children whose name starts with this are the missing pieces. The rest of the name "
                 + "is the plane part label that fills the hole.")]
        [SerializeField] string _prefix = "Fitted.";

        /// <summary>Which holes are filled, one bit per entry in <see cref="_wanted"/>.</summary>
        readonly SyncVar<int> _fitted = new();

        readonly List<string> _wanted = new();
        readonly List<GameObject> _pieces = new();

        float _objectiveAt;

        /// <summary>The one in the world, so the parts and the harness can find it without a search.</summary>
        public static PlaneAssembly Instance { get; private set; }

        public int Needed => _wanted.Count;
        public int Fitted => Count(_fitted.Value);

        /// <summary>
        /// How many pieces are actually standing on the model. The harness's client half asks this
        /// rather than the SyncVar, because the acceptance is about what a player can see, and a
        /// replicated integer that nobody turned into a wing is not progress anybody can read.
        /// </summary>
        public int Showing
        {
            get
            {
                int on = 0;
                foreach (GameObject piece in _pieces)
                    if (piece != null && piece.activeSelf) on++;

                return on;
            }
        }
        public bool Complete => _wanted.Count > 0 && Fitted == _wanted.Count;

        /// <summary>
        /// Whether this group has ever finished an aeroplane. See the note in OnStartServer: the
        /// airframe on the far island is the same group's, so it stands there whole.
        /// </summary>
        public static bool Owned { get; private set; }

        void Awake()
        {
            Instance = this;
            _fitted.OnChange += OnFittedChanged;

            foreach (Transform child in transform)
            {
                if (!child.name.StartsWith(_prefix)) continue;

                _wanted.Add(child.name.Substring(_prefix.Length));
                _pieces.Add(child.gameObject);
                child.gameObject.SetActive(false);
            }

            if (_wanted.Count == 0)
                Debug.LogWarning($"[PlaneAssembly] {name} has no '{_prefix}*' children; "
                                 + "it is already finished and nothing can be fitted to it.");
        }

        void OnDestroy()
        {
            _fitted.OnChange -= OnFittedChanged;
            if (Instance == this) Instance = null;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            // A late joiner gets the SyncVar's value rather than its changes, so the pieces are put
            // up by hand here. Without this, somebody who joined after the engine went in sees a
            // plane that is still missing it, which is exactly the bug the acceptance is about.
            Show();
        }

        public override void OnStartServer()
        {
            // #73. The group built an aeroplane once, and the one waiting on the other island is
            // theirs too. Flying back to fetch somebody and finding three fresh holes in the airframe
            // would mean hauling the same three parts across an island that has none of them.
            //
            // ponytail: a static, exactly like BoatVoyage's owned parts and for the same reason - an
            // airframe is a scene object and scene objects do not cross scenes, so "the group has a
            // plane" cannot live on one. It survives as long as the process does; it moves into the
            // save file when #75 gives one a home.
            // #75. The static above dies with the process and the save file does not, so the file
            // gets the first word. Read here rather than pushed in from outside: RunSave arms inside
            // the server's own started callback, which is before any scene object has spawned.
            if (Core.RunSave.SavedPlaneOwned) Owned = true;

            if (Owned && _wanted.Count > 0) _fitted.Value = (1 << _wanted.Count) - 1;

            base.OnStartServer();

            // And the ordinary case the save exists for: an aeroplane somebody quit halfway through
            // building. Only ever adds - see ServerFit.
            foreach (string label in Core.RunSave.SavedParts) ServerFit(label);

            Show();
        }

        // ------------------------------------------------------------------ the crosshair

        public string Prompt
        {
            get
            {
                // Null once it is whole, not "The plane is finished". A prompt is an offer, and this
                // component has nothing left to offer; saying so out loud would also park the
                // crosshair on this component forever and leave the Vehicle underneath it - the one
                // that flies you home - unreachable. #72.
                if (_wanted.Count == 0 || Complete) return null;

                return $"The plane is missing {Missing()}";
            }
        }

        /// <summary>
        /// Server only. True when the actor is holding a part this plane still has a hole for.
        ///
        /// Deliberately not "is the actor near enough" - <see cref="Player.PlayerInteractor"/> owns
        /// that, and owns it on the server, so repeating it here would be two places to get one
        /// number wrong in.
        /// </summary>
        public bool ServerCanInteract(NetworkObject actor)
        {
            if (!IsServerStarted || actor == null || Complete) return false;

            PlanePart held = PlanePart.HeldBy(actor);
            return held != null && Hole(held.Label) >= 0;
        }

        public void ServerInteract(NetworkObject actor)
        {
            if (!ServerCanInteract(actor)) return;

            PlanePart held = PlanePart.HeldBy(actor);
            int hole = Hole(held.Label);
            if (hole < 0) return;

            _fitted.Value |= 1 << hole;

            // Off the shoulder before it stops existing, so the collision-ignore bookkeeping the
            // carry put on the player unwinds through the same path a normal drop uses.
            held.ServerPutDown();
            ServerManager.Despawn(held.NetworkObject);

            Debug.Log($"[PlaneAssembly] {held.Label} fitted; {Fitted}/{_wanted.Count} done"
                      + (Complete ? ", the plane is finished." : $", still missing {Missing()}."));
        }

        // ------------------------------------------------------------------ presentation

        void OnFittedChanged(int previous, int next, bool asServer) => Show();

        void Show()
        {
            for (int i = 0; i < _pieces.Count; i++)
                if (_pieces[i] != null)
                    _pieces[i].SetActive((_fitted.Value & (1 << i)) != 0);

            // One place, because every road to "it is whole" goes through here: the last part being
            // fitted, and a peer being handed a finished plane it never watched get built.
            if (Complete) Owned = true;
        }

        void Update()
        {
            // The objective line, on every peer, at 2Hz. PlanePart owns it while any part is still
            // lying out in the world; once they are all on a shoulder or in a hole, it is this one's.
            if (Time.time < _objectiveAt) return;
            _objectiveAt = Time.time + 0.5f;

            foreach (PlanePart part in PlanePart.All)
                if (!part.IsCarried) return;

            // #73. Once there is somebody to go back for, the chain is theirs: Castaway writes every
            // line from "find them" onward. Two components taking turns on one global line is a
            // flicker, and the one standing next to the player wins.
            if (AI.Castaway.Instance != null && !AI.Castaway.Instance.Rescued) return;

            if (Complete) Objective.Set("Get in the plane and fly to the other island", transform);
            else Objective.Set($"Bring the {Missing()} to the plane", transform);
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>Which hole <paramref name="label"/> fills, or -1 if there is no empty one.</summary>
        int Hole(string label)
        {
            for (int i = 0; i < _wanted.Count; i++)
                if (_wanted[i] == label && (_fitted.Value & (1 << i)) == 0) return i;

            return -1;
        }

        /// <summary>"engine and wing", for a prompt somebody reads while standing in front of it.</summary>
        /// <summary>
        /// Fills every hole at once. This is the <see cref="Owned"/> case applied late: a harness
        /// that starts on the first island is starting after the group already built an aeroplane,
        /// and there are no parts lying about on that island to fetch. #74.
        /// </summary>
        internal void ServerFitAll()
        {
            if (!IsServerStarted || _wanted.Count == 0) return;

            _fitted.Value = (1 << _wanted.Count) - 1;
        }

        /// <summary>
        /// Server only. Fills the hole <paramref name="label"/> belongs to, if it is still empty.
        /// This is what a loaded save calls, once per part it remembers being fitted. #75.
        ///
        /// It only ever adds. A save is a snapshot of a moment that has already passed, and a part
        /// fitted since that moment must not be pulled back out by a stale file.
        /// </summary>
        internal void ServerFit(string label)
        {
            if (!IsServerStarted || string.IsNullOrEmpty(label)) return;

            int hole = Hole(label);
            if (hole < 0) return;

            _fitted.Value |= 1 << hole;
            Show();
        }

        /// <summary>Which parts are in, by label, for the save file. Server-side. #75.</summary>
        internal List<string> ServerFittedLabels()
        {
            var fitted = new List<string>();

            for (int i = 0; i < _wanted.Count; i++)
                if ((_fitted.Value & (1 << i)) != 0) fitted.Add(_wanted[i]);

            return fitted;
        }

        public string Missing()
        {
            var left = new List<string>();

            for (int i = 0; i < _wanted.Count; i++)
                if ((_fitted.Value & (1 << i)) == 0) left.Add(_wanted[i]);

            if (left.Count == 0) return "nothing";
            if (left.Count == 1) return left[0];

            return string.Join(", ", left.GetRange(0, left.Count - 1)) + " and " + left[left.Count - 1];
        }

        /// <summary>True if that label's hole is filled. For the harness and for #72.</summary>
        public bool Has(string label)
        {
            for (int i = 0; i < _wanted.Count; i++)
                if (_wanted[i] == label && (_fitted.Value & (1 << i)) != 0) return true;

            return false;
        }

        static int Count(int mask)
        {
            int count = 0;
            while (mask != 0) { count += mask & 1; mask >>= 1; }
            return count;
        }
    }
}
