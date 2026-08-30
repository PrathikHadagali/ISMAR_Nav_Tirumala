// WaypointMarker.cs
// ---------------------------------------------------------------------------
// The floating 3D arrows standing along the route, plus the destination
// beacon.
//
// One marker per entry in the backend's `anchors[]` (~15 m spacing, capped at
// 60). Never one per `path[]` point: that would be several hundred objects,
// and if each became a real ARCore anchor the session would collapse.
//
// Each arrow is a solid, thickness-extruded mesh (not a flat cutout) floating
// above head height, and it billboards to always face the camera. A flat
// marker lying on or just above the ground is viewed nearly edge-on by a
// phone held at ordinary standing height — from that angle a paper-thin mesh
// presents almost no visible surface at all, which is why earlier attempts at
// a ground-hugging chevron were effectively invisible in the field, height or
// ground-detection accuracy notwithstanding. Billboarding guarantees the
// arrow always presents its full face to the viewer, and the built-in bob
// keeps it easy to spot in a crowd.
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using HariAR.Core;
using HariAR.Localization;

namespace HariAR.AR
{
    public class WaypointMarker : MonoBehaviour
    {
        public double latitude;
        public double longitude;
        public double? bearing;
        public bool isDestination;
        public int index;

        GeoAnchorManager _anchors;
        GpsProvider _gps;
        Camera _camera;
        Material _material;
        Transform _visual;

        [HideInInspector] public float visibleRangeM = 70f;
        [HideInInspector] public float bobAmplitude = 0.08f;
        [HideInInspector] public float bobSpeed = 1.6f;

        [Tooltip("Height above the detected ground the arrow floats at — " +
                 "above typical head height, so it stays visible over a crowd.")]
        [HideInInspector] public float floatHeightM = 1.7f;

        float _phase;

        public static WaypointMarker Create(RouteAnchor anchor,
                                            GeoAnchorManager anchors,
                                            GpsProvider gps,
                                            Camera camera,
                                            Transform parent)
        {
            var go = new GameObject(anchor.isDestination
                ? "Waypoint_Destination"
                : $"Waypoint_{anchor.index}");
            go.transform.SetParent(parent, false);

            var marker = go.AddComponent<WaypointMarker>();
            marker.latitude = anchor.lat;
            marker.longitude = anchor.lng;
            marker.bearing = anchor.heading;
            marker.isDestination = anchor.isDestination;
            marker.index = anchor.index;
            marker._anchors = anchors;
            marker._gps = gps;
            marker._camera = camera;
            marker.Build();
            return marker;
        }

        void Build()
        {
            _phase = index * 0.35f;   // stagger the bob so the line reads as flowing

            if (isDestination)
            {
                // A tall beacon, visible from further away than an arrow.
                _visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder).transform;
                _visual.localScale = new Vector3(0.25f, 1.1f, 0.25f);
                _visual.localPosition = new Vector3(0f, 1.1f, 0f);
                _material = RouteRenderer.CreateMaterial(new Color(0.30f, 1f, 0.45f, 0.85f));
                visibleRangeM = 250f;
            }
            else
            {
                _visual = Build3DArrow().transform;
                _visual.localPosition = new Vector3(0f, floatHeightM, 0f);
                _material = RouteRenderer.CreateMaterial(new Color(0.15f, 0.85f, 1f, 0.95f));
            }

            _visual.SetParent(transform, false);

            var col = _visual.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var mr = _visual.GetComponent<MeshRenderer>();
            mr.material = _material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // Stays hidden until LateUpdate has placed it at a real geo-anchored
            // position. Without this, the marker renders at its default local
            // (0,0,0) — coincident with the AR rig's origin — for every frame
            // before GeoAnchorManager.IsReady flips true, which can envelop the
            // camera in a giant, view-filling shape.
            _visual.gameObject.SetActive(false);
        }

        /// <summary>
        /// A solid arrowhead with real thickness, authored in the local XY
        /// plane (X = right, Y = "forward along the path") so that once
        /// billboarded to face the camera, the arrow reads as pointing the
        /// way to walk. Extruded along Z rather than left flat: a paper-thin
        /// mesh viewed even slightly off from perfectly face-on all but
        /// disappears, and a billboard's rotation is smoothed per frame
        /// rather than snapping, so it is never guaranteed perfectly edge-on.
        /// </summary>
        static GameObject Build3DArrow()
        {
            var go = new GameObject("Arrow3D");
            var mf = go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();

            var mesh = new Mesh { name = "Arrow3D" };
            const float w = 0.34f, l = 0.55f, notch = 0.18f, half = 0.05f;

            Vector2[] profile =
            {
                new Vector2(0f, l),          // tip
                new Vector2(-w, -notch),     // left wing
                new Vector2(0f, 0f),         // inner notch
                new Vector2(w, -notch),      // right wing
            };
            int pc = profile.Length;

            var vertices = new List<Vector3>(pc * 2);
            var triangles = new List<int>();

            int frontBase = vertices.Count;
            foreach (var p in profile) vertices.Add(new Vector3(p.x, p.y, half));
            int backBase = vertices.Count;
            foreach (var p in profile) vertices.Add(new Vector3(p.x, p.y, -half));

            int[] capTris = { 0, 2, 1, 0, 3, 2 };
            foreach (var t in capTris) triangles.Add(frontBase + t);
            // Reversed winding so the back cap's normal faces the other way —
            // between the two caps the arrow is visible face-on from either side.
            for (int i = capTris.Length - 1; i >= 0; i--) triangles.Add(backBase + capTris[i]);

            // Side walls, one quad per profile edge, giving the arrow a
            // visible silhouette even when caught slightly off-axis.
            for (int i = 0; i < pc; i++)
            {
                int a = frontBase + i, b = frontBase + (i + 1) % pc;
                int c = backBase + i, d = backBase + (i + 1) % pc;
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
                triangles.Add(b); triangles.Add(d); triangles.Add(c);
            }

            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            mf.mesh = mesh;
            return go;
        }

