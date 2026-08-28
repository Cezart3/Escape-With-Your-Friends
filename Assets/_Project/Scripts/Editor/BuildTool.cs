using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Headless Windows build entry point.
    ///
    ///   Unity.exe -quit -batchmode -nographics -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.BuildTool.PerformBuild
    ///
    /// Optional args: -buildOutput &lt;dir&gt;  -development  -scriptingBackend &lt;il2cpp|mono&gt;
    /// </summary>
    public static class BuildTool
    {
        const string DefaultOutputDir = "BuildOutput";
        const string ExeName = "EscapeWithYourFriends.exe";

        public static void PerformBuild()
        {
            var args = Environment.GetCommandLineArgs();

            string outputDir = GetArg(args, "-buildOutput") ?? DefaultOutputDir;
            bool development = args.Contains("-development");
            string backend = GetArg(args, "-scriptingBackend") ?? "il2cpp";

            // Batchmode gives a relative projectPath, so anchor everything to the project root.
            if (!Path.IsPathRooted(outputDir))
                outputDir = Path.Combine(Directory.GetCurrentDirectory(), outputDir);
            Directory.CreateDirectory(outputDir);

            string[] scenes = EnabledScenes();
            if (scenes.Length == 0)
            {
                // Building zero scenes produces a player that opens to nothing, which looks
                // like a successful build until someone runs it. Fail loudly instead.
                Fail("No enabled scenes in Build Settings. Add at least one before building.");
                return;
            }

            PlayerSettings.SetScriptingBackend(
                NamedBuildTarget.Standalone,
                backend.Equals("mono", StringComparison.OrdinalIgnoreCase)
                    ? ScriptingImplementation.Mono2x
                    : ScriptingImplementation.IL2CPP);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(outputDir, ExeName),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = development
                    ? BuildOptions.Development | BuildOptions.AllowDebugging
                    : BuildOptions.None
            };

            Debug.Log($"[BuildTool] {scenes.Length} scene(s), backend={backend}, " +
                      $"development={development}, output={outputDir}");

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[BuildTool] Succeeded in {summary.totalTime.TotalSeconds:F1}s, " +
                          $"{summary.totalSize / (1024f * 1024f):F1} MB -> {summary.outputPath}");
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            else
            {
                Fail($"Build {summary.result} with {summary.totalErrors} error(s).");
            }
        }

        static string[] EnabledScenes() =>
            EditorBuildSettings.scenes
                .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                .Select(s => s.path)
                .ToArray();

        static string GetArg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
        }

        static void Fail(string message)
        {
            Debug.LogError($"[BuildTool] {message}");
            // Non-zero exit so CI and shell callers actually notice.
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
}
