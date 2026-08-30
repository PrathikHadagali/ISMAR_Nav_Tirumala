// PackageBootstrap.cs
// ---------------------------------------------------------------------------
// Adds the AR packages HARI-AR needs, resolving versions through the Package
// Manager rather than hand-written manifest entries. Pinning versions by hand
// is how a project ends up refusing to open: one wrong string and resolution
// fails before any script can run.
//
// Run from the menu (HARI-AR ▸ Setup ▸ Add AR Packages) or headless:
//   Unity.exe -batchmode -quit -projectPath <path> \
//             -executeMethod HariAR.EditorTools.PackageBootstrap.AddArPackages
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace HariAR.EditorTools
{
    public static class PackageBootstrap
    {
        /// <summary>Registry packages required for the AR client.</summary>
        static readonly string[] RequiredPackages =
        {
            "com.unity.xr.arfoundation",   // AR session, camera, anchors, raycast
            "com.unity.xr.arcore",         // Android ARCore provider
            "com.unity.xr.management",     // XR loader lifecycle
            "com.unity.nuget.newtonsoft-json", // robust JSON for the API contract
        };

        /// <summary>
        /// ARCore Extensions (Geospatial API) ships from GitHub, not the
        /// registry, and needs git on PATH. It is deliberately separate: if it
        /// fails to resolve, the app must still build and run on the
        /// GPS + compass fallback path.
        /// </summary>
        public const string GeospatialPackage =
            "https://github.com/google-ar/arcore-unity-extensions.git";

        [MenuItem("HARI-AR/Setup/Add AR Packages", priority = 0)]
        public static void AddArPackages()
        {
            var installed = ListInstalled();
            var missing = RequiredPackages.Where(p => !installed.Contains(p)).ToList();

            if (missing.Count == 0)
            {
                Debug.Log("[HARI-AR] All required AR packages already installed.");
                return;
            }

            Debug.Log($"[HARI-AR] Adding {missing.Count} package(s): " +
                      string.Join(", ", missing));

            // AddAndRemove installs as one atomic resolution pass, so the
            // resolver sees the whole dependency set at once instead of
            // recomputing (and potentially conflicting) per package.
            var request = Client.AddAndRemove(missing.ToArray(), null);
            WaitFor(request, "add AR packages");

            if (request.Status == StatusCode.Success)
            {
                foreach (var p in request.Result)
                    Debug.Log($"[HARI-AR]   installed {p.name}@{p.version}");
            }
        }

        [MenuItem("HARI-AR/Setup/Add ARCore Geospatial (optional)", priority = 1)]
        public static void AddGeospatial()
        {
            if (ListInstalled().Contains("com.google.ar.core.arfoundation.extensions"))
            {
                Debug.Log("[HARI-AR] ARCore Extensions already installed.");
                return;
            }

            Debug.Log("[HARI-AR] Adding ARCore Extensions from git — needs git on PATH.");
            var request = Client.Add(GeospatialPackage);
            WaitFor(request, "add ARCore Extensions");

            if (request.Status == StatusCode.Success)
            {
                Debug.Log($"[HARI-AR] Installed {request.Result.name}@{request.Result.version}");
                DefineSymbols.Add("HARIAR_GEOSPATIAL");
                Debug.Log("[HARI-AR] Defined HARIAR_GEOSPATIAL — Geospatial code paths are now live.");
            }
            else
            {
                Debug.LogWarning(
                    "[HARI-AR] ARCore Extensions failed to install. The app still " +
                    "works on GPS + compass; geospatial terrain anchors stay disabled. " +
                    $"Reason: {request.Error?.message}");
            }
        }

        static HashSet<string> ListInstalled()
        {
            var list = Client.List(offlineMode: true, includeIndirectDependencies: false);
            WaitFor(list, "list packages");
            return list.Status == StatusCode.Success
                ? new HashSet<string>(list.Result.Select(p => p.name))
                : new HashSet<string>();
        }

        /// <summary>
        /// Block until a Package Manager request finishes.
        /// Batch mode has no editor loop to pump, so polling is the only option.
        /// </summary>
        static void WaitFor(Request request, string what)
        {
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (!request.IsCompleted)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"[HARI-AR] Timed out trying to {what}.");
                System.Threading.Thread.Sleep(100);
            }

            if (request.Status >= StatusCode.Failure)
                Debug.LogError($"[HARI-AR] Failed to {what}: {request.Error?.message}");
        }
    }

    /// <summary>Scripting-define-symbol helper shared by the setup tools.</summary>
    public static class DefineSymbols
    {
        public static void Add(string symbol)
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(BuildTargetGroup.Android);
            var defines = PlayerSettings.GetScriptingDefineSymbols(target);
            var parts = defines.Split(';').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (parts.Contains(symbol)) return;
            parts.Add(symbol);
            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", parts));
        }

        public static void Remove(string symbol)
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(BuildTargetGroup.Android);
            var defines = PlayerSettings.GetScriptingDefineSymbols(target);
            var parts = defines.Split(';')
                               .Where(s => !string.IsNullOrWhiteSpace(s) && s != symbol)
                               .ToList();
            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", parts));
        }
    }
}
