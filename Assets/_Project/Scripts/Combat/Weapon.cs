using System;
using System.Collections;
using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Player;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Combat
{
    /// <summary>
    /// The one thing on a player that hurts other players. Fists, machete, pistol - one component.
    ///
    /// This replaced <c>MeleeAttack</c> rather than sitting next to it. #49's acceptance is that a new
    /// weapon is one asset plus a prefab and no new code, and a second attack component would have
    /// made "no new code" false the first time somebody added a crossbow: there would have been a
    /// third component to write, a third equip path, and a third place to forget the aim check. There
    /// is exactly one branch on <see cref="WeaponDef.Kind"/>, in <see cref="ServerResolve"/>, and it
    /// picks between a cone and a ray. Everything on either side of that branch is shared.
    ///
    /// **The client sends an aim direction and nothing else.** Range, cone, spread, damage, cooldown
    /// and what was actually standing there all come from the server's copy of the definition, so
    /// editing a local asset buys a cheater nothing. The direction is checked against the character's
    /// facing, because otherwise a client could shoot somebody standing behind it.
    ///
    /// **Holding it is equipping it.** The server reads the selected hotbar slot, asks the catalog
    /// which weapon that item is, and writes the index into a <see cref="SyncVar{T}"/>. Nobody calls
    /// an equip function; there is no equip function to call per weapon. An item that is not a weapon
    /// falls back to fists, so you can punch somebody while holding a fish.
    /// </summary>
    public class Weapon : NetworkBehaviour
    {
        [Header("References")]
        [Tooltip("Origin of the attack - normally the camera or head, so you hit what you look at.")]
        [SerializeField] Transform _aimOrigin;

        [SerializeField] WeaponCatalog _catalog;

        [SerializeField] Inventory _inventory;

        [SerializeField] LayerMask _hitMask = ~0;

        [Header("Anti-cheat")]
        [Tooltip("Degrees the requested aim may deviate from where the character is actually facing.")]
        [Range(30f, 180f)]
        [SerializeField] float _maxAimDeviation = 100f;

        /// <summary>What is in the hand, as a catalog index. 0 until the server has decided.</summary>
        readonly SyncVar<ushort> _equippedIndex = new();

        /// <summary>Rounds in the equipped weapon's magazine. Mirrors the server's book-keeping.</summary>
        readonly SyncVar<int> _loaded = new();

        /// <summary>True while a reload is running. Firing during one is refused rather than queued.</summary>
        readonly SyncVar<bool> _reloading = new();

        /// <summary>
        /// Rounds left in every gun this player has loaded, on the server only.
        ///
        /// Per weapon rather than one counter, because otherwise swapping to the shotgun and back
        /// would refill the pistol, and the shop sells ammunition. Guns start empty: the magazine you
        /// are carrying is one you paid for and put in yourself.
        /// </summary>
        readonly Dictionary<WeaponDef, int> _magazines = new();

        Health _health;
        StunState _stun;
        BuffState _buffs;

        readonly Collider[] _overlap = new Collider[32];
        readonly List<Health> _hitThisAttack = new();

        float _serverNextAttackAt;
        float _localNextAttackAt;
        int _lastHitCount;

        /// <summary>Raised on every peer when an attack starts, for animation and sound.</summary>
        public event Action<WeaponDef> Attacked;

        /// <summary>Raised on every peer when an attack connects, at the contact point.</summary>
        public event Action<Vector3> HitLanded;

        /// <summary>
        /// Raised on every peer for a shot that was fired, with where it started and where each ray
        /// ended. Tracers, muzzle flashes and recoil hang off this.
        /// </summary>
        public event Action<Vector3, Vector3[]> Fired;

        /// <summary>Raised on every peer when the trigger was pulled on an empty magazine.</summary>
        public event Action<WeaponDef> DryFired;

        /// <summary>Raised on every peer when a reload starts, and again when it ends.</summary>
        public event Action<WeaponDef, bool> Reloading;

        /// <summary>
        /// What this player is holding. Falls back to fists, which is a real asset with real numbers
        /// rather than a hard-coded ten damage.
        /// </summary>
        public WeaponDef Equipped
        {
            get
            {
                WeaponCatalog catalog = _catalog != null ? _catalog : WeaponCatalog.Active;
                if (catalog == null) return null;

                return catalog.At(_equippedIndex.Value) ?? catalog.Fists;
            }
        }

        /// <summary>Rounds in the equipped magazine. Zero for anything that does not take ammunition.</summary>
        public int Loaded => _loaded.Value;

        /// <summary>True while a reload is in progress, on every peer.</summary>
        public bool IsReloading => _reloading.Value;

        /// <summary>Ammunition in the bag for what is equipped. What the HUD shows after the slash.</summary>
        public int Reserve
        {
            get
            {
                WeaponDef weapon = Equipped;
                if (weapon == null || weapon.Ammo == null || _inventory == null) return 0;

                return _inventory.CountOf(weapon.Ammo);
            }
        }

        void Awake()
        {
            _health = GetComponent<Health>();
            _stun = GetComponent<StunState>();
            _buffs = GetComponent<BuffState>();
            if (_inventory == null) _inventory = GetComponent<Inventory>();

            WeaponCatalog.Use(_catalog);
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            ServerRefreshEquipped();
        }

        void Update()
        {
            if (IsServerStarted) ServerRefreshEquipped();
        }

        /// <summary>
        /// Reads the selected slot and writes what it means. Cheap enough to do every frame - it is a
        /// dictionary lookup and a comparison, and the SyncVar only sends when the answer changes.
        ///
        /// Polling rather than subscribing to <c>Inventory.Changed</c> on purpose: the equipped weapon
        /// is a function of the bag's state, and a function is more robust than a notification. There
        /// is no way to be holding a machete because somebody forgot to raise an event.
        /// </summary>
        [Server]
        void ServerRefreshEquipped()
        {
            WeaponCatalog catalog = _catalog != null ? _catalog : WeaponCatalog.Active;
            if (catalog == null) return;

            ItemDef held = _inventory != null ? _inventory.Selected.Def : null;
            WeaponDef weapon = catalog.ForItem(held) ?? catalog.Fists;
            ushort index = catalog.IndexOf(weapon);

            if (_equippedIndex.Value != index) _equippedIndex.Value = index;

            int rounds = ServerRounds(weapon);
            if (_loaded.Value != rounds) _loaded.Value = rounds;
        }

        // ---------------------------------------------------------------- ammunition

        [Server]
        int ServerRounds(WeaponDef weapon)
            => weapon != null && _magazines.TryGetValue(weapon, out int rounds) ? rounds : 0;

        [Server]
        void ServerSetRounds(WeaponDef weapon, int rounds)
        {
            if (weapon == null) return;

            _magazines[weapon] = Mathf.Clamp(rounds, 0, weapon.Magazine);
            if (Equipped == weapon) _loaded.Value = _magazines[weapon];
        }

        /// <summary>
        /// Takes one round for a shot, or refuses the shot.
        ///
        /// One round per *shot*, never per pellet - a shotgun blast is eight rays and one shell, and
        /// the day somebody writes a twenty-pellet weapon is not the day the ammunition economy
        /// should change. Anything with no magazine at all - every melee weapon - is waved through.
        /// </summary>
        [Server]
        bool ServerSpendRound(WeaponDef weapon)
        {
            if (weapon.Magazine <= 0) return true;
            if (_reloading.Value) return false;

            int rounds = ServerRounds(weapon);
            if (rounds <= 0)
            {
                ObserversDryFired();
                return false;
            }

            ServerSetRounds(weapon, rounds - 1);
            return true;
        }

        /// <summary>Owner-side entry point. Call from input.</summary>
        public void RequestReload()
        {
            if (!IsOwner) return;

            ServerReloadRpc();
        }

        [ServerRpc]
        void ServerReloadRpc() => ServerReload();

        /// <summary>
        /// Starts a reload if there is anything to reload with. Returns whether one started, which is
        /// what the harness reads; a client learns the same thing from <see cref="IsReloading"/>.
        ///
        /// Rounds come out of the bag and go into the magazine, so ammunition is an item with a
        /// weight and a price the whole way through rather than a counter that appears on the HUD.
        /// </summary>
        [Server]
        public bool ServerReload()
        {
            WeaponDef weapon = Equipped;

            if (weapon == null || weapon.Magazine <= 0 || weapon.Ammo == null) return false;
            if (_reloading.Value || !CanAct()) return false;
            if (ServerRounds(weapon) >= weapon.Magazine) return false;
            if (_inventory == null || _inventory.CountOf(weapon.Ammo) <= 0) return false;

            StartCoroutine(RunReload(weapon));
            return true;
        }

        IEnumerator RunReload(WeaponDef weapon)
        {
            _reloading.Value = true;
            ObserversReloading(true);

            yield return new WaitForSeconds(weapon.ReloadSeconds);

            _reloading.Value = false;
            ObserversReloading(false);

            // Swapping weapons mid-reload cancels it. The rounds were never taken out of the bag, so
            // there is nothing to give back and no way to duplicate anything by swapping quickly.
            if (Equipped != weapon || !CanAct()) yield break;

            int room = weapon.Magazine - ServerRounds(weapon);
            if (room <= 0) yield break;

            int taken = _inventory.Remove(weapon.Ammo, room);
            if (taken > 0) ServerSetRounds(weapon, ServerRounds(weapon) + taken);

            if (CommandLine.HasFlag("-weaponLog"))
                Debug.Log($"[Weapon] {ObjectId} reloaded {weapon.Id} with {taken} round(s), "
                          + $"magazine {_loaded.Value}/{weapon.Magazine}, "
                          + $"{_inventory.CountOf(weapon.Ammo)} left in the bag.");
        }

        /// <summary>Server only. Loads a magazine outright, for tests and for anything that grants gear.</summary>
        [Server]
        public void ServerLoad(WeaponDef weapon, int rounds) => ServerSetRounds(weapon, rounds);

        /// <summary>
        /// Moves what is in one weapon's magazine into another's, for #52's upgrades. Returns how
        /// many rounds made the trip.
        ///
        /// Rounds in a magazine are not in the bag, so an upgrade would otherwise destroy them - and
        /// losing half a magazine to a bench visit would make upgrading a thing you do carefully
        /// rather than a thing you do. Different calibres still lose them: a pistol magazine does not
        /// fit a shotgun, and the alternative is inventing rounds of a type nobody paid for. The old
        /// weapon is emptied either way, because it is the weapon you no longer have.
        /// </summary>
        [Server]
        public int ServerCarryMagazine(WeaponDef from, WeaponDef to)
        {
            if (from == null || to == null || from == to) return 0;

            int rounds = ServerRounds(from);
            ServerSetRounds(from, 0);

            if (rounds <= 0 || from.Ammo == null || from.Ammo != to.Ammo) return 0;

            int room = Mathf.Max(0, to.Magazine - ServerRounds(to));
            int moved = Mathf.Min(rounds, room);
            if (moved <= 0) return 0;

            ServerSetRounds(to, ServerRounds(to) + moved);
            return moved;
        }

        /// <summary>Owner-side entry point. Call from input.</summary>
        public void RequestAttack()
        {
            if (!IsOwner || !CanAct()) return;

            WeaponDef weapon = Equipped;
            if (weapon == null || Time.time < _localNextAttackAt) return;

            // Predicted locally so the swing feels instant; the server still decides what it touched.
            _localNextAttackAt = Time.time + weapon.Cooldown;
            Attacked?.Invoke(weapon);

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
            WeaponDef weapon = Equipped;
            if (weapon == null || !CanAct()) return;
            if (Time.time < _serverNextAttackAt) return;

            if (aimDirection.sqrMagnitude < 0.001f) return;
            if (!AimValidation.IsFacing(transform, aimDirection, _maxAimDeviation)) return;

            Vector3 direction = aimDirection.normalized;

            // The cooldown is taken before the magazine is checked, so a client that spams the RPC at
            // an empty gun is rate-limited by the same clock as one that is actually shooting.
            _serverNextAttackAt = Time.time + weapon.Cooldown;

            if (!ServerSpendRound(weapon)) return;

            ObserversAttack();

            if (weapon.Windup > 0f) StartCoroutine(ResolveAfterWindup(weapon, direction));
            else ServerResolve(weapon, direction);
        }

        IEnumerator ResolveAfterWindup(WeaponDef weapon, Vector3 direction)
        {
            yield return new WaitForSeconds(weapon.Windup);

            // The attack was already committed, but dying mid-windup cancels it.
            if (CanAct()) ServerResolve(weapon, direction);
        }

        /// <summary>The whole difference between a bat and a pistol, in one switch.</summary>
        [Server]
        void ServerResolve(WeaponDef weapon, Vector3 direction)
        {
            _hitThisAttack.Clear();
            _lastHitCount = 0;

            switch (weapon.Kind)
            {
                case WeaponKind.Hitscan:
                    ResolveHitscan(weapon, direction);
                    return;

                default:
                    ResolveMelee(weapon, direction);
                    return;
            }
        }

        // ---------------------------------------------------------------- melee

        void ResolveMelee(WeaponDef weapon, Vector3 direction)
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

            for (int i = 0; i < count && _hitThisAttack.Count < weapon.MaxTargets; i++)
            {
                Collider hit = _overlap[i];
                if (hit == null) continue;

                Health victim = hit.GetComponentInParent<Health>();
                if (victim == null || victim == _health) continue;
                if (_hitThisAttack.Contains(victim)) continue;

                Vector3 contact = hit.ClosestPoint(originPosition);
                Vector3 toContact = contact - originPosition;

                if (toContact.magnitude > reach + weapon.Radius) continue;
                if (toContact.sqrMagnitude > 0.001f
                    && Vector3.Angle(toContact, direction) > weapon.ConeHalfAngle) continue;

                _hitThisAttack.Add(victim);
                _lastHitCount++;
                ApplyHit(weapon, victim, direction, contact);
            }
        }

        // ---------------------------------------------------------------- ranged

        /// <summary>
        /// One ray per pellet, scattered inside the spread cone, resolved the instant the trigger is
        /// pulled. Hitscan rather than a projectile because a projectile is a networked object with a
        /// position to reconcile, and none of these guns are slow enough for anybody to see the
        /// difference. Where the tracer is drawn is a lie the client tells; where the damage landed is
        /// this.
        ///
        /// The spread is deterministic per shot only in the sense that the server rolls it: the client
        /// never sends a scatter, so a modified client cannot ask for a shotgun that fires straight.
        /// </summary>
        void ResolveHitscan(WeaponDef weapon, Vector3 direction)
        {
            Transform origin = _aimOrigin != null ? _aimOrigin : transform;
            Vector3 originPosition = origin.position;

            int pellets = weapon.Pellets;
            var ends = new Vector3[pellets];

            for (int i = 0; i < pellets; i++)
            {
                Vector3 shot = Scatter(direction, weapon.Spread + Wobble());
                ends[i] = originPosition + shot * weapon.Range;

                if (!Physics.Raycast(originPosition, shot, out RaycastHit hit, weapon.Range,
                                     _hitMask, QueryTriggerInteraction.Ignore))
                    continue;

                ends[i] = hit.point;

                Health victim = hit.collider.GetComponentInParent<Health>();
                if (victim == null || victim == _health) continue;

                // No dedupe here, unlike the swing. Every pellet of a shotgun blast that lands on the
                // same person is meant to hurt - that is what makes it a shotgun. The melee list
                // exists to stop one wide swing counting a ragdoll's eleven colliders as eleven hits;
                // a pellet already picked exactly one collider.
                _lastHitCount++;
                ApplyHit(weapon, victim, shot, hit.point);
            }

            ObserversFired(originPosition, ends);
        }

        /// <summary>
        /// Degrees of scatter this shooter is adding on their own, from #66's drink. Added to the
        /// weapon's own cone rather than multiplying it, because a pistol's spread is zero and
        /// "much harder to aim" has to mean something while you are holding one.
        ///
        /// Server-side, like every other number in this file: the client sends a direction, and what
        /// the drink does to it is not theirs to leave out.
        /// </summary>
        float Wobble() => _buffs != null ? _buffs.AimWobble : 0f;

        /// <summary>A direction nudged inside a cone. Uniform enough for a gun, cheap enough for eight.</summary>
        static Vector3 Scatter(Vector3 direction, float spreadDegrees)
        {
            if (spreadDegrees <= 0f) return direction;

            Vector2 disc = UnityEngine.Random.insideUnitCircle * spreadDegrees;
            return Quaternion.Euler(disc.y, disc.x, 0f) * direction;
        }

        // ---------------------------------------------------------------- damage

        void ApplyHit(WeaponDef weapon, Health victim, Vector3 direction, Vector3 contact)
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

            if (CommandLine.HasFlag("-weaponLog"))
                Debug.Log($"[Weapon] {ObjectId} hit {victim.ObjectId} with {weapon.Id} for "
                          + $"{info.Amount}, victim now {victim.Current:F0} hp, alive {victim.IsAlive}.");

            ObserversHit(contact);
        }

        [ObserversRpc(ExcludeOwner = true)]
        void ObserversAttack() => Attacked?.Invoke(Equipped);

        [ObserversRpc(RunLocally = true)]
        void ObserversHit(Vector3 contact) => HitLanded?.Invoke(contact);

        [ObserversRpc(RunLocally = true)]
        void ObserversFired(Vector3 origin, Vector3[] ends) => Fired?.Invoke(origin, ends);

        [ObserversRpc(RunLocally = true)]
        void ObserversDryFired() => DryFired?.Invoke(Equipped);

        [ObserversRpc(RunLocally = true)]
        void ObserversReloading(bool started) => Reloading?.Invoke(Equipped, started);

        // ---------------------------------------------------------------- the harness's door

        /// <summary>
        /// Server-side attack with no client and no cooldown, for headless tests. Not a back door for
        /// gameplay: nothing calls it except the harness, and it still resolves through the same
        /// <see cref="ServerResolve"/> everything else uses, so what it proves is what players get.
        ///
        /// The cooldown and the aim check are skipped; **the magazine is not.** A door that handed out
        /// free ammunition would make every test of the ammunition economy a test of the door.
        /// </summary>
        [Server]
        public int ServerAttackNow(Vector3 direction)
        {
            WeaponDef weapon = Equipped;
            if (weapon == null || direction.sqrMagnitude < 0.001f) return 0;
            if (!ServerSpendRound(weapon)) return 0;

            ServerResolve(weapon, direction.normalized);
            return _lastHitCount;
        }

        /// <summary>Bake time only.</summary>
        public void Configure(WeaponCatalog catalog, Inventory inventory, Transform aimOrigin)
        {
            _catalog = catalog;
            _inventory = inventory;
            _aimOrigin = aimOrigin;
        }

        void OnDrawGizmosSelected()
        {
            WeaponDef weapon = Equipped;
            if (weapon == null || weapon.Kind != WeaponKind.Melee) return;

            Transform origin = _aimOrigin != null ? _aimOrigin : transform;
            Gizmos.color = new Color(1f, 0.4f, 0.2f, 0.6f);
            Gizmos.DrawWireSphere(origin.position + origin.forward * (weapon.Range * 0.5f),
                                  weapon.Range * 0.5f + weapon.Radius);
        }
    }
}
