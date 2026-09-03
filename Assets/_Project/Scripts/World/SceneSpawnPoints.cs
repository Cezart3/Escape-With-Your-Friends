using System.Collections.Generic;
using EscapeWithYourFriends.Net;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The spawn points belonging to a gameplay scene, handed to the spawner when that scene loads.
    ///
    /// This exists because of a limitation with a good reason behind it. <see cref="PlayerSpawner"/>
    /// lives on the NetworkManager in Bootstrap, which is loaded once and never unloaded, while the
    /// arena and the island are scenes loaded around it. A serialized `Transform[]` cannot cross that
    /// boundary - Unity has no cross-scene references - so the spawn points cannot be wired in an
    /// inspector. They register themselves instead.
    ///
    /// The order works out: FishNet loads the global scenes for a client before it raises
    /// OnClientLoadedStartScenes, which is what the spawner spawns on, so by the time anybody needs a
    /// spawn point this has already run its Awake.
    /// </summary>
    public class SceneSpawnPoints : MonoBehaviour
    {
        [Tooltip("Order matters: it is the order players are placed in, so slot 0 should be the nicest spot.")]
        [SerializeField] Transform[] _points;

        bool _registered;

        void Awake()
        {
            if (_points == null || _points.Length == 0) _points = Collect();
            Register();
        }

        // Awake has usually done this already; OnEnable covers a scene that is enabled later, and the
        // flag stops the normal path logging the same line twice.
        void OnEnable() => Register();

        void OnDestroy()
        {
            // Handing the spawner back nothing rather than a list of destroyed transforms: the scene
            // is going away, and a spawner holding dead references would put the next player at the
            // origin without saying why.
            if (!_registered) return;
            _registered = false;
            if (PlayerSpawner.Instance != null) PlayerSpawner.Instance.UseSpawnPoints(null, name);
        }

        void Register()
        {
            if (_registered) return;
            if (PlayerSpawner.Instance == null || _points == null || _points.Length == 0) return;

            _registered = true;
            PlayerSpawner.Instance.UseSpawnPoints(_points, name);
        }

        Transform[] Collect()
        {
            var found = new List<Transform>();
            foreach (Transform child in transform) found.Add(child);
            return found.ToArray();
        }
    }
}
