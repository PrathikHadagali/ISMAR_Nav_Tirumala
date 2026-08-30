// RouteRenderer.cs
// ---------------------------------------------------------------------------
// Draws the AR pathway on the ground — the "AR Pathway" of the research goal.
//
// Two visual layers, matching the two tiers the backend returns:
//   • path[]    — dense ~3 m ribbon, rendered as a continuous ground mesh
//   • anchors[] — sparse ~15 m markers, rendered as directional chevrons
//
// Only the portion near the user is built. A 2 km route holds ~700 ribbon
// points; meshing all of them wastes memory on geometry that is behind the
// pilgrim or hundreds of metres ahead, and ARCore's tracking is not accurate
// enough at that range for it to mean anything anyway.
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using HariAR.Core;
using HariAR.Localization;

namespace HariAR.AR
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class RouteRenderer : MonoBehaviour
    {
        [Header("Dependencies")]
        public GeoAnchorManager anchors;
        public GpsProvider gps;
        public ArrowController arrowController;

        [Header("Live View 3D Arrows")]
        [Tooltip("Render Google Maps Live View-style floating 3D blue arrows.")]
        public bool render3DArrows = true;

        [Tooltip("Render legacy flat ground ribbon.")]
        public bool renderRibbonMesh = false;

        [Header("Ribbon")]
        [Tooltip("Width of the painted pathway, in metres.")]
        public float ribbonWidthM = 0.9f;

        [Tooltip("Only render ribbon within this distance of the user.")]
        public float visibleRangeM = 60f;

        [Tooltip("Keep this much of the path behind the user, for orientation.")]
        public float trailingRangeM = 8f;

        [Header("Appearance")]
        [Tooltip("Solid, opaque colour of the ribbon itself — a floor decal, " +
                 "not a translucent overlay, so it reads clearly against any " +
                 "ground surface.")]
        public Color pathColor = new Color(0.10f, 0.35f, 0.95f, 1f);
        public Color arrivedColor = new Color(1f, 0.80f, 0.15f, 1f);

        [Tooltip("Colour of the chevrons painted on top of the ribbon.")]
        public Color chevronColor = Color.white;

        [Tooltip("Real-world length of one chevron, in metres.")]
        public float chevronSpacingM = 1.4f;

        [Tooltip("Chevron repeats travelled per second, toward the goal.")]
        public float flowSpeed = 0.45f;

        [Header("Rebuild")]
        [Tooltip("Rebuild the mesh when the user has moved this far.")]
        public float rebuildDistanceM = 2f;

        Mesh _mesh;
        MeshRenderer _renderer;
        Material _material;
        Material _chevronMaterial;
        List<PathPoint> _path;
        Vector3 _lastBuildPosition;
        bool _dirty;
        float _flowOffset;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        void Awake()
        {
            _mesh = new Mesh { name = "HariAR_RouteRibbon" };
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().mesh = _mesh;

            _renderer = GetComponent<MeshRenderer>();

            // Two materials rendering the same single submesh: an opaque base
            // (the solid-colour "paint") and a transparent overlay carrying the
            // chevron cutout tinted plain white. Two draws of identical geometry
            // is how a floor decal with a distinct arrow colour is done without
            // a second mesh — Unity renders one material per pass when there
            // are more materials than submeshes.
            _material = CreateMaterial(pathColor, transparent: false);
            _chevronMaterial = CreateMaterial(chevronColor, transparent: true);

            // Chevrons rather than a plain band: a flat stripe tells the pilgrim
            // where the path is but not which way along it to walk. Arrows do
            // both, and scrolling them makes the direction unmistakable even
            // while standing still.
            var chevrons = ChevronTexture();
            if (_chevronMaterial.HasProperty("_BaseMap")) _chevronMaterial.SetTexture("_BaseMap", chevrons);
            if (_chevronMaterial.HasProperty("_MainTex")) _chevronMaterial.SetTexture("_MainTex", chevrons);

            _renderer.materials = new[] { _material, _chevronMaterial };
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
        }

        static Texture2D _chevronTexture;

        /// <summary>
        /// A vertically-tiling chevron strip. U spans the ribbon width, V runs
        /// along its length and is tiled per metre by the mesh UVs, so the
        /// arrows keep a constant real-world size however long the route is.
        /// </summary>
        static Texture2D ChevronTexture()
        {
            if (_chevronTexture != null) return _chevronTexture;

            const int w = 64, h = 64;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };

            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = x / (float)(w - 1);          // 0..1 across the width
                float v = y / (float)h;               // 0..1 along the length

                // A chevron: |u-0.5| grows as v decreases, giving a V shape that
                // points along +V, i.e. toward the destination.
                float centre = Mathf.Abs(u - 0.5f) * 2f;      // 0 centre, 1 edge
                float band = Mathf.Repeat(v - centre * 0.35f, 1f);

                // Solid for the leading 55% of each repeat, transparent after,
                // which reads as a chevron with a gap behind it.
                byte a = band < 0.55f ? (byte)255 : (byte)0;

                // Soften the outer edges so the ribbon does not look cut out.
                float edgeFade = Mathf.Clamp01((1f - centre) * 4f);
                px[y * w + x] = new Color32(255, 255, 255, (byte)(a * edgeFade));
            }

            tex.SetPixels32(px);
            tex.Apply();
            _chevronTexture = tex;
            return tex;
        }

        /// <summary>
        /// Build an unlit material at runtime.
        /// Created in code so the scene needs no pre-authored assets — the
        /// whole client can be reconstructed from scripts alone.
        /// </summary>
        /// <param name="transparent">
        /// True (the default) for alpha-blended content that fades with
        /// distance, like markers and labels. False for an opaque, depth-
        /// writing surface — used for the route ribbon's solid base coat, so
        /// it reads as painted onto the ground rather than a translucent
        /// ghost, and so the chevron overlay drawn after it z-tests cleanly.
        /// </param>
        public static Material CreateMaterial(Color color, bool transparent = true)
        {
            // URP first, then the built-in pipeline, then whatever exists.
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
                            ?? Shader.Find("Unlit/Color")
                            ?? Shader.Find("Sprites/Default");

            var mat = new Material(shader);

            if (mat.HasProperty(BaseColorId)) mat.SetColor(BaseColorId, color);
            if (mat.HasProperty(ColorId)) mat.SetColor(ColorId, color);

            // Every write is guarded: the fallback shaders do not declare these
            // properties, and writing a missing property logs an error per call.
            if (transparent)
            {
                SetIfPresent(mat, "_Surface", 1f);
                SetIfPresent(mat, "_Blend", 0f);
                SetIfPresent(mat, "_ZWrite", 0f);
                SetIfPresent(mat, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                SetIfPresent(mat, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);

                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            }
            else
            {
                SetIfPresent(mat, "_Surface", 0f);
                SetIfPresent(mat, "_ZWrite", 1f);
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            }

            return mat;
        }

        static void SetIfPresent(Material mat, string property, float value)
        {
            if (mat.HasProperty(property)) mat.SetFloat(property, value);
        }

        // ── Route lifecycle ──────────────────────────────────────────────────

        public void SetRoute(List<PathPoint> path)
        {
            _path = path;
            _dirty = true;
            SetColor(pathColor);

            if (render3DArrows && arrowController != null)
            {
                arrowController.SetRoute(path);
            }
        }

        public void Clear()
        {
            _path = null;
            _mesh.Clear();

            if (arrowController != null)
            {
                arrowController.Clear();
            }
        }

        /// <summary>Retint the ribbon's solid base coat. Chevrons stay their own colour.</summary>
        public void SetColor(Color c)
        {
            if (_material == null) return;
            if (_material.HasProperty(BaseColorId)) _material.SetColor(BaseColorId, c);
            if (_material.HasProperty(ColorId)) _material.SetColor(ColorId, c);
        }

        public void MarkArrived()
        {
            SetColor(arrivedColor);
            if (arrowController != null)
            {
                arrowController.MarkArrived();
            }
        }

        void Update()
        {
            if (!renderRibbonMesh)
            {
                if (_mesh.vertexCount > 0) _mesh.Clear();
                return;
            }

            if (_path == null || _path.Count < 2 || anchors == null || !anchors.IsReady)
                return;

            var camPos = anchors.arCamera != null
                ? anchors.arCamera.transform.position
                : Vector3.zero;

            if (_dirty || Vector3.Distance(camPos, _lastBuildPosition) > rebuildDistanceM)
            {
                Rebuild();
                _lastBuildPosition = camPos;
                _dirty = false;
            }

            // Scroll the chevron texture toward the destination, so the pathway
            // reads as directional even when the user is standing still.
            _flowOffset = Mathf.Repeat(_flowOffset + flowSpeed * Time.deltaTime, 1f);
            var offset = new Vector2(0f, -_flowOffset);
            if (_chevronMaterial == null) return;
            if (_chevronMaterial.HasProperty("_BaseMap")) _chevronMaterial.SetTextureOffset("_BaseMap", offset);
            if (_chevronMaterial.HasProperty("_MainTex")) _chevronMaterial.SetTextureOffset("_MainTex", offset);
        }

        // ── Mesh construction ────────────────────────────────────────────────

        void Rebuild()
        {
            _mesh.Clear();
            if (gps == null || !gps.HasFix) return;

            // Find the path point nearest the user, then take a window around it.
            int nearest = 0;
            double nearestDist = double.MaxValue;
            for (int i = 0; i < _path.Count; i++)
            {
                double d = GeoUtils.Haversine(gps.Latitude, gps.Longitude,
                                              _path[i].lat, _path[i].lng);
                if (d < nearestDist) { nearestDist = d; nearest = i; }
            }

            int start = nearest, end = nearest;
            double back = 0, forward = 0;

            while (start > 0 && back < trailingRangeM)
            {
                back += GeoUtils.Haversine(_path[start].lat, _path[start].lng,
                                           _path[start - 1].lat, _path[start - 1].lng);
                start--;
            }
            while (end < _path.Count - 1 && forward < visibleRangeM)
            {
                forward += GeoUtils.Haversine(_path[end].lat, _path[end].lng,
                                              _path[end + 1].lat, _path[end + 1].lng);
                end++;
            }

            int count = end - start + 1;
            if (count < 2) return;

            // World positions for the visible window.
            var pts = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                var p = _path[start + i];
                pts[i] = anchors.GeoToWorld(p.lat, p.lng);
            }

            BuildRibbon(pts);
        }

        /// <summary>
        /// Extrude a flat quad strip along the polyline.
        ///
        /// Each joint is mitred using the average of the incoming and outgoing
        /// directions; without that, sharp turns produce a visible pinch where
        /// the two quads meet.
        /// </summary>
        void BuildRibbon(Vector3[] pts)
        {
            int n = pts.Length;
            var vertices = new Vector3[n * 2];
            var uvs = new Vector2[n * 2];
            var triangles = new int[(n - 1) * 6];
            float half = ribbonWidthM * 0.5f;

            float running = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 forward;
                if (i == 0) forward = pts[1] - pts[0];
                else if (i == n - 1) forward = pts[n - 1] - pts[n - 2];
                else forward = (pts[i + 1] - pts[i - 1]);

                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
                forward.Normalize();

                Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

                vertices[i * 2] = pts[i] - right * half;
                vertices[i * 2 + 1] = pts[i] + right * half;

                if (i > 0) running += Vector3.Distance(pts[i - 1], pts[i]);

                // V is measured in metres / chevronSpacing, so one chevron
                // occupies a fixed real-world length regardless of route length.
                float v = running / Mathf.Max(chevronSpacingM, 0.1f);
                uvs[i * 2] = new Vector2(0f, v);
                uvs[i * 2 + 1] = new Vector2(1f, v);
            }

            for (int i = 0; i < n - 1; i++)
            {
                int v = i * 2, t = i * 6;
                triangles[t] = v;
                triangles[t + 1] = v + 2;
                triangles[t + 2] = v + 1;
                triangles[t + 3] = v + 1;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }

            _mesh.vertices = vertices;
            _mesh.uv = uvs;
            _mesh.triangles = triangles;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
        }
    }
}
