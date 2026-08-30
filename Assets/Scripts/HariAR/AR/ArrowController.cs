// ArrowController.cs
// ---------------------------------------------------------------------------
// Google Maps Live View AR Arrow Controller.
//
// Manages the placement, spacing, orientation, scaling, and dynamic pooling of
// 3D floating blue chevron arrows along the active navigation route:
//   • Samples path[] every 3.0–3.5 meters.
//   • Positions arrows 0.3m above the detected horizontal AR ground plane.
//   • Orientates each arrow to point toward the next path node.
//   • Dynamically scales arrows with distance (Near 1.0, Mid 0.7, Far 0.5).
//   • Fades out and recycles arrows within 1.5m or passed behind the camera.
//   • Throttles anchor reprojections to movements >1m or heading turns >5°.
//   • Smoothly lerps and slerps transforms to avoid teleporting.
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using HariAR.Core;
using HariAR.Localization;

namespace HariAR.AR
{
    public class ArrowController : MonoBehaviour
    {
        [Header("Dependencies")]
        public GeoAnchorManager anchors;
        public GpsProvider gps;
        public HeadingProvider heading;
        public ArrowPool arrowPool;
        public Camera arCamera;

        [Header("Arrow Placement")]
        [Tooltip("Distance along the path between consecutive arrows (metres).")]
        public float arrowSpacingM = 3.2f;

        [Tooltip("Height above the detected ground plane the arrows float at (metres).")]
        public float heightAboveGroundM = 0.30f;

        [Tooltip("Maximum forward distance ahead of the user to spawn arrows.")]
        public float forwardHorizonM = 50f;

        [Tooltip("Distance threshold to fade out and remove passed arrows (metres).")]
        public float arrivalDismissDistanceM = 1.5f;

        [Header("Distance Scaling")]
        public float nearDistanceM = 6f;
        public float midDistanceM = 20f;
        public float farDistanceM = 45f;
        public float nearScale = 1.0f;
        public float midScale = 0.72f;
        public float farScale = 0.50f;

        [Header("Update Thresholds")]
        public float positionUpdateThresholdM = 1.0f;
        public float headingUpdateThresholdDeg = 5.0f;

        // Internal path state
        readonly List<PathPoint> _rawPath = new List<PathPoint>();
        readonly List<SampledNode> _sampledNodes = new List<SampledNode>();
        readonly Dictionary<int, ArrowRenderer> _activeArrowMap = new Dictionary<int, ArrowRenderer>();

        Vector3 _lastUpdateCameraPos;
        float _lastUpdateHeading;
        bool _routeDirty;
        bool _isArrived;

        struct SampledNode
        {
            public double lat;
            public double lng;
            public double distanceAlongRoute;
        }

        public int ActiveArrowCount => _activeArrowMap.Count;
        public int TotalSampledNodes => _sampledNodes.Count;

        void Awake()
        {
            if (arrowPool == null)
            {
                arrowPool = GetComponent<ArrowPool>() ?? gameObject.AddComponent<ArrowPool>();
            }
        }

        void Start()
        {
            if (anchors != null)
            {
                anchors.OnOriginChanged += HandleOriginChanged;
            }
        }

        void OnDestroy()
        {
            if (anchors != null)
            {
                anchors.OnOriginChanged -= HandleOriginChanged;
            }
        }

        void HandleOriginChanged()
        {
            _routeDirty = true;
        }

        // ── Route Lifecycle ──────────────────────────────────────────────────

        public void SetRoute(List<PathPoint> path)
        {
            _rawPath.Clear();
            _sampledNodes.Clear();
            _isArrived = false;

            if (path != null && path.Count >= 2)
            {
                _rawPath.AddRange(path);
                ResamplePath();
            }

            _routeDirty = true;
        }

        public void Clear()
        {
            _rawPath.Clear();
            _sampledNodes.Clear();
            _activeArrowMap.Clear();
            arrowPool?.ReturnAll();
            _isArrived = false;
        }

        public void MarkArrived()
        {
            _isArrived = true;
            foreach (var kvp in _activeArrowMap)
            {
                kvp.Value?.SetArrivedColor();
            }
        }

