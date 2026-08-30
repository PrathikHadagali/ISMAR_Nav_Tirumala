// ProjectSetup.cs
// ---------------------------------------------------------------------------
// One-click configuration of the Android/AR build settings, plus scene
// generation.
//
// Player Settings for an ARCore app have a dozen interlocking requirements
// (ARM64 only, IL2CPP, no Vulkan on older ARCore stacks, min SDK, no splash
// on the AR camera...). Setting them by hand is where AR projects usually go
// wrong, so they are codified here and can be re-applied at any time.
//
//   Unity.exe -batchmode -quit -projectPath <path> \
//             -executeMethod HariAR.EditorTools.ProjectSetup.ConfigureAll
// ---------------------------------------------------------------------------

using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HariAR.EditorTools
{
    public static class ProjectSetup
    {
        const string ScenePath = "Assets/Scenes/HariAR_Navigation.unity";

        [MenuItem("HARI-AR/Setup/Configure Everything", priority = 10)]
        public static void ConfigureAll()
        {
            ConfigureAndroidPlayerSettings();
            CreateNavigationScene();
            AssetDatabase.SaveAssets();
            Debug.Log("[HARI-AR] Project configured. Open " + ScenePath +
                      ", set the backend URL on HariArApp, then Build And Run.");
        }

        // ── Player settings ──────────────────────────────────────────────────

        [MenuItem("HARI-AR/Setup/Configure Android Player Settings", priority = 11)]
        public static void ConfigureAndroidPlayerSettings()
        {
            var android = NamedBuildTarget.Android;

            PlayerSettings.companyName = "HARI-AR";
            PlayerSettings.productName = "HARI-AR Navigation";
            PlayerSettings.SetApplicationIdentifier(android, "com.hariar.navigation");

            // ARCore requires ARM64 + IL2CPP. ARMv7 is not supported by
            // AR Foundation 6 and Google Play no longer accepts 32-bit only.
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // ARCore needs API 24+; 26 avoids a long tail of driver problems and
            // is required for reliable Geospatial support.
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;

            // The AR camera background does not composite correctly under the
            // auto-graphics API ordering; force OpenGLES3 first.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[]
            {
                UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3,
                UnityEngine.Rendering.GraphicsDeviceType.Vulkan,
            });

            // Multithreaded rendering interacts badly with the AR camera on
            // some Adreno drivers.
            PlayerSettings.SetMobileMTRendering(android, false);

            // Portrait only: the HUD is laid out for it and a pilgrim walking
            // with the phone up should not have the UI rotate underfoot.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;

            // We request location and microphone ourselves, at the moment they
            // are needed, rather than in a wall of prompts on first launch.
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.Android.forceSDCardPermission = false;

            // Allow plain-HTTP requests. Unity blocks them by default, which
            // fails every call with "InvalidOperationException: Insecure
            // connection not allowed".
            //
            // This is a deliberate, scoped trade-off: the HARI-AR backend is a
            // uvicorn process on the local network with no TLS certificate, and
            // the phone reaches it over the venue's own LAN. AlwaysAllowed
            // rather than DevelopmentOnly because the study builds are release
            // builds — DevelopmentOnly would work in the editor and then fail
            // on the participants' device, which is the worst time to find out.
            //
            // If this is ever deployed beyond a controlled network, terminate
            // TLS in front of the API and set this back to NotAllowed.
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;

            EditorUserBuildSettings.SwitchActiveBuildTarget(
                BuildTargetGroup.Android, BuildTarget.Android);

            Debug.Log("[HARI-AR] Android player settings configured " +
                      "(ARM64, IL2CPP, minSdk 26, portrait, GLES3). " +
                      "Permissions are injected at build time by " +
                      "AndroidManifestPostProcessor.");
        }

        // ── Scene ────────────────────────────────────────────────────────────

        [MenuItem("HARI-AR/Setup/Create Navigation Scene", priority = 12)]
        public static void CreateNavigationScene()
        {
            Directory.CreateDirectory("Assets/Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                                                    NewSceneMode.Single);

            // HariArApp builds the entire rig at runtime, so the saved scene
            // holds exactly one object. Nothing to mis-wire, nothing to drift.
            var root = new GameObject("HARI-AR");
            root.AddComponent<HariAR.HariArApp>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings(ScenePath);

            Debug.Log($"[HARI-AR] Created {ScenePath}");
        }

        static void AddSceneToBuildSettings(string path)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Any(s => s.path == path)) return;
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        // ── Diagnostics ──────────────────────────────────────────────────────

        [MenuItem("HARI-AR/Check Setup", priority = 30)]
        public static void CheckSetup()
        {
            var report = new System.Text.StringBuilder("HARI-AR setup:\n");

            report.AppendLine(TypeExists("UnityEngine.XR.ARFoundation.ARSession")
                ? "  OK   AR Foundation installed"
                : "  MISS AR Foundation — run HARI-AR ▸ Setup ▸ Add AR Packages");

            report.AppendLine(TypeExists("Newtonsoft.Json.JsonConvert")
                ? "  OK   Newtonsoft JSON installed"
                : "  MISS Newtonsoft JSON");

            report.AppendLine(
                TypeExists("Google.XR.ARCoreExtensions.AREarthManager")
                ? "  OK   ARCore Geospatial available"
                : "  --   ARCore Geospatial not installed (GPS + compass fallback in use)");

            report.AppendLine(File.Exists(ScenePath)
                ? $"  OK   Scene {ScenePath}"
                : "  MISS Navigation scene — run HARI-AR ▸ Setup ▸ Create Navigation Scene");

            report.AppendLine(
                EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android
                ? "  OK   Build target is Android"
                : "  WARN Build target is not Android");

            Debug.Log(report.ToString());
        }

        static bool TypeExists(string fullName)
        {
            return System.AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetType(fullName) != null);
        }
    }
}
