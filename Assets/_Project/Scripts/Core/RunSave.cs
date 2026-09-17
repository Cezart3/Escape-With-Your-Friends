using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Economy;
using EscapeWithYourFriends.Items;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Vehicles;
using EscapeWithYourFriends.World;
using FishNet;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.Core
{
    /// <summary>One slot of somebody's bag, as it goes on disk.</summary>
    [Serializable]
    public class SavedSlot
    {
        public int slot;

        /// <summary>
        /// <see cref="ItemDef.Id"/>, not <see cref="ItemStack.Index"/>. The index is a position in
        /// the catalogue, and the catalogue is regenerated every time somebody adds an item - so a
        /// save written with indices would quietly turn a bag of fish into a bag of dynamite the
        /// first time the content list grew. Ids cost a few bytes and survive that.
        /// </summary>
        public string item;

        public int count;
    }

    /// <summary>What one player keeps between sessions, keyed by <see cref="PlayerKey"/>.</summary>
    [Serializable]
    public class SavedPlayer
    {
        public string key;
        public int money;
        public int chips;
        public List<SavedSlot> bag = new();
    }

    /// <summary>Upgrades bolted to one vehicle, by tier per part. Matched by scene object name.</summary>
    [Serializable]
    public class SavedVehicle
    {
        public string name;
        public int[] tiers = Array.Empty<int>();
    }

    /// <summary>The whole file. Public fields and plain lists, because JsonUtility reads nothing else.</summary>
    [Serializable]
    public class SavedRun
    {
        /// <summary>Bumped when the shape changes; a file from an older shape is discarded, not migrated.</summary>
        public int version = 1;

        public string island = "Island";
        public bool planeOwned;
        public List<string> planeParts = new();
        public List<SavedPlayer> players = new();
        public List<SavedVehicle> vehicles = new();
    }

    /// <summary>
    /// The run, written to disk and put back. #75.
    ///
    /// The acceptance is *"quit and resume a run without losing progress"*, and the honest reading of
    /// "quit" includes the ways a session actually ends - alt-F4, a crash, a router. So this does not
    /// wait for <c>OnApplicationQuit</c>, which only fires on the polite ending: it writes every
    /// <see cref="AutosaveSeconds"/> as well, and that periodic write is the one that will do the
    /// real work.
    ///
    /// **One file, host only.** The host is already the authority on every number worth keeping, so
    /// the save is a snapshot of what the server believes and the other three get it the way they get
    /// everything else - replicated, when their body spawns and their wallet and bag are filled in.
    /// There is no client-side save, no merge between four copies, and nothing to reconcile.
    ///
    /// **Keyed by <see cref="PlayerKey"/>, which #111 already had to solve.** A saved bag has to find
    /// its way back to the same person across a process restart, and FishNet's client ids are reused
    /// while every Tugboat client shares one address. The key is the only identifier in the project
    /// that means the same thing tomorrow.
    ///
    /// **Nothing here pushes state into the world; the world pulls it.** This class arms inside the
    /// server's own "started" callback, which is before FishNet has spawned a single scene object -
    /// a restore written as a loop over the scene finds an aeroplane that is not spawned yet and
    /// vehicles whose tier lists are still empty, and does nothing at all without saying so. So each
    /// thing restores itself in its own <c>OnStartServer</c>, through <see cref="SavedParts"/>,
    /// <see cref="SavedPlaneOwned"/> and <see cref="TiersFor"/>, because that is the first moment it
    /// can. It also means sailing between the islands needs no special handling: scene objects are
    /// rebuilt by the load and ask again on the way up.
    ///
    /// **What is saved is what the issue asks for and nothing else**: money, chips, bags, which island
    /// the group is on, which holes in the aeroplane are filled, and which upgrades are bolted to
    /// which vehicle. Not saved: where anybody was standing, what is lying on the ground, which
    /// animals are alive, how full the chests are, the time of day. A resumed run puts everyone back
    /// at the spawn with their things, and the island is regenerated from the same seed it always was.
    /// Position is the most tempting of those and the least valuable - a body restored mid-air or
    /// inside the geometry that moved under it is worse than a walk back.
    ///
    /// **Off by default in a headless run.** A build with no graphics device is a harness, and twenty
    /// harnesses sharing one <c>persistentDataPath</c> would each inherit the last one's island and
    /// wallet. Headless has to ask with <c>-save</c>; a real build saves unless told <c>-noSave</c>.
    /// <c>-savePath &lt;file&gt;</c> moves the file, which is how two harness lanes stay out of each
    /// other's way.
    /// </summary>
    public class RunSave : MonoBehaviour
    {
        /// <summary>Seconds between writes. Cheap enough not to think about; short enough to lose little.</summary>
        const float AutosaveSeconds = 30f;

        const int Version = 1;

        static RunSave _driver;
        static SavedRun _run;
        static string _path;

        /// <summary>The state as it stands, merged into as the session goes. Null until armed.</summary>
        public static SavedRun Run => _run;

        /// <summary>Where the file is. Handy for a test that wants to delete it.</summary>
        public static string Path => _path;

        /// <summary>True when this process is saving at all. Everything else no-ops when it is false.</summary>
        public static bool Armed => _driver != null;

        internal static void Begin()
        {
            if (_driver != null) return;

            bool headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
            bool wanted = !CommandLine.HasFlag("-noSave")
                          && (!headless || CommandLine.HasFlag("-save"));

            if (!wanted) return;

            _path = CommandLine.GetString("-savePath",
                                          System.IO.Path.Combine(Application.persistentDataPath,
                                                                 "run.json"));

            // Read before the component exists, and that order is the whole of a bug worth a
            // comment. AddComponent runs OnEnable synchronously, OnEnable starts the loop, and the
            // loop's first MoveNext runs synchronously too - it only yields once, at a wait for a
            // server that has already started by the time Begin is called. So it reached _run before
            // the line below had filled it in, and died on the spot, taking every world restore with
            // it while the per-player restore carried on working through PlayerSpawner.
            _run = Read() ?? new SavedRun();

            var go = new GameObject("RunSave");
            DontDestroyOnLoad(go);
            _driver = go.AddComponent<RunSave>();

            Debug.Log($"[RunSave] Armed on {_path}; island '{_run.island}', "
                      + $"{_run.players.Count} player(s), {_run.planeParts.Count} part(s) fitted.");
        }

        void OnEnable() => StartCoroutine(Loop());

        /// <summary>
        /// The polite ending. Everything this catches the autosave would have caught within
        /// <see cref="AutosaveSeconds"/> anyway; it is here so that quitting on purpose loses nothing
        /// at all, which is the case the issue names.
        /// </summary>
        void OnApplicationQuit() => ServerSave();

        // ---------------------------------------------------------------- putting it back

        IEnumerator Loop()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            // The island first, and before anything else asks what map this is. Travelling is a scene
            // load, so everything below waits for it to land.
            if (!string.IsNullOrEmpty(_run.island) && _run.island != GameSceneLoader.Current
                && GameSceneLoader.Instance != null)
            {
                Debug.Log($"[RunSave] The save is on '{_run.island}' and this is "
                          + $"'{GameSceneLoader.Current}'; sailing there.");

                GameSceneLoader.Instance.ServerTravel(_run.island);
            }

            while (true)
            {
                yield return new WaitForSeconds(AutosaveSeconds);
                ServerSave();
            }
        }

        // ---------------------------------------------------------------- what the world asks for

        /// <summary>
        /// The saved tiers for the vehicle called <paramref name="name"/>, or null if the file has
        /// never seen it. Asked for by <see cref="VehicleUpgrades.OnStartServer"/>.
        /// </summary>
        public static int[] TiersFor(string name)
        {
            SavedVehicle saved = _run?.vehicles.Find(v => v != null && v.name == name);
            return saved?.tiers;
        }

        /// <summary>Which aeroplane parts the file says are in. Empty when nothing was saved.</summary>
        public static IReadOnlyList<string> SavedParts
            => _run != null ? _run.planeParts : (IReadOnlyList<string>)System.Array.Empty<string>();

        /// <summary>Whether the file remembers the group finishing an aeroplane.</summary>
        public static bool SavedPlaneOwned => _run != null && _run.planeOwned;

        /// <summary>
        /// Server only. Fills in one player's wallet and bag from the file. Called by
        /// <see cref="PlayerSpawner"/> the moment a body is stamped with its key, which is the only
        /// point at which the two halves - who this is, and what they own - are both known.
        /// </summary>
        public static void ServerApply(string key, GameObject body)
        {
            if (_run == null || body == null || string.IsNullOrEmpty(key)) return;

            SavedPlayer saved = _run.players.Find(p => p != null && p.key == key);
            if (saved == null) return;

            var wallet = body.GetComponent<Wallet>();
            if (wallet != null)
            {
                wallet.ServerSetBalance(saved.money);
                wallet.ServerSetChips(saved.chips);
            }

            var bag = body.GetComponent<Inventory>();
            if (bag != null && ItemCatalog.Active != null)
            {
                bag.ServerClear();

                foreach (SavedSlot slot in saved.bag)
                {
                    ItemDef def = ItemCatalog.Active.Find(slot.item);
                    if (def == null)
                    {
                        // An item that no longer exists in the catalogue. Dropped rather than
                        // guessed at, and said out loud, because it means content was removed
                        // under a save and somebody will want to know which.
                        Debug.LogWarning($"[RunSave] '{slot.item}' is no longer an item; "
                                         + $"{PlayerKey.Short(key)} loses {slot.count} of it.");
                        continue;
                    }

                    bag.ServerRestore(slot.slot, def, slot.count);
                }
            }

            Debug.Log($"[RunSave] {PlayerKey.Short(key)} resumed with {saved.money} money, "
                      + $"{saved.chips} chip(s) and {saved.bag.Count} stack(s).");
        }

        // ---------------------------------------------------------------- taking the snapshot

        /// <summary>
        /// Server only. Reads the world into <see cref="Run"/> and writes it out.
        ///
        /// Everything merges rather than replaces, and that is not tidiness. A player who left an
        /// hour ago is not in the scene any more, and an island the group sailed away from has no
        /// vehicles loaded - a snapshot that rebuilt the lists from what happens to be in memory
        /// would delete both. What is present overwrites its own entry; what is absent keeps the
        /// one it had.
        /// </summary>
        public static void ServerSave()
        {
            if (_run == null) return;
            if (InstanceFinder.NetworkManager == null
                || !InstanceFinder.NetworkManager.IsServerStarted) return;

            _run.version = Version;

            if (!string.IsNullOrEmpty(GameSceneLoader.Current)) _run.island = GameSceneLoader.Current;

            _run.planeOwned |= PlaneAssembly.Owned;

            PlaneAssembly plane = PlaneAssembly.Instance;
            if (plane != null)
                foreach (string label in plane.ServerFittedLabels())
                    if (!_run.planeParts.Contains(label)) _run.planeParts.Add(label);

            foreach (BodyPersistence body in
                     FindObjectsByType<BodyPersistence>(FindObjectsSortMode.None))
            {
                if (body == null || string.IsNullOrEmpty(body.OwnerKey)) continue;
                Gather(body.OwnerKey, body.gameObject);
            }

            foreach (VehicleUpgrades fitted in
                     FindObjectsByType<VehicleUpgrades>(FindObjectsSortMode.None))
            {
                if (fitted == null || !fitted.IsSpawned) continue;

                SavedVehicle saved = _run.vehicles.Find(v => v != null && v.name == fitted.name);
                if (saved == null)
                {
                    saved = new SavedVehicle { name = fitted.name };
                    _run.vehicles.Add(saved);
                }

                saved.tiers = new int[VehicleUpgrades.Slots];
                for (int i = 0; i < VehicleUpgrades.Slots; i++)
                    saved.tiers[i] = fitted.TierOf((VehiclePart)i);
            }

            Write();
        }

        static void Gather(string key, GameObject body)
        {
            SavedPlayer saved = _run.players.Find(p => p != null && p.key == key);
            if (saved == null)
            {
                saved = new SavedPlayer { key = key };
                _run.players.Add(saved);
            }

            var wallet = body.GetComponent<Wallet>();
            if (wallet != null)
            {
                saved.money = wallet.Balance;
                saved.chips = wallet.Chips;
            }

            var bag = body.GetComponent<Inventory>();
            if (bag == null) return;

            saved.bag.Clear();

            for (int i = 0; i < bag.SlotCount; i++)
            {
                ItemStack stack = bag[i];
                if (stack.IsEmpty || stack.Def == null) continue;

                saved.bag.Add(new SavedSlot { slot = i, item = stack.Def.Id, count = stack.Count });
            }
        }

        // ---------------------------------------------------------------- the file itself

        static SavedRun Read()
        {
            try
            {
                if (!File.Exists(_path)) return null;

                var file = JsonUtility.FromJson<SavedRun>(File.ReadAllText(_path));
                if (file == null) return null;

                if (file.version != Version)
                {
                    // Discarded rather than migrated. There is one version of this game and nobody
                    // has a save worth writing a migration for; when somebody does, this is where
                    // it goes.
                    Debug.LogWarning($"[RunSave] {_path} is version {file.version}, this build reads "
                                     + $"{Version}. Starting a fresh run.");
                    return null;
                }

                // JsonUtility leaves a list null when the field was absent from the file rather than
                // giving it the initialiser's empty list, so every one of them is checked here and
                // nowhere else.
                file.planeParts ??= new List<string>();
                file.players ??= new List<SavedPlayer>();
                file.vehicles ??= new List<SavedVehicle>();

                foreach (SavedPlayer player in file.players)
                    if (player != null) player.bag ??= new List<SavedSlot>();

                return file;
            }
            catch (Exception error)
            {
                Debug.LogError($"[RunSave] {_path} could not be read ({error.Message}). "
                               + "Starting a fresh run.");
                return null;
            }
        }

        static void Write()
        {
            try
            {
                string folder = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                // Written beside the real file and moved into place. A write interrupted halfway -
                // which is exactly what a crash is, and a crash is the case this whole class exists
                // for - would otherwise leave a truncated file where the run used to be.
                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(_run, prettyPrint: true));
                File.Copy(temporary, _path, overwrite: true);
                File.Delete(temporary);
            }
            catch (Exception error)
            {
                Debug.LogError($"[RunSave] {_path} could not be written ({error.Message}).");
            }
        }

        /// <summary>
        /// Forgets the loaded run and re-reads the file. Only the harness calls this: it is how one
        /// process can play the part of the second launch without being a second launch.
        /// </summary>
        internal static void Reload()
        {
            _run = Read() ?? new SavedRun();
        }
    }
}
