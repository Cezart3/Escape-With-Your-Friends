using EscapeWithYourFriends.Player;
using FishNet.Object;
using UnityEngine;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// Something warm to stand next to. On the campfire, and later on the shop's stove and the
    /// wreck's burning fuselage.
    ///
    /// This is what makes a campfire worth the four planks. #40 left night at a warmth of 40 - cold
    /// and worth solving, but with nothing yet to solve it with. This is the answer, and it is why the
    /// campfire is in the tier-1 progression rather than decoration.
    ///
    /// Server only. It pushes warmth into <see cref="SurvivalStats.ServerFeed"/>, which is the same
    /// door a coconut goes through, so nothing here has to know how warmth works.
    /// </summary>
    public class HeatSource : NetworkBehaviour
    {
        [Tooltip("Metres. Standing in it should be an obvious place to stand, not a pixel-hunt.")]
        [Min(0.5f)]
        [SerializeField] float _radius = 5f;

        [Tooltip("Warmth per second added while inside. Beats the night's drain several times over, "
                 + "so a fire is a solution rather than a slower loss.")]
        [SerializeField] float _warmthPerSecond = 12f;

        [Tooltip("Seconds between sweeps. Nobody can tell a quarter of a second, and this runs for "
                 + "the whole session on every fire anybody ever built.")]
        [Min(0.05f)]
        [SerializeField] float _interval = 0.25f;

        float _nextSweepAt;

        public float Radius => _radius;

        void Update()
        {
            if (!IsServerStarted || Time.time < _nextSweepAt) return;

            _nextSweepAt = Time.time + _interval;

            // OverlapSphere against nothing in particular would catch terrain and trees. The players
            // are the only thing that can be warmed, and there are four of them, so the sweep is over
            // the survival components directly.
            SurvivalStats[] players = FindObjectsByType<SurvivalStats>(FindObjectsSortMode.None);
            if (players.Length == 0) return;

            float radius = _radius * _radius;
            Vector3 here = transform.position;

            foreach (SurvivalStats player in players)
            {
                if (player == null || !player.IsSpawned) continue;
                if ((player.transform.position - here).sqrMagnitude > radius) continue;

                player.ServerFeed(warmth: _warmthPerSecond * _interval);
            }
        }

        /// <summary>Bake time only.</summary>
        public void Configure(float radius, float warmthPerSecond)
        {
            _radius = radius;
            _warmthPerSecond = warmthPerSecond;
        }
    }
}
