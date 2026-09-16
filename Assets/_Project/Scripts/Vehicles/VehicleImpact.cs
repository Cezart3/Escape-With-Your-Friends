using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.Vehicles
{
    /// <summary>
    /// #60: a vehicle that hits a person hurts them, stuns them and sends them flying, with all
    /// three scaled by how fast it was going.
    ///
    /// There is no new combat machinery here, and there should not be. A hit from a spearman already
    /// builds a <see cref="DamageInfo"/>, calls <see cref="Health.TakeDamage"/> and then
    /// <see cref="StunState.ServerStun(in DamageInfo)"/>, which ragdolls the victim locally and fans
    /// the impulse to every client through an ObserversRpc. A car is a spearman with a bigger number.
    /// This component's whole job is turning a collision into that number.
    ///
    /// **Impact speed is the vehicle's speed, not the relative speed.** A player is a
    /// CharacterController, so how fast they were walking does not reliably show up in
    /// <c>Collision.relativeVelocity</c> at all — and more usefully, sprinting into a parked car
    /// should do nothing, which the vehicle's own speedometer answers for free.
    ///
    /// Riders and carried bodies never arrive here: <see cref="VehicleRider"/> and
    /// <see cref="Carryable"/> already put a <c>Physics.IgnoreCollision</c> pair between them and
    /// the hull, so PhysX never raises the contact.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class VehicleImpact : NetworkBehaviour
    {
        /// <summary>
        /// Metres per second below which a vehicle is just leaning on somebody. Reversing out of a
        /// parking space at walking pace should be a shove that does not land, or four people
        /// standing around a boat at a jetty would be permanently on the floor.
        /// </summary>
        [SerializeField] float _minSpeed = 3.5f;

        /// <summary>Newton-seconds of launch per metre per second of impact.</summary>
        [SerializeField] float _launchPerSpeed = 55f;

        /// <summary>Health points per metre per second of impact.</summary>
        [SerializeField] float _damagePerSpeed = 3.5f;

        /// <summary>
        /// How much of the launch goes upward. Straight along the bonnet would bowl people over like
        /// skittles; a body wants to go over the roof, which is the joke.
        /// </summary>
        [SerializeField] float _lift = 0.5f;

        [SerializeField] float _stunBase = 1.2f;
        [SerializeField] float _stunPerSpeed = 0.12f;
        [SerializeField] float _maxStun = 5f;

        /// <summary>
        /// Seconds before the same person can be hit by this vehicle again. A ragdoll under a moving
        /// car is a stream of fresh contacts, one per bone, and without this the first person run
        /// over would take the damage of all of them at once.
        /// </summary>
        [SerializeField] float _cooldown = 0.8f;

        /// <summary>
        /// Seconds the vehicle drives straight through the person it just hit.
        ///
        /// Without it the launch is not the launch. The impulse is worth about 15 m/s to a 56kg
        /// ragdoll, which is four metres of air; the first clean run of this suite measured a body
        /// thrown 225 metres and 186 metres *up*, because a 900kg chassis still doing 22 m/s spends
        /// the next second shoving a fresh ragdoll along the ground and PhysX resolves the overlap
        /// by firing it out like a bar of soap. A car that passes through its victim gives the
        /// number in <see cref="_launchPerSpeed"/> back its meaning, and it is the same trick
        /// <see cref="Carryable"/> already uses to stop a carried body fighting its carrier.
        /// </summary>
        [SerializeField] float _passThrough = 1.5f;

        Rigidbody _body;

        /// <summary>
        /// How fast the vehicle was going when the physics step that produced this collision *began*.
        ///
        /// Reading <c>linearVelocity</c> inside <see cref="OnCollisionEnter"/> reads it after the
        /// solver has run, and a 900kg chassis sweeping into a CharacterController comes out of
        /// depenetration with a spike: the first run of this measured a buggy limited to 22 m/s
        /// hitting somebody at 57 m/s, which paid out 200 damage and threw the body a hundred and
        /// fifty metres into the air. FixedUpdate runs before the step, so this is the speed the
        /// collision was actually delivered at.
        /// </summary>
        float _speed;

        /// <summary>Victim ObjectId to the time it may next be hit. Bounded by the player count.</summary>
        readonly Dictionary<int, float> _struck = new();

        // ------------------------------------------------------------------ readback for the suite

        public int Hits { get; private set; }
        public string LastVictim { get; private set; } = "nobody";
        public float LastSpeed { get; private set; }
        public float LastDamage { get; private set; }
        public Vector3 LastImpulse { get; private set; }

        public float MinSpeed => _minSpeed;

        void Awake() => _body = GetComponent<Rigidbody>();

        void FixedUpdate() => _speed = _body.linearVelocity.magnitude;

        void OnCollisionEnter(Collision other)
        {
            if (!IsServerStarted) return;

            var stun = other.collider.GetComponentInParent<StunState>();
            if (stun == null) return;

            float speed = _speed;
            if (speed < _minSpeed) return;

            int victimId = stun.ObjectId;

            if (_struck.TryGetValue(victimId, out float free) && Time.time < free) return;
            _struck[victimId] = Time.time + _cooldown;

            Vector3 contact = other.contactCount > 0 ? other.GetContact(0).point
                                                     : stun.transform.position;

            // Where the vehicle was going, plus a bit of sky. Falls back to the line from the hull
            // to the victim, for the rare hit delivered by a spin rather than by travel.
            Vector3 travel = _body.linearVelocity;
            travel.y = 0f;

            if (travel.sqrMagnitude < 0.01f)
            {
                travel = stun.transform.position - transform.position;
                travel.y = 0f;
            }

            Vector3 push = travel.sqrMagnitude > 0.0001f ? travel.normalized : transform.forward;
            Vector3 impulse = (push + Vector3.up * _lift).normalized * (speed * _launchPerSpeed);

            float damage = speed * _damagePerSpeed;
            float stunFor = Mathf.Min(_maxStun, _stunBase + speed * _stunPerSpeed);

            var info = new DamageInfo(damage, DamageType.Vehicle, impulse, contact, stunFor, ObjectId);

            // Same order as Native.Land: damage first, and only stun on top of it if the victim is
            // still standing afterwards. Going down already lays them out.
            var health = stun.GetComponent<Health>();

            bool wasStanding = health != null && health.IsAlive;
            if (health != null) health.TakeDamage(info);

            if (!(wasStanding && health.IsIncapacitated)) stun.ServerStun(info);

            StartCoroutine(PassThrough(stun));

            Hits++;
            LastVictim = stun.name;
            LastSpeed = speed;
            LastDamage = damage;
            LastImpulse = impulse;

            if (CommandLine.HasFlag("-vehicleLog"))
                Debug.Log($"[VehicleImpact] {name} hit {stun.name} at {speed:F1} m/s: "
                          + $"{damage:F0} damage, {stunFor:F1}s stun, {impulse.magnitude:F0} Ns.");
        }

        /// <summary>See <see cref="_passThrough"/>. WheelColliders are skipped because PhysX does not
        /// accept them here — they are raycasts, not shapes.</summary>
        IEnumerator PassThrough(StunState victim)
        {
            Collider[] mine = GetComponentsInChildren<Collider>(true)
                              .Where(c => c != null && c is not WheelCollider).ToArray();

            Collider[] theirs = victim.GetComponentsInChildren<Collider>(true)
                                .Where(c => c != null).ToArray();

            Pair(mine, theirs, true);

            yield return new WaitForSeconds(_passThrough);

            Pair(mine, theirs, false);
        }

        static void Pair(Collider[] mine, Collider[] theirs, bool ignore)
        {
            foreach (Collider a in mine)
            {
                if (a == null) continue;

                foreach (Collider b in theirs)
                {
                    if (b == null) continue;

                    Physics.IgnoreCollision(a, b, ignore);
                }
            }
        }
    }
}
