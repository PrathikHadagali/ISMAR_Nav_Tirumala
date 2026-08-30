// BuildScript.cs — headless APK build or Editor menu build tool.
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HariAR.EditorTools
{
    public static class BuildScript
    {
        [MenuItem("HARI-AR/Build Android APK", priority = 50)]
        public static void BuildApk()
        {
            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled).Select(s => s.path).ToArray();

            if (scenes.Length == 0)
            {
                if (!Application.isBatchMode)
                {
                    EditorUtility.DisplayDialog("Build Failed", "No active scenes found in Build Settings.", "OK");
                }
                return;
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = "Builds/HariAR.apk",
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,   // build only, never launch
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log($"[HARI-AR][Build] result={summary.result} " +
                      $"errors={summary.totalErrors} size={summary.totalSize}");

            if (summary.result == BuildResult.Succeeded)
            {
                if (!Application.isBatchMode)
                {
                    EditorUtility.DisplayDialog("Build Succeeded", $"APK built successfully at Builds/HariAR.apk\nSize: {summary.totalSize / 1024f / 1024f:F2} MB", "OK");
                }
            }
            else
            {
                if (!Application.isBatchMode)
                {
                    EditorUtility.DisplayDialog("Build Failed", $"APK build failed with {summary.totalErrors} errors. Check console for details.", "OK");
                }
                else
                {
                    EditorApplication.Exit(1);
                }
            }
        }
    }
}
