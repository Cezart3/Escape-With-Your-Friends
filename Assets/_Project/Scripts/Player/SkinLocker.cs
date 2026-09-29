using System;
using System.Collections.Generic;
using EscapeWithYourFriends.Data;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// Which weapon skins this player owns and which one each weapon wears. #209.
    ///
    /// The server decides both. **Only what is worn is replicated** - a weapon id to a skin id -
    /// because that is all a friend's screen needs to tint the pistol in your hand; what you own but
    /// are not wearing is nobody else's business, and lives in a plain server-side set that
    /// <see cref="Core.RunSave"/> reads and refills.
    /// </summary>
    public class SkinLocker : NetworkBehaviour
    {
        readonly SyncDictionary<string, string> _worn = new();

        // Server only. Not replicated; see the class comment.
        readonly HashSet<string> _owned = new();

        /// <summary>Fired on every peer when what a weapon wears changes.</summary>
        public event Action Changed;

        void Awake() => _worn.OnChange += (op, key, value, asServer) => Changed?.Invoke();

        /// <summary>Server only. The skin ids this player owns.</summary>
        public IEnumerable<string> Owned => _owned;

        /// <summary>Server only. The worn skin ids, for the save.</summary>
        public IEnumerable<string> WornIds => _worn.Values;

        /// <summary>Server only.</summary>
        public bool Owns(string skinId) => _owned.Contains(skinId);

        /// <summary>The skin id a weapon wears, or null.</summary>
        public string WornOn(string weaponId)
            => weaponId != null && _worn.TryGetValue(weaponId, out string skin) ? skin : null;

        /// <summary>The tint a weapon wears, or null for its own colours.</summary>
        public Color? TintFor(string weaponId)
            => SkinCatalog.Find(WornOn(weaponId), out WeaponSkin skin) ? skin.Tint : null;

        /// <summary>Server only. Owns it from now on and wears it. Also what buying an owned one again does.</summary>
        [Server]
        public void ServerUnlock(WeaponSkin skin)
        {
            _owned.Add(skin.Id);
            _worn[skin.WeaponId] = skin.Id;
        }

        /// <summary>Server only. Puts a saved locker back; ids that are no longer skins are dropped.</summary>
        [Server]
        public void ServerRestore(IEnumerable<string> owned, IEnumerable<string> worn)
        {
            _owned.Clear();
            _worn.Clear();

            foreach (string id in owned)
                if (SkinCatalog.Find(id, out WeaponSkin skin)) _owned.Add(skin.Id);

            foreach (string id in worn)
                if (_owned.Contains(id) && SkinCatalog.Find(id, out WeaponSkin skin)) _worn[skin.WeaponId] = skin.Id;
        }
    }
}
