// UrpArSetup.cs
// ---------------------------------------------------------------------------
// Registers AR Foundation's background renderer feature with the URP renderers.
//
// This is the single most common reason an AR app shows a blank or garbage
// colour instead of the camera feed. Under the Built-in pipeline
// ARCameraBackground blits the camera texture itself, but under URP the blit
// has to be injected as a ScriptableRendererFeature. With
// `m_RendererFeatures: []` the feed is simply never drawn — the AR session
// tracks correctly, planes are detected, content is placed, and the user sees
// a solid colour behind it all.
//
// Applied to every UniversalRendererData in the project so the Editor (PC
// renderer) and the device (Mobile renderer) behave the same.
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;

namespace HariAR.EditorTools
{
    public static class UrpArSetup
    {
        [MenuItem("HARI-AR/Setup/Add AR Background to URP Renderers", priority = 14)]
        public static void AddArBackgroundFeature()
        {
            var renderers = FindRendererData();
            if (renderers.Count == 0)
            {
                Debug.LogWarning("[HARI-AR] No UniversalRendererData assets found.");
                return;
            }

            int patched = 0;
            foreach (var data in renderers)
            {
                if (HasArBackground(data))
                {
                    Debug.Log($"[HARI-AR] {data.name}: AR background feature already present.");
                    continue;
                }

                var feature = ScriptableObject.CreateInstance<ARBackgroundRendererFeature>();
                feature.name = "AR Background";

                // The feature must live inside the renderer asset, not as a
                // loose file, or the reference breaks on reimport.
                AssetDatabase.AddObjectToAsset(feature, data);

                var list = GetFeatureList(data);
                list.Add(feature);

                EditorUtility.SetDirty(data);
                patched++;
                Debug.Log($"[HARI-AR] {data.name}: added AR background renderer feature.");
            }

            if (patched > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log($"[HARI-AR] Patched {patched} renderer(s). " +
                          $"The camera feed will now render under URP.");
            }
        }

        static List<UniversalRendererData> FindRendererData()
        {
            return AssetDatabase.FindAssets("t:UniversalRendererData")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<UniversalRendererData>)
                .Where(d => d != null)
                .ToList();
        }

        static bool HasArBackground(ScriptableRendererData data)
        {
            return GetFeatureList(data).Any(f => f is ARBackgroundRendererFeature);
        }

        /// <summary>
        /// `rendererFeatures` is exposed read-only publicly, so reach the
        /// backing list. Editing the serialised property directly would mean
        /// hand-maintaining m_RendererFeatureMap as well.
        /// </summary>
        static List<ScriptableRendererFeature> GetFeatureList(ScriptableRendererData data)
        {
            var field = typeof(ScriptableRendererData).GetField(
                "m_RendererFeatures",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);

            return field?.GetValue(data) as List<ScriptableRendererFeature>
                   ?? new List<ScriptableRendererFeature>();
        }

        /// <summary>Report whether the camera feed can render. Used by Check Setup.</summary>
        public static bool IsConfigured()
        {
            var renderers = FindRendererData();
            return renderers.Count > 0 && renderers.All(HasArBackground);
        }
    }
}
