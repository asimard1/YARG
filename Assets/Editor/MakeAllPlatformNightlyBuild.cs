using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Editor
{
    /// <summary>
    /// Adds a "File/Make Nightly Build (All Platforms)" menu item that builds
    /// Windows, macOS, and Linux standalone builds back to back in one click,
    /// strips the YARG_BurstDebugInformation_DoNotShip folders, and zips each
    /// platform's output folder.
    ///
    /// This is separate from MakeTestBuild.cs (which only builds whichever
    /// single platform is currently selected in Build Settings) so the
    /// existing single-platform workflow is untouched.
    /// </summary>
    public static class MakeAllPlatformNightlyBuild
    {
        private const string YARG_NIGHTLY_BUILD = "YARG_NIGHTLY_BUILD";
        private const string BURST_DEBUG_FOLDER_NAME = "YARG_BurstDebugInformation_DoNotShip";

        [MenuItem("File/Make Nightly Build (All Platforms)", false, 221)]
        public static void MakeAllPlatformsClicked()
        {
            // Builds go into a "Builds" folder next to the project folder
            // (i.e. NOT inside Assets, so Unity won't try to import them).
            string root = Path.Combine(Path.GetDirectoryName(Application.dataPath)!, "Builds");

            var platforms = new (BuildTarget target, string folderName, string exeName)[]
            {
                (BuildTarget.StandaloneWindows64, "Windows", "YARG.exe"),
                (BuildTarget.StandaloneOSX,        "macOS",   "YARG.app"),
                (BuildTarget.StandaloneLinux64,     "Linux",   "YARG.x86_64"),
            };

            foreach (var (target, folderName, exeName) in platforms)
            {
                string platformDir = Path.Combine(root, folderName);
                string locationPathName = Path.Combine(platformDir, exeName);

                var result = BuildOnePlatform(target, locationPathName);

                if (result != BuildResult.Succeeded)
                {
                    Debug.LogError($"{target}: build did not succeed ({result}) -- skipping cleanup/zip for this platform.");
                    continue;
                }

                RemoveBurstDebugFolder(platformDir);
                ZipPlatformFolder(platformDir, root, folderName);
            }

            // Building for Mac/Linux leaves the Editor on that platform, which
            // triggers a reimport next time you hit Play. Switch back to Windows.
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                Debug.Log("Switching active build target back to Windows...");
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
            }

            Debug.Log($"Nightly build run complete. Output: {root}");
        }

        private static BuildResult BuildOnePlatform(BuildTarget target, string locationPathName)
        {
            Debug.Log($"Building {target} -> {locationPathName} ...");

            // Same enabled-scenes list Unity's own Build Settings window uses.
            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            // Standalone (Windows/Mac/Linux) all share one scripting-define group.
            var buildGroup = BuildTargetGroup.Standalone;
            PlayerSettings.GetScriptingDefineSymbolsForGroup(buildGroup, out var originalDefines);
            originalDefines ??= Array.Empty<string>();

            var defines = originalDefines;
            if (!defines.Contains(YARG_NIGHTLY_BUILD))
            {
                ArrayUtility.Add(ref defines, YARG_NIGHTLY_BUILD);
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = locationPathName,
                target = target,
                extraScriptingDefines = defines
            };

            var report = BuildPipeline.BuildPlayer(options);

            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"Build FAILED for {target}: {report.summary.result} " +
                                $"({report.summary.totalErrors} errors). " +
                                $"Check that the build module for this platform is installed in Unity Hub.");
            }

            return report.summary.result;
        }

        private static void RemoveBurstDebugFolder(string platformDir)
        {
            string burstFolder = Path.Combine(platformDir, BURST_DEBUG_FOLDER_NAME);
            if (Directory.Exists(burstFolder))
            {
                Directory.Delete(burstFolder, true);
                Debug.Log($"Deleted {burstFolder}");
            }
        }

        private static void ZipPlatformFolder(string platformDir, string root, string folderName)
        {
            string zipPath = Path.Combine(root, $"YARG-{folderName}.zip");

            // ZipFile.CreateFromDirectory throws if the target already exists,
            // so clear out yesterday's zip first.
            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);

                ZipFile.CreateFromDirectory(platformDir, zipPath, System.IO.Compression.CompressionLevel.Optimal, false);
                Debug.Log($"Zipped {platformDir} -> {zipPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to create {zipPath}: {ex}");
            }
        }
    }
}
