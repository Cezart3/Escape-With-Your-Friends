using System.Collections.Generic;
using EscapeWithYourFriends.Core;
using FishNet;
using FishNet.Managing;
using UnityEngine;
using UnityEngine.AI;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// Walks an agent across the island and complains if it stops.
    ///
    /// #37 asks for agents that path from the village to the base "without getting stuck on terrain
    /// seams", and there is no way to check that from a bake report. A path can come back
    /// PathComplete and still strand an agent, because a complete path is a list of corners and
    /// getting stuck happens between them - on a tile boundary, on a ledge the agent can see across
    /// but not step down, on a sliver of NavMesh too narrow for its radius.
    ///
    /// So this is the acceptance criterion, in code: put a real NavMeshAgent on the island, send it
    /// somewhere, and watch whether it keeps moving. It exists only when asked for, runs only on the
    /// server, and is not networked - nobody needs to see the probe.
    ///
    ///   -navWalk village:camp.base   one leg, from one catalog id to another
    ///   -navWalk all                 every landmark to the camp, one after another
    /// </summary>
    public class NavWalk : MonoBehaviour
    {
        [Tooltip("Metres per second. Faster than a player, because this is a test and not a scene.")]
        [SerializeField] float _speed = 6f;

        [Tooltip("Seconds a single leg may take before it is called a failure.")]
        [SerializeField] float _timeout = 180f;

        [Tooltip("Seconds of near-stillness that count as stuck. Long enough to survive a corner, short enough to notice a seam.")]
        [SerializeField] float _stallWindow = 4f;

        [Tooltip("Metres that count as having moved at all within the stall window.")]
        [SerializeField] float _stallDistance = 0.6f;

        [Tooltip("Seconds between progress lines.")]
        [SerializeField] float _logEvery = 5f;

        readonly Queue<Leg> _legs = new();

        NavMeshAgent _agent;
        Leg _leg;
        bool _running;

        float _legStarted;
        float _lastLog;
        float _lastProgress;
        Vector3 _lastProgressAt;
        float _travelled;
        Vector3 _previous;

        int _arrived;
        int _failed;
        float _offset;
        Vector3 _destination;

        struct Leg
        {
            public string FromId;
            public string ToId;
            public Vector3 From;
            public Vector3 To;
        }

        void Start()
        {
            string request = CommandLine.GetString("-navWalk", null);
            if (string.IsNullOrWhiteSpace(request) || request.StartsWith("-"))
            {
                enabled = false;
                return;
            }

            NetworkManager manager = InstanceFinder.NetworkManager;
            if (manager == null || !manager.ServerManager.Started)
            {
                // Clients load the island too. One walker per session, on the machine that owns the
                // world, or four processes fight over the same answer.
                enabled = false;
                return;
            }

            POISpawner spawner = POISpawner.Instance;
            if (spawner == null)
            {
                Debug.LogError("[NavWalk] No POI spawner in this scene; nowhere to walk to.");
                enabled = false;
                return;
            }

            BuildLegs(request, spawner);

            if (_legs.Count == 0)
            {
                Debug.LogError($"[NavWalk] '{request}' named no journey this island can make.");
                enabled = false;
                return;
            }

            Debug.Log($"[NavWalk] {_legs.Count} leg(s) to walk at {_speed} m/s.");
            Next();
        }

        void BuildLegs(string request, POISpawner spawner)
        {
            if (string.Equals(request, "all", System.StringComparison.OrdinalIgnoreCase))
            {
                foreach (POISpawner.Placement placement in spawner.Placements)
                {
                    if (placement == null || placement.Id == "camp.base") continue;
                    Add(placement.Id, "camp.base", spawner);
                }

                return;
            }

            string[] parts = request.Split(':');
            if (parts.Length != 2)
            {
                Debug.LogError($"[NavWalk] '{request}' is not a journey. Use 'from:to' with two catalog ids, or 'all'.");
                return;
            }

            Add(parts[0], parts[1], spawner);
        }

        void Add(string fromId, string toId, POISpawner spawner)
        {
            Vector3 from = spawner.PositionOf(fromId);
            Vector3 to = spawner.PositionOf(toId);

            if (from == Vector3.zero || to == Vector3.zero)
            {
                Debug.LogError($"[NavWalk] '{fromId}' or '{toId}' is not a catalog id on this island.");
                return;
            }

            _legs.Enqueue(new Leg { FromId = fromId, ToId = toId, From = from, To = to });
        }

        void Next()
        {
            if (_legs.Count == 0)
            {
                Finish();
                return;
            }

            _leg = _legs.Dequeue();

            // The catalog coordinate of a landmark is usually inside its building. NavApproach finds
            // the nearest spot outside that is actually connected to where the walk starts.
            if (!NavApproach.Route(_leg.From, _leg.To, out Vector3 origin, out Vector3 destination,
                                   out float startOffset, out float endOffset))
            {
                Debug.LogError($"[NavWalk] No route from {_leg.FromId} to {_leg.ToId} exists at all; "
                               + "skipping this leg.");
                _failed++;
                Next();
                return;
            }

            _offset = endOffset;
            _destination = destination;

            // The agent has to be standing on the mesh when the component is added, or Unity refuses
            // to create it. Position first, component second.
            if (_agent == null)
            {
                var walker = new GameObject("NavWalker");
                walker.transform.position = origin;

                _agent = walker.AddComponent<NavMeshAgent>();
                _agent.radius = 0.5f;
                _agent.height = 2f;
                _agent.speed = _speed;
                _agent.acceleration = 20f;
                _agent.angularSpeed = 480f;
                _agent.stoppingDistance = 1.5f;
                _agent.autoBraking = true;
            }
            else
            {
                _agent.Warp(origin);
            }

            _agent.SetDestination(destination);

            _running = true;
            _legStarted = Time.time;
            _lastLog = Time.time;
            _lastProgress = Time.time;
            _lastProgressAt = origin;
            _previous = origin;
            _travelled = 0f;

            Debug.Log($"[NavWalk] {_leg.FromId} -> {_leg.ToId}: "
                      + $"{Vector3.Distance(origin, destination):F0}m as the crow flies, starting at "
                      + $"({origin.x:F0}, {origin.y:F1}, {origin.z:F0})"
                      + (startOffset > 1f ? $", setting off {startOffset:F0}m from the marker" : "")
                      + (_offset > 1f ? $", stopping {_offset:F0}m short of it" : "") + ".");
        }

        void Update()
        {
            if (!_running || _agent == null) return;

            Vector3 here = _agent.transform.position;
            _travelled += Vector3.Distance(here, _previous);
            _previous = here;

            if (_agent.pathPending) return;

            if (_agent.pathStatus == NavMeshPathStatus.PathInvalid)
            {
                Fail("no path at all - the two ends are on different pieces of NavMesh");
                return;
            }

            // Standing still inside the stopping distance is the only honest "arrived": an agent
            // that still has a path may be a metre out and coasting.
            if (_agent.remainingDistance <= _agent.stoppingDistance
                && (!_agent.hasPath || _agent.velocity.sqrMagnitude < 0.01f))
            {
                if (_agent.pathStatus == NavMeshPathStatus.PathPartial)
                    Fail($"stopped short - the path was partial and ran out "
                         + $"{Vector3.Distance(_agent.transform.position, _leg.To):F0}m from {_leg.ToId}");
                else
                    Arrive();

                return;
            }

            if (Time.time - _lastLog >= _logEvery)
            {
                _lastLog = Time.time;
                // remainingDistance is Infinity until the agent is near the end of a long path, so
                // the straight line to the destination is the number that is always readable.
                Debug.Log($"[NavWalk] {_leg.FromId} -> {_leg.ToId}: "
                          + $"{Vector3.Distance(here, _destination):F0}m to go, "
                          + $"{_agent.velocity.magnitude:F1} m/s at ({here.x:F0}, {here.y:F1}, {here.z:F0}), "
                          + $"path {_agent.pathStatus}.");
            }

            // Progress, not speed: an agent turning on the spot is fine for a moment and stuck after
            // four seconds, and the difference is whether the position changed.
            if (Vector3.Distance(here, _lastProgressAt) >= _stallDistance)
            {
                _lastProgress = Time.time;
                _lastProgressAt = here;
            }
            else if (Time.time - _lastProgress >= _stallWindow)
            {
                Fail($"stuck for {_stallWindow:F0}s at ({here.x:F1}, {here.y:F1}, {here.z:F1}) "
                     + $"with {_agent.remainingDistance:F0}m still to go, path {_agent.pathStatus}, "
                     + $"on NavMesh {_agent.isOnNavMesh}");
                return;
            }

            if (Time.time - _legStarted >= _timeout) Fail($"still going after {_timeout:F0}s");
        }

        void Arrive()
        {
            _arrived++;
            _running = false;

            float straight = Vector3.Distance(_leg.From, _leg.To);
            float miss = Vector3.Distance(_agent.transform.position, _leg.To);

            // Landing a few metres out is expected when the marker is inside a building. Landing much
            // further out than the approach ring means the walk ended somewhere else entirely.
            if (miss > NavApproach.Ring + 6f)
                Debug.LogWarning($"[NavWalk] {_leg.ToId}: arrived {miss:F0}m from where the catalog says it is, "
                                 + $"further than the {NavApproach.Ring:F0}m approach ring explains.");

            Debug.Log($"[NavWalk] {_leg.FromId} -> {_leg.ToId}: arrived in {Time.time - _legStarted:F1}s, "
                      + $"walked {_travelled:F0}m for {straight:F0}m of straight line "
                      + $"({_travelled / Mathf.Max(1f, straight):F2}x).");

            Next();
        }

        void Fail(string why)
        {
            _failed++;
            _running = false;
            Debug.LogError($"[NavWalk] {_leg.FromId} -> {_leg.ToId} FAILED: {why}. Walked {_travelled:F0}m "
                           + $"in {Time.time - _legStarted:F1}s.");
            Next();
        }

        void Finish()
        {
            if (_agent != null) Destroy(_agent.gameObject);
            _agent = null;
            enabled = false;

            string line = $"[NavWalk] Done: {_arrived} arrived, {_failed} failed.";
            if (_failed > 0) Debug.LogError(line);
            else Debug.Log(line);
        }
    }
}