        void LateUpdate()
        {
            if (_anchors == null || !_anchors.IsReady) return;

            var basePos = _anchors.GeoToWorld(latitude, longitude);

            float bob = Mathf.Sin(Time.time * bobSpeed + _phase) * bobAmplitude;
            transform.position = basePos + Vector3.up * bob;

            if (!isDestination && _camera != null && _visual != null)
            {
                // Billboard toward the camera so the arrow always presents its
                // full face — the whole point of the 3D-arrow redesign. Its
                // built-in "point up" shape then reads correctly as "forward"
                // without needing a (compass-derived, often unreliable)
                // bearing at all.
                Vector3 toCamera = _camera.transform.position - _visual.position;
                toCamera.y = 0f;
                if (toCamera.sqrMagnitude > 1e-4f)
                    _visual.rotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
            }

            UpdateVisibility();
        }

        void UpdateVisibility()
        {
            double distance = _gps != null && _gps.HasFix
                ? GeoUtils.Haversine(_gps.Latitude, _gps.Longitude, latitude, longitude)
                : 0.0;

            bool visible = distance <= visibleRangeM;
            if (_visual != null && _visual.gameObject.activeSelf != visible)
                _visual.gameObject.SetActive(visible);

            if (!visible || _material == null) return;

            float alpha = Mathf.Clamp(
                Mathf.InverseLerp(visibleRangeM, visibleRangeM * 0.3f, (float)distance),
                0.2f, 0.95f);

            if (_material.HasProperty("_BaseColor"))
            {
                var c = _material.GetColor("_BaseColor"); c.a = alpha;
                _material.SetColor("_BaseColor", c);
            }
            if (_material.HasProperty("_Color"))
            {
                var c = _material.GetColor("_Color"); c.a = alpha;
                _material.SetColor("_Color", c);
            }
        }

        /// <summary>True once positioned and within range, i.e. actually visible on screen.</summary>
        public bool IsVisualActive => _visual != null && _visual.gameObject.activeSelf;

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }

    /// <summary>Owns the marker and label objects for the active route.</summary>
    public class ArContentManager : MonoBehaviour
    {
        public GeoAnchorManager anchors;
        public GpsProvider gps;
        public Camera arCamera;

        [Tooltip("Cap on simultaneously instantiated waypoint markers.")]
        public int maxMarkers = 60;

        [Tooltip("Render discrete 15m floating waypoint chevrons. Set false when dense Live View 3D arrows are active.")]
        public bool renderWaypointChevrons = false;

        readonly List<WaypointMarker> _markers = new List<WaypointMarker>();
        readonly List<LandmarkLabel> _labels = new List<LandmarkLabel>();

        Transform _root;

        /// <summary>Waypoint markers currently instantiated for the active route.</summary>
        public int MarkerCount => _markers.Count;

        /// <summary>Of those, how many are actually rendering right now (in range and positioned).</summary>
        public int VisibleMarkerCount
        {
            get
            {
                int n = 0;
                foreach (var m in _markers)
                    if (m != null && m.IsVisualActive) n++;
                return n;
            }
        }

        void Awake()
        {
            _root = new GameObject("HariAR_Content").transform;
            _root.SetParent(transform, false);
        }

        public void BuildRoute(NavResponse route)
        {
            ClearRoute();
            if (route == null) return;

            if (route.anchors != null && route.anchors.Count > 0)
            {
                if (renderWaypointChevrons)
                {
                    int step = Mathf.Max(1, Mathf.CeilToInt(route.anchors.Count / (float)maxMarkers));
                    for (int i = 0; i < route.anchors.Count; i += step)
                    {
                        var a = route.anchors[i];
                        _markers.Add(WaypointMarker.Create(a, anchors, gps, arCamera, _root));
                    }
                }

                // The destination beacon must always be rendered.
                var last = route.anchors[route.anchors.Count - 1];
                if (last.isDestination && (_markers.Count == 0 || !_markers[_markers.Count - 1].isDestination))
                {
                    _markers.Add(WaypointMarker.Create(last, anchors, gps, arCamera, _root));
                }
            }

            if (route.steps != null)
            {
                foreach (var s in route.steps)
                {
                    if (!s.HasLandmark) continue;
                    string hint = s.IsArrival ? "Destination" : TurnHint(s.type);
                    _labels.Add(LandmarkLabel.Create(s.landmark, hint, s.lat, s.lng,
                                                     s.index, anchors, gps,
                                                     arCamera, _root));
                }
            }
        }

        static string TurnHint(string type) => type switch
        {
            "left" => "Turn left here",
            "sharp_left" => "Sharp left here",
            "slight_left" => "Bear left here",
            "right" => "Turn right here",
            "sharp_right" => "Sharp right here",
            "slight_right" => "Bear right here",
            _ => "Continue",
        };

        /// <summary>Highlight the landmark for the active step, dim the passed ones.</summary>
        public void SetCurrentStep(int stepIndex)
        {
            foreach (var label in _labels)
            {
                if (label.stepIndex < stepIndex) label.SetPassed();
                else label.SetHighlighted(label.stepIndex == stepIndex);
            }
        }

        public void ClearRoute()
        {
            foreach (var m in _markers) if (m != null) Destroy(m.gameObject);
            foreach (var l in _labels) if (l != null) Destroy(l.gameObject);
            _markers.Clear();
            _labels.Clear();
        }

        public void SetContentVisible(bool visible)
        {
            if (_root != null) _root.gameObject.SetActive(visible);
        }
    }
}
