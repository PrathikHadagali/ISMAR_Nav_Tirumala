// AndroidManifestPostProcessor.cs
// ---------------------------------------------------------------------------
// Injects the permissions HARI-AR needs into the *generated* Gradle manifest.
//
// Why not just ship an Assets/Plugins/Android/AndroidManifest.xml? Because that
// file REPLACES Unity's generated manifest wholesale, which means it must also
// declare the launcher activity — and the correct activity class depends on the
// Application Entry Point setting:
//
//     Activity      → com.unity3d.player.UnityPlayerActivity
//     GameActivity  → com.unity3d.player.UnityPlayerGameActivity   (Unity 6 default)
//
// Hardcoding the wrong one produces exactly this build failure:
//
//     "No activity in the manifest with action MAIN and category LAUNCHER"
//
// because Unity's merger sets android:enabled="false" on the activity that does
// not match the selected entry point, leaving the LAUNCHER filter attached to a
// disabled component.
//
// So we let Unity author the manifest — it always gets the entry point right —
// and only add the few permissions that no PlayerSettings toggle covers.
// ---------------------------------------------------------------------------

using System.IO;
using System.Linq;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

namespace HariAR.EditorTools
{
    public class AndroidManifestPostProcessor : IPostGenerateGradleAndroidProject
    {
        // Runs after AR Foundation's own manifest work.
        public int callbackOrder => 100;

        const string AndroidNs = "http://schemas.android.com/apk/res/android";

        /// <summary>
        /// Permissions Unity will not add on its own.
        ///
        /// Deliberately NOT listed here because something already adds them:
        ///   CAMERA               — ARCore XR Plugin
        ///   INTERNET             — PlayerSettings.Android.forceInternetPermission
        ///   ACCESS_NETWORK_STATE — ARCore XR Plugin
        /// Adding a duplicate is harmless (we check first), but the comment
        /// records where each one actually comes from.
        /// </summary>
        static readonly string[] RequiredPermissions =
        {
            // Position on the OSM navigation graph. No PlayerSettings equivalent.
            "android.permission.ACCESS_FINE_LOCATION",
            "android.permission.ACCESS_COARSE_LOCATION",

            // Spoken destination queries. Unity only auto-adds RECORD_AUDIO when
            // it detects the Microphone class; we drive Android's recogniser
            // through AndroidJavaObject, which static analysis cannot see.
            "android.permission.RECORD_AUDIO",
        };

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var manifestPath = ResolveManifestPath(path);
            if (manifestPath == null)
            {
                Debug.LogError(
                    $"[HARI-AR] Could not locate the generated AndroidManifest under '{path}'. " +
                    $"Location permission will be missing from the build.");
                return;
            }

            var doc = new XmlDocument();
            doc.Load(manifestPath);

            var manifest = doc.SelectSingleNode("/manifest") as XmlElement;
            if (manifest == null)
            {
                Debug.LogError("[HARI-AR] Malformed AndroidManifest — no <manifest> root.");
                return;
            }

            int added = AddPermissions(doc, manifest);
            added += AddGpsFeature(doc, manifest);
            added += AllowCleartextTraffic(doc, manifest);

            if (added > 0)
            {
                doc.Save(manifestPath);
                Debug.Log($"[HARI-AR] Added {added} manifest entr(ies) to {manifestPath}");
            }
            else
            {
                Debug.Log("[HARI-AR] Manifest already contained every required entry.");
            }
        }

        /// <summary>
        /// Find the manifest to patch.
        ///
        /// Unity has handed this callback different roots across versions —
        /// sometimes the unityLibrary module, sometimes the Gradle project root.
        /// Rather than assume, probe the known layouts in order.
        /// </summary>
        static string ResolveManifestPath(string path)
        {
            var candidates = new[]
            {
                Path.Combine(path, "src", "main", "AndroidManifest.xml"),
                Path.Combine(path, "unityLibrary", "src", "main", "AndroidManifest.xml"),
                Path.Combine(path, "launcher", "src", "main", "AndroidManifest.xml"),
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        static int AddPermissions(XmlDocument doc, XmlElement manifest)
        {
            var existing = manifest.SelectNodes("uses-permission")
                                   ?.Cast<XmlElement>()
                                   .Select(e => e.GetAttribute("name", AndroidNs))
                                   .ToHashSet() ?? new System.Collections.Generic.HashSet<string>();

            int added = 0;
            foreach (var permission in RequiredPermissions)
            {
                if (existing.Contains(permission)) continue;

                var element = doc.CreateElement("uses-permission");
                element.SetAttribute("name", AndroidNs, permission);
                manifest.AppendChild(element);
                added++;
            }
            return added;
        }

        /// <summary>
        /// Permit plain-HTTP traffic to the backend.
        ///
        /// Android 9 (API 28) and later block cleartext by default, and that
        /// block is enforced independently of Unity's insecureHttpOption — set
        /// only one of the two and the app still fails, on device only, after
        /// working perfectly in the editor.
        ///
        /// The backend is a uvicorn process on the local network with no TLS
        /// certificate. If HARI-AR ever runs outside a controlled network, put
        /// TLS in front of the API and drop this along with insecureHttpOption.
        /// </summary>
        static int AllowCleartextTraffic(XmlDocument doc, XmlElement manifest)
        {
            var application = manifest.SelectSingleNode("application") as XmlElement;
            if (application == null)
            {
                Debug.LogWarning("[HARI-AR] No <application> node — cannot enable cleartext traffic.");
                return 0;
            }

            if (application.GetAttribute("usesCleartextTraffic", AndroidNs) == "true")
                return 0;

            application.SetAttribute("usesCleartextTraffic", AndroidNs, "true");
            return 1;
        }

        /// <summary>
        /// Declare GPS as an optional feature.
        ///
        /// Optional, not required: marking it required would hide the app on
        /// Play from devices without a GPS radio, and the app is still usable
        /// for browsing destinations without one.
        /// </summary>
        static int AddGpsFeature(XmlDocument doc, XmlElement manifest)
        {
            const string feature = "android.hardware.location.gps";

            bool present = manifest.SelectNodes("uses-feature")
                                   ?.Cast<XmlElement>()
                                   .Any(e => e.GetAttribute("name", AndroidNs) == feature)
                           ?? false;
            if (present) return 0;

            var element = doc.CreateElement("uses-feature");
            element.SetAttribute("name", AndroidNs, feature);
            element.SetAttribute("required", AndroidNs, "false");
            manifest.AppendChild(element);
            return 1;
        }
    }
}
