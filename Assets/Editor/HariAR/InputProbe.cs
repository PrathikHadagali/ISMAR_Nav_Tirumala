// InputProbe.cs
// ---------------------------------------------------------------------------
// Reports which legacy UnityEngine.Input APIs are usable under the project's
// Active Input Handling setting.
//
// Useful because the rule is not obvious: location and compass keep working
// under "Input System Package (New)" (the new system does not cover them),
// while gyro, mouse and keyboard throw InvalidOperationException because it
// does. Run this rather than guessing.
// ---------------------------------------------------------------------------

using UnityEditor;
using UnityEngine;

namespace HariAR.EditorTools
{
    public static class InputProbe
    {
        [MenuItem("HARI-AR/Diagnose Legacy Input APIs", priority = 31)]
        public static void Probe()
        {
            Try("Input.location.isEnabledByUser", () => _ = Input.location.isEnabledByUser);
            Try("Input.location.status",          () => _ = Input.location.status);
            Try("Input.compass.enabled=true",     () => Input.compass.enabled = true);
            Try("Input.compass.trueHeading",      () => _ = Input.compass.trueHeading);
            Try("Input.compass.headingAccuracy",  () => _ = Input.compass.headingAccuracy);
            Try("Input.gyro.enabled=true",        () => Input.gyro.enabled = true);
            Try("Input.mousePosition",            () => _ = Input.mousePosition);
        }

        static void Try(string label, System.Action action)
        {
            try { action(); Debug.Log($"[PROBE] OK    {label}"); }
            catch (System.Exception e) { Debug.Log($"[PROBE] THROW {label} -> {e.GetType().Name}"); }
        }
    }
}
