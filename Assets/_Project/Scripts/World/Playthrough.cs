using System.Collections;
using System.IO;
using System.Linq;
using EscapeWithYourFriends.AI;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.Vehicles;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// <c>-playthrough &lt;folder&gt;</c>: a bot plays the first island the way a person would, in a
    /// real window, and screenshots every step with the HUD on. It holds the controls through
    /// <see cref="PlayerInputReader.BotDriven"/> - stick, look, the keys - so everything it does goes
    /// through the same motor, interactor, hotbar and weapon a player's hands do. Nothing is called
    /// around the game except the money (F5's cheat) and a teleport when it is stuck, which it logs.
    ///
    /// Spawn, the wreck, pick up the pistol and rounds, load, shoot a boar, the trader, four boat
    /// parts, fit them, board, sail off the edge, the second island. Each step is a PASS or a FAIL;
    /// a FAIL does not stop the run, because the point is the list of what is wrong.
    /// </summary>
    public class Playthrough : MonoBehaviour
    {
        static bool _started;
        string _folder;
        int _passed, _failed, _shot;
        PlayerInputReader _input;
        PlayerMotor _motor;
        Inventory _bag;

        internal static void Begin()
        {
            string folder = CommandLine.GetString("-playthrough", null);
            if (_started || string.IsNullOrEmpty(folder)) return;
            _started = true;

            var go = new GameObject("Playthrough");
            DontDestroyOnLoad(go);
            var bot = go.AddComponent<Playthrough>();
            bot._folder = folder;
            bot.StartCoroutine(bot.Run());
        }

        float _beatSeen = -1f;
        bool _beatShot;

        /// <summary>
        /// #197. The bot watches a story beat for two and a half seconds - long enough for a screenshot
        /// of it - and then skips, like an impatient player. Its eye is Camera.main, and a camera
        /// orbiting a wreck is no use for walking.
        /// </summary>
        void Update()
        {
            if (!StoryBeat.Playing) { _beatSeen = -1f; return; }
            if (_beatSeen < 0f) { _beatSeen = Time.time; return; }
            if (Time.time - _beatSeen < 2.5f) return;

            // Shot this frame, skipped the next: a beat destroyed in the same Update never draws
            // its letterbox into the capture.
            if (!_beatShot)
            {
                _beatShot = true;
                ScreenCapture.CaptureScreenshot(Path.Combine(_folder, $"beat_{StoryBeat.Played[StoryBeat.Played.Count - 1].Replace(':', '_')}.png"));
                return;
            }

            _beatShot = false;
            StoryBeat.Skip();
        }

        void Check(string what, bool ok)
        {
            if (ok) _passed++; else _failed++;
            Debug.Log($"[Playthrough] {(ok ? "PASS" : "FAIL")} {what}");
        }

        bool _hitShot;

        IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            string file = Path.Combine(_folder, $"{++_shot:00}_{name}.png");
            ScreenCapture.CaptureScreenshot(file);
            string aimed = _motor != null ? _motor.GetComponent<PlayerInteractor>()?.Aimed?.Prompt : null;
            Debug.Log($"[Playthrough] shot {file} - objective \"{Objective.Text}\", crosshair \"{aimed}\", "
                      + $"at {(_motor != null ? _motor.transform.position : Vector3.zero)}");
            yield return new WaitForSeconds(0.3f);
        }

        Vector3 Eye => Camera.main != null ? Camera.main.transform.position : _motor.transform.position + Vector3.up * 1.6f;

        void Look(Vector3 at)
        {
            Vector3 d = at - Eye;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
            _input.BotLook(yaw, pitch);
        }

        /// <summary>
        /// Walks along a navmesh path, straight at it where there is none. Jumps when it has not
        /// gained a metre in three seconds, and after twelve logs STUCK and teleports the rest of
        /// the way - a stuck spot is a finding, not a reason to end the run.
        /// </summary>
        IEnumerator Walk(string where, Vector3 to, float within, float timeout = 120f)
        {
            var path = new NavMeshPath();
            float start = Time.time, lastProgress = Time.time, repath = 0f, best = float.MaxValue;
            Vector3 next = to;
            _input.BotSprint = true;

            float nextThreat = 0f;
            while (Flat(_motor.transform.position, to) > within)
            {
                if (Me.IsIncapacitated)
                {
                    _input.BotMove = Vector2.zero;
                    yield return WaitOutDown();
                    lastProgress = Time.time; best = float.MaxValue; start = Time.time;
                }
                if (Time.time > nextThreat)
                {
                    nextThreat = Time.time + 0.5f;
                    Component threat = PlanePart.HeldBy(_motor.NetworkObject) == null && Count("pistol") > 0
                                         && (_motor.GetComponent<Weapon>().Loaded > 0 || Count("pistol_ammo") > 0) ? Threat() : null;
                    if (threat != null)
                    {
                        Debug.Log($"[Playthrough] a {threat.name} is after me on the way to {where}; shooting it.");
                        yield return Fight(threat, false);
                        if (threat is Native) yield return Scavenge(15f);
                        lastProgress = Time.time;
                    }
                }
                Vector3 here = _motor.transform.position;
                if (Time.time > repath)
                {
                    repath = Time.time + 1f;
                    next = to;
                    if (NavMesh.SamplePosition(here, out NavMeshHit a, 4f, NavMesh.AllAreas)
                        && NavMesh.SamplePosition(to, out NavMeshHit b, 8f, NavMesh.AllAreas)
                        && NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, path)
                        && path.corners.Length > 1)
                        next = path.corners.FirstOrDefault(c => Flat(c, here) > 1.5f);
                    if (next == Vector3.zero) next = to;
                }

                Vector3 d = next - here;
                _input.BotLook(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 5f);
                _input.BotMove = Vector2.up;

                float left = Flat(here, to);
                if (left < best - 1f) { best = left; lastProgress = Time.time; }
                // Trees are not on the navmesh. Stalled: jump, and slide sideways, alternating sides
                // every two seconds, which is what a person does against a trunk.
                float stalled = Time.time - lastProgress;
                if (stalled > 2f)
                {
                    _input.BotPress("jump");
                    _input.BotMove = new Vector2(((int)(stalled / 2f) % 2 == 0) ? 1f : -1f, 0.3f);
                }
                if (stalled > 16f || Time.time - start > timeout)
                {
                    Debug.Log($"[Playthrough] STUCK walking to {where} at {here}, {left:0}m short. Teleporting.");
                    yield return Shot("stuck_" + where);
                    // Beside it on our side, not on top: landing inside a part or a hull shoves the
                    // capsule out wherever physics likes.
                    Vector3 back = here - to; back.y = 0f;
                    Vector3 spot = to + back.normalized * Mathf.Max(1f, within * 0.7f);
                    Terrain land = Terrain.activeTerrain;
                    if (land != null) spot.y = land.SampleHeight(spot) + land.transform.position.y;
                    _motor.ServerTeleport(spot + Vector3.up * 0.2f, _input.Yaw);
                    yield return new WaitForSeconds(1f);
                    break;
                }
                yield return null;
            }
            _input.BotMove = Vector2.zero;
            _input.BotSprint = false;
            Debug.Log($"[Playthrough] reached {where} in {Time.time - start:0}s.");
        }

        /// <summary>Shoots until it is dead or twenty trigger pulls are spent.</summary>
        IEnumerator Fight(Component prey, bool photo)
        {
            Weapon weapon = _motor.GetComponent<Weapon>();
            Health health = prey.GetComponent<Health>();
            if (weapon.Equipped == null) yield return Hold("pistol");
            float hpBefore = health.Current, near = Flat(prey.transform.position, _motor.transform.position);
            int shots = 0;
            for (int i = 0; i < 20 && prey != null && !health.IsDead && !Me.IsIncapacitated; i++)
            {
                // A trunk in the way is the bot's problem, not the gun's: close in until the eye
                // sees the animal, like a person stepping round the tree.
                for (float t = 0f; t < 8f && !Sees(prey) && Flat(prey.transform.position, _motor.transform.position) > 3f; t += Time.deltaTime)
                {
                    Vector3 d = prey.transform.position - _motor.transform.position;
                    _input.BotLook(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 5f);
                    _input.BotMove = Vector2.up;
                    yield return null;
                }
                _input.BotMove = Vector2.zero;
                if (weapon.Loaded == 0) { _input.BotPress("reload"); yield return new WaitForSeconds(2.5f); }
                Look(Chest(prey));
                yield return new WaitForSeconds(0.15f);
                if (i == 0 && photo) yield return Shot("aim_animal");
                bool landed = false;
                void OnLanded(Vector3 c) => landed = true;
                weapon.HitLanded += OnLanded;
                _input.BotPress("attack");
                shots++;
                // The tracer lives 90 ms and arrives a network tick after the trigger: caught on the
                // frame the shot comes back, with the flash (#207).
                if (i == 0 && photo)
                {
                    bool fired = false;
                    void OnFired(Vector3 o, Vector3[] e) => fired = true;
                    weapon.Fired += OnFired;
                    for (float t = 0f; !fired && t < 0.5f; t += Time.deltaTime) yield return null;
                    weapon.Fired -= OnFired;
                    yield return Shot("firing");
                }
                // The first round that connects, photographed a beat later: the blood and the
                // hitmarker (#207).
                for (float t = 0f; !landed && t < 0.4f; t += Time.deltaTime) yield return null;
                weapon.HitLanded -= OnLanded;
                if (landed && photo && !_hitShot) { _hitShot = true; yield return new WaitForSeconds(0.06f); yield return Shot("hit"); }
                yield return new WaitForSeconds(0.4f);
            }
            // Reload between fights, not at the start of the next one: 2.5 s with a headhunter
            // closing at 7 m/s is the fight lost (Island2 with natives, bot downed on an empty gun).
            if (!Me.IsIncapacitated && weapon.Equipped != null && weapon.Loaded < weapon.Equipped.Magazine
                && Count("pistol_ammo") > 0)
            {
                _input.BotPress("reload");
                yield return new WaitForSeconds(2.5f);
            }
            Debug.Log($"[Playthrough] fight with {prey.name}: {shots} shot(s) from {near:0}m, {hpBefore:0} -> "
                      + $"{(health != null ? health.Current : 0f):0} hp, {weapon.Loaded} loaded, {Count("pistol_ammo")} spare.");
        }

        bool _scavenging;

        /// <summary>
        /// Picks up the pistol rounds lying within <paramref name="radius"/>, as a player would after
        /// a fight: natives drop them, and Island2 has no shop. Without this the bot reached the
        /// village empty and died there on a loop.
        /// </summary>
        IEnumerator Scavenge(float radius)
        {
            if (_scavenging || PlanePart.HeldBy(_motor.NetworkObject) != null) yield break;
            _scavenging = true;
            for (int n = 0; n < 4 && !Me.IsIncapacitated; n++)
            {
                WorldItem item = FindObjectsByType<WorldItem>(FindObjectsSortMode.None)
                    .Where(i => i.Stack.Def != null && i.Stack.Def.Id == "pistol_ammo"
                                && Flat(i.transform.position, _motor.transform.position) < radius)
                    .OrderBy(i => Flat(i.transform.position, _motor.transform.position)).FirstOrDefault();
                if (item == null) break;
                yield return Walk("ammo", item.transform.position, 1.8f, 30f);
                int before = Count("pistol_ammo");
                for (int t = 0; t < 6 && item != null && Count("pistol_ammo") == before; t++)
                {
                    Look(item.transform.position);
                    yield return new WaitForSeconds(0.2f);
                    _input.BotPress("interact");
                    yield return new WaitForSeconds(0.5f);
                }
                string seen = Camera.main != null && Physics.Raycast(Camera.main.transform.position, Camera.main.transform.forward,
                                  out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore)
                    ? $"{hit.collider.name} under {hit.collider.transform.root.name}" : "nothing";
                Debug.Log($"[Playthrough] scavenged pistol rounds: {before} -> {Count("pistol_ammo")} (looking at {seen}, "
                          + $"crosshair \"{_motor.GetComponent<PlayerInteractor>().Aimed?.Prompt}\").");
                if (Count("pistol_ammo") == before) break;
            }
            _scavenging = false;
        }

        Health Me => _motor.GetComponent<Health>();

        /// <summary>Whatever animal or native has picked this player as its target, if it is close.</summary>
        Component Threat()
        {
            Component animal = FindObjectsByType<Animal>(FindObjectsSortMode.None)
                .FirstOrDefault(a => a.Target == Me && a.Def != null && a.Def.Temperament != Temperament.Skittish && a.GetComponent<Health>() is Health h && !h.IsDead
                                     && Vector3.Distance(a.transform.position, _motor.transform.position) < 20f);
            if (animal != null) return animal;
            return FindObjectsByType<Native>(FindObjectsSortMode.None)
                .FirstOrDefault(n => n.Target == Me && n.GetComponent<Health>() is Health h && !h.IsDead
                                     && Vector3.Distance(n.transform.position, _motor.transform.position) < 25f);
        }

        static Vector3 Chest(Component c) => c.transform.position + Vector3.up * (c is Native ? 1.2f : 0.5f);

        /// <summary>Down with nobody to lift you: log it and wait for whatever the game does next.</summary>
        IEnumerator WaitOutDown()
        {
            Debug.Log($"[Playthrough] DOWNED at {_motor.transform.position}; waiting to get up or respawn.");
            yield return Shot("downed");
            float t = Time.time;
            while (Me.IsIncapacitated && Time.time - t < 120f) yield return null;
            Debug.Log($"[Playthrough] back up after {Time.time - t:0}s at {_motor.transform.position}.");
            yield return new WaitForSeconds(2f);
        }

        bool Sees(Component prey)
        {
            return Physics.Linecast(Eye, Chest(prey), out RaycastHit hit, ~0, QueryTriggerInteraction.Ignore)
                && hit.collider.GetComponentInParent<Health>() == prey.GetComponent<Health>();
        }

        static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        int SlotOf(string id)
        {
            for (int i = 0; i < _bag.SlotCount; i++)
                if (_bag[i].Def != null && _bag[i].Def.Id == id) return i;
            return -1;
        }

        int Count(string id) => ItemCatalog.Active?.Find(id) is ItemDef def ? _bag.CountOf(def) : 0;

        /// <summary>Puts the stack in hand: hotbar key if it is on the hotbar, else moved there first.</summary>
        IEnumerator Hold(string id)
        {
            int slot = SlotOf(id);
            if (slot < 0) yield break;
            if (slot >= Inventory.HotbarSlots)
            {
                _bag.ServerMove(slot, 0);
                slot = 0;
                yield return new WaitForSeconds(0.3f);
            }
            _input.BotHotbar(slot);
            yield return new WaitForSeconds(0.5f);
        }

        IEnumerator Run()
        {
            Directory.CreateDirectory(_folder);
            yield return new WaitForSeconds(14f);

            _motor = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None).FirstOrDefault(m => m.IsOwner);
            Check("there is a player to drive", _motor != null);
            if (_motor == null) { Finish(); yield break; }
            _input = _motor.GetComponent<PlayerInputReader>();
            _bag = _motor.GetComponent<Inventory>();
            _input.BotDriven = true;

            // -scene island2 starts at the second half; a finished plane on Island starts at the rescue.
            if (GameSceneLoader.Current == "Island" && !PlaneAssembly.Owned) yield return FirstIsland();
            if (GameSceneLoader.Current == "Island2") yield return SecondIsland();
            if (GameSceneLoader.Current == "Island" && PlaneAssembly.Owned) yield return Rescue();
            Finish();
        }

        IEnumerator FirstIsland()
        {
            yield return Shot("spawn");
            Check($"the first objective points at the wreck (\"{Objective.Text}\")", Objective.Text != null && Objective.Text.Contains("wreck"));

            // ------------------------------------------------ the wreck, and what lies at it
            Landmark wreck = Landmark.All.Find(l => l.Id == "wreck");
            Check("there is a wreck", wreck != null);
            if (wreck != null)
            {
                yield return Walk("wreck", wreck.transform.position, 8f);
                Look(wreck.transform.position + Vector3.up);
                yield return Shot("wreck");

                foreach (string id in new[] { "pistol", "pistol_ammo" })
                {
                    WorldItem item = FindObjectsByType<WorldItem>(FindObjectsSortMode.None)
                        .Where(i => i.Stack.Def != null && i.Stack.Def.Id == id)
                        .OrderBy(i => Flat(i.transform.position, _motor.transform.position)).FirstOrDefault();
                    if (item == null) { Check($"a {id} lies at the wreck", false); continue; }

                    yield return Walk(id, item.transform.position, 1.8f, 40f);
                    int before = Count(id);
                    for (int tries = 0; tries < 6 && item != null && Count(id) == before; tries++)
                    {
                        Look(item.transform.position);
                        yield return new WaitForSeconds(0.2f);
                        if (tries == 0) yield return Shot("aim_" + id);
                        _input.BotPress("interact");
                        yield return new WaitForSeconds(0.5f);
                    }
                    Check($"picked up the {id} with E (had {before}, now {Count(id)})", Count(id) > before);
                }
            }

            // ------------------------------------------------ the gun in hand, loaded
            Weapon weapon = _motor.GetComponent<Weapon>();
            yield return Hold("pistol");
            Check($"the pistol is in hand (equipped {weapon?.Equipped?.name})", weapon != null && weapon.Equipped != null);
            _input.BotPress("reload");
            yield return new WaitForSeconds(3f);
            Check($"R loads it ({weapon?.Loaded} in the magazine)", weapon != null && weapon.Loaded > 0);
            yield return Shot("pistol_in_hand");

            // ------------------------------------------------ an animal to shoot
            Animal prey = FindObjectsByType<Animal>(FindObjectsSortMode.None)
                .Where(a => a.GetComponent<Health>() is Health h && !h.IsDead)
                .OrderBy(a => (a.Def != null && a.Def.Id.Contains("oar") ? 0f : 1000f)
                              + Flat(a.transform.position, _motor.transform.position)).FirstOrDefault();
            Check("there is an animal to hunt", prey != null);
            if (prey != null)
            {
                Health health = prey.GetComponent<Health>();
                float was = health.Current;
                Debug.Log($"[Playthrough] hunting a {prey.Def?.Id} {Flat(prey.transform.position, _motor.transform.position):0}m off.");
                yield return Walk(prey.Def?.Id ?? "animal", prey.transform.position, 12f, 60f);
                yield return Fight(prey, true);
                Check($"shooting hurts it ({was:0} to {(health != null ? health.Current : 0):0} hp, dead {health != null && health.IsDead})",
                      health == null || health.Current < was);
                yield return Shot("after_shooting");
                Check($"the hunter is still standing ({Me.Current:0} hp)", !Me.IsIncapacitated);
                yield return new WaitForSeconds(1f);
                Check($"after the wreck it points at the boat (\"{Objective.Text}\")", Objective.Text != null && Objective.Text.Contains("boat"));
            }

            // ------------------------------------------------ the trader and the boat, in trips
            // Four parts weigh more than one back carries (by design: two a trip, or a friend), so a
            // solo run walks between the counter and the mooring until the boat is whole.
            ShopCounter counter = FindObjectsByType<ShopCounter>(FindObjectsSortMode.None)
                .FirstOrDefault(c => c.Shop != null && c.Shop.Offers.Any(o => o.IsValid && o.Item.Id == "rope"));
            BoatVoyage boat = FindAnyObjectByType<BoatVoyage>();
            Check("there is a trader", counter != null);
            Check("there is a boat", boat != null);
            if (counter != null && boat != null)
            {
                for (int i = 0; i < 6; i++) DevCheats.GiveMoney();
                int offer = System.Array.FindIndex(counter.Shop.Offers, o => o.IsValid && o.Item.Id == BoatVoyage.PartItem);
                Check($"the boat part is on a shelf row the screen draws (row {offer} of {UI.InventoryScreen.ShopRows})",
                      offer >= 0 && offer < UI.InventoryScreen.ShopRows);
                Trading trading = _motor.GetComponent<Trading>();

                for (int trip = 1; trip <= 3 && !boat.Seaworthy; trip++)
                {
                    yield return Walk("trader", counter.transform.position, 3f);
                    Look(counter.transform.position + Vector3.up);
                    yield return new WaitForSeconds(0.3f);
                    _input.BotPress("inventory");
                    yield return new WaitForSeconds(0.8f);
                    if (trip == 1) yield return Shot("shop_open");

                    int want = boat.Missing - Count(BoatVoyage.PartItem);
                    for (int i = 0; i < want; i++)
                    {
                        trading.RequestBuy(counter, offer, 1);
                        yield return new WaitForSeconds(0.4f);
                    }
                    // Thirty-six rounds do not last to the far island's boars and natives.
                    int ammo = System.Array.FindIndex(counter.Shop.Offers, o => o.IsValid && o.Item.Id == "pistol_ammo");
                    if (trip == 1 && ammo >= 0)
                        { trading.RequestBuy(counter, ammo, 36); yield return new WaitForSeconds(0.4f); }
                    Debug.Log($"[Playthrough] trip {trip}: carrying {Count(BoatVoyage.PartItem)} part(s), {_bag.Weight:0}kg.");
                    Debug.Log($"[Playthrough] pistol rounds: {Count("pistol_ammo")}.");
                    yield return Shot($"bought_trip{trip}");
                    _input.BotPress("inventory");
                    yield return new WaitForSeconds(0.5f);

                    yield return Walk("boat", boat.transform.position, 3.5f);
                    for (int i = 0; i < 8 && !boat.Seaworthy && Count(BoatVoyage.PartItem) > 0; i++)
                    {
                        yield return Hold(BoatVoyage.PartItem);
                        Look(boat.transform.position + Vector3.up * 0.5f);
                        yield return new WaitForSeconds(0.2f);
                        if (i == 0 && trip == 1) yield return Shot("fitting");
                        _input.BotPress("interact");
                        yield return new WaitForSeconds(0.6f);
                    }
                    Debug.Log($"[Playthrough] trip {trip}: the boat has {boat.Fitted}/{boat.Needed}.");
                }
                Check($"the boat is whole ({boat.Fitted}/{boat.Needed})", boat.Seaworthy);

                yield return Hold("pistol");
                VehicleRider rider = _motor.GetComponent<VehicleRider>();
                for (int i = 0; i < 5 && !rider.IsSeated; i++)
                {
                    Look(boat.transform.position + Vector3.up * 0.5f);
                    yield return new WaitForSeconds(0.2f);
                    _input.BotPress("interact");
                    yield return new WaitForSeconds(0.8f);
                }
                Check("E boards it", rider.IsSeated);
                yield return Shot("aboard");

                // Straight away from the island's centre, steering on the stick like a person.
                string from = GameSceneLoader.Current;
                float sailed = Time.time, nextShot = Time.time + 15f;
                while (rider.IsSeated && GameSceneLoader.Current == from && Time.time - sailed < 150f)
                {
                    Terrain t = Terrain.activeTerrain;
                    Vector3 centre = t.transform.position + t.terrainData.size * 0.5f;
                    Vector3 out_ = boat.transform.position - centre; out_.y = 0f;
                    float turn = Vector3.SignedAngle(Vector3.ProjectOnPlane(boat.transform.forward, Vector3.up), out_, Vector3.up);
                    _input.BotMove = new Vector2(Mathf.Clamp(turn / 30f, -1f, 1f), 1f);
                    if (Time.time > nextShot) { nextShot = Time.time + 20f; yield return Shot("sailing"); }
                    yield return null;
                }
                _input.BotMove = Vector2.zero;
                Debug.Log($"[Playthrough] sailed {Time.time - sailed:0}s, {BoatVoyage.ToTheEdge(boat != null ? boat.transform.position : Vector3.zero):0}m to the edge.");

                float wait = Time.time;
                while (GameSceneLoader.Current == from && Time.time - wait < 30f) yield return null;
                Check($"the voyage reaches the other island ({from} to {GameSceneLoader.Current})", GameSceneLoader.Current != from);
                yield return Arrive("island2");
            }
        }

        /// <summary>After a scene swap: find the (possibly new) body and take its controls again.</summary>
        IEnumerator Arrive(string name)
        {
            yield return new WaitForSeconds(8f);
            _motor = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None).FirstOrDefault(m => m.IsOwner);
            _input = _motor.GetComponent<PlayerInputReader>();
            _bag = _motor.GetComponent<Inventory>();
            _input.BotDriven = true;
            _input.BotMove = Vector2.zero;
            _input.BotSprint = false;
            yield return Shot(name);
        }

        // ------------------------------------------------ Island2: three parts on a shoulder
        IEnumerator SecondIsland()
        {
            yield return new WaitForSeconds(2f);
            PlaneAssembly plane = PlaneAssembly.Instance;
            // Started here rather than sailed here: arrive as a player would, armed.
            if (Count("pistol") == 0 && ItemCatalog.Active != null)
            {
                _bag.Add(ItemCatalog.Active.Find("pistol"), 1);
                _bag.Add(ItemCatalog.Active.Find("pistol_ammo"), 72);
                yield return Hold("pistol");
                _input.BotPress("reload");
                yield return new WaitForSeconds(2.5f);
            }
            // Island2's wreck washes up the same crate as the first one's: rounds for the village.
            Landmark wreck2 = Landmark.All.Find(l => l.Id == "wreck");
            if (wreck2 != null)
            {
                yield return Walk("wreck", wreck2.transform.position, 8f, 90f);
                yield return Scavenge(22f);
            }
            Check($"there is a plane on {GameSceneLoader.Current}", plane != null);
            Check($"and {PlanePart.All.Count} parts lying about for it", PlanePart.All.Count > 0 || (plane != null && plane.Complete));
            if (plane == null) yield break;

            for (int n = 0; n < 6 && !plane.Complete; n++)
            {
                PlanePart part = PlanePart.All.Where(p => !p.IsCarried)
                    .OrderBy(p => Flat(p.transform.position, _motor.transform.position)).FirstOrDefault();
                if (part == null) break;
                string label = part.Label;
                Debug.Log($"[Playthrough] objective \"{Objective.Text}\"; going for the {label}, "
                          + $"{Flat(part.transform.position, _motor.transform.position):0}m off.");

                yield return Walk(label, part.transform.position, 1.8f, 180f);
                for (int i = 0; i < 5 && part != null && part.Carrier != _motor.NetworkObject; i++)
                {
                    if (Vector3.Distance(part.transform.position, Eye) > 3f)
                        yield return Walk(label, part.transform.position, 1.5f, 30f);
                    // The middle of the crate, not its pivot: the pivot is on the ground, and a look
                    // at the ground finds the ground first.
                    Collider box = part.GetComponent<Collider>();
                    Look(box != null ? box.bounds.center : part.transform.position);
                    yield return new WaitForSeconds(0.2f);
                    if (i == 0) yield return Shot("lift_" + label);
                    _input.BotPress("interact");
                    yield return new WaitForSeconds(0.6f);
                }
                if (part != null && part.Carrier != _motor.NetworkObject)
                    Debug.Log($"[Playthrough] the {label} is at {part.transform.position}, the eye at {Eye} "
                              + $"({Vector3.Distance(part.transform.position, Eye):0.0}m), spawned {part.IsSpawned}, "
                              + $"server {part.IsServerStarted}, scene {part.gameObject.scene.name}, "
                              + $"kinematic {part.GetComponent<Rigidbody>()?.isKinematic}, {PlanePart.All.Count} parts; cast "
                              + string.Join(", ", Physics.SphereCastAll(Camera.main.transform.position, 0.5f, Camera.main.transform.forward, 3.5f, ~0, QueryTriggerInteraction.Ignore)
                                  .OrderBy(h => h.distance).Take(4).Select(h => $"{h.collider.name}/{h.collider.transform.root.name} {h.distance:0.00}"))
                              + "; crosshair "
                              + $"\"{_motor.GetComponent<PlayerInteractor>().Aimed?.Prompt}\", "
                              + $"colliders {string.Join(",", part.GetComponentsInChildren<Collider>().Select(c => c.GetType().Name + (c.isTrigger ? "(trigger)" : "") + (c.enabled ? "" : "(off)")))}.");
                Check($"E lifts the {label}", part != null && part.Carrier == _motor.NetworkObject);

                int before = plane.Fitted;
                yield return Walk("plane with the " + label, plane.transform.position, 4f, 400f);
                for (int i = 0; i < 6 && plane.Fitted == before; i++)
                {
                    // Dropped on the way (a stun puts it down): E here would only board the plane.
                    if (PlanePart.HeldBy(_motor.NetworkObject) == null)
                    {
                        Debug.Log($"[Playthrough] lost the {label} on the way; going back for it.");
                        break;
                    }
                    Look(plane.transform.position + Vector3.up);
                    yield return new WaitForSeconds(0.2f);
                    if (i == 0) yield return Shot("fit_" + label);
                    _input.BotPress("interact");
                    yield return new WaitForSeconds(0.6f);
                }
                if (PlanePart.HeldBy(_motor.NetworkObject) == null && plane.Fitted == before) continue;
                Check($"E fits the {label} ({plane.Fitted}/{plane.Needed})", plane.Fitted > before);
            }
            Check("the plane is whole", plane.Complete);
            if (!plane.Complete) yield break;

            yield return Board(plane.GetComponent<Vehicle>(), "plane");
            yield return Fly();
            if (GameSceneLoader.Current == "Island") yield return Arrive("island1_again");
        }

        IEnumerator Board(Vehicle vehicle, string what)
        {
            VehicleRider rider = _motor.GetComponent<VehicleRider>();
            yield return Walk(what, vehicle.transform.position, 4f, 120f);
            for (int i = 0; i < 6 && !rider.IsSeated; i++)
            {
                Look(vehicle.transform.position + Vector3.up);
                yield return new WaitForSeconds(0.2f);
                _input.BotPress("interact");
                yield return new WaitForSeconds(0.8f);
            }
            if (!rider.IsSeated && Camera.main != null)
            {
                Transform eye = Camera.main.transform;
                string seen = Physics.Raycast(eye.position, eye.forward, out RaycastHit hit, 10f, ~0, QueryTriggerInteraction.Ignore)
                    ? $"{hit.collider.name} ({hit.collider.GetType().Name}) {hit.distance:0.0}m" : "nothing";
                Debug.Log($"[Playthrough] can't board: eye {eye.position}, {what} at {vehicle.transform.position}, looking at {seen}.");
            }
            Check($"E boards the {what}", rider.IsSeated);
            yield return Shot("aboard_" + what);
        }

        /// <summary>
        /// FlightTest's two rules - hold the throttle, pull back once it is fast - then hold sixty
        /// metres and bank away from the island's centre until the game moves us on or the run ends.
        /// </summary>
        IEnumerator Fly()
        {
            VehicleRider rider = _motor.GetComponent<VehicleRider>();
            if (!rider.IsSeated) yield break;
            PlaneController plane = rider.Vehicle.GetComponent<PlaneController>();
            string from = GameSceneLoader.Current;
            Debug.Log($"[Playthrough] flying from {from}: driving {rider.IsDriving}, owner {plane.IsOwner}, "
                      + $"heading {plane.transform.eulerAngles.y:0}, at {plane.transform.position}, {plane.FlightReport()}");
            POIEntry pad = POISpawner.Instance != null ? POISpawner.Instance.Catalog.Find("plane") : null;
            float runway = pad != null ? pad.Yaw : plane.transform.eulerAngles.y;
            float began = Time.time, nextShot = Time.time + 10f, top = 0f, strip = plane.transform.position.y;
            _input.BotSprint = true;

            while (plane != null && GameSceneLoader.Current == from && !RunSummary.Over && Time.time - began < 240f)
            {
                float alt = plane.transform.position.y;
                top = Mathf.Max(top, alt);
                // FlightTest's rules and nothing cleverer: throttle held, back pressure once it is
                // fast, a gentle climb to fifty metres over the strip, then hands off - the plane
                // levels its own wings, and every heading off a strip reaches the edge.
                float pitch = !plane.IsAirborne ? (plane.Airspeed >= 20f ? 0.5f : 0f)
                            : alt < strip + 50f ? 0.3f : 0f;
                // On the wheels, keep to the strip's cleared line the way a pilot would: the parts
                // hauled in knock the parked plane twenty degrees off it, and on island2 that was
                // straight into the treeline.
                float roll = plane.IsAirborne ? 0f
                           : Mathf.Clamp(Mathf.DeltaAngle(plane.transform.eulerAngles.y, runway) / 15f, -1f, 1f);
                _input.BotMove = new Vector2(roll, pitch);
                if (Time.time > nextShot) { nextShot = Time.time + 15f; yield return Shot("flying"); }
                yield return null;
            }
            _input.BotMove = Vector2.zero;
            _input.BotSprint = false;
            // The voyage despawns the plane a beat before the scene load lands.
            for (float t = Time.time; GameSceneLoader.Current == from && !RunSummary.Over && Time.time - t < 20f;) yield return null;
            Debug.Log($"[Playthrough] flew {Time.time - began:0}s, {top:0}m at the highest"
                      + (plane != null ? $": {plane.FlightReport()}" : "."));
            Check($"the flight leaves {from} ({(RunSummary.Over ? "the run is over" : "now " + GameSceneLoader.Current)})",
                  GameSceneLoader.Current != from || RunSummary.Over);
        }

        // ------------------------------------------------ back on Island: the one left behind
        IEnumerator Rescue()
        {
            yield return new WaitForSeconds(2f);
            Castaway who = Castaway.Instance;
            PlaneController flier = FindObjectsByType<PlaneController>(FindObjectsSortMode.None).FirstOrDefault();
            Vehicle plane = flier != null ? flier.GetComponent<Vehicle>() : null;
            Check("there is somebody to go back for", who != null);
            Check("and a plane to take them in", plane != null);
            if (who == null || plane == null) yield break;
            Debug.Log($"[Playthrough] objective \"{Objective.Text}\"; they are {Flat(who.transform.position, _motor.transform.position):0}m off.");

            VehicleRider rider = _motor.GetComponent<VehicleRider>();
            if (rider.IsSeated) { _input.BotPress("interact"); yield return new WaitForSeconds(1.5f); }
            Check("E gets out of the plane", !rider.IsSeated);

            yield return Walk("castaway", who.transform.position, 2.5f, 240f);
            for (int i = 0; i < 5 && who.Where == Castaway.Stage.Waiting; i++)
            {
                Look(who.transform.position + Vector3.up);
                yield return new WaitForSeconds(0.2f);
                if (i == 0) yield return Shot("castaway");
                _input.BotPress("interact");
                yield return new WaitForSeconds(0.8f);
            }
            Check($"E gets them on their feet ({who.Where})", who.Where == Castaway.Stage.Following);

            yield return Walk("plane, with company", plane.transform.position, 5f, 240f);
            // They get in once they are beside the plane, which is usually a step after the pilot.
            yield return Board(plane, "plane");
            float t = Time.time;
            while (who.Where == Castaway.Stage.Following && Time.time - t < 30f) yield return null;
            Check($"they climb in on their own ({who.Where})", who.Where == Castaway.Stage.Aboard);
            yield return Shot("castaway_aboard");

            yield return Fly();
            Check("that is the run", RunSummary.Over);
            Check($"the story beats played ({string.Join(", ", StoryBeat.Played)})",
                  StoryBeat.Played.Contains("plane") && StoryBeat.Played.Contains("castaway"));
            yield return new WaitForSeconds(3f);
            yield return Shot("the_end");
        }

        void Finish()
        {
            Debug.Log($"[Playthrough] {_passed} passed, {_failed} failed. Shots in {_folder}.");
            Application.Quit();
        }
    }
}