        /// <summary>
        /// Resamples the raw path into uniform equidistant points (~3.2m spacing).
        /// </summary>
        void ResamplePath()
        {
            if (_rawPath.Count < 2) return;

            _sampledNodes.Clear();
            double totalDist = 0;

            // First node
            _sampledNodes.Add(new SampledNode
            {
                lat = _rawPath[0].lat,
                lng = _rawPath[0].lng,
                distanceAlongRoute = 0
            });

            double accumulated = 0;
            for (int i = 0; i < _rawPath.Count - 1; i++)
            {
                var pA = _rawPath[i];
                var pB = _rawPath[i + 1];
                double segDist = GeoUtils.Haversine(pA.lat, pA.lng, pB.lat, pB.lng);

                if (segDist < 0.1) continue;

                double remaining = segDist;
                double segOffset = 0;

                while (accumulated + (remaining - segOffset) >= arrowSpacingM)
                {
                    double needed = arrowSpacingM - accumulated;
                    segOffset += needed;
                    double fraction = segOffset / segDist;

                    double lat = pA.lat + (pB.lat - pA.lat) * fraction;
                    double lng = pA.lng + (pB.lng - pA.lng) * fraction;
                    totalDist += arrowSpacingM;

                    _sampledNodes.Add(new SampledNode
                    {
                        lat = lat,
                        lng = lng,
                        distanceAlongRoute = totalDist
                    });

                    accumulated = 0;
                }

                accumulated += (segDist - segOffset);
            }

            // Always add destination node
            var last = _rawPath[_rawPath.Count - 1];
            double finalDist = GeoUtils.Haversine(_sampledNodes[_sampledNodes.Count - 1].lat,
                                                  _sampledNodes[_sampledNodes.Count - 1].lng,
                                                  last.lat, last.lng);
            _sampledNodes.Add(new SampledNode
            {
                lat = last.lat,
                lng = last.lng,
                distanceAlongRoute = totalDist + finalDist
            });
        }

        // ── Per-Frame Update ─────────────────────────────────────────────────

        void Update()
        {
            if (arCamera == null || anchors == null || arrowPool == null) return;
            if (_sampledNodes.Count < 2) return;

            // AR Plane / Tracking Gate: ensure tracking and horizontal ground detection
            if (!IsTrackingAndGroundReady())
            {
                arrowPool.ReturnAll();
                _activeArrowMap.Clear();
                return;
            }

            Vector3 camPos = arCamera.transform.position;
            float currentHdg = heading != null ? heading.Heading : 0f;

            bool posShifted = Vector3.Distance(camPos, _lastUpdateCameraPos) > positionUpdateThresholdM;
            bool rotShifted = Mathf.Abs(Mathf.DeltaAngle(currentHdg, _lastUpdateHeading)) > headingUpdateThresholdDeg;

            if (_routeDirty || posShifted || rotShifted)
            {
                UpdateActiveArrows(camPos);
                _lastUpdateCameraPos = camPos;
                _lastUpdateHeading = currentHdg;
                _routeDirty = false;
            }

            // Step all active arrows for smooth interpolation
            float dt = Time.deltaTime;
            foreach (var arrow in arrowPool.ActiveArrows)
            {
                if (arrow != null && arrow.IsInUse)
                {
                    arrow.Step(dt);
                }
            }
        }

        bool IsTrackingAndGroundReady()
        {
            if (Application.isEditor) return anchors.IsReady;

            // In device builds, verify ARSession is tracking
            bool tracking = ARSession.state == ARSessionState.SessionTracking;
            return tracking && (anchors.GroundEstablished || anchors.IsReady);
        }

