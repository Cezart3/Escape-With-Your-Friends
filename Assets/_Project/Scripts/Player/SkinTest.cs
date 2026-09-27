using System;
using System.Collections;
using System.Linq;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Net;
using EscapeWithYourFriends.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace EscapeWithYourFriends.Player
{
    /// <summary>
    /// The half of T9 a terminal can settle, behind <c>-skinTest</c>. Host, solo, either island.
    ///
    /// Whether the bodies look right is an eye's job. What this checks is everything that makes them
    /// look wrong no matter who is looking: a body over budget, the colour on the skin, the primitives
    /// still drawn through it, an animator that does not walk, and above all a skin that stays
    /// standing while the ragdoll under it goes flying - which is the one failure that would make the
    /// whole art pass worse than the capsules it replaced.
    /// </summary>
    public class SkinTest : MonoBehaviour
    {
        /// <summary>Degrees a limb may lie off its physics bone. By construction it is zero; this catches a wrong side or a stale offset.</summary>
        const float Aligned = 20f;

        /// <summary>Metres a hip, elbow or knee may sit from its collider: about a limb's thickness.</summary>
        const float Near = 0.2f;

        static bool _started;

        int _passed;
        int _failed;

        internal static void Begin()
        {
            if (_started || !CommandLine.HasFlag("-skinTest")) return;
            _started = true;

            var go = new GameObject("SkinTest");
            DontDestroyOnLoad(go);
            go.AddComponent<SkinTest>();
        }

        void OnEnable() => StartCoroutine(Run());

        IEnumerator Run()
        {
            GameObject player = null;
            for (float waited = 0f; player == null && waited < 40f; waited += 0.5f)
            {
                yield return new WaitForSeconds(0.5f);

                foreach (NetworkPlayerRegistry.PlayerBody body in NetworkPlayerRegistry.Players)
                    if (body.IsValid && body.Object.IsOwner) player = body.Object.gameObject;
            }

            Check("a player spawned", player != null);
            if (player == null) { Finish(); yield break; }

            // Landed, and the colour slot has arrived.
            yield return new WaitForSeconds(3f);

            Check("headless keeps the bodies only under -skinTest",
                  !CharacterSkin.Wanted(true, false) && CharacterSkin.Wanted(true, true) && CharacterSkin.Wanted(false, false));

            var skin = player.GetComponent<CharacterSkin>();
            Check("the player wears a CharacterSkin (run CharacterArt.Build, then BuildPlayerPrefab)",
                  skin != null && skin.enabled && skin.Bodies.Length > 0);
            if (skin == null || !skin.enabled || skin.Bodies.Length == 0) { Finish(); yield break; }

            CharacterSkin.Body[] bodies = skin.Bodies;
            int cap = ArtVisual.Cap(ArtCategory.Character);

            foreach (CharacterSkin.Body body in bodies)
            {
                string name = body.Root != null ? body.Root.name : "(missing)";
                int links = body.Bones?.Length ?? 0;

                bool wired = body.Root != null && body.Animator != null && body.Band != null && body.Hips != null
                             && body.Animator.runtimeAnimatorController != null
                             && body.Animator.avatar != null && body.Animator.avatar.isHuman
                             && body.Renderers != null && body.Renderers.Length > 0 && links == 11
                             && body.To.Length == links && body.Physics.Length == links
                             && body.PhysicsTo.Length == links && body.Offsets.Length == links
                             && body.Bones.Concat(body.To).Concat(body.Physics).Concat(body.PhysicsTo).All(t => t != null);
                Check($"{name} is wired: animator, controller, humanoid avatar, band, {links}/11 links", wired);
                if (body.Root == null) continue;

                int triangles = ArtVisual.Triangles(body.Root);
                Check($"{name} is {triangles} triangles, cap {cap}", triangles <= cap);

                int materials = body.Renderers.Where(r => r != null).SelectMany(r => r.sharedMaterials)
                                    .Where(m => m != null).Distinct().Count();
                Check($"{name} wears {materials} material(s), at most 3", materials is > 0 and <= 3);

                Debug.Log($"[SkinTest] {name}: {triangles} triangles, {materials} materials, {links} links.");
            }

            int shown = bodies.Count(b => b.Root != null && b.Root.activeSelf);
            Check($"exactly one body is shown ({shown})", shown == 1);

            CharacterSkin.Body worn = skin.Active;
            var identity = player.GetComponent<PlayerIdentity>();
            Check("and it is the one the colour slot picks",
                  worn != null && identity != null && worn == bodies[identity.ColorIndex % bodies.Length]);
            if (worn == null) { Finish(); yield break; }

            var ragdoll = player.GetComponent<RagdollController>();
            Renderer[] primitives = ragdoll.HipBone.GetComponentsInChildren<Renderer>(true);
            Check($"the {primitives.Length} primitives are not drawn", primitives.Length > 0 && primitives.All(r => !r.enabled));

            Check("the player colour is on the band", worn.Band.HasPropertyBlock());
            Check("and not on the textured body", worn.Renderers.All(r => !r.HasPropertyBlock()));
            Check("your own body is shadow only",
                  worn.Renderers.All(r => r.shadowCastingMode == ShadowCastingMode.ShadowsOnly));

            Check($"standing still reads as still (Speed {skin.Speed:F2})", skin.Speed < 0.5f);

            // Walking. Forced rather than driven: the motor is a predicted body and fights anything
            // that moves it from outside, and what is under test here is the animator, not the motor.
            int legIndex = Array.FindIndex(worn.Physics, t => t != null && t.name == "UpperLeg.L");
            Transform thigh = legIndex >= 0 ? worn.Bones[legIndex] : null;

            skin.ForceSpeed = 3f;
            yield return new WaitForSeconds(0.6f);

            Quaternion first = thigh != null ? thigh.localRotation : Quaternion.identity;
            float swing = 0f;
            for (float t = 0f; t < 1.5f; t += Time.deltaTime)
            {
                if (thigh != null) swing = Mathf.Max(swing, Quaternion.Angle(first, thigh.localRotation));
                yield return null;
            }

            float blend = worn.Animator.GetFloat("Speed");
            skin.ForceSpeed = null;

            Check($"walking drives the blend (Speed {blend:F2})", blend > 1.5f);
            Check($"and the legs swing ({swing:F0} degrees)", swing > 10f);

            // Limp. A modest shove, so it is lying still by the time it is measured.
            ragdoll.EnableRagdoll(player.transform.forward * 40f, Vector3.zero);
            yield return new WaitForSeconds(2.5f);

            Check("limp, the body follows the ragdoll", skin.Tracking && !worn.Animator.enabled);

            float worst = 0f;
            string worstLink = "";
            float farthest = 0f;
            string farthestJoint = "";

            for (int i = 0; i < worn.Bones.Length; i++)
            {
                float angle = Vector3.Angle(worn.To[i].position - worn.Bones[i].position,
                                            worn.PhysicsTo[i].position - worn.Physics[i].position);
                if (angle > worst) { worst = angle; worstLink = worn.Physics[i].name; }

                string joint = worn.Physics[i].name;
                if (!(joint == "Hips" || joint.StartsWith("LowerArm") || joint.StartsWith("LowerLeg"))) continue;

                float gap = Vector3.Distance(worn.Bones[i].position, worn.Physics[i].position);
                if (gap > farthest) { farthest = gap; farthestJoint = joint; }
            }

            Check($"every limb lies along its physics bone (worst {worstLink}, {worst:F0} degrees)", worst < Aligned);
            Check($"and hips, elbows and knees sit on theirs (worst {farthestJoint}, {farthest:F2} m)", farthest < Near);

            ragdoll.DisableRagdoll();
            yield return new WaitForSeconds(1f);

            Check("standing again, the animator has it back", !skin.Tracking && worn.Animator.enabled);

            skin.ForceCarry = true;
            yield return new WaitForSeconds(1f);

            float weight = worn.Animator.layerCount > 1 ? worn.Animator.GetLayerWeight(1) : 0f;
            skin.ForceCarry = false;
            Check($"carrying raises the arms (layer weight {weight:F2})", weight > 0.9f);

            Finish();
        }

        void Finish()
        {
            Debug.Log($"[SkinTest] {_passed} passed, {_failed} failed.");
            if (_failed > 0) Debug.LogError($"[SkinTest] {_failed} check(s) failed.");
        }

        void Check(string what, bool passed)
        {
            if (passed) { _passed++; return; }

            _failed++;
            Debug.LogError($"[SkinTest] FAILED: {what}.");
        }
    }
}
