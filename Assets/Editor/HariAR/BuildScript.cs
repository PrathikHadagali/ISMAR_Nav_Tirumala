// BuildScript.cs — headless APK build, used to verify the generated manifest.
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HariAR.EditorTools
{
    public static class BuildScript
    {
        public static void BuildApk()
        {
            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled).Select(s => s.path).ToArray();

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

            if (summary.result != BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }
    }
}
