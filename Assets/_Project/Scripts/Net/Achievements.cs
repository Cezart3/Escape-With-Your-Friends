using System.Collections.Generic;
using FishNet;
using FishNet.Broadcast;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;

namespace EscapeWithYourFriends.Net
{
    /// <summary>Server to one client: you earned this. See <see cref="Achievements"/>.</summary>
    public struct AchievementUnlock : IBroadcast
    {
        public string Id;
    }

    /// <summary>
    /// Achievements for the stupid stuff. #92.
    ///
    /// **The server decides; the player's own machine unlocks.** Only the host saw it happen - a
    /// client does not know it ran anybody over, only that the car it was in moved - but an
    /// achievement belongs to the Steam account at the keyboard, and the host's Steam client cannot
    /// unlock anything for somebody else. So every award is one reliable broadcast to the connection
    /// that owns the body that earned it, and that client makes the Steam call.
    ///
    /// **No event bus.** Each award is one line in the place that already knows it happened:
    ///
    /// - <see cref="RanOverFriend"/>: <c>VehicleImpact</c>, to whoever is in the driver's seat when a
    ///   player goes under the wheels.
    /// - <see cref="LostItAll"/>: <c>RouletteWheel.Settle</c>, to anybody who bet this spin and is now
    ///   holding no chips.
    /// - <see cref="DiedTen"/>: <c>Health</c>, on the tenth death of one body.
    /// - <see cref="FirstTry"/>: <c>PlaneVoyage</c>, to everybody aboard when the run ends, if that
    ///   aeroplane never came back down after taking off. The issue says "land the plane on the first
    ///   try"; this game ends in the air, so the first flight being the last one is the version of it
    ///   that exists.
    ///
    /// The ids are the API names the Steamworks backend will need. Spacewar (480) has its own fixed
    /// set, so until the real app id exists a Trigger does nothing and the log line is the evidence.
    /// </summary>
    public static class Achievements
    {
        public const string RanOverFriend = "RAN_OVER_FRIEND";
        public const string LostItAll = "LOST_IT_ALL";
        public const string DiedTen = "DIED_TEN";
        public const string FirstTry = "FIRST_TRY";

        /// <summary>Deaths of one body in one run that earn <see cref="DiedTen"/>.</summary>
        public const int Deaths = 10;

        /// <summary>Client side. What this machine has been told it earned, for the harness.</summary>
        public static readonly HashSet<string> Unlocked = new();

        /// <summary>Client side. Every unlock message that arrived, repeats included.</summary>
        public static int Received { get; private set; }

        /// <summary>Server side. Every award sent, as "ID objectId".</summary>
        public static readonly List<string> Awarded = new();

        /// <summary>Server only. Tells the owner of <paramref name="body"/> they earned it.</summary>
        public static void ServerAward(NetworkObject body, string id)
        {
            if (body == null || !body.IsServerInitialized) return;

            // Natives and boars die and drive too, and nobody is playing them.
            if (!body.Owner.IsValid) return;

            Awarded.Add($"{id} {body.ObjectId}");
            Debug.Log($"[Achievements] {id} to object {body.ObjectId} (connection {body.OwnerId}).");

            InstanceFinder.ServerManager.Broadcast(body.Owner, new AchievementUnlock { Id = id });
        }

        /// <summary>Client side. Registered once by <see cref="NetworkBootstrap"/>.</summary>
        internal static void OnUnlock(AchievementUnlock unlock, Channel channel)
        {
            Received++;
            if (!Unlocked.Add(unlock.Id)) return;

            Debug.Log($"[Achievements] unlocked {unlock.Id}.");

            if (!SteamRuntime.Available) return;

            var achievement = new Steamworks.Data.Achievement(unlock.Id);
            if (!achievement.State) achievement.Trigger();
        }
    }
}
