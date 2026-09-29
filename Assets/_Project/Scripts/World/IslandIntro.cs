using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The first thirty seconds of a fresh session: four people standing round a fire on a beach with
    /// no idea what to do.
    ///
    /// #39 asks for "an obvious first objective", and obvious means visible from where you land. The
    /// wreck is on the tideline within sight of the camp, it is the thing you plainly arrived on, and
    /// it is where the boat parts come from in M5 - so it is the first place the game points at.
    ///
    /// The objective is set locally on every peer rather than replicated. Everyone can see the same
    /// wreck; nobody needs to be told about it over the network.
    /// </summary>
    public class IslandIntro : MonoBehaviour
    {
        [Tooltip("Catalog id of the landmark the first objective points at.")]
        [SerializeField] string _firstTarget = "wreck";

        [Tooltip("What the objective says. Imperative, second person, one line.")]
        [SerializeField] string _firstObjective = "Search the wreck on the beach";

        [Tooltip("Seconds to keep looking for the target. The POIs are spawned by the server, so a client arrives before they exist.")]
        [SerializeField] float _patience = 20f;

        float _giveUpAt;
        bool _done;
        Landmark _target;
        bool _beat;
        float _foundAt;

        /// <summary>
        /// This peer's player has stood at the wreck. Until then the Castaway's chain leaves the wreck
        /// line up: the boat line had taken over by the time anybody spawned, and a fresh player was
        /// sent 98m to the boat with no gun and the pistol lying at the wreck (playthrough bot).
        /// </summary>
        public static bool Visited { get; internal set; }

        void Start()
        {
            _giveUpAt = Time.time + _patience;
            Objective.Set(_firstObjective);
        }

        void Update()
        {
            if (_done)
            {
                // #197. The arrival shot, once the local body exists so the camera is where the
                // player is. Retried every quarter second until it plays: a peer that spawns out of
                // Reach gets it on the walk over instead, which is a reveal rather than a miss.
                if (!_beat && !Visited && _target != null && Time.frameCount % 15 == 0)
                    foreach (Player.PlayerMotor motor in FindObjectsByType<Player.PlayerMotor>(FindObjectsSortMode.None))
                        if (motor != null && motor.IsOwner && Time.time - _foundAt > 1.5f)
                        {
                            bool second = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name.Contains("2");
                            _beat = StoryBeat.Play("arrive:" + (second ? "island2" : "island"),
                                second ? "The far island" : "Day one",
                                second ? "Somewhere on here is a way home." : "Whatever brought you here is on the tideline.",
                                _target.transform);
                        }

                if (!Visited && _target != null && Time.frameCount % 15 == 0)
                    foreach (Player.PlayerMotor motor in FindObjectsByType<Player.PlayerMotor>(FindObjectsSortMode.None))
                        if (motor != null && motor.IsOwner
                            && Vector3.Distance(motor.transform.position, _target.transform.position) < 14f)
                            Visited = true;
                return;
            }

            // The landmarks are spawned by the server and arrive on a client whenever they arrive.
            // Polling for a few seconds is both simpler and more robust than an event that has
            // already fired by the time anybody subscribes to it.
            Landmark target = Find(_firstTarget);
            if (target != null)
            {
                Objective.Set(_firstObjective, target.transform);
                _target = target;
                _foundAt = Time.time;
                _done = true;
                return;
            }

            if (Time.time < _giveUpAt) return;

            _done = true;
            Debug.LogWarning($"[IslandIntro] No landmark called '{_firstTarget}' turned up in "
                             + $"{_patience:F0}s. The objective stands, but with no arrow on it.");
        }

        /// <summary>
        /// Case-insensitively, because there are two names for the same place: the catalog says
        /// "wreck" and the prefab was built as "Wreck". The server stamps the catalog id onto what it
        /// spawns, but a client receives the object with the prefab's own id, so a lookup that cared
        /// about case would work on the host and quietly fail on everybody else.
        /// </summary>
        static Landmark Find(string id)
        {
            foreach (Landmark landmark in Landmark.All)
            {
                if (landmark == null) continue;
                if (string.Equals(landmark.Id, id, System.StringComparison.OrdinalIgnoreCase))
                    return landmark;
            }

            return null;
        }
    }
}
