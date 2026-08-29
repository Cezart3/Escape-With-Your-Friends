using EscapeWithYourFriends.Combat;
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
        [SerializeField] MeleeAttack _melee;
        [SerializeField] TaserWeapon _taser;
        [SerializeField] CarrySystem _carry;
        [SerializeField] PlayerInteractor _interactor;
        [SerializeField] GhostController _ghost;

        // Diagnostics only, behind -cameraLog: a headless run cannot see a punch land, so the count of
        // verbs actually issued is the difference between "combat is wired" and "combat is silent".
        bool _log;
        float _nextLogAt;
        int _attacks, _altAttacks, _interacts, _drops;

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

            // Attack means two different verbs depending on whether you have a body. The dead get the
            // shove; routing it through here rather than letting GhostController poll input itself is
            // the same rule as everything else in this file — two pollers would race for the same
            // buffered press and one of them would silently lose it.
            if (attack)
            {
                if (_ghost != null && _ghost.IsActive) _ghost.RequestNudge();
                else if (_melee != null) _melee.RequestAttack();
            }

            if (altAttack && _taser != null) _taser.RequestFire();

            // The priority list this file always said it would need. Machines win over bodies: the
            // gesture the Revive Machine wants is walking up to it holding a corpse and pressing
            // Interact, and if carrying won that key the press would put the body on the floor
            // instead. Dropping still has its own button, and the machine takes the body off your
            // shoulder itself, so nothing is unreachable.
            if (interact)
            {
                bool used = _interactor != null && _interactor.RequestInteract();
                if (!used && _carry != null) _carry.RequestPickupOrDrop();
            }
            if (drop && _carry != null) _carry.RequestThrow();

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
