using System;
using EscapeWithYourFriends.Core;
using UnityEngine;

namespace EscapeWithYourFriends.Data
{
    /// <summary>
    /// What one hit does. Embedded in every weapon definition so that "a shotgun sends you flying
    /// and a pistol does not" is a tuning value in an asset, never a branch in weapon code.
    ///
    /// Knockback and stun are separate numbers on purpose. A sniper can hit like a truck without
    /// stunning; a bat can knock you down without doing much damage. Note though that knockback on a
    /// victim who is *not* stunned does almost nothing — an upright character is driven by its
    /// controller, not by physics — so a weapon meant to launch people needs both.
    /// </summary>
    [Serializable]
    public class HitProfile
    {
        [SerializeField] float _damage = 10f;
        [SerializeField] DamageType _damageType = DamageType.Blunt;

        [Tooltip("Impulse along the hit direction. Fists ~4, bat ~10, shotgun ~30, sniper ~45.")]
        [SerializeField] float _knockback = 4f;

        [Tooltip("How much of the knockback is redirected upward, so victims arc instead of sliding.")]
        [Range(0f, 1f)]
        [SerializeField] float _upwardBias = 0.3f;

        [Tooltip("Seconds the victim spends ragdolled. Zero means the hit only hurts.")]
        [SerializeField] float _stunDuration = 1.5f;

        public float Damage => _damage;
        public DamageType DamageType => _damageType;
        public float Knockback => _knockback;
        public float UpwardBias => _upwardBias;
        public float StunDuration => _stunDuration;

        /// <summary>
        /// Builds the damage event for a hit travelling along <paramref name="direction"/>.
        /// Called on the server only — the profile itself is shared data, the impulse is per-hit.
        /// </summary>
        public DamageInfo Build(Vector3 direction, Vector3 hitPoint, int attackerId)
        {
            Vector3 flat = direction.sqrMagnitude > 0f ? direction.normalized : Vector3.forward;
            Vector3 impulse = (flat + Vector3.up * _upwardBias).normalized * _knockback;

            return new DamageInfo(_damage, _damageType, impulse, hitPoint, _stunDuration, attackerId);
        }
    }
}
