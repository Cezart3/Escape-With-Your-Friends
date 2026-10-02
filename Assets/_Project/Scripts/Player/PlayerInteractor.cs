using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// The player's end of <see cref="IInteractable"/>: finds what is being aimed at and asks the
    /// server to use it.
    ///
    /// Deliberately the same shape as <see cref="CarrySystem"/> — same sphere cast, same aim origin,
    /// same server-side range re-validation with the same slack — because they are the same gesture
    /// competing for the same key, and two different reaches would mean the prompt appears at a
    /// distance where the action then fails. The only difference is what the cast is looking for.
    ///
    /// The sphere cast is a little wider and a little longer than the carry reach on purpose: a
    /// machine is a big fixed object you walk up to, a body is a small thing on the floor. Where both
    /// are in range, <see cref="PlayerCombatInput"/> decides, and it prefers this.
    ///
    /// Nothing here trusts the client's target. <see cref="RequestInteract"/> returns whether it
    /// found something worth sending — that is the signal the input component needs to know whether
    /// to fall through to carrying — but the server re-runs every check before anything happens.
    /// </summary>
    public class PlayerInteractor : NetworkBehaviour
    {
        [Header("References")]
        [Tooltip("Origin for the search — the same eye-height transform the weapons aim from.")]
        [SerializeField] Transform _aimOrigin;

        [Header("Rules")]
        [Tooltip("How far away a machine can be used. Longer than the carry reach; see the notes.")]
        [SerializeField] float _range = 3.5f;

        [Tooltip("Fat cast, so aiming at a large object does not require aiming at its centre.")]
        [SerializeField] float _castRadius = 0.5f;

        [Tooltip("Server-side slack on the range check, to forgive latency between aim and request.")]
        [SerializeField] float _serverRangeTolerance = 1.5f;

        [SerializeField] LayerMask _mask = ~0;

        StunState _stun;
        Health _health;

        /// <summary>
        /// What the local player is aiming at, or null. Recomputed on demand rather than cached: the
        /// HUD (#106) will want it every frame and the input path wants it on a key press, and a
        /// sphere cast is cheaper than the bookkeeping to keep a cache honest.
        /// </summary>
        public IInteractable Aimed => FindTarget(out _);

        void Awake()
        {
            _stun = GetComponent<StunState>();
            _health = GetComponent<Health>();
            _rider = GetComponent<Vehicles.VehicleRider>();
        }

        Vehicles.VehicleRider _rider;
        string _prompt, _controls;
        GUIStyle _promptStyle, _controlsStyle;

        // The key to press, under the crosshair, and the controls while seated. Nothing on screen
        // used to say either: the user stood at the trader pressing nothing, and sat in the plane
        // not knowing Shift was the throttle. Cached here, drawn in OnGUI, which runs twice a frame.
        void Update()
        {
            _prompt = _controls = null;
            if (!IsOwner || UI.HudRoot.InventoryOpen) return;
            if (_health != null && _health.IsIncapacitated) return;

            if (_rider != null && _rider.IsSeated)
            {
                _controls = !_rider.IsDriving
                    ? "V  camera\nE  get out"
                    : _rider.Vehicle.GetComponent<Vehicles.PlaneController>() != null
                        ? "Shift  throttle\nW / S  nose up / down\nA / D  bank to turn\nCtrl  brake\nV  camera\nE  get out"
                        : "W / S  forward / reverse\nA / D  steer\nCtrl  brake\nV  camera\nE  get out";
                return;
            }

            IInteractable aimed = Aimed;
            if (aimed != null) _prompt = $"[E]  {aimed.Prompt}";
        }

        void OnGUI()
        {
            if (_prompt == null && _controls == null) return;
            float scale = Screen.height / 1080f;

            if (_promptStyle == null)
            {
                // A label on a dark plate: the skin's box is grey on grey and was hard to read on sand.
                _promptStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false };
                _controlsStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = false };
                _promptStyle.normal.textColor = _controlsStyle.normal.textColor = Color.white;
            }
            _promptStyle.fontSize = _controlsStyle.fontSize = Mathf.RoundToInt(22f * scale);
            _controlsStyle.padding = new RectOffset(Mathf.RoundToInt(14f * scale), Mathf.RoundToInt(14f * scale),
                                                    Mathf.RoundToInt(10f * scale), Mathf.RoundToInt(10f * scale));

            if (_prompt != null)
            {
                Vector2 size = _promptStyle.CalcSize(new GUIContent(_prompt)) + new Vector2(24f, 10f) * scale;
                Plate(new Rect((Screen.width - size.x) * 0.5f, Screen.height * 0.5f + 60f * scale, size.x, size.y),
                      _prompt, _promptStyle);
            }
            else
            {
                Vector2 size = _controlsStyle.CalcSize(new GUIContent(_controls));
                Plate(new Rect(24f * scale, Screen.height * 0.5f - size.y * 0.5f, size.x, size.y), _controls, _controlsStyle);
            }
        }

        static void Plate(Rect rect, string text, GUIStyle style)
        {
            Color was = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = was;
            GUI.Label(rect, text, style);
        }

        /// <summary>
        /// Owner-side entry point. Returns true if something interactable was aimed at and a request
        /// went out — false means the caller should try the next thing on its list.
        /// </summary>
        public bool RequestInteract()
        {
            if (!IsOwner) return false;

            // A body on the floor does not get to press buttons. Checked here as well as on the
            // server so a downed player does not spam requests that will all be refused.
            if (_health != null && _health.IsIncapacitated) return false;
            if (_stun != null && _stun.IsStunned) return false;

            IInteractable aimed = FindTarget(out NetworkObject target);
            if (aimed == null) return false;

            // A trader and a chest are screens, not actions: E opens the bag with them beside it.
            // They used to want Tab, which nothing said, and the user stood at the counter pressing E
            // with nothing happening.
            if (aimed is Economy.ShopCounter || aimed is Items.Storage)
            {
                GetComponent<PlayerInputReader>()?.QueueInventory();
                return true;
            }

            ServerInteract(target);
            return true;
        }

        IInteractable FindTarget(out NetworkObject networkObject)
        {
            networkObject = null;

            Transform origin = _aimOrigin != null ? _aimOrigin : transform;

            // Every hit, nearest first, and never the plane part in your own arms unless nothing
            // else is there: it rides at your face, so the nearest hit was always the part itself,
            // and E at the plane put the propeller down instead of fitting it (playthrough bot).
            // With nothing else in front of you, E on it is still how you put it down.
            RaycastHit[] hits = Physics.SphereCastAll(origin.position, _castRadius, origin.forward,
                                                      _range, _mask, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            World.PlanePart held = World.PlanePart.HeldBy(NetworkObject);
            IInteractable fallback = null;
            NetworkObject fallbackObject = null;

            foreach (RaycastHit hit in hits)
            {
                // In parents, not on the collider: a machine's hit box is a child mesh, and the
                // component that knows what the machine does sits on the networked root.
                //
                // All of them, not the first: #72's aeroplane is one object that is both a thing
                // you fit parts to and a thing you get into, and which of those it is depends on
                // what is on your shoulder. An empty prompt means the component is present but has
                // nothing to offer, so the first one with something to say is the answer, and if
                // none has anything the key falls through to carrying, which is what lets a corpse
                // be picked up at all.
                IInteractable interactable = null;
                foreach (IInteractable candidate in hit.collider.GetComponentsInParent<IInteractable>())
                {
                    if (candidate == null || string.IsNullOrEmpty(candidate.Prompt)) continue;
                    interactable = candidate;
                    break;
                }

                NetworkObject owner = hit.collider.GetComponentInParent<NetworkObject>();
                // Your own capsule and limbs: the arm holding a pistol sits in front of the camera, and
                // at the wrong pitch it was the nearest hit and stopped the look. This used to be a
                // skip of every hit overlapping the cast's start, which also threw away the plane you
                // were pressed against and left E doing nothing at 0.4 m (playthrough bot).
                if (owner == NetworkObject) continue;
                if (held != null && owner == held.NetworkObject)
                {
                    if (interactable != null) { fallback = interactable; fallbackObject = owner; }
                    continue;
                }

                // Anything else stops the look, as the single cast did: no reaching through a wall.
                if (interactable == null || owner == null) break;

                networkObject = owner;
                return interactable;
            }

            networkObject = fallbackObject;
            return fallback;
        }

        /// <summary>
        /// The object is sent, not the interface: FishNet can serialise a spawned
        /// <see cref="NetworkObject"/> by id, and the server resolves the component from it. That
        /// also means a client can only ever name something that actually exists on the server.
        /// </summary>
        [ServerRpc]
        void ServerInteract(NetworkObject target)
        {
            if (target == null) return;

            if (_health != null && _health.IsIncapacitated) return;
            if (_stun != null && _stun.IsStunned) return;

            // The client picked the target; it does not get to decide how far away it was allowed
            // to be. Measured to the object's origin, with the same slack carrying uses.
            float maxDistance = _range + _serverRangeTolerance;
            if ((target.transform.position - transform.position).sqrMagnitude > maxDistance * maxDistance)
                return;

            // Children as well as the root, and the first one that will actually take the actor
            // rather than simply the first one. The client picked an object, not a component, so on
            // an object wearing two interactables — #72's aeroplane wears both PlaneAssembly and
            // Vehicle — this is where the two are told apart, on the machine that owns the answer.
            foreach (IInteractable candidate in target.GetComponentsInChildren<IInteractable>())
            {
                if (candidate == null || !candidate.ServerCanInteract(NetworkObject)) continue;

                candidate.ServerInteract(NetworkObject);
                return;
            }
        }
    }
}
