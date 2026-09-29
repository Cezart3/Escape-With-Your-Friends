using System;
using System.Collections;
using System.Linq;
using EscapeWithYourFriends.AI;
using EscapeWithYourFriends.Combat;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.Items;
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

            // The arm itself, not only the weight this script wrote: an empty layer or a wrong mask
            // leaves the arm where the idle has it.
            int armIndex = Array.FindIndex(worn.Physics, t => t != null && t.name == "UpperArm.L");
            Transform arm = armIndex >= 0 ? worn.Bones[armIndex] : null;
            Quaternion down = arm != null ? arm.localRotation : Quaternion.identity;

            skin.ForceCarry = true;
            yield return new WaitForSeconds(1f);

            float weight = worn.Animator.layerCount > 1 ? worn.Animator.GetLayerWeight(1) : 0f;
            float raised = arm != null ? Quaternion.Angle(down, arm.localRotation) : 0f;
            skin.ForceCarry = false;
            Check($"carrying raises the arms (layer weight {weight:F2}, upper arm {raised:F0} degrees)",
                  weight > 0.9f && raised > 20f);

            // Held: the selected weapon in the right hand, off the replicated inventory.
            var inventory = player.GetComponent<Inventory>();
            WeaponCatalog weapons = WeaponCatalog.Active;
            WeaponDef armed = weapons == null ? null
                : Enumerable.Range(1, weapons.Count).Select(i => weapons.At((ushort)i))
                            .FirstOrDefault(d => d != null && d.Item != null && d.ViewPrefab != null);
            Check("a weapon with a model is in the catalog (run WeaponFactory.Build)", armed != null && inventory != null);

            if (armed != null && inventory != null)
            {
                inventory.Add(armed.Item, 1);
                int slot = Enumerable.Range(0, inventory.SlotCount).FirstOrDefault(s => inventory[s].Def == armed.Item);
                inventory.ServerSelect(slot);

                // On a host the inventory's Changed comes on the client pass of the next network tick,
                // not the next frame, and a headless frame is far shorter than a tick.
                for (float waited = 0f; skin.Held == null && waited < 2f; waited += Time.deltaTime)
                    yield return null;

                GameObject held = skin.Held;
                Transform hand = worn.Animator.GetBoneTransform(HumanBodyBones.RightHand);
                Renderer[] drawn = held != null ? held.GetComponentsInChildren<Renderer>() : Array.Empty<Renderer>();
                Check($"the selected {armed.Id} is drawn in the right hand",
                      hand != null && held != null && held.transform.IsChildOf(hand) && drawn.Length > 0);
                Check("and, like your own body, only as a shadow",
                      drawn.Length > 0 && drawn.All(r => r.shadowCastingMode == ShadowCastingMode.ShadowsOnly));
                Check("with no collider of its own",
                      held != null && held.GetComponentsInChildren<Collider>().All(c => !c.enabled));

                // Wrist to model, in the hand's space: a grip read off the wrong bones lands a metre out.
                Bounds? onHand = held != null && hand != null ? WorldItem.Drawn(hand, held) : null;
                float gap = onHand is Bounds box ? Mathf.Sqrt(box.SqrDistance(Vector3.zero)) : float.PositiveInfinity;
                Check($"the hand closes on it ({gap:F2} m from the wrist)", gap < 0.1f);

                yield return new WaitForSeconds(1f);
                float armedWeight = worn.Animator.layerCount > 2 ? worn.Animator.GetLayerWeight(2) : 0f;
                Check($"and the arms hold it like a {(armed.Kind == WeaponKind.Melee ? "blade" : "gun")} " +
                      $"(layer weight {armedWeight:F2}; re-run CharacterArt.Build if 0)",
                      armedWeight > 0.9f && skin.ArmedWeight > 0.9f);

                // Reload (#204): the weapon's own event, as every peer gets it. A gun only.
                if (armed.Kind != WeaponKind.Melee)
                {
                    skin.OnReloading(armed, true);
                    bool reloading = false;
                    for (float waited = 0f; !reloading && waited < 1f; waited += Time.deltaTime)
                    {
                        yield return null;
                        reloading = worn.Animator.GetCurrentAnimatorStateInfo(2).IsName("Reload");
                    }

                    Check("a reload starts the arms' reload clip within a second", reloading);
                }

                // First person: the same model hangs off the camera, where the hand's copy is a shadow.
                GameObject view = skin.View;
                Check($"and again in front of your own camera ({(Camera.main != null ? "shown" : "no camera")})",
                      view != null && view.GetComponentsInChildren<Renderer>().Length > 0
                      && view.GetComponentsInChildren<Collider>().All(c => !c.enabled)
                      && (Camera.main == null || view.activeInHierarchy));
                if (armed.Kind != WeaponKind.Melee)
                    Check("with a muzzle on each copy for the shot to leave from",
                          skin.Muzzle != null && view != null && view.GetComponentsInChildren<Transform>().Any(t => t.name == "Muzzle"));

                // Anything else selected is held too, not only weapons.
                ItemDef plain = ItemCatalog.Active == null ? null
                    : ItemCatalog.Active.Items.FirstOrDefault(d => d != null && d.WorldPrefab != null && weapons.ForItem(d) == null);
                if (plain != null)
                {
                    inventory.Add(plain, 1);
                    inventory.ServerSelect(Enumerable.Range(0, inventory.SlotCount).FirstOrDefault(s => inventory[s].Def == plain));
                    for (float waited = 0f; (skin.Held == null || !skin.Held.name.Contains(plain.Id)) && waited < 2f; waited += Time.deltaTime)
                        yield return null;

                    Bounds? size = skin.Held != null ? WorldItem.Drawn(skin.transform, skin.Held) : null;
                    Check($"a plain {plain.Id} is held too, hand-sized ({(size is Bounds b ? b.size.magnitude : 0f):F2} m)",
                          skin.Held != null && skin.Held.name.Contains(plain.Id) && skin.View != null
                          && size is Bounds s2 && s2.size.magnitude < 0.6f);
                }
            }

            // Every state of #204 exists in the controller (re-run CharacterArt.Build if not).
            Animator animator = worn.Animator;
            foreach (string parameter in new[] { "Speed", "Airborne", "Seated", "Punch", "Dead", "Armed", "Fire", "Swimming", "Reload" })
                Check($"the controller has the {parameter} parameter", animator.parameters.Any(p => p.name == parameter));

            foreach (string state in new[] { "Base Layer.Move", "Base Layer.Seated", "Base Layer.Air", "Base Layer.Punch",
                                             "Base Layer.Dead", "Base Layer.Swim", "Carry.Carry", "Armed.Gun", "Armed.Blade",
                                             "Armed.Reload" })
                Check($"the controller has the {state} state", animator.HasState(state.StartsWith("Base") ? 0 : state.StartsWith("Carry") ? 1 : 2,
                                                                                Animator.StringToHash(state)));

            // Swimming, forced: the sea is not under the spawn point.
            skin.ForceSwim = true;
            bool swimming = false;
            for (float waited = 0f; !swimming && waited < 1.5f; waited += Time.deltaTime)
            {
                yield return null;
                swimming = animator.GetCurrentAnimatorStateInfo(0).IsName("Swim");
            }

            skin.ForceSwim = false;
            Check("swimming reaches the Swim state within 1.5 s", swimming);

            bool wading = false;
            for (float waited = 0f; !wading && waited < 1.5f; waited += Time.deltaTime)
            {
                yield return null;
                wading = animator.GetCurrentAnimatorStateInfo(0).IsName("Move");
            }

            Check("and leaving the water returns to Move", wading);

            // The NPCs (T10): the barman on one island, the castaway on the other, natives if on.
            NpcSkin[] npcs = NpcSkin.Live.Where(n => n != null).ToArray();
            Check($"the island's NPCs wear bodies ({npcs.Length} found)", npcs.Length > 0);

            foreach (NpcSkin npc in npcs)
            {
                NpcSkin.Body on = npc.Active;
                int showing = npc.Bodies.Count(b => b.Root != null && b.Root.activeSelf);
                bool animated = on != null && on.Animator.enabled
                                && on.Animator.runtimeAnimatorController != null
                                && on.Animator.avatar != null && on.Animator.avatar.isHuman;
                Check($"{npc.name} shows one animated body ({showing} shown)", showing == 1 && animated);

                int boxes = npc.GetComponentsInChildren<MeshRenderer>(true)
                               .Count(r => npc.Bodies.All(b => !r.transform.IsChildOf(b.Root.transform)));
                Check($"{npc.name}'s greybox is not drawn ({boxes} box(es) left)", boxes == 0);

                if (on == null) continue;

                int triangles = ArtVisual.Triangles(on.Root);
                Check($"{npc.name} is {triangles} triangles, cap {cap}", triangles <= cap);

                if (npc.TryGetComponent(out Native _))
                    Check($"{npc.name} wears its role's warpaint", on.Band.HasPropertyBlock());

                Debug.Log($"[SkinTest] {npc.name}: {on.Root.name}, {triangles} triangles.");
            }

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
