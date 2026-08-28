using System;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// Ranged stun with a battery. The client asks and aims; the server decides what it hit and what
    /// the charge was.
    ///
    /// The battery is not synced per frame. Two values change on a shot — the charge left at that
    /// moment and the tick recharging starts — and every peer recomputes the current level from the
    /// same formula. That is two writes per shot instead of a continuous stream, and because the
    /// server uses the identical formula there is nothing to drift.
    /// </summary>
    public class TaserWeapon : NetworkBehaviour
    {
        [Header("References")]
        [Tooltip("Origin of the shot — normally the camera, so you hit what you look at.")]
        [SerializeField] Transform _aimOrigin;

        [SerializeField] TaserDef _taser;

        [SerializeField] LayerMask _hitMask = ~0;

        [Header("Anti-cheat")]
        [Tooltip("Degrees the requested aim may deviate from where the character is actually facing.")]
        [Range(30f, 180f)]
        [SerializeField] float _maxAimDeviation = 100f;

        readonly SyncVar<float> _chargeAtLastShot = new();
        readonly SyncVar<uint> _rechargeStartTick = new();

        Health _health;
        StunState _stun;

        float _serverNextShotAt;
        float _localNextShotAt;

        /// <summary>Raised on every peer when the taser fires, for the arc effect and the sound.</summary>
        public event Action Fired;

        /// <summary>Raised on every peer when a shot connects, at the contact point.</summary>
        public event Action<Vector3> HitLanded;

        /// <summary>Raised on the owner when a shot is refused for lack of charge.</summary>
        public event Action Depleted;

        public TaserDef Definition => _taser;

        /// <summary>Current charge, recomputed from the last shot. Valid on every peer, including late joiners.</summary>
        public float Charge
        {
            get
            {
                if (_taser == null) return 0f;
                if (TimeManager == null) return _chargeAtLastShot.Value;

                uint now = TimeManager.Tick;
                uint start = _rechargeStartTick.Value;
                if (now <= start) return _chargeAtLastShot.Value;

                float elapsed = (float)TimeManager.TicksToTime(now - start);
                return Mathf.Min(_taser.Capacity,
                                 _chargeAtLastShot.Value + elapsed * _taser.RechargeRate);
            }
        }

        public float ChargeNormalized => _taser != null && _taser.Capacity > 0f
            ? Charge / _taser.Capacity
            : 0f;

        void Awake()
        {
            _health = GetComponent<Health>();
            _stun = GetComponent<StunState>();
        }

        public override void OnStartServer()
        {
            base.OnStartServer();

            // Spawn with a full battery, and with recharging already "finished" so the getter does
            // not have to special-case a tick of zero.
            if (_taser != null) _chargeAtLastShot.Value = _taser.Capacity;
            if (TimeManager != null) _rechargeStartTick.Value = TimeManager.Tick;
        }

        /// <summary>Server only. Swaps the equipped taser, e.g. a shop upgrade with a bigger battery.</summary>
        public void ServerEquip(TaserDef taser)
        {
            if (!IsServerStarted) return;
            _taser = taser;
        }

        /// <summary>Owner-side entry point. Call from input.</summary>
        public void RequestFire()
        {
            if (!IsOwner || _taser == null || !CanAct()) return;
            if (Time.time < _localNextShotAt) return;

            if (Charge < _taser.ShotCost)
            {
                Depleted?.Invoke();
                return;
            }

            // Predicted locally so the trigger feels instant. The server still decides the outcome.
            _localNextShotAt = Time.time + _taser.Cooldown;
            Fired?.Invoke();

            ServerFire(AimDirection());
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
        void ServerFire(Vector3 aimDirection)
        {
            if (_taser == null || !CanAct()) return;
            if (Time.time < _serverNextShotAt) return;
            if (aimDirection.sqrMagnitude < 0.001f) return;
            if (!AimValidation.IsFacing(transform, aimDirection, _maxAimDeviation)) return;

            float charge = Charge;
            if (charge < _taser.ShotCost) return;

            _serverNextShotAt = Time.time + _taser.Cooldown;

            _chargeAtLastShot.Value = charge - _taser.ShotCost;
            if (TimeManager != null)
                _rechargeStartTick.Value = TimeManager.Tick + TimeManager.TimeToTicks(_taser.RechargeDelay);

            ObserversFire();

            Vector3 direction = aimDirection.normalized;
            Transform origin = _aimOrigin != null ? _aimOrigin : transform;

            // A fat cast, not a thin ray. Hitting a flailing ragdoll with a pinpoint shot is not the
            // kind of difficulty this game is interested in.
            if (!Physics.SphereCast(origin.position, _taser.Radius, direction,
                                    out RaycastHit hit, _taser.Range, _hitMask,
                                    QueryTriggerInteraction.Ignore))
                return;

            Health victim = hit.collider.GetComponentInParent<Health>();
            if (victim == null || victim == _health) return;

            ApplyShock(victim, direction, hit.point);
            ObserversHit(hit.point);
        }

        void ApplyShock(Health victim, Vector3 direction, Vector3 contact)
        {
            DamageInfo info = _taser.Hit.Build(direction, contact, ObjectId);

            bool wasStanding = victim.IsAlive;
            victim.TakeDamage(info);

            // If the shot put them down, Health already broadcast the impulse with the incapacitation.
            // Pushing again here would double the force.
            bool knockedDown = wasStanding && victim.IsIncapacitated;
            if (!knockedDown)
            {
                StunState victimStun = victim.GetComponent<StunState>();
                if (victimStun != null) victimStun.ServerStun(info);
            }

            // The stun puts them on the floor; this is what makes it look like electricity.
            ShockState shock = victim.GetComponent<ShockState>();
            if (shock != null)
                shock.ServerShock(_taser.Hit.StunDuration, _taser.JitterForce,
                                  _taser.JitterInterval, _taser.CameraShake);
        }

        [ObserversRpc(ExcludeOwner = true)]
        void ObserversFire() => Fired?.Invoke();

        [ObserversRpc(RunLocally = true)]
        void ObserversHit(Vector3 contact) => HitLanded?.Invoke(contact);

        void OnDrawGizmosSelected()
        {
            if (_taser == null) return;

            Transform origin = _aimOrigin != null ? _aimOrigin : transform;
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.7f);
            Gizmos.DrawLine(origin.position, origin.position + origin.forward * _taser.Range);
            Gizmos.DrawWireSphere(origin.position + origin.forward * _taser.Range, _taser.Radius);
        }
    }
}
