using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using FishNet;
using UnityEngine;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// The half of #77 a terminal can settle, behind <c>-animTest</c>. Solo, any scene with a player.
    ///
    /// Whether the walk looks good is a playtest. Whether it is a walk at all is arithmetic, and it is
    /// the part that breaks silently: a gait where both legs swing together is a hopping penguin, a
    /// gait that never returns to rest is a permanent stagger, and a gait that keeps writing bones
    /// while the body is ragdolled is a corpse with a twitching hip - the exact bug #50 spent a week
    /// on, arriving from the other direction.
    ///
    /// So the body is dragged forward by hand, which is all <see cref="BodyAnimator"/> reads, and the
    /// bones are measured against the pose the rig was built in.
    /// </summary>
    public class AnimTest : MonoBehaviour
    {
        const float WaitForPlayer = 60f;

        /// <summary>Metres per second to drag the body at. Comfortably a run.</summary>
        const float DragSpeed = 4f;

        /// <summary>Degrees off the rest pose that count as "this bone is moving".</summary>
        const float Moving = 5f;

        /// <summary>Degrees off the rest pose that count as "this bone has settled".</summary>
        const float Settled = 4f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-animTest")) return;

            _started = true;

            var go = new GameObject("AnimTest");
            DontDestroyOnLoad(go);
            go.AddComponent<AnimTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            while (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServerStarted)
                yield return null;

            RagdollController body = null;
            float deadline = Time.time + WaitForPlayer;

            while (Time.time < deadline && body == null)
            {
                body = FindObjectsByType<RagdollController>(FindObjectsSortMode.None)
                       .FirstOrDefault(r => r != null && r.HipBone != null
                                            && r.GetComponent<FishNet.Object.NetworkObject>() != null);

                if (body == null) yield return new WaitForSeconds(0.5f);
            }

            if (body == null)
            {
                Debug.LogError("[AnimTest] No player body spawned. Nothing was checked.");
                yield break;
            }

            Transform root = body.transform;
            Transform legL = Bone(body, "UpperLeg.L");
            Transform legR = Bone(body, "UpperLeg.R");
            Transform armL = Bone(body, "UpperArm.L");

            if (legL == null || legR == null || armL == null)
            {
                Debug.LogError("[AnimTest] The rig has no UpperLeg.L/UpperLeg.R/UpperArm.L. "
                               + "Run PlayerPrefabBuilder.BuildPlayerPrefab.");
                yield break;
            }

            // Two frames: one for the watcher to notice the body, one for it to pose it.
            yield return null;
            yield return null;

            Quaternion restL = legL.localRotation;
            Quaternion restR = legR.localRotation;
            Quaternion restArm = armL.localRotation;

            Check($"the watcher found the skeleton ({BodyAnimator.Tracked} tracked)",
                  BodyAnimator.Tracked >= 1);

            // --- walking -------------------------------------------------------------------------

            float swungL = 0f;
            int opposedLegs = 0;
            int opposedArm = 0;
            int frames = 0;

            for (float t = 0f; t < 3f; t += Time.deltaTime)
            {
                root.position += root.forward * (DragSpeed * Time.deltaTime);

                // The pose is written in LateUpdate, so read it at the end of the same frame.
                yield return new WaitForEndOfFrame();

                float pitchL = Pitch(legL, restL);
                float pitchR = Pitch(legR, restR);
                float pitchArm = Pitch(armL, restArm);

                swungL = Mathf.Max(swungL, Mathf.Abs(pitchL));
                frames++;

                if (Mathf.Abs(pitchL) < 2f) continue;

                if (pitchL * pitchR < 0f) opposedLegs++;
                if (pitchL * pitchArm < 0f) opposedArm++;
            }

            Debug.Log($"[AnimTest] {frames} frames of walking, legs peaked at {swungL:F1} deg, "
                      + $"{opposedLegs} frames with the legs opposed, {opposedArm} with the arm opposed.");

            Check($"walking bends the legs ({swungL:F1} deg off the rest pose)", swungL > Moving);

            Check($"the legs swing opposite each other ({opposedLegs} of {frames} frames)",
                  opposedLegs > frames / 4);

            Check($"and the arms counter-swing the legs ({opposedArm} of {frames} frames)",
                  opposedArm > frames / 4);

            Check($"a moving body is being posed ({BodyAnimator.Posed} this frame)",
                  BodyAnimator.Posed >= 1);

            // --- standing still ------------------------------------------------------------------

            yield return new WaitForSeconds(1.5f);
            yield return new WaitForEndOfFrame();

            float restingL = Mathf.Abs(Pitch(legL, restL));
            Check($"standing still returns the legs to rest ({restingL:F1} deg)", restingL < Settled);

            // --- ragdolled -----------------------------------------------------------------------

            body.EnableRagdoll(Vector3.zero, root.position);

            yield return new WaitForSeconds(0.5f);
            yield return new WaitForEndOfFrame();

            int posedWhileLimp = BodyAnimator.Posed;

            yield return new WaitForEndOfFrame();
            posedWhileLimp += BodyAnimator.Posed;

            Check($"a ragdoll is left to the physics ({posedWhileLimp} poses written while limp)",
                  posedWhileLimp == 0);

            body.DisableRagdoll();

            yield return new WaitForSeconds(0.5f);
            yield return new WaitForEndOfFrame();

            Check($"and getting up hands the bones back ({BodyAnimator.Posed} posed)",
                  BodyAnimator.Posed >= 1);

            Debug.Log($"[AnimTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[AnimTest] {_failed} check(s) failed.");
        }

        static Transform Bone(RagdollController body, string name)
        {
            foreach (Transform t in body.HipBone.GetComponentsInChildren<Transform>(includeInactive: true))
                if (t.name == name) return t;

            return null;
        }

        /// <summary>Degrees this bone is pitched away from where the rig left it, signed.</summary>
        static float Pitch(Transform bone, Quaternion rest)
        {
            float x = (Quaternion.Inverse(rest) * bone.localRotation).eulerAngles.x;
            return x > 180f ? x - 360f : x;
        }

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[AnimTest] FAILED: {what}.");
        }
    }
}
