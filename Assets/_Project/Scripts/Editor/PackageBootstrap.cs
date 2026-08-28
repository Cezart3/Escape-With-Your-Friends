using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace EscapeWithYourFriends.EditorTools
{
    /// <summary>
    /// Installs the project's package dependencies.
    ///
    ///   Unity.exe -quit -batchmode -projectPath . -executeMethod EscapeWithYourFriends.EditorTools.PackageBootstrap.Install
    ///
    /// Package ids are deliberately unversioned so UPM resolves whatever is compatible with the
    /// current editor. Pinning versions here means re-editing this file on every editor upgrade,
    /// and a wrong pin fails the whole resolve.
    /// </summary>
    public static class PackageBootstrap
    {
        static readonly string[] Packages =
        {
            "com.unity.render-pipelines.universal",
            "com.unity.inputsystem",
            "com.unity.cinemachine",
            "com.unity.ai.navigation",
            // FishNet free tier, straight from the repo. The Asset Store build is the same code.
            "https://github.com/FirstGearGames/FishNet.git?path=Assets/FishNet",
        };

        public static void Install()
        {
            var failed = new List<string>();

            foreach (string id in Packages)
            {
                Debug.Log($"[PackageBootstrap] adding {id}");
                AddRequest request = Client.Add(id);

                // Batchmode has no editor update loop driving UPM, so poll the request directly.
                while (!request.IsCompleted)
                    Thread.Sleep(100);

                if (request.Status == StatusCode.Success)
                {
                    Debug.Log($"[PackageBootstrap] ok {request.Result.packageId}");
                }
                else
                {
                    string error = request.Error?.message ?? "unknown error";
                    Debug.LogError($"[PackageBootstrap] FAILED {id}: {error}");
                    failed.Add($"{id} ({error})");
                }
            }

            if (failed.Count > 0)
            {
                Debug.LogError($"[PackageBootstrap] {failed.Count} package(s) failed:\n  " +
                               string.Join("\n  ", failed));
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            Debug.Log($"[PackageBootstrap] all {Packages.Length} packages resolved");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
