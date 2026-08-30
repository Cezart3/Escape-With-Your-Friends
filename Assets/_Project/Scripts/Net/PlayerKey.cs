using System;
using EscapeWithYourFriends.Core;
using UnityEngine;

namespace EscapeWithYourFriends.Net
{
    /// <summary>
    /// Who this machine is, in a form that survives a reconnect.
    ///
    /// Everything else the server can see about a connection is useless for the one question #111
    /// asks — "is this the player whose corpse is lying over there?". FishNet reuses client ids, so
    /// keying by id hands a body to whoever takes the freed slot, which is worse than not adopting at
    /// all. Every Tugboat test client shares the address 127.0.0.1, and behind one flat four friends
    /// share it too. So the key has to come from the client, and be the same string each time it
    /// connects.
    ///
    /// **Resolution order, and why it is this order.**
    /// <list type="number">
    /// <item><c>-playerKey &lt;string&gt;</c>. An explicit flag always wins, because it is the only
    /// thing that makes the feature testable: two headless processes on one machine share one Steam
    /// login and one PlayerPrefs store, so without the flag a host and its client would resolve to
    /// the same key and every reconnect test would pass for the wrong reason.</item>
    /// <item>The Steam id, when Steam is up. It is the real answer for a shipped game: tied to an
    /// account, not to a file anyone can copy, and already present because FishyFacepunch needs it.</item>
    /// <item>A GUID in <see cref="PlayerPrefs"/>, generated once. This is the Tugboat fallback and it
    /// is a *name*, not a credential — see the warning below.</item>
    /// </list>
    ///
    /// **This is identification, not authentication.** A client sends whatever string it likes, so
    /// someone who learns a key can claim the body it belongs to. That is deliberate for now: the
    /// server validates only that the key is not already held by a live connection, and the thing at
    /// stake is a ragdoll in a four-player game friends start from a Steam invite. It stops being
    /// acceptable the moment a body carries a run's worth of loot (#41) *and* strangers can join, and
    /// at that point the Steam path stops being a preference and becomes the only allowed key, with
    /// the id read from the connection rather than from the client's broadcast.
    /// </summary>
    public static class PlayerKey
    {
        /// <summary>PlayerPrefs slot for the generated fallback key. Namespaced; prefs are global.</summary>
        const string PrefsKey = "ewyf.playerKey";

        static string _local;

        /// <summary>
        /// This process's key, resolved once on first use.
        ///
        /// Lazy rather than resolved in an <c>Awake</c>: the Steam path needs <see cref="SteamRuntime"/>
        /// to have finished starting, and the first thing that asks for this is the authenticator's
        /// broadcast on connect, which is long after every Awake in the scene has run.
        /// </summary>
        public static string Local => _local ??= Resolve();

        /// <summary>
        /// A key shortened for logs. Full keys are a Steam id or a GUID — one identifies a person
        /// across every game they own, the other is thirty-two characters of noise that makes a log
        /// line unreadable. Eight characters is plenty to tell four players apart in a test run.
        /// </summary>
        public static string Short(string key)
        {
            if (string.IsNullOrEmpty(key)) return "(none)";
            return key.Length <= 8 ? key : key.Substring(0, 8) + "…";
        }

        static string Resolve()
        {
            string flag = CommandLine.GetString("-playerKey", null);
            if (!string.IsNullOrWhiteSpace(flag))
            {
                Debug.Log($"[PlayerKey] {Short(flag)} from -playerKey.");
                return flag;
            }

            if (SteamRuntime.Available && SteamRuntime.LocalSteamId != 0)
            {
                string steam = "steam:" + SteamRuntime.LocalSteamId;
                Debug.Log($"[PlayerKey] {Short(steam)} from Steam.");
                return steam;
            }

            string stored = PlayerPrefs.GetString(PrefsKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(stored))
            {
                Debug.Log($"[PlayerKey] {Short(stored)} from PlayerPrefs.");
                return stored;
            }

            string generated = "local:" + Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(PrefsKey, generated);

            // Written now rather than at quit: the run this key was generated for is quite likely the
            // one that crashes, and a key that only persists after a clean exit persists nothing.
            PlayerPrefs.Save();

            Debug.Log($"[PlayerKey] {Short(generated)} generated and stored.");
            return generated;
        }
    }
}
