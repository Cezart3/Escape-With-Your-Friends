using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// One-shot project configuration: URP pipeline assets per quality tier, physics layers,
    /// tags, and player settings.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.ProjectSetup.Run
    ///
    /// Idempotent — re-running overwrites the generated assets and leaves everything else alone.
    /// </summary>
    public static class ProjectSetup
    {
        const string SettingsDir = "Assets/_Project/Settings";

        /// <summary>
        /// Layers 0-7 are reserved by Unity, so these start at 8. Order is stable: changing it
        /// silently remaps every collider already assigned in a scene or prefab.
        /// </summary>
        static readonly string[] CustomLayers =
        {
            "Player", "Ragdoll", "Corpse", "Vehicle",
            "Water", "Interactable", "NPC", "Projectile",
        };

        static readonly string[] CustomTags =
        {
            "Corpse", "Vehicle", "Interactable", "NPC", "ReviveMachine",
        };

        /// <summary>Shadow distance in metres per tier. Low targets a Radeon 760M iGPU.</summary>
        static readonly (string name, float shadowDistance, int cascades, float renderScale)[] Tiers =
        {
            ("Low",    60f,  1, 0.8f),
            ("Medium", 80f,  2, 1.0f),
            ("High",   150f, 4, 1.0f),
        };

        public static void Run()
        {
            Directory.CreateDirectory(SettingsDir);
            AssetDatabase.Refresh();

            var pipelines = CreateUrpAssets();
            AssignPipelines(pipelines);
            SetupLayers();
            SetupTags();
            SetupCollisionMatrix();
            SetupInputHandler();
            SetupPlayerSettings();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[ProjectSetup] done: URP tiers, layers, tags, player settings");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static List<UniversalRenderPipelineAsset> CreateUrpAssets()
        {
            var result = new List<UniversalRenderPipelineAsset>();

            foreach (var (name, shadowDistance, cascades, renderScale) in Tiers)
            {
                var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                string rendererPath = $"{SettingsDir}/URP_{name}_Renderer.asset";
                AssetDatabase.CreateAsset(rendererData, rendererPath);

                var pipeline = UniversalRenderPipelineAsset.Create(rendererData);
                pipeline.shadowDistance = shadowDistance;
                pipeline.shadowCascadeCount = cascades;
                pipeline.renderScale = renderScale;
                // SSAO and heavy post are opt-in per tier later; the default renderer ships neither.
                pipeline.supportsHDR = name != "Low";
                pipeline.msaaSampleCount = name == "High" ? 4 : 1;

                string pipelinePath = $"{SettingsDir}/URP_{name}.asset";
                AssetDatabase.CreateAsset(pipeline, pipelinePath);
                result.Add(pipeline);

                Debug.Log($"[ProjectSetup] created {pipelinePath} " +
                          $"(shadows {shadowDistance}m, {cascades} cascade(s), scale {renderScale})");
            }

            return result;
        }

        static void AssignPipelines(List<UniversalRenderPipelineAsset> pipelines)
        {
            // Highest tier is the project default so the editor looks right while working.
            GraphicsSettings.defaultRenderPipeline = pipelines[^1];

            int levels = QualitySettings.count;
            int original = QualitySettings.GetQualityLevel();

            for (int i = 0; i < levels; i++)
            {
                // Spread however many quality levels exist across the three tiers.
                int tier = Mathf.Clamp(i * Tiers.Length / Mathf.Max(levels, 1), 0, Tiers.Length - 1);
                QualitySettings.SetQualityLevel(i, applyExpensiveChanges: false);
                QualitySettings.renderPipeline = pipelines[tier];
            }

            QualitySettings.SetQualityLevel(original, applyExpensiveChanges: false);
            Debug.Log($"[ProjectSetup] assigned URP across {levels} quality level(s)");
        }

        static void SetupLayers()
        {
            var tagManager = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");

            int next = 8;
            foreach (string layer in CustomLayers)
            {
                if (LayerExists(layers, layer)) continue;

                while (next < layers.arraySize &&
                       !string.IsNullOrEmpty(layers.GetArrayElementAtIndex(next).stringValue))
                    next++;

                if (next >= layers.arraySize)
                {
                    Debug.LogWarning($"[ProjectSetup] no free layer slot for '{layer}'");
                    continue;
                }

                layers.GetArrayElementAtIndex(next).stringValue = layer;
                Debug.Log($"[ProjectSetup] layer {next} = {layer}");
                next++;
            }

            tagManager.ApplyModifiedProperties();
        }

        static bool LayerExists(SerializedProperty layers, string name) =>
            Enumerable.Range(0, layers.arraySize)
                      .Any(i => layers.GetArrayElementAtIndex(i).stringValue == name);

        static void SetupTags()
        {
            foreach (string tag in CustomTags)
            {
                if (!UnityEditorInternal.InternalEditorUtility.tags.Contains(tag))
                    UnityEditorInternal.InternalEditorUtility.AddTag(tag);
            }
            Debug.Log($"[ProjectSetup] tags ensured: {string.Join(", ", CustomTags)}");
        }

        /// <summary>
        /// Pairs that must never collide. Everything else keeps Unity's default of colliding.
        /// Ragdoll-vs-Ragdoll is deliberately left on — pileups are the point.
        /// </summary>
        static readonly (string a, string b)[] IgnoredCollisions =
        {
            // A carried or stunned body shoving its carrier around reads as a physics bug.
            ("Ragdoll", "Player"),
            ("Corpse", "Player"),
            // Bullets passing through each other costs nothing and avoids silly mid-air hits.
            ("Projectile", "Projectile"),
        };

        static void SetupCollisionMatrix()
        {
            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset")[0];
            var dynamics = new SerializedObject(asset);
            SerializedProperty matrix = dynamics.FindProperty("m_LayerCollisionMatrix");

            if (matrix == null)
            {
                Debug.LogWarning("[ProjectSetup] m_LayerCollisionMatrix not found, skipping");
                return;
            }

            foreach (var (a, b) in IgnoredCollisions)
            {
                int la = LayerMask.NameToLayer(a);
                int lb = LayerMask.NameToLayer(b);
                if (la < 0 || lb < 0)
                {
                    Debug.LogWarning($"[ProjectSetup] cannot ignore {a}/{b}: layer missing");
                    continue;
                }

                ClearCollisionBit(matrix, la, lb);
                Debug.Log($"[ProjectSetup] collision ignored: {a} <-> {b}");
            }

            dynamics.ApplyModifiedProperties();
        }

        /// <summary>
        /// The matrix is 32 uint masks; bit j of mask i means "layer i collides with layer j".
        /// It is symmetric, so both directions have to be cleared.
        /// </summary>
        static void ClearCollisionBit(SerializedProperty matrix, int layerA, int layerB)
        {
            foreach (var (row, col) in new[] { (layerA, layerB), (layerB, layerA) })
            {
                SerializedProperty element = matrix.GetArrayElementAtIndex(row);
                element.uintValue &= ~(1u << col);
            }
        }

        static void SetupInputHandler()
        {
            // No public API for this one; it lives in ProjectSettings.asset.
            // 0 = old input manager, 1 = new Input System, 2 = both.
            var settings = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            SerializedProperty handler = settings.FindProperty("activeInputHandler");

            if (handler == null)
            {
                Debug.LogWarning("[ProjectSetup] activeInputHandler not found, skipping");
                return;
            }

            // "Both" rather than "new only": several packages still poll legacy Input in editor
            // tooling, and the cost of keeping the old backend enabled is negligible.
            handler.intValue = 2;
            settings.ApplyModifiedProperties();
            Debug.Log("[ProjectSetup] active input handling = Both (new Input System enabled)");
        }

        static void SetupPlayerSettings()
        {
            PlayerSettings.companyName = "Cezart3";
            PlayerSettings.productName = "Escape With Your Friends";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultScreenWidth = 1920;
            PlayerSettings.defaultScreenHeight = 1080;
            PlayerSettings.runInBackground = true;
            // Multiplayer testing means several instances of the player at once.
            PlayerSettings.allowFullscreenSwitch = true;
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;

            Debug.Log("[ProjectSetup] player settings applied (Linear colour space, 1080p default)");
        }
    }
}