        /// <summary>
        /// Recalculates which sampled nodes should have active 3D arrows and updates their targets.
        /// </summary>
        void UpdateActiveArrows(Vector3 camPos)
        {
            if (gps == null || !gps.HasFix) return;

            // Find closest sampled node to user's geographic location
            int closestIdx = 0;
            double minGeoDist = double.MaxValue;

            for (int i = 0; i < _sampledNodes.Count; i++)
            {
                double d = GeoUtils.Haversine(gps.Latitude, gps.Longitude, _sampledNodes[i].lat, _sampledNodes[i].lng);
                if (d < minGeoDist)
                {
                    minGeoDist = d;
                    closestIdx = i;
                }
            }

            // Determine visible window [startIdx, endIdx]
            int startIdx = closestIdx;
            int endIdx = closestIdx;

            // Allow up to 1 trailing arrow for continuity, but check distance to dismiss
            if (startIdx > 0)
            {
                Vector3 prevPos = anchors.GeoToWorld(_sampledNodes[startIdx - 1].lat, _sampledNodes[startIdx - 1].lng, heightAboveGroundM);
                if (Vector3.Distance(camPos, prevPos) > arrivalDismissDistanceM)
                {
                    // Keep startIdx
                }
                else
                {
                    startIdx--;
                }
            }

            // Lookahead up to forwardHorizonM
            double forwardAcc = 0;
            while (endIdx < _sampledNodes.Count - 1 && forwardAcc < forwardHorizonM)
            {
                forwardAcc += GeoUtils.Haversine(_sampledNodes[endIdx].lat, _sampledNodes[endIdx].lng,
                                                 _sampledNodes[endIdx + 1].lat, _sampledNodes[endIdx + 1].lng);
                endIdx++;
            }

            // Set of indices that should be active this cycle
            var targetIndices = new HashSet<int>();
            for (int i = startIdx; i <= endIdx; i++)
            {
                targetIndices.Add(i);
            }

            // 1. Recycle arrows no longer in the active window
            var toRemove = new List<int>();
            foreach (var kvp in _activeArrowMap)
            {
                int nodeIdx = kvp.Key;
                var arrow = kvp.Value;

                if (!targetIndices.Contains(nodeIdx))
                {
                    arrow.TargetAlpha = 0f;
                    arrowPool.Return(arrow);
                    toRemove.Add(nodeIdx);
                }
            }

            foreach (int idx in toRemove)
            {
                _activeArrowMap.Remove(idx);
            }

            // 2. Position and update active arrows
            for (int i = startIdx; i <= endIdx; i++)
            {
                var node = _sampledNodes[i];
                Vector3 worldPos = anchors.GeoToWorld(node.lat, node.lng, heightAboveGroundM);

                // Compute orientation toward next node
                Vector3 forward;
                if (i < _sampledNodes.Count - 1)
                {
                    var nextNode = _sampledNodes[i + 1];
                    Vector3 nextWorldPos = anchors.GeoToWorld(nextNode.lat, nextNode.lng, heightAboveGroundM);
                    forward = nextWorldPos - worldPos;
                }
                else if (i > 0)
                {
                    var prevNode = _sampledNodes[i - 1];
                    Vector3 prevWorldPos = anchors.GeoToWorld(prevNode.lat, prevNode.lng, heightAboveGroundM);
                    forward = worldPos - prevWorldPos;
                }
                else
                {
                    forward = Vector3.forward;
                }

                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
                Quaternion targetRot = Quaternion.LookRotation(forward.normalized, Vector3.up);

                // Distance from camera for scale and alpha calculation
                float distToCam = Vector3.Distance(camPos, worldPos);

                // Behind camera check (using camera forward vector)
                Vector3 toArrow = (worldPos - camPos).normalized;
                float dotForward = Vector3.Dot(arCamera.transform.forward, toArrow);
                bool isBehind = dotForward < -0.2f && distToCam > 2.0f;

                // Dismiss if too close (user walked through it) or behind
                if (distToCam < arrivalDismissDistanceM || isBehind)
                {
                    if (_activeArrowMap.TryGetValue(i, out var retiringArrow))
                    {
                        retiringArrow.TargetAlpha = 0f;
                        arrowPool.Return(retiringArrow);
                        _activeArrowMap.Remove(i);
                    }
                    continue;
                }

                // Compute dynamic scale
                float scaleFactor = CalculateScale(distToCam);
                Vector3 targetScale = Vector3.one * scaleFactor;

                // Compute alpha fade
                float targetAlpha = CalculateAlpha(distToCam);

                // Get or create arrow from pool
                if (!_activeArrowMap.TryGetValue(i, out var activeArrow))
                {
                    activeArrow = arrowPool.Get();
                    activeArrow.PathIndex = i;
                    activeArrow.Spawn(worldPos, targetRot, targetScale, initialAlpha: 0f);
                    _activeArrowMap[i] = activeArrow;

                    if (_isArrived) activeArrow.SetArrivedColor();
                    else activeArrow.ResetColor();
                }

                // Update targets
                activeArrow.TargetPosition = worldPos;
                activeArrow.TargetRotation = targetRot;
                activeArrow.TargetScale = targetScale;
                activeArrow.TargetAlpha = targetAlpha;
            }
        }

        float CalculateScale(float distance)
        {
            if (distance <= nearDistanceM)
            {
                return nearScale;
            }
            if (distance <= midDistanceM)
            {
                float t = Mathf.InverseLerp(nearDistanceM, midDistanceM, distance);
                return Mathf.Lerp(nearScale, midScale, t);
            }
            if (distance <= farDistanceM)
            {
                float t = Mathf.InverseLerp(midDistanceM, farDistanceM, distance);
                return Mathf.Lerp(midScale, farScale, t);
            }
            return farScale;
        }

        float CalculateAlpha(float distance)
        {
            // Close fade-in from 1.5m to 3.0m
            if (distance < 3.0f)
            {
                return Mathf.Clamp01(Mathf.InverseLerp(arrivalDismissDistanceM, 3.0f, distance));
            }
            // Far fade-out from 35m to forwardHorizonM
            if (distance > 35f)
            {
                return Mathf.Clamp01(Mathf.InverseLerp(forwardHorizonM, 35f, distance));
            }
            return 0.95f;
        }
    }
}
