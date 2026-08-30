// LandmarkLabel.cs
// ---------------------------------------------------------------------------
// A world-anchored label naming the landmark at a junction.
//
// This is the mechanism the paper's RQ2 measures. The baseline condition shows
// a compass arrow; HARI-AR additionally shows *this* — "Dhwaja Sthambam"
// floating at the actual flagpole — which is what let 21/24 participants turn
// correctly where 14/24 managed it from an arrow alone.
//
// Rendered with a runtime-generated TextMesh rather than a prefab so the whole
// client can be reconstructed from scripts with no authored assets.
// ---------------------------------------------------------------------------

using UnityEngine;
using HariAR.Core;
using HariAR.Localization;

namespace HariAR.AR
{
    public class LandmarkLabel : MonoBehaviour
    {
        [Header("Content")]
        public string landmarkName;
        public string turnHint;          // "Turn right here"
        public double latitude;
        public double longitude;
        public int stepIndex;

        [Header("Appearance")]
        public float heightAboveGroundM = 1.8f;
        public float baseFontSize = 0.18f;

        [Tooltip("Label is hidden beyond this distance — a name floating 200 m " +
                 "away is noise, not guidance.")]
        public float visibleRangeM = 90f;

        [Tooltip("Below this distance the label is fully opaque.")]
        public float fullOpacityRangeM = 35f;

        GeoAnchorManager _anchors;
        GpsProvider _gps;
        Camera _camera;

        TextMesh _nameMesh;
        TextMesh _hintMesh;
        Transform _pin;
        MeshRenderer _pinRenderer;
        Material _pinMaterial;

        public bool IsActive { get; private set; } = true;

        public static LandmarkLabel Create(string landmark, string hint,
                                           double lat, double lon, int stepIndex,
                                           GeoAnchorManager anchors,
                                           GpsProvider gps, Camera cam,
                                           Transform parent)
        {
            var go = new GameObject($"Landmark_{stepIndex}_{landmark}");
            go.transform.SetParent(parent, false);

            var label = go.AddComponent<LandmarkLabel>();
            label.landmarkName = landmark;
            label.turnHint = hint;
            label.latitude = lat;
            label.longitude = lon;
            label.stepIndex = stepIndex;
            label._anchors = anchors;
            label._gps = gps;
            label._camera = cam;
            label.Build();
            return label;
        }

        void Build()
        {
            // Vertical pin connecting the label to the ground, so the pilgrim
            // can tell *which* point on the ground the name refers to.
            _pin = GameObject.CreatePrimitive(PrimitiveType.Cylinder).transform;
            _pin.name = "Pin";
            _pin.SetParent(transform, false);
            _pin.localScale = new Vector3(0.035f, heightAboveGroundM * 0.5f, 0.035f);
            _pin.localPosition = new Vector3(0f, heightAboveGroundM * 0.5f, 0f);

            var collider = _pin.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            _pinRenderer = _pin.GetComponent<MeshRenderer>();
            _pinMaterial = RouteRenderer.CreateMaterial(new Color(1f, 0.82f, 0.25f, 0.85f));
            _pinRenderer.material = _pinMaterial;
            _pinRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            _nameMesh = CreateText(landmarkName, heightAboveGroundM + 0.28f,
                                   baseFontSize, new Color(1f, 0.95f, 0.75f));
            if (!string.IsNullOrEmpty(turnHint))
                _hintMesh = CreateText(turnHint, heightAboveGroundM + 0.06f,
                                       baseFontSize * 0.72f, Color.white);

            // Stays hidden until LateUpdate has placed it at a real geo-anchored
            // position — see the matching guard in WaypointMarker.Build().
            _pinRenderer.enabled = false;
            _nameMesh.gameObject.SetActive(false);
            if (_hintMesh != null) _hintMesh.gameObject.SetActive(false);
        }

        static Font _sharedFont;

        static Font GetFont()
        {
            if (_sharedFont == null)
            {
                // Arial.ttf was renamed in 2022; fall back for safety. A
                // TextMesh with a null font silently renders nothing at all.
                _sharedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                              ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            return _sharedFont;
        }

        TextMesh CreateText(string text, float y, float size, Color color)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, y, 0f);

            var tm = go.AddComponent<TextMesh>();
            var font = GetFont();
            tm.font = font;
            tm.text = text;
            tm.color = color;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.fontSize = 96;                 // high res, scaled down below
            tm.characterSize = size / 8f;
            tm.richText = false;

            var mr = go.GetComponent<MeshRenderer>();
            // The renderer must use the font's own atlas material, or the glyphs
            // have no texture to sample and the label stays blank.
            if (font != null) mr.sharedMaterial = font.material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            return tm;
        }

        void LateUpdate()
        {
            if (_anchors == null || !_anchors.IsReady || _camera == null) return;

            transform.position = _anchors.GeoToWorld(latitude, longitude);

            // Billboard toward the camera, upright — a label tilted with the
            // phone is much harder to read while walking.
            Vector3 toCamera = _camera.transform.position - transform.position;
            toCamera.y = 0f;
            if (toCamera.sqrMagnitude > 1e-4f)
                transform.rotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);

            UpdateVisibility();
        }

        void UpdateVisibility()
        {
            double distance = _gps != null && _gps.HasFix
                ? GeoUtils.Haversine(_gps.Latitude, _gps.Longitude, latitude, longitude)
                : Vector3.Distance(_camera.transform.position, transform.position);

            bool visible = IsActive && distance <= visibleRangeM;
            if (_nameMesh != null) _nameMesh.gameObject.SetActive(visible);
            if (_hintMesh != null) _hintMesh.gameObject.SetActive(visible);
            if (_pinRenderer != null) _pinRenderer.enabled = visible;
            if (!visible) return;

            // Fade in with proximity rather than popping into existence.
            float alpha = Mathf.InverseLerp(visibleRangeM, fullOpacityRangeM, (float)distance);
            alpha = Mathf.Clamp(alpha, 0.15f, 1f);

            if (_nameMesh != null)
            {
                var c = _nameMesh.color; c.a = alpha; _nameMesh.color = c;
            }
            if (_hintMesh != null)
            {
                var c = _hintMesh.color; c.a = alpha * 0.9f; _hintMesh.color = c;
            }

            // Grow slightly with distance so far labels stay legible without
            // dominating the view up close.
            float scale = Mathf.Lerp(1f, 2.2f, Mathf.InverseLerp(10f, visibleRangeM, (float)distance));
            transform.localScale = Vector3.one * scale;
        }

        /// <summary>Highlight the landmark for the step the pilgrim is walking now.</summary>
        public void SetHighlighted(bool highlighted)
        {
            IsActive = true;
            var color = highlighted
                ? new Color(0.30f, 1f, 0.45f, 0.95f)
                : new Color(1f, 0.82f, 0.25f, 0.85f);
            if (_pinMaterial != null)
            {
                if (_pinMaterial.HasProperty("_BaseColor")) _pinMaterial.SetColor("_BaseColor", color);
                if (_pinMaterial.HasProperty("_Color")) _pinMaterial.SetColor("_Color", color);
            }
        }

        /// <summary>Dim a landmark the pilgrim has already passed.</summary>
        public void SetPassed()
        {
            IsActive = false;
            if (_nameMesh != null) _nameMesh.gameObject.SetActive(false);
            if (_hintMesh != null) _hintMesh.gameObject.SetActive(false);
            if (_pinRenderer != null) _pinRenderer.enabled = false;
        }

        void OnDestroy()
        {
            if (_pinMaterial != null) Destroy(_pinMaterial);
        }
    }
}
