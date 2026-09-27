using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using EscapeWithYourFriends.Core;
using EscapeWithYourFriends.World;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// The people: Quaternius's Universal Base Characters, animated by his Universal Animation Library
    /// (#76, #77, docs/ART-PLAN.md T9).
    ///
    ///   Unity.exe -batchmode -quit -projectPath . -logFile characters.log
    ///     -executeMethod EscapeWithYourFriends.EditorTools.CharacterArt.Build -artZips "D:\Downloads\ewyf-art"
    ///
    /// then <c>PlayerPrefabBuilder.BuildPlayerPrefab</c>, which dresses the ragdoll in what this made.
    ///
    /// One command for three jobs - extract, import, build the controller - because none of them is
    /// useful alone and each depends on the one before. Unlike the Kenney kits, the file names inside
    /// these zips were never seen by this project (only the models, re-exported elsewhere as glTF;
    /// ART-PLAN §1), so extraction is by kind rather than by name: every FBX, the base-colour
    /// textures and the licence, and the log says exactly what it found.
    ///
    /// The controller is deleted and made again on every run, which gives it a new GUID. That is
    /// why the player prefab is rebuilt straight after, and why -skinTest fails on a body with no
    /// controller rather than letting it stand in a T-pose.
    /// </summary>
    public static class CharacterArt
    {
        const string DefaultZips = @"D:\Downloads\ewyf-art";

        public const string Folder = ArtCatalog.Root + "/_Characters";
        public const string ControllerPath = Folder + "/Player.controller";
        const string MaskPath = Folder + "/UpperBody.mask";

        const string BodiesPack = "UniversalBaseCharacters";
        const string MovesPack = "UniversalAnimationLibrary";

        /// <summary>
        /// Bodies on one prefab. Every one is a full skeleton under every player, so this is a cost
        /// per player, not per scene; four is one per colour of a full lobby.
        /// </summary>
        const int MaxBodies = 4;

        /// <summary>
        /// The locomotion blend, at the motor's own speeds (PlayerMotor: crouch 2.2, walk 4.5,
        /// sprint 7.5 m/s) so a stride lands where the body actually is. Idle and walk are required.
        /// </summary>
        static readonly (string Clip, float Speed)[] Moves =
        {
            ("Idle_Loop", 0f),
            ("Walk_Loop", 2.2f),
            ("Jog_Fwd_Loop", 4.5f),
            ("Sprint_Loop", 7.5f),
        };

        // Arms forward and bent, which from the side is somebody carrying something heavy.
        const string CarryClip = "Driving_Loop";
        const string SeatedClip = "Driving_Loop";
        const string AirClip = "Jump_Loop";
        const string PunchClip = "Punch_Jab";

        public static void Build()
        {
            string zips = CommandLine.GetString("-artZips", DefaultZips);
            ArtCatalog.Pack bodies = ArtCatalog.FindPack(BodiesPack);
            ArtCatalog.Pack moves = ArtCatalog.FindPack(MovesPack);

            // Both, before failing: one run should say everything that is missing.
            bool ok = Extract(bodies, zips, textures: true);
            ok &= Extract(moves, zips, textures: false);

            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();

            if (ok)
            {
                foreach (string path in Models(bodies)) ImportBody(path, bodies);
                foreach (string path in Models(moves)) ImportMoves(path);

                GameObject[] found = Bodies();
                Debug.Log($"[CharacterArt] {found.Length} bodies: {string.Join(", ", found.Select(b => b.name))}.");
                if (found.Length == 0)
                {
                    Debug.LogError($"[CharacterArt] No usable body in {bodies.Folder}: none is a humanoid "
                                   + "with a skinned mesh between 1.4 and 2.2 m tall. The log above says why each was refused.");
                    ok = false;
                }

                ok &= BuildController();
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[CharacterArt] {(ok ? "Done" : "FAILED")}.");
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        // ---------------------------------------------------------------------------- extracting

        static bool Extract(ArtCatalog.Pack pack, string folder, bool textures)
        {
            bool present = Models(pack).Length > 0;
            string zipPath = FindZip(folder, pack);

            if (zipPath == null)
            {
                if (present)
                {
                    Debug.Log($"[CharacterArt] No {pack.Name} zip in {folder}; using what is already in {pack.Folder}.");
                    return true;
                }

                Debug.LogError($"[CharacterArt] Missing: no zip with '{pack.ZipHint}' in its name in {folder}. "
                               + $"Download {pack.Author} {pack.Name} from {pack.Page}.");
                return false;
            }

            using ZipArchive zip = ArtExtract.Open(zipPath);
            Directory.CreateDirectory(pack.Folder);

            ZipArchiveEntry[] fbx = Unique(zip, e => Extension(e) == ".fbx");
            foreach (ZipArchiveEntry entry in fbx) ArtExtract.Copy(entry, $"{pack.Folder}/{entry.Name}");

            int images = 0;
            if (textures)
            {
                Directory.CreateDirectory($"{pack.Folder}/Textures");
                foreach (ZipArchiveEntry entry in Unique(zip, IsTexture))
                {
                    ArtExtract.Copy(entry, $"{pack.Folder}/Textures/{entry.Name}");
                    images++;
                }
            }

            // Same rule as ArtExtract: no licence from the zip, no import (ART-PLAN §1).
            ZipArchiveEntry licence = zip.Entries.FirstOrDefault(e => e.Name.ToLowerInvariant().StartsWith("licen")
                                                                      && Extension(e) is ".txt" or ".md");
            if (licence != null) ArtExtract.Copy(licence, $"{pack.Folder}/License.txt");

            Debug.Log($"[CharacterArt] {pack.Author} {pack.Name} <- {Path.GetFileName(zipPath)}: {fbx.Length} FBX "
                      + $"({string.Join(", ", fbx.Select(e => e.Name))}), {images} texture(s), "
                      + $"licence {(licence == null ? "MISSING" : licence.FullName)}.");

            if (fbx.Length == 0)
            {
                string kinds = string.Join(", ", zip.Entries.Select(Extension).Where(x => x.Length > 0).Distinct());
                Debug.LogError($"[CharacterArt] {Path.GetFileName(zipPath)} holds no FBX (it has: {kinds}). "
                               + "Is it the Unity/FBX download rather than the Blender source?");
            }

            if (licence == null)
                Debug.LogError($"[CharacterArt] Missing: no licence file in {Path.GetFileName(zipPath)}.");

            return fbx.Length > 0 && licence != null;
        }

        /// <summary>
        /// The zip for a pack: of those whose name mentions it, the one with the most FBX in it. The
        /// animation library has also shipped as "UAL1_Standard", hence the second name.
        /// </summary>
        static string FindZip(string folder, ArtCatalog.Pack pack)
        {
            if (!Directory.Exists(folder)) return null;

            string best = null;
            int most = 0;

            foreach (string path in Directory.GetFiles(folder, "*.zip", SearchOption.TopDirectoryOnly))
            {
                string name = Plain(Path.GetFileNameWithoutExtension(path));
                bool named = name.Contains(pack.ZipHint) || (pack.Name == MovesPack && name.StartsWith("ual"));
                if (!named) continue;

                using ZipArchive zip = ArtExtract.Open(path);
                int count = zip.Entries.Count(e => Extension(e) == ".fbx");
                if (count <= most) continue;

                best = path;
                most = count;
            }

            return best;
        }

        /// <summary>
        /// One entry per file name. Quaternius zips carry each model once per engine; the copy under a
        /// "Unity" folder wins, then one under "FBX", then whichever came first.
        /// </summary>
        static ZipArchiveEntry[] Unique(ZipArchive zip, Func<ZipArchiveEntry, bool> wanted)
            => zip.Entries.Where(e => e.Name.Length > 0 && wanted(e))
                  .GroupBy(e => e.Name.ToLowerInvariant())
                  .Select(g => g.OrderByDescending(e => Has(e.FullName, "unity"))
                                .ThenByDescending(e => Has(e.FullName, "fbx"))
                                .First())
                  .ToArray();

        static bool IsTexture(ZipArchiveEntry entry)
        {
            if (Extension(entry) is not (".png" or ".jpg" or ".jpeg" or ".tga")) return false;

            string path = entry.FullName.ToLowerInvariant();
            return !new[] { "preview", "screenshot", "render", "thumbnail" }.Any(path.Contains);
        }

        static string Extension(ZipArchiveEntry entry) => Path.GetExtension(entry.Name).ToLowerInvariant();

        static bool Has(string text, string part) => text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Lower-case letters and digits only, so "Universal Base Characters[Standard]" matches "basecharacter".</summary>
        static string Plain(string text) => new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        static string[] Models(ArtCatalog.Pack pack)
            => Directory.Exists(pack.Folder)
                ? Directory.GetFiles(pack.Folder, "*.fbx", SearchOption.TopDirectoryOnly)
                           .Select(p => p.Replace('\\', '/')).OrderBy(p => p).ToArray()
                : Array.Empty<string>();

        // ---------------------------------------------------------------------------- importing

        /// <summary>
        /// Humanoid, with the axis conversion baked in. Baked matters more here than for a crate: an
        /// FBX that keeps the conversion puts a quarter turn on its root, and a humanoid animator on a
        /// tipped root plays every clip lying on its back.
        /// </summary>
        static ModelImporter Humanoid(string path, bool animations)
        {
            if (AssetImporter.GetAtPath(path) is not ModelImporter importer)
            {
                Debug.LogError($"[CharacterArt] {path} is not a model.");
                return null;
            }

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = animations;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importBlendShapes = false;
            importer.bakeAxisConversion = true;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            importer.SaveAndReimport();

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) return importer;

            // Units: a character exported in centimetres is 175 m tall. Snap to the power of ten
            // that makes it a person; the prefab builder does the last few percent.
            Vector3 size = ArtLibrary.NativeBounds(asset).size;
            if (size.y > 0f && (size.y < 0.2f || size.y > 10f))
            {
                float factor = Mathf.Pow(10f, Mathf.Round(Mathf.Log10(1.75f / size.y)));
                importer.globalScale *= factor;
                importer.SaveAndReimport();
                asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Debug.Log($"[CharacterArt] {path} was {size.y:F2} tall; global scale x{factor}.");
                size = ArtLibrary.NativeBounds(asset).size;
            }

            if (size.y > 0f && !ArtVisual.Standing(size))
            {
                importer.bakeAxisConversion = false;
                importer.SaveAndReimport();
                asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Debug.LogWarning($"[CharacterArt] {path} lies down with the axis conversion baked; importing it "
                                 + "unbaked instead. If its clips play on their back, this is why.");
            }

            Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
                Debug.LogError($"[CharacterArt] {path}: the humanoid avatar did not build. Open the model's Rig "
                               + "tab once to see which bone Unity could not map.");

            return importer;
        }

        static void ImportBody(string path, ArtCatalog.Pack pack)
        {
            ModelImporter importer = Humanoid(path, animations: false);
            if (importer == null) return;

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) return;

            bool changed = false;
            var seen = new HashSet<string>();

            foreach (Renderer renderer in asset.GetComponentsInChildren<Renderer>(true))
            foreach (Material worn in renderer.sharedMaterials)
            {
                if (worn == null || !seen.Add(worn.name)) continue;
                if (AssetDatabase.GetAssetPath(worn).StartsWith(ArtLibrary.MaterialFolder)) continue;

                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), worn.name),
                                  Paint(worn, pack));
                changed = true;
            }

            if (changed) importer.SaveAndReimport();
        }

        /// <summary>
        /// One shared material per slot the artist named. Painted, in order of preference, from the
        /// texture the FBX itself pointed at, from a texture in the pack named after the slot, or
        /// from the slot's own colour - and the log says which, because a white body is the one
        /// failure that looks like a style choice.
        /// </summary>
        static Material Paint(Material worn, ArtCatalog.Pack pack)
        {
            string slot = new(worn.name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            string path = $"{ArtLibrary.MaterialFolder}/Quaternius_{slot}.mat";

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Texture texture = worn.HasProperty("_BaseMap") ? worn.GetTexture("_BaseMap") : null;
            if (texture == null) texture = worn.mainTexture;
            string source = texture != null ? "the FBX" : null;

            if (texture == null)
            {
                string file = BaseColour(pack, slot);
                if (file != null)
                {
                    if (AssetImporter.GetAtPath(file) is TextureImporter importer && importer.maxTextureSize != 1024)
                    {
                        importer.maxTextureSize = 1024;
                        importer.SaveAndReimport();
                    }

                    texture = AssetDatabase.LoadAssetAtPath<Texture2D>(file);
                    source = file;
                }
            }

            Material material = ArtLibrary.NewLit($"Quaternius_{slot}");
            material.SetFloat("_Smoothness", 0.18f);

            if (texture != null)
            {
                material.SetTexture("_BaseMap", texture);
                material.mainTexture = texture;
                material.SetColor("_BaseColor", Color.white);
            }
            else
            {
                Color colour = worn.HasProperty("_BaseColor") ? worn.GetColor("_BaseColor") : worn.color;
                material.SetColor("_BaseColor", colour);
                material.color = colour;
                Debug.LogWarning($"[CharacterArt] {worn.name}: no texture in the FBX or in {pack.Folder}/Textures; "
                                 + $"flat #{ColorUtility.ToHtmlStringRGB(colour)}.");
            }

            // Eyebrows and lashes are single cards. Drawn from one side only, they vanish in profile.
            if (Has(slot, "hair") || Has(slot, "brow") || Has(slot, "lash"))
                material.SetFloat("_Cull", 0f);

            if (source != null) Debug.Log($"[CharacterArt] {worn.name} is painted from {source}.");
            return ArtLibrary.Save(material, path);
        }

        /// <summary>A base-colour texture named after the slot: "MI_Superhero_Male" finds "T_Superhero_Male_BaseColor".</summary>
        static string BaseColour(ArtCatalog.Pack pack, string slot)
        {
            string folder = $"{pack.Folder}/Textures";
            if (!Directory.Exists(folder)) return null;

            string stem = Plain(slot.StartsWith("MI_") || slot.StartsWith("M_") ? slot.Substring(slot.IndexOf('_') + 1) : slot);
            string[] maps = { "normal", "rough", "metal", "occlusion", "_ao", "orm", "emiss", "height", "mask" };

            return Directory.GetFiles(folder)
                            .Where(f => !f.EndsWith(".meta"))
                            .Select(f => f.Replace('\\', '/'))
                            .Where(f => Plain(Path.GetFileNameWithoutExtension(f)).Contains(stem))
                            .Where(f => !maps.Any(m => Path.GetFileName(f).ToLowerInvariant().Contains(m)))
                            .OrderByDescending(f => new[] { "basecolor", "albedo", "diffuse", "color" }
                                                        .Any(Plain(Path.GetFileName(f)).Contains))
                            .ThenBy(f => f)
                            .FirstOrDefault();
        }

        /// <summary>
        /// The library, as humanoid clips. Every clip keeps its body where the clip put it vertically
        /// (a crouch crouches) and facing where it faced, and gives its travel to root motion, which
        /// the animator throws away: the motor moves the player, the clip only moves the legs.
        /// </summary>
        static void ImportMoves(string path)
        {
            ModelImporter importer = Humanoid(path, animations: true);
            if (importer == null) return;

            ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
            foreach (ModelImporterClipAnimation clip in clips)
            {
                // "Armature|Idle_Loop" is Blender's way of saying "Idle_Loop".
                clip.name = clip.takeName.Contains('|') ? clip.takeName.Substring(clip.takeName.LastIndexOf('|') + 1)
                                                        : clip.takeName;
                clip.loopTime = Has(clip.name, "loop");
                clip.lockRootRotation = true;
                clip.lockRootHeightY = true;
                clip.lockRootPositionXZ = false;
                clip.keepOriginalOrientation = true;
                clip.keepOriginalPositionY = true;
                clip.keepOriginalPositionXZ = true;
            }

            importer.clipAnimations = clips;
            importer.SaveAndReimport();

            Debug.Log($"[CharacterArt] {Path.GetFileName(path)}: {clips.Length} clip(s)"
                      + (clips.Length > 0 ? $": {string.Join(", ", clips.Select(c => c.name))}." : "."));
        }

        /// <summary>
        /// The bodies the player prefab wears: humanoid, skinned, the height of a person. When the pack
        /// has a "FullBody" variant beside modular parts, only those. At most <see cref="MaxBodies"/>.
        /// </summary>
        public static GameObject[] Bodies()
        {
            var found = new List<GameObject>();

            foreach (string path in Models(ArtCatalog.FindPack(BodiesPack)))
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null) continue;

                var animator = asset.GetComponent<Animator>();
                if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                {
                    Debug.Log($"[CharacterArt] {path} is not a humanoid; not a body.");
                    continue;
                }

                if (asset.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                {
                    Debug.Log($"[CharacterArt] {path} has no skinned mesh; not a body.");
                    continue;
                }

                float height = ArtLibrary.NativeBounds(asset).size.y;
                if (height < 1.4f || height > 2.2f)
                {
                    Debug.Log($"[CharacterArt] {path} is {height:F2} m tall; not a body.");
                    continue;
                }

                found.Add(asset);
            }

            if (found.Any(b => Has(b.name, "fullbody"))) found.RemoveAll(b => !Has(b.name, "fullbody"));
            return found.Take(MaxBodies).ToArray();
        }

        // ---------------------------------------------------------------------------- the controller

        /// <summary>Every clip in the library by the name it was given above, lower-case letters only.</summary>
        static Dictionary<string, AnimationClip> Clips()
        {
            var clips = new Dictionary<string, AnimationClip>();

            foreach (string path in Models(ArtCatalog.FindPack(MovesPack)))
            foreach (AnimationClip clip in AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>())
            {
                if (clip.name.StartsWith("__preview__")) continue;
                clips.TryAdd(Plain(clip.name), clip);
            }

            return clips;
        }

        static AnimationClip Clip(Dictionary<string, AnimationClip> clips, string name, List<string> missing)
        {
            string wanted = Plain(name);
            if (clips.TryGetValue(wanted, out AnimationClip clip)) return clip;

            // Some exports prefix the rig: "ual_idle_loop", "mannequin_idle_loop".
            clip = clips.Where(pair => pair.Key.EndsWith(wanted)).Select(pair => pair.Value).FirstOrDefault();
            if (clip == null) missing.Add(name);
            return clip;
        }

        /// <summary>
        /// The one controller every body shares. Four parameters, which is everything the rest of the
        /// game already knows about a body: how fast it goes, whether it is off the ground, in a seat,
        /// or throwing a punch. Carrying is a layer over the arms rather than a state, so a carrier
        /// can still walk.
        /// </summary>
        static bool BuildController()
        {
            Dictionary<string, AnimationClip> clips = Clips();
            var missing = new List<string>();

            Debug.Log($"[CharacterArt] {clips.Count} clip(s) in the library.");

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ControllerPath) != null)
                AssetDatabase.DeleteAsset(ControllerPath);

            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Airborne", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Seated", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Punch", AnimatorControllerParameterType.Trigger);

            AnimatorState move = controller.CreateBlendTreeInController("Move", out BlendTree tree, 0);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;

            foreach ((string name, float speed) in Moves)
            {
                AnimationClip step = Clip(clips, name, missing);
                if (step != null) tree.AddChild(step, speed);
            }

            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            machine.defaultState = move;

            // A state with no clip holds the bind pose, which is a T-pose; a missing clip is left out
            // instead, and the body keeps walking through whatever it was for.
            AnimationClip clip = Clip(clips, SeatedClip, missing);
            if (clip != null)
            {
                AnimatorState seated = State(machine, "Seated", clip);
                Quick(machine.AddAnyStateTransition(seated)).AddCondition(AnimatorConditionMode.If, 0f, "Seated");
                Quick(seated.AddTransition(move)).AddCondition(AnimatorConditionMode.IfNot, 0f, "Seated");
            }

            clip = Clip(clips, AirClip, missing);
            if (clip != null)
            {
                AnimatorState air = State(machine, "Air", clip);
                AnimatorStateTransition jump = Quick(machine.AddAnyStateTransition(air));
                jump.AddCondition(AnimatorConditionMode.If, 0f, "Airborne");
                jump.AddCondition(AnimatorConditionMode.IfNot, 0f, "Seated");
                Quick(air.AddTransition(move)).AddCondition(AnimatorConditionMode.IfNot, 0f, "Airborne");
            }

            clip = Clip(clips, PunchClip, missing);
            if (clip != null)
            {
                AnimatorState punch = State(machine, "Punch", clip);
                AnimatorStateTransition swing = Quick(machine.AddAnyStateTransition(punch));
                swing.AddCondition(AnimatorConditionMode.If, 0f, "Punch");
                swing.AddCondition(AnimatorConditionMode.IfNot, 0f, "Seated");
                swing.canTransitionToSelf = true;

                AnimatorStateTransition recover = punch.AddTransition(move);
                recover.hasExitTime = true;
                recover.exitTime = 0.85f;
                recover.duration = 0.1f;
            }

            var mask = new AvatarMask { name = "UpperBody" };
            foreach (AvatarMaskBodyPart part in Enum.GetValues(typeof(AvatarMaskBodyPart)))
            {
                if (part == AvatarMaskBodyPart.LastBodyPart) continue;
                mask.SetHumanoidBodyPartActive(part, part is AvatarMaskBodyPart.LeftArm or AvatarMaskBodyPart.RightArm
                                                         or AvatarMaskBodyPart.LeftFingers or AvatarMaskBodyPart.RightFingers);
            }

            if (AssetDatabase.LoadAssetAtPath<AvatarMask>(MaskPath) != null) AssetDatabase.DeleteAsset(MaskPath);
            AssetDatabase.CreateAsset(mask, MaskPath);

            controller.AddLayer("Carry");
            AnimatorControllerLayer[] layers = controller.layers;
            layers[1].defaultWeight = 0f;
            layers[1].avatarMask = mask;
            clip = Clip(clips, CarryClip, missing);
            if (clip != null) State(layers[1].stateMachine, "Carry", clip);
            controller.layers = layers;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            foreach (string name in missing.Distinct())
                Debug.LogWarning($"[CharacterArt] No clip called {name} in the library; that state is left out.");

            bool ok = !missing.Contains("Idle_Loop") && !missing.Contains("Walk_Loop");
            if (!ok)
                Debug.LogError($"[CharacterArt] The library has no Idle_Loop or no Walk_Loop. What it has: "
                               + string.Join(", ", clips.Values.Select(c => c.name).OrderBy(n => n)));

            Debug.Log($"[CharacterArt] Built {ControllerPath}.");
            return ok;
        }

        static AnimatorState State(AnimatorStateMachine machine, string name, Motion motion)
        {
            AnimatorState state = machine.AddState(name);
            state.motion = motion;
            return state;
        }

        static AnimatorStateTransition Quick(AnimatorStateTransition transition)
        {
            transition.hasExitTime = false;
            transition.duration = 0.15f;
            transition.canTransitionToSelf = false;
            return transition;
        }
    }
}
