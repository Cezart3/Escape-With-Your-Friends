using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Items;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// Turns buffered button presses into calls on the combat systems. Owner only.
    ///
    /// Every one of those systems already exposes an owner-side <c>Request…</c> method that validates
    /// locally and then goes to the server; none of them read input themselves, deliberately, because
    /// a weapon that polls the keyboard cannot be fired by an NPC, a scripted test bot, or a vehicle
    /// turret. This is the one place that knows which button means which verb — so remapping, an
    /// input-blocking cutscene, or a menu that swallows clicks is a change in one file.
    ///
    /// Unlike movement this is not predicted, so it runs on the frame rather than the tick. The
    /// presses are still consumed from the reader's buffer: at 30Hz a tap can easily fall between two
    /// frames of a stuttering client, and a punch that silently did not happen is the worst kind of
    /// bug in a game whose whole point is punching your friends.
    /// </summary>
    public class PlayerCombatInput : NetworkBehaviour
    {
        [SerializeField] PlayerInputReader _input;
        [SerializeField] Combat.Weapon _weapon;
        [SerializeField] TaserWeapon _taser;
        [SerializeField] CarrySystem _carry;
        [SerializeField] PlayerInteractor _interactor;
        [SerializeField] GhostController _ghost;
        [SerializeField] RescueSystem _rescue;
        [SerializeField] ItemDropper _dropper;
        [SerializeField] ItemUse _use;
        [SerializeField] Fishing _fishing;
        [SerializeField] Inventory _inventory;
        [SerializeField] Vehicles.VehicleRider _rider;

        // Diagnostics only, behind -cameraLog: a headless run cannot see a punch land, so the count of
        // verbs actually issued is the difference between "combat is wired" and "combat is silent".
        bool _log;
        float _nextLogAt;
        int _attacks, _altAttacks, _interacts, _drops;

        bool _interactHeld;
        bool _dropHeld;

        // When the drop key went down. A tap drops, a hold throws, and the difference is decided on
        // release rather than on press - deciding on press would mean the throw fires while you are
        // still winding up, which is the wrong moment for the item to leave your hand.
        float _dropPressedAt;

        public override void OnStartClient()
        {
            base.OnStartClient();

            // A body you do not own has no input to read, and its reader is never bound.
            if (!IsOwner) enabled = false;
            else
            {
                _log = Core.CommandLine.HasFlag("-cameraLog");
                _nextLogAt = Time.time + 2f;
            }
        }

        void Update()
        {
            if (_input == null || !_input.IsBound) return;

            // Consume unconditionally, even when the matching system is missing: leaving a press in
            // the buffer means it fires later, at a moment the player has forgotten pressing anything.
            bool attack = _input.ConsumeAttack();
            bool altAttack = _input.ConsumeAltAttack();
            bool interact = _input.ConsumeInteract();
            bool drop = _input.ConsumeDrop();
            bool use = _input.ConsumeUse();
            bool reload = _input.ConsumeReload();

            // Attack means three different verbs, and the order is the same rule as everywhere
            // else in this file: the more specific state wins. A corpse shoves, a rod casts, and
            // everything left over swings whatever is in your hand. Fishing answers false unless it
            // is actually holding a rod or already has a line in the water, so a machete never has
            // to know this branch exists.
            if (attack)
            {
                if (_ghost != null && _ghost.IsActive) _ghost.RequestNudge();
                else if (_fishing != null && _fishing.RequestAttack()) { }
                else if (_weapon != null) _weapon.RequestAttack();
            }

            // The reel. Sent from here rather than polled inside Fishing for the same reason nothing
            // else polls input: one reader, one owner of the buffer, no two systems racing for the
            // same key. Fishing only puts it on the wire when it changes and only during a fight.
            if (_fishing != null) _fishing.NotifyReel(_input.AttackHeld);

            // Driving reuses Move and Crouch rather than asking for an action map of its own. Two
            // axes and a handbrake is the whole control scheme of an arcade car, the body they came
            // from is going nowhere on its own while seated, and a second map is a second place for a
            // rebind to be forgotten.
            if (_rider != null && _rider.IsDriving) _rider.Drive(_input.Move, _input.Crouch);

            if (altAttack && _taser != null) _taser.RequestFire();

            // The priority list this file always said it would need. Machines win over bodies: the
            // gesture the Revive Machine wants is walking up to it holding a corpse and pressing
            // Interact, and if carrying won that key the press would put the body on the floor
            // instead. Dropping still has its own button, and the machine takes the body off your
            // shoulder itself, so nothing is unreachable.
            if (interact)
            {
                // Top of the list, and only while actually sitting in something. Getting out is the
                // only verb a passenger has, and routing it through the interactor instead would mean
                // aiming at the car you are inside - a sphere cast from a seat hits the chassis, the
                // dashboard or nothing at all depending on which way your head is turned, and "I
                // cannot get out of the boat" is not a bug anybody should have to report.
                bool left = _rider != null && _rider.RequestExit();

                bool used = left || (_interactor != null && _interactor.RequestInteract());
                if (!used && _carry != null) _carry.RequestPickupOrDrop();
            }
            // Drop is the same priority list as Interact, one step down. A body on your shoulder is
            // the bigger commitment, so it goes first; with your hands free the key means the bag.
            // The buffered press opens the hold as well as recording when it started. Without that,
            // a tap fast enough to go down and up inside one frame would never look like a release
            // and would be swallowed entirely.
            if (drop)
            {
                _dropPressedAt = Time.time;
                _dropHeld = true;
            }

            bool dropReleased = _dropHeld && !_input.DropHeld;
            _dropHeld = _input.DropHeld;

            if (dropReleased)
            {
                bool thrown = Time.time - _dropPressedAt >= ItemDropper.HoldToThrow;

                if (_carry != null && _carry.IsCarrying) _carry.RequestThrow();
                else if (_dropper != null) _dropper.RequestDrop(thrown);
            }

            if (use && _use != null) _use.RequestUse();

            // Harmless on a bat: the server refuses a reload for anything with no magazine, so the
            // key does not need to know what is in your hand.
            if (reload && _weapon != null) _weapon.RequestReload();

            // Hotbar. Sent straight through rather than buffered per tick: selection is idempotent,
            // so the last one to arrive wins and a lost packet costs nothing.
            if (_inventory != null)
            {
                int picked = _input.ConsumeHotbarSlot();
                int steps = _input.ConsumeHotbarSteps();

                if (picked >= 0) _inventory.SelectSlot(picked);
                else if (steps != 0) _inventory.SelectSlot(_inventory.SelectedSlot + steps);
            }

            // Interact is the only verb in the game that is a hold. The press above starts it, going
            // through the interactor like everything else so the range is validated once; this is the
            // release, and it is the only other thing the server needs from the client. Sent on the
            // edge rather than every frame — the server times the hold itself, so a stream of "still
            // holding" packets would tell it nothing it does not already assume.
            if (_rescue != null && _interactHeld && !_input.InteractHeld) _rescue.NotifyReleased();
            _interactHeld = _input.InteractHeld;

            if (attack) _attacks++;
            if (altAttack) _altAttacks++;
            if (interact) _interacts++;
            if (drop) _drops++;

            if (!_log || Time.time < _nextLogAt) return;

            Debug.Log($"[PlayerCombatInput] owner {OwnerId}: {_attacks} attack(s), {_altAttacks} taser, "
                      + $"{_interacts} interact, {_drops} drop.");

            _nextLogAt = Time.time + 2f;
            _attacks = _altAttacks = _interacts = _drops = 0;
        }
    }
}
