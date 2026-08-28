using UnityEngine;

namespace EscapeWithYourFriends.Core
{
    /// <summary>
    /// What caused a hit. Damage is always applied on the host, so this travels as an argument to
    /// server-side calls rather than being trusted from a client.
    /// </summary>
    public enum DamageType
    {
        Blunt,      // fists, bats, shovels
        Bullet,
        Fall,
        Drowning,
        Animal,
        Vehicle,
        Electric,   // taser
        Environment,
    }

    /// <summary>
    /// A single damage event. Carries the impulse alongside the number so the ragdoll can be
    /// launched consistently by whatever applied the damage — the two always belong together here,
    /// because being sent flying *is* the feedback.
    /// </summary>
    public readonly struct DamageInfo
    {
        public readonly float Amount;
        public readonly DamageType Type;

        /// <summary>World-space impulse to apply to the victim, already scaled by the attacker.</summary>
        public readonly Vector3 Impulse;

        /// <summary>World-space contact point, used to pick which ragdoll bone takes the force.</summary>
        public readonly Vector3 HitPoint;

        /// <summary>
        /// Seconds of stun to apply on top of the damage. Zero means "no stun" — a bullet hurts
        /// without knocking you down, a bat does both.
        /// </summary>
        public readonly float StunDuration;

        /// <summary>
        /// ObjectId of the attacking NetworkObject, or 0 for world damage (falling, drowning).
        /// Kept as an id rather than a reference so it stays valid if the attacker despawns.
        /// </summary>
        public readonly int AttackerId;

        public DamageInfo(
            float amount,
            DamageType type,
            Vector3 impulse = default,
            Vector3 hitPoint = default,
            float stunDuration = 0f,
            int attackerId = 0)
        {
            Amount = amount;
            Type = type;
            Impulse = impulse;
            HitPoint = hitPoint;
            StunDuration = stunDuration;
            AttackerId = attackerId;
        }

        public bool CausesStun => StunDuration > 0f;

        /// <summary>World damage with no attacker and no knockback.</summary>
        public static DamageInfo World(float amount, DamageType type) => new(amount, type);
    }
}
