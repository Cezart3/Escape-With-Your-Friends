using System;
using System.Collections.Generic;
using FishNet;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// Server side. Puts the island's points of interest into the session.
    ///
    /// This is what <see cref="WorldSpawner"/> was the first draft of. The difference is where the
    /// list comes from: WorldSpawner holds an array somebody wired into a scene, this holds a baked
    /// copy of <see cref="POICatalog"/>, which is a text asset anyone can append to from a terminal.
    ///
    /// The prefabs are resolved at bake time rather than looked up here, because a runtime lookup by
    /// path only works in the editor - a built player has no AssetDatabase - and Resources folders
    /// are a load-everything-always tax this project does not need to pay.
    ///
    /// Spawned rather than placed in the scene, same as the revive machine and for the same reason:
    /// FishNet identifies scene objects by an id baked at save time, and every scene here is written
    /// by an editor script in batchmode, which is a path where that baking is unproven. Spawning
    /// from a registered prefab is the path that already works every time a client connects.
    /// </summary>
    public class POISpawner : MonoBehaviour
    {
        [Serializable]
        public class Placement
        {
            [Tooltip("Which catalog entry this came from. For logs; nothing keys off it.")]
            public string Id;

            [Tooltip("Resolved from POIEntry.PrefabPath at bake time. Must be in the spawnable prefabs list.")]
            public NetworkObject Prefab;

            public Vector3 Position;
            public Vector3 Euler;
        }

        [Tooltip("Baked from the POI catalog. Regenerated whole on every island build; do not hand-edit.")]
        [SerializeField] Placement[] _placements = Array.Empty<Placement>();

        [Tooltip("The catalog these came from, for reference and for anything that wants the pads at run time.")]
        [SerializeField] POICatalog _catalog;

        NetworkManager _manager;
        bool _spawned;

        readonly List<NetworkObject> _live = new();

        /// <summary>Everything this spawner put into the world. Server-side only; empty on clients.</summary>
        public IReadOnlyList<NetworkObject> Spawned => _live;

        /// <summary>
        /// Where the island's buildings will stand, before anything has spawned them. The NavMesh
        /// bake needs this: the POIs are server-spawned, so at bake time the scene is bare terrain
        /// and the only record of the shop's position is this list.
        /// </summary>
        public IReadOnlyList<Placement> Placements => _placements;

        /// <summary>The spawner in the loaded island scene, for anything that needs to find a POI.</summary>
        public static POISpawner Instance { get; private set; }

        /// <summary>The catalog the island was built from. Null before the island scene is loaded.</summary>
        public POICatalog Catalog => _catalog;

        void Awake()
        {
            Instance = this;

            _manager = InstanceFinder.NetworkManager;
            if (_manager == null)
            {
                Debug.LogWarning("[POISpawner] No NetworkManager yet; waiting for one.");
                return;
            }

            _manager.ServerManager.OnServerConnectionState += OnServerState;
        }

        void Start()
        {
            // The island is loaded as a scene after the server is already up, so the state event that
            // would have started this has been and gone. Both paths are needed: this one for the
            // normal case, the event for a scene that happens to be loaded before the server starts.
            if (_manager != null && _manager.ServerManager.Started) SpawnAll();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_manager != null) _manager.ServerManager.OnServerConnectionState -= OnServerState;
        }

        void OnServerState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started) SpawnAll();
            else if (args.ConnectionState == LocalConnectionState.Stopped) _spawned = false;
        }

        void SpawnAll()
        {
            if (_spawned) return;
            _spawned = true;

            int placed = 0;
            int skipped = 0;

            foreach (Placement placement in _placements)
            {
                if (placement == null || placement.Prefab == null)
                {
                    skipped++;
                    continue;
                }

                NetworkObject instance = Instantiate(placement.Prefab, placement.Position,
                                                     Quaternion.Euler(placement.Euler));
                instance.name = placement.Prefab.name + " (" + placement.Id + ")";

                // Into this island's scene, explicitly. A parentless Instantiate lands in whatever
                // scene is active, which during a load is still Bootstrap - the scene that never
                // unloads - so without this the whole island stays standing after you have sailed
                // away from it, boat included. #69 found that the hard way.
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(
                    instance.gameObject, gameObject.scene);

                // The catalog id wins over whatever the prefab was built with. The prefab is a kind
                // of place; the catalog entry is this particular one, and everything that looks a
                // landmark up - objectives, the HUD, the abduction target in #107 - means that one.
                var landmark = instance.GetComponent<Landmark>();
                if (landmark != null) landmark.Id = placement.Id;

                _manager.ServerManager.Spawn(instance);
                _live.Add(instance);
                placed++;

                if (Loot.ContainsKey(placement.Id)) StartCoroutine(Scatter(placement.Id, placement.Position));
                string quest = gameObject.scene.name + "/" + placement.Id;
                if (Quest.ContainsKey(quest)) StartCoroutine(PlaceQuest(quest, instance.transform));
            }

            Debug.Log($"[POISpawner] Placed {placed} points of interest"
                      + (skipped > 0 ? $", skipped {skipped} with no prefab" : "") + ".");
        }

        /// <summary>
        /// What lies on the ground at a place when the island starts. The second playtest walked to
        /// the wreck the first objective names and found a hull and nothing else; a wreck is where the
        /// crate of whatever the ship carried washed up, and it is the one place the run's first gun
        /// can be picked up rather than bought.
        /// </summary>
        static readonly Dictionary<string, (string Item, int Count)[]> Loot = new()
        {
            ["wreck"] = new[] { ("pistol", 1), ("pistol_ammo", 36), ("rope", 3), ("cloth", 4),
                                ("plank", 4), ("bandage", 2), ("empty_bottle", 2) },
            ["cave"] = new[] { ("shotgun_shell", 12), ("torch", 1), ("flint", 3) },
        };

        /// <summary>
        /// #274. Two of the boat's four parts lie where Act 1 sends you for them, on the first island
        /// only: the cult's fuel by their totem and the chart at the back of the cave. In the place's
        /// own frame, so they turn with it, and dropped onto the floor from just under the ceiling.
        /// Keyed by scene and place. #275 adds Radu's journal pages two to four on Temple Isle: where he
        /// landed, where he saw the cages, and the cave he left from. Page five waits for Act 3.
        /// </summary>
        static readonly Dictionary<string, (string Item, Vector3 At)> Quest = new()
        {
            ["Island/village"] = ("fuel_drum", new Vector3(2f, 2.5f, -1.5f)),
            ["Island/cave"] = ("chart_page", new Vector3(-1.5f, 2.5f, -3.5f)),
            ["Island2/wreck"] = ("journal_page_2", new Vector3(1.5f, 2.5f, 2f)),
            ["Island2/village"] = ("journal_page_3", new Vector3(-2f, 2.5f, 1f)),
            ["Island2/cave"] = ("journal_page_4", new Vector3(-1.5f, 2.5f, -3.5f)),
        };

        static System.Collections.IEnumerator PlaceQuest(string id, Transform place)
        {
            float until = Time.time + 60f;
            while (Data.ItemCatalog.Active == null && Time.time < until) yield return null;
            Data.ItemDef def = Data.ItemCatalog.Active != null ? Data.ItemCatalog.Active.Find(Quest[id].Item) : null;
            if (def == null || place == null) yield break;

            Vector3 at = place.TransformPoint(Quest[id].At);
            if (Physics.Raycast(at, Vector3.down, out RaycastHit hit, 6f, ~0, QueryTriggerInteraction.Ignore))
                at = hit.point;

            Items.WorldItemSpawner.Drop(new Items.ItemStack(Data.ItemCatalog.Active.IndexOf(def), 1),
                                        at + Vector3.up * 0.3f, place.rotation);
            Debug.Log($"[POISpawner] {def.Id} waits at the {id}, {at}.");
        }

        static System.Collections.IEnumerator Scatter(string id, Vector3 centre)
        {
            // The catalog is published by the first inventory to wake, which is the host's body, and
            // that arrives after the island's POIs do.
            float until = Time.time + 60f;
            while (Data.ItemCatalog.Active == null && Time.time < until) yield return null;
            if (Data.ItemCatalog.Active == null) yield break;

            var lines = Loot[id];

            for (int i = 0; i < lines.Length; i++)
            {
                Data.ItemDef def = Data.ItemCatalog.Active.Find(lines[i].Item);
                ushort index = def != null ? Data.ItemCatalog.Active.IndexOf(def) : (ushort)0;
                if (index == 0) continue;

                // A ring 9 to 12m out, clear of the hull, each dropped from above onto whatever is there.
                float angle = i * Mathf.PI * 2f / lines.Length;
                float radius = 9f + (i % 3);
                Vector3 at = centre + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * radius;
                if (Physics.Raycast(at + Vector3.up * 40f, Vector3.down, out RaycastHit hit, 80f, ~0,
                                    QueryTriggerInteraction.Ignore))
                    at = hit.point;

                Items.WorldItemSpawner.Drop(new Items.ItemStack(index, lines[i].Count),
                                            at + Vector3.up * 0.5f, Quaternion.identity);
            }

            Debug.Log($"[POISpawner] {lines.Length} stacks of loot on the ground at {id}.");
        }

        /// <summary>Where a named POI stands, for spawn points and objectives. Zero if it is not in the list.</summary>
        public Vector3 PositionOf(string id)
        {
            foreach (Placement placement in _placements)
                if (placement != null && placement.Id == id) return placement.Position;

            return Vector3.zero;
        }
    }
}
