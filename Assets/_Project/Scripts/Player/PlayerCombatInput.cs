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

            if (attack && _melee != null) _melee.RequestAttack();
            if (altAttack && _taser != null) _taser.RequestFire();

            // Interact is carry for now. Once there are shop counters, the revive machine and the
            // roulette table (#25, #105), this becomes a short priority list: whatever is aimed at
            // first, a carryable body last.
            if (interact && _carry != null) _carry.RequestPickupOrDrop();
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
