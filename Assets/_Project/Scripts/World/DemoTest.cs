using System.Collections;
using System.Linq;
using EscapeWithYourFriends.AI;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.Player;
using EscapeWithYourFriends.Vehicles;
using FishNet;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EscapeWithYourFriends.World
{
    /// <summary>
    /// The part of #90 a harness can check, behind <c>-demoTest</c>: in the demo, leaving the first
    /// island ends the run on every machine instead of taking anybody anywhere. Whether the demo is
    /// fun and sells wishlists is not a thing a log can say.
    ///
    /// A pair on <c>-scene island -noNatives -noAnimals</c>, both with <c>-demo -demoTest</c>, the
    /// host also with <c>-save</c> so that "the demo keeps no save" is a real question. The host flies
    /// the plane off the map with the castaway still on the beach - the exact move that crosses to
    /// the second island in the full game - and the client checks that it was told the run ended,
    /// with the death the host gave it, and that no second island arrived.
    /// </summary>
    public class DemoTest : MonoBehaviour
    {
        const float WaitForWorld = 60f;
        const float WaitForPlayers = 90f;
        const float ClientPatience = 240f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-demoTest")) return;
            _started = true;

            var go = new GameObject("DemoTest");
            DontDestroyOnLoad(go);
            go.AddComponent<DemoTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null) yield return null;

            Check("this is the demo (run it with -demo)", Demo.On);

            yield return InstanceFinder.NetworkManager.IsServerStarted ? Server() : Client();

            Debug.Log($"[DemoTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[DemoTest] {_failed} check(s) failed.");
        }

        IEnumerator Client()
        {
            float deadline = Time.time + ClientPatience;
            while (Time.time < deadline && !RunSummary.Over) yield return new WaitForSeconds(0.5f);

            // A crossing, if one had started, would have had time to load.
            yield return new WaitForSeconds(3f);

            Check("the client was told the run is over", RunSummary.Over);
            Check($"with the host's figures ({RunSummary.Deaths} death(s))", RunSummary.Deaths == 1);
            Check("and nobody sent it to the second island", !SceneManager.GetSceneByName("Island2").isLoaded);
        }

        IEnumerator Server()
        {
            Check("the demo keeps no save, even when asked to", !RunSave.Armed);

            float deadline = Time.time + WaitForWorld;
            while (Time.time < deadline && (Castaway.Instance == null || Plane() == null))
                yield return new WaitForSeconds(0.5f);

            Vehicle plane = Plane();
            if (Castaway.Instance == null || plane == null)
            {
                Fail("the first island has a plane and a castaway - run after a POI bake");
                yield break;
            }

            PlayerMotor host = null, guest = null;
            deadline = Time.time + WaitForPlayers;
            while (Time.time < deadline && guest == null)
            {
                PlayerMotor[] players = FindObjectsByType<PlayerMotor>(FindObjectsSortMode.None)
                                        .Where(p => p != null && p.IsSpawned).ToArray();
                host = players.FirstOrDefault(p => p.IsOwner);
                guest = players.FirstOrDefault(p => !p.IsOwner);
                yield return new WaitForSeconds(0.5f);
            }

            if (host == null || guest == null)
            {
                Fail("a second player joined - run the client with -demo -demoTest too");
                yield break;
            }

            RunSummary.Reset();

            // Something for the ending to carry, so the client's figures are a real comparison.
            var health = guest.GetComponent<Health>();
            health.ServerKill(new DamageInfo(0f, DamageType.Blunt));
            yield return null;
            health.ServerRescue();
            health.ServerRevive(1f);

            plane.GetComponent<PlaneAssembly>()?.ServerFitAll();
            yield return null;

            host.ServerTeleport(plane.transform.position + plane.transform.right * 4f, 0f);
            yield return null;

            int seat = plane.ServerEnter(host.NetworkObject);
            Check($"the host is flying it (seat {seat})", seat == 0);
            Check("with the castaway still on the beach", Castaway.Instance.Where != Castaway.Stage.Aboard);

            string was = GameSceneLoader.Current;

            Terrain terrain = Terrain.activeTerrain;
            Vector3 size = terrain.terrainData.size;
            Vector3 centre = terrain.transform.position + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f);
            float out_ = Mathf.Max(size.x, size.z) * 0.5f + 160f;

            var body = plane.GetComponent<Rigidbody>();
            float ended = Time.time + 30f;
            while (Time.time < ended && !RunSummary.Over)
            {
                plane.transform.position = new Vector3(centre.x + out_, 300f, centre.z);
                if (body != null) body.linearVelocity = Vector3.zero;
                Physics.SyncTransforms();
                yield return new WaitForSeconds(0.25f);
            }

            Check("flying off the first island ends the demo", RunSummary.Over);
            Check($"with the death in it ({RunSummary.Deaths})", RunSummary.Deaths == 1);

            yield return new WaitForSeconds(3f);

            Check($"and nobody changed islands ({GameSceneLoader.Current})",
                  GameSceneLoader.Current == was && !SceneManager.GetSceneByName("Island2").isLoaded);
        }

        static Vehicle Plane()
            => Vehicle.All.FirstOrDefault(v => v != null && v.IsSpawned && v.GetComponent<PlaneController>() != null);

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[DemoTest] FAILED: {what}.");
        }

        void Fail(string what) => Check(what, false);
    }
}
