using System.Collections.Generic;
using FishNet;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Transporting;

namespace EscapeWithYourFriends.World
{
    /// <summary>Client to server: I am watching this scene (or stopped), or I want it skipped.</summary>
    public struct SkipWish : IBroadcast
    {
        public string Id;
        public bool Watching, Skip;
    }

    /// <summary>Server to everybody: how the vote on a scene stands, and whether it has carried.</summary>
    public struct SkipTally : IBroadcast
    {
        public string Id;
        public int Votes, Needed;
        public bool Carried;
    }

    /// <summary>
    /// #272. Skipping a scene is a vote. While a scene plays its watchers cannot move, so one
    /// player skipping alone would leave the others frozen beside someone who can; instead a scene
    /// ends for everyone watching it once more than half of them have pressed a key. Alone, one
    /// key is a majority and nothing changes.
    ///
    /// Broadcasts, not RPCs: no networked object has to exist for it, so it works in the attic as
    /// well as on either island. The server counts; the watchers are whoever said they were.
    /// Offline (no client running) a wish is answered on the spot.
    /// </summary>
    public static class SkipVote
    {
        static readonly Dictionary<string, HashSet<int>> _watching = new();
        static readonly Dictionary<string, HashSet<int>> _votes = new();

        /// <summary>A strict majority of the watchers: 1 of 1, 2 of 2, 2 of 3, 3 of 4.</summary>
        internal static int Needed(int watchers) => watchers / 2 + 1;

        internal static void Watching(string id, bool watching) => Send(new SkipWish { Id = id, Watching = watching });

        internal static void Want(string id) => Send(new SkipWish { Id = id, Watching = true, Skip = true });

        static void Send(SkipWish wish)
        {
            if (InstanceFinder.ClientManager != null && InstanceFinder.ClientManager.Started)
                InstanceFinder.ClientManager.Broadcast(wish);
            else
                OnTally(Count(-1, wish), Channel.Reliable);
        }

        internal static void OnWish(NetworkConnection from, SkipWish wish, Channel channel)
        {
            SkipTally tally = Count(from.ClientId, wish);
            InstanceFinder.ServerManager.Broadcast(tally);
        }

        /// <summary>The server's book-keeping, apart from the network so the test can drive it.</summary>
        internal static SkipTally Count(int client, SkipWish wish)
        {
            if (!_watching.TryGetValue(wish.Id, out HashSet<int> watching)) _watching[wish.Id] = watching = new HashSet<int>();
            if (!_votes.TryGetValue(wish.Id, out HashSet<int> votes)) _votes[wish.Id] = votes = new HashSet<int>();

            if (wish.Watching) watching.Add(client);
            else { watching.Remove(client); votes.Remove(client); }
            if (wish.Skip) votes.Add(client);

            // ponytail: a watcher who disconnects mid-scene stays counted; the scene still ends by
            // itself inside forty seconds. Drop them on ServerManager.OnRemoteConnectionState if it matters.
            var tally = new SkipTally { Id = wish.Id, Votes = votes.Count, Needed = Needed(watching.Count) };
            tally.Carried = votes.Count > 0 && votes.Count >= tally.Needed;
            if (tally.Carried || watching.Count == 0)
            {
                _watching.Remove(wish.Id);
                _votes.Remove(wish.Id);
            }

            return tally;
        }

        internal static void OnTally(SkipTally tally, Channel channel) => StoryBeat.Tally(tally);
    }
}
