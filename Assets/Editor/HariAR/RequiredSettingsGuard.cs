// RequiredSettingsGuard.cs
// ---------------------------------------------------------------------------
// Enforces the project settings HARI-AR cannot run without.
//
// There is exactly one such setting today: cleartext HTTP. The backend is a
// uvicorn process on the LAN with no TLS certificate, and Unity refuses plain
// HTTP by default, failing every request with
//
//     InvalidOperationException: Insecure connection not allowed
//
// before a packet is sent. There is no runtime workaround — no header, no
// certificate handler, no UnityWebRequest flag reaches it. The project setting
// is the only lever, so relying on someone remembering to flip a menu item
// makes the app's basic function depend on a manual step. It is applied here
// instead, on editor load and after every recompile.
//
// The change is announced in the Console rather than made silently, and is
// idempotent — it writes only when the value is actually wrong.
//
// SECURITY: this permits unencrypted, unauthenticated traffic between the
// client and the API. That is acceptable for a controlled LAN deployment and
// nothing else. Before HARI-AR runs on any untrusted network, terminate TLS in
// front of the backend, delete this file, and set Allow downloads over HTTP
// back to "Not allowed".
// ---------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;

namespace HariAR.EditorTools
{
    [InitializeOnLoad]
    public static class RequiredSettingsGuard
    {
        const string BackendIsHttpOnly =
            "the HARI-AR backend serves plain HTTP on the local network";

        static RequiredSettingsGuard()
        {
            // Deferred: PlayerSettings is not reliably writable during the
            // static constructor itself on a domain reload.
            EditorApplication.delayCall += Apply;
        }

        [MenuItem("HARI-AR/Setup/Apply Required Settings", priority = 13)]
        public static void Apply()
        {
            bool changed = false;

            if (PlayerSettings.insecureHttpOption != InsecureHttpOption.AlwaysAllowed)
            {
                PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
                changed = true;
                Debug.Log(
                    "[HARI-AR] Set 'Allow downloads over HTTP' to Always allowed — " +
                    BackendIsHttpOnly + ". Exit and re-enter Play mode for it to " +
                    "take effect on in-flight requests.");
            }

            // Under URP the camera feed is drawn by a renderer feature. Without
            // it the AR session runs perfectly and the user stares at a blank
            // colour, which reads as "the camera is broken" and is very hard to
            // diagnose from the symptom.
            if (!UrpArSetup.IsConfigured())
            {
                UrpArSetup.AddArBackgroundFeature();
                changed = true;
            }

            if (changed)
            {
                // PlayerSettings edits only mark the asset dirty; without this
                // the value is lost if the editor is killed rather than closed.
                AssetDatabase.SaveAssets();
            }
        }
    }
}
