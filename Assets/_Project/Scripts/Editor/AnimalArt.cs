using System.Collections.Generic;
using System.Linq;
using EscapeWithYourFriends.AI;
using EscapeWithYourFriends.Data;
using EscapeWithYourFriends.World;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The animals wear Quaternius's animated animals (#79, docs/ART-PLAN.md T13).
    ///
    /// The animal prefab is one prefab for every species, so every species' model hangs in it, fitted
    /// into that species' body box, and <see cref="Animal"/> shows the one its species index names.
    /// A species is matched to a model by id: the catalogue's Animal row whose id is the species id
    /// with a capital. A species without one keeps its boxes, so a fourth species dropped in by hand
    /// is still visible.
    ///
    /// Each model gets its own controller built from the clips in its own file: a speed blend of
    /// idle, walk and run, and a death that holds. The clip names differ by animal, so they are found
    /// by word, and whatever is missing is left out rather than posed in bind pose.
    /// </summary>
    internal static class AnimalArt
    {
        const string ControllerFolder = ArtCatalog.Root + "/_Animals";

        /// <summary>Hangs a model per species under <paramref name="root"/>. Empty when no kit is extracted.</summary>
        public static Animal.Skin[] Dress(GameObject root, AnimalCatalog catalog)
        {
            var skins = new List<Animal.Skin>();
            if (catalog == null) return skins.ToArray();

            foreach (AnimalDef def in catalog.Animals)
            {
                if (def == null) continue;

                ArtCatalog.Model model = ArtCatalog.Models.FirstOrDefault(
                    m => m.Category == ArtCategory.Animal && m.Id.ToLowerInvariant() == def.Id);
                if (model.Id == null) continue;

                // The body box, feet on the ground, as Animal.ApplyShape builds it. Kept in shape, so
                // the model is as big as the box lets it be and never stretched into it.
                Vector3 size = def.BodySize;
                var box = new Bounds(new Vector3(0f, size.y * 0.5f, 0f), size);

                // A quarter turn when the model's long side runs across rather than along +z. Which
                // end is the nose is not knowable from bounds; the Quaternius animals face +z.
                GameObject source = ArtLibrary.Source(model.Id);
                if (source == null) continue;
                Vector3 native = ArtLibrary.NativeBounds(source).size;
                int turns = native.x > native.z ? 1 : 0;

                string name = $"Skin.{def.Id}";
                if (!ArtDress.FitBox(root.transform, box, model.Id, true, name, turns)) continue;

                GameObject wrapper = root.transform.Find(name).gameObject;
                var skin = new Animal.Skin { Species = def.Id, Root = wrapper, Animator = Animate(wrapper, model) };
                wrapper.SetActive(false);
                skins.Add(skin);

                Debug.Log($"[AnimalArt] {def.Id} wears {model.Pack}/{model.File}"
                          + (skin.Animator != null ? ", animated." : ", with no clips: it slides."));
            }

            return skins.ToArray();
        }

        static Animator Animate(GameObject wrapper, ArtCatalog.Model model)
        {
            string path = ArtCatalog.PathOf(model);
            AnimationClip[] clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__")).ToArray();

            Debug.Log($"[AnimalArt] {model.File}: {clips.Length} clip(s): "
                      + string.Join(", ", clips.Select(c => c.name)));

            AnimationClip idle = Pick(clips, "idle");
            AnimationClip walk = Pick(clips, "walk");
            AnimationClip run = Pick(clips, "gallop", "run", "fly");
            AnimationClip death = clips.FirstOrDefault(c => ArtLibrary.IsDeath(c.name));

            AnimationClip rest = idle != null ? idle : walk != null ? walk : run;
            if (rest == null) return null;

            var controller = Controller(model.Id, rest, walk, run, death);

            Animator animator = wrapper.GetComponentInChildren<Animator>(true);
            if (animator == null) animator = wrapper.transform.GetChild(0).gameObject.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            return animator;
        }

        /// <summary>
        /// A clip called exactly one of the words (past an "Armature|" prefix), else the shortest
        /// whose name holds one: "Idle" before "Idle_HitReact", "Gallop" before "Gallop_Jump".
        /// </summary>
        static AnimationClip Pick(AnimationClip[] clips, params string[] words)
        {
            foreach (string word in words)
            {
                AnimationClip clip = clips.FirstOrDefault(c => Bare(c.name) == word)
                                     ?? clips.Where(c => Bare(c.name).Contains(word) && !ArtLibrary.IsDeath(c.name))
                                             .OrderBy(c => c.name.Length).FirstOrDefault();
                if (clip != null) return clip;
            }

            return null;
        }

        static string Bare(string clip)
        {
            string name = clip.ToLowerInvariant();
            int bar = name.LastIndexOf('|');
            return bar >= 0 ? name.Substring(bar + 1) : name;
        }

        /// <summary>
        /// Speed 0 is standing, 2 m/s a walk, 7 m/s a run: the seeds' walk and run speeds sit close
        /// to those, and the blend between covers everything the agent does in between.
        /// </summary>
        static AnimatorController Controller(string id, AnimationClip rest, AnimationClip walk, AnimationClip run,
                                             AnimationClip death)
        {
            System.IO.Directory.CreateDirectory(ControllerFolder);
            string path = $"{ControllerFolder}/{id}.controller";

            // Emptied rather than deleted, so the GUID the prefab holds stays good.
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null) controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            while (controller.layers.Length > 0) controller.RemoveLayer(0);
            while (controller.parameters.Length > 0) controller.RemoveParameter(0);
            controller.AddLayer("Base Layer");

            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Dead", AnimatorControllerParameterType.Bool);

            AnimatorState move = controller.CreateBlendTreeInController("Move", out BlendTree tree, 0);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.AddChild(rest, 0f);
            if (walk != null && walk != rest) tree.AddChild(walk, 2f);
            if (run != null && run != rest && run != walk) tree.AddChild(run, 7f);

            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            machine.defaultState = move;

            if (death != null)
            {
                AnimatorState dead = machine.AddState("Dead");
                dead.motion = death;

                AnimatorStateTransition fall = machine.AddAnyStateTransition(dead);
                fall.AddCondition(AnimatorConditionMode.If, 0f, "Dead");
                fall.canTransitionToSelf = false;
                fall.duration = 0.1f;
                fall.hasExitTime = false;
            }

            EditorUtility.SetDirty(controller);
            return controller;
        }
    }
}
