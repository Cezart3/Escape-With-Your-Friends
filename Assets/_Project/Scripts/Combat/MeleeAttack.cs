using System;
using System.Collections;
using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// Swinging fists, bats and shovels. The client asks; the server decides what got hit.
    ///
    /// The client sends only an aim direction. Range, cone, cooldown and damage all come from the
    /// server copy of the weapon definition, so editing a local asset buys a cheater nothing. The
    /// direction is checked against the character facing, because otherwise a client could punch
    /// somebody standing behind it.
    /// </summary>
    public class MeleeAttack : NetworkBehaviour
    {
        [Header("References")]
        [Tooltip("Origin of the swing — normally the camera or head, so you hit what you look at.")]
        [SerializeField] Transform _aimOrigin;

        [Tooltip("Fallback weapon. Bare hands are a weapon like any other, defined in an asset.")]
        [SerializeField] MeleeWeaponDef _fists;

        [SerializeField] LayerMask _hitMask = ~0;

        [Header("Anti-cheat")]
        [Tooltip("Degrees the requested aim may deviate from where the character is actually facing.")]
        [Range(30f, 180f)]
        [SerializeField] float _maxAimDeviation = 100f;

        MeleeWeaponDef _equipped;
        Health _health;
        StunState _stun;

        readonly Collider[] _overlap = new Collider[32];
        readonly List<Health> _hitThisSwing = new();

        float _serverNextAttackAt;
        float _localNextAttackAt;

        /// <summary>Raised on every peer when a swing starts, for animation and sound.</summary>
        public event Action<MeleeWeaponDef> Swung;

        /// <summary>Raised on every peer when a swing connects, at the contact point.</summary>
        public event Action<Vector3> HitLanded;

        /// <summary>
        /// Currently held melee weapon. Set on the server when the shop or inventory hands one over;
        /// clients use it for animation only, and the server never trusts a client copy for damage.
        /// </summary>
        public MeleeWeaponDef Equipped => _equipped != null ? _equipped : _fists;

        void Awake()
        {
            _health = GetComponent<Health>();
            _stun = GetComponent<StunState>();
        }

        /// <summary>Server only. Swaps the held weapon.</summary>
        public void ServerEquip(MeleeWeaponDef weapon)
        {
            if (!IsServerStarted) return;
            _equipped = weapon;
        }

        /// <summary>Owner-side entry point. Call from input.</summary>
        public void RequestAttack()
        {
            if (!IsOwner || !CanAct()) return;

            MeleeWeaponDef weapon = Equipped;
            if (weapon == null || Time.time < _localNextAttackAt) return;

            // Predicted locally so the swing feels instant; the server still decides what it touched.
            _localNextAttackAt = Time.time + weapon.Cooldown;
            Swung?.Invoke(weapon);

            ServerAttack(AimDirection());
        }

        bool CanAct()
        {
            if (_health != null && _health.IsIncapacitated) return false;
            if (_stun != null && _stun.IsStunned) return false;
            return true;
        }

        Vector3 AimDirection()
        {
            Transform origin = _aimOrigin != null ? _aimOrigin : transform;
            return origin.forward;
        }

        [ServerRpc]
        void ServerAttack(Vector3 aimDirection)
        {
            MeleeWeaponDef weapon = Equipped;
            if (weapon == null || !CanAct()) return;
            if (Time.time < _serverNextAttackAt) return;

            if (aimDirection.sqrMagnitude < 0.001f) return;
            if (!AimValidation.IsFacing(transform, aimDirection, _maxAimDeviation)) return;

            Vector3 direction = aimDirection.normalized;
            _serverNextAttackAt = Time.time + weapon.Cooldown;
            ObserversSwing();

            if (weapon.Windup > 0f) StartCoroutine(ResolveAfterWindup(weapon, direction));
            else ServerResolveSwing(weapon, direction);
        }

        IEnumerator ResolveAfterWindup(MeleeWeaponDef weapon, Vector3 direction)
        {
            yield return new WaitForSeconds(weapon.Windup);

            // The swing was already committed, but dying mid-windup cancels it.
            if (CanAct()) ServerResolveSwing(weapon, direction);
        }

        void ServerResolveSwing(MeleeWeaponDef weapon, Vector3 direction)
        {
            Transform origin = _aimOrigin != null ? _aimOrigin : transform;
            Vector3 originPosition = origin.position;

            // One fat sphere covering the whole swing, then a cone filter. Cheaper than a sweep and
            // forgiving enough that a wild punch into a pile of friends connects with several of them.
            float reach = weapon.Range;
            Vector3 center = originPosition + direction * (reach * 0.5f);
            float sphereRadius = reach * 0.5f + weapon.Radius;

            int count = Physics.OverlapSphereNonAlloc(center, sphereRadius, _overlap, _hitMask,
                                                      QueryTriggerInteraction.Ignore);

            _hitThisSwing.Clear();

            for (int i = 0; i < count && _hitThisSwing.Count < weapon.MaxTargets; i++)
            {
                Collider hit = _overlap[i];
                if (hit == null) continue;

                Health victim = hit.GetComponentInParent<Health>();
                if (victim == null || victim == _health) continue;
                if (_hitThisSwing.Contains(victim)) continue;

                Vector3 contact = hit.ClosestPoint(originPosition);
                Vector3 toContact = contact - originPosition;

                if (toContact.magnitude > reach + weapon.Radius) continue;
                if (toContact.sqrMagnitude > 0.001f
                    && Vector3.Angle(toContact, direction) > weapon.ConeHalfAngle) continue;

                _hitThisSwing.Add(victim);
                ApplyHit(weapon, victim, direction, contact);
            }
        }

        void ApplyHit(MeleeWeaponDef weapon, Health victim, Vector3 direction, Vector3 contact)
        {
            DamageInfo info = weapon.Hit.Build(direction, contact, ObjectId);

            bool wasStanding = victim.IsAlive;
            victim.TakeDamage(info);

            // If the blow put them down, Health already broadcast the impulse with the incapacitation
            // and pushing again would double the force. Otherwise the stun component does the shoving.
            bool knockedDown = wasStanding && victim.IsIncapacitated;
            if (!knockedDown)
            {
                StunState victimStun = victim.GetComponent<StunState>();
                if (victimStun != null) victimStun.ServerStun(info);
            }

            ObserversHit(contact);
        }

        [ObserversRpc(ExcludeOwner = true)]
        void ObserversSwing() => Swung?.Invoke(Equipped);

        [ObserversRpc(RunLocally = true)]
        void ObserversHit(Vector3 contact) => HitLanded?.Invoke(contact);

        void OnDrawGizmosSelected()
        {
            MeleeWeaponDef weapon = Equipped;
            if (weapon == null) return;

            Transform origin = _aimOrigin != null ? _aimOrigin : transform;
            Gizmos.color = new Color(1f, 0.4f, 0.2f, 0.6f);
            Gizmos.DrawWireSphere(origin.position + origin.forward * (weapon.Range * 0.5f),
                                  weapon.Range * 0.5f + weapon.Radius);
        }
    }
}
