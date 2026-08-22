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

        // Shared by both the menu entry point (spawns subprocesses) and the
        // CLI entry point (runs inside a spawned subprocess), so keep it in
        // one place.
        private static readonly (BuildTarget target, string folderName, string exeName)[] Platforms =
        {
            (BuildTarget.StandaloneWindows64, "Windows", "YARG.exe"),
            (BuildTarget.StandaloneOSX,        "macOS",   "YARG.app"),
            (BuildTarget.StandaloneLinux64,     "Linux",   "YARG.x86_64"),
        };

        [MenuItem("File/Make Nightly Build (All Platforms)", false, 221)]
        public static void MakeAllPlatformsClicked()
        {
            // Builds go into a "Builds" folder next to the project folder
            // (i.e. NOT inside Assets, so Unity won't try to import them).
            string root = Path.Combine(Path.GetDirectoryName(Application.dataPath)!, "Builds");
            Directory.CreateDirectory(root);

            try
            {
                foreach (var (target, folderName, exeName) in Platforms)
                {
                    try
                    {
                        string platformDir = Path.Combine(root, folderName);

                        // Each platform now builds in its own fresh Unity process, so
                        // memory used by the shader compiler (and everything else) for
                        // one platform can't pile up and starve the next one. This is
                        // what was causing "out of memory during compilation" on Linux
                        // after Windows + macOS had already built in the same session.
                        bool succeeded = BuildOnePlatformInSubprocess(root, folderName);

                        if (!succeeded)
                        {
                            Debug.LogError($"Stopping nightly build because {target} failed. Check the per-platform log in {root}.");
                            return;
                        }

                        RemoveBurstDebugFolder(platformDir);
                        ZipPlatformFolder(platformDir, root, folderName);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                        Debug.LogError($"Stopping nightly build because an exception occurred while building {target}.");
                        return;
                    }
                }
            }
            finally
            {
                // Building for Mac/Linux leaves the Editor on that platform, which
                // triggers a reimport next time you hit Play. Switch back to Windows.
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                {
                    Debug.Log("Switching active build target back to Windows...");

                    EditorUserBuildSettings.SwitchActiveBuildTarget(
                        BuildPipeline.GetBuildTargetGroup(BuildTarget.StandaloneWindows64),
                        BuildTarget.StandaloneWindows64);
                }
            }

            Debug.Log($"Nightly build run complete. Output: {root}");
        }

        /// <summary>
        /// Launches a separate, headless Unity process to build a single platform,
        /// so each platform gets a clean process (and clean RAM) instead of all
        /// three sharing one long-lived Editor session.
        /// </summary>
        private static bool BuildOnePlatformInSubprocess(string root, string folderName)
        {
            string projectPath = Path.GetDirectoryName(Application.dataPath)!;
            string logPath = Path.Combine(root, $"{folderName}.log");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = EditorApplication.applicationPath,
                Arguments =
                    "-batchmode -nographics -quit " +
                    $"-projectPath \"{projectPath}\" " +
                    $"-logFile \"{logPath}\" " +
                    "-executeMethod Editor.MakeAllPlatformNightlyBuild.BuildSinglePlatformCLI " +
                    $"-buildTargetArg {folderName}",
                UseShellExecute = false
            };

            Debug.Log($"Launching isolated build process for {folderName} (log: {logPath})");

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                Debug.LogError($"Failed to start Unity subprocess for {folderName}.");
                return false;
            }

            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                Debug.LogError(
                    $"Subprocess build for {folderName} exited with code {proc.ExitCode}. " +
                    $"See {logPath} for details.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Entry point invoked via -executeMethod inside the spawned subprocess.
        /// Not meant to be called directly from the Editor UI.
        /// </summary>
        public static void BuildSinglePlatformCLI()
        {
            var args = Environment.GetCommandLineArgs();
            int argIndex = Array.IndexOf(args, "-buildTargetArg");
            string wanted = argIndex >= 0 && argIndex + 1 < args.Length ? args[argIndex + 1] : null;

            var platform = Array.Find(Platforms, p => p.folderName == wanted);
            if (platform.folderName == null)
            {
                Debug.LogError($"BuildSinglePlatformCLI: unknown or missing -buildTargetArg '{wanted}'.");
                EditorApplication.Exit(1);
                return;
            }

            string root = Path.Combine(Path.GetDirectoryName(Application.dataPath)!, "Builds");
            string platformDir = Path.Combine(root, platform.folderName);
            string locationPathName = Path.Combine(platformDir, platform.exeName);

            BuildResult result;
            try
            {
                result = BuildOnePlatform(platform.target, locationPathName);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                result = BuildResult.Failed;
            }

            EditorApplication.Exit(result == BuildResult.Succeeded ? 0 : 1);
        }

        private static BuildResult BuildOnePlatform(BuildTarget target, string locationPathName)
        {
            Debug.Log($"Building {target} -> {locationPathName} ...");

            // Explicitly switch to the target platform before building so Unity has a
            // chance to reimport platform-specific assets (especially shaders).
            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                Debug.Log($"Switching active build target to {target}...");

                try
                {
                    bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(
                        BuildPipeline.GetBuildTargetGroup(target),
                        target);

                    if (!switched)
                    {
                        Debug.LogError(
                            $"SwitchActiveBuildTarget returned false. " +
                            $"Current={EditorUserBuildSettings.activeBuildTarget}, Requested={target}");

                        return BuildResult.Failed;
                    }

                    if (EditorUserBuildSettings.activeBuildTarget != target)
                    {
                        Debug.LogError(
                            $"SwitchActiveBuildTarget claimed success but active target is " +
                            $"{EditorUserBuildSettings.activeBuildTarget} instead of {target}.");

                        return BuildResult.Failed;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    return BuildResult.Failed;
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                Debug.Log($"Active build target is now {EditorUserBuildSettings.activeBuildTarget}");
            }

            // Same enabled-scenes list Unity's own Build Settings window uses.
            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            // Standalone (Windows/Mac/Linux) all share one scripting-define group.
            var buildGroup = BuildPipeline.GetBuildTargetGroup(target);
            var originalDefineString = PlayerSettings.GetScriptingDefineSymbolsForGroup(buildGroup);
            var defines = originalDefineString?.Split(';') ?? Array.Empty<string>();
            if (!defines.Contains(YARG_NIGHTLY_BUILD))
            {
                var defineList = defines.ToList();
                defineList.Add(YARG_NIGHTLY_BUILD);
                defines = defineList.ToArray();
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = locationPathName,
                target = target,
                extraScriptingDefines = defines
            };

            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                Debug.LogError(
                    $"Refusing to build. Active target is {EditorUserBuildSettings.activeBuildTarget} but expected {target}.");

                return BuildResult.Failed;
            }

            Debug.Log($"Beginning build for {target}...");

            BuildReport report;
            try
            {
                string outputDirectory = Path.GetDirectoryName(locationPathName)!;

                Debug.Log($"Preparing output directory: {outputDirectory}");

                // Previously this deleted and recreated the whole output folder on
                // every run, forcing Unity to rewrite everything from scratch even
                // when most assets hadn't changed. Just making sure the folder
                // exists lets BuildPipeline.BuildPlayer overwrite in place and
                // reuse what it can, which is noticeably faster.
                //
                // Trade-off: files from assets that were removed from the project
                // since the last build can linger in the output folder. The
                // DoNotShip folder isn't affected either way since it's
                // regenerated fresh by the build and stripped below every run.
                // If you ever suspect stale leftovers, delete the platform's
                // Builds/<Platform> folder manually before the next run.
                Directory.CreateDirectory(outputDirectory);

                report = BuildPipeline.BuildPlayer(options);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return BuildResult.Failed;
            }

            Debug.Log(
                $"Build finished for {target}. " +
                $"Result={report.summary.result}, " +
                $"Warnings={report.summary.totalWarnings}, " +
                $"Errors={report.summary.totalErrors}, " +
                $"Size={(report.summary.totalSize > long.MaxValue ? report.summary.totalSize.ToString("N0") + " bytes" : EditorUtility.FormatBytes((long) report.summary.totalSize))}");

            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError(
                    $"Build FAILED for {target}: {report.summary.result}\n" +
                    $"Output: {locationPathName}\n" +
                    $"Errors: {report.summary.totalErrors}\n" +
                    $"Warnings: {report.summary.totalWarnings}");
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

                // Fastest instead of Optimal: noticeably quicker for ~0.5GB of build
                // output, at the cost of a somewhat larger zip. These are nightly
                // dev builds, not a shipping release archive, so the size trade-off
                // is worth the time saved.
                ZipFile.CreateFromDirectory(platformDir, zipPath, System.IO.Compression.CompressionLevel.Fastest, false);
                Debug.Log($"Zipped {platformDir} -> {zipPath}");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Debug.LogError($"Failed to create {zipPath}");
            }
        }
    }
}
