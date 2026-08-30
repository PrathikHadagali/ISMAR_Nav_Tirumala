// GeoAnchorManager.cs
// ---------------------------------------------------------------------------
// Converts geographic coordinates into Unity world positions, which is the
// single most important piece of the AR client: get this wrong and the whole
// pathway points somewhere else.
//
// The mapping needs two things the AR session does not provide on its own:
//   • an ORIGIN — a known (lat, lon) tied to a known Unity position
//   • a ROTATION — how Unity's +z relates to true north, from HeadingProvider
//
// Given both, a coordinate becomes:
//     enu   = ToEnu(lat, lon, originLat, originLon)      // metres east/north
//     world = originWorld + Rot(-northOffset) * (enu.x, 0, enu.y)
//
// Two drift sources fight each other over a 2 km walk:
//   • AR session drift — visual-inertial tracking slips over distance.
//   • GPS noise — 3–9 m at this site, worse in the corridors.
// We re-synchronise the origin to GPS when the two disagree beyond a
// threshold, and blend the correction in over a few frames so the pathway
// slides rather than teleports.
//
// ARCore Geospatial, when the package is installed and VPS coverage exists,
// replaces all of this with rooftop-accurate anchors. It is compiled in only
// behind HARIAR_GEOSPATIAL so the app always builds without it.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using HariAR.Core;

namespace HariAR.Localization
{
    public enum LocalizationMode
    {
        /// <summary>GPS + compass + AR pose. Always available.</summary>
        GpsCompass,
        /// <summary>ARCore Geospatial terrain anchors. Needs VPS coverage.</summary>
        Geospatial,
    }

    public class GeoAnchorManager : MonoBehaviour
    {
        [Header("Dependencies")]
        public GpsProvider gps;
        public HeadingProvider heading;
        public Camera arCamera;
        public ARRaycastManager raycastManager;
        public ARPlaneManager planeManager;

        [Header("Origin re-synchronisation")]
        [Tooltip("Re-sync the origin when GPS and the AR-derived position " +
                 "disagree by more than this many metres.")]
        public float resyncThresholdM = 8f;

        [Tooltip("Minimum seconds between re-syncs, so the pathway is not " +
                 "constantly nudged by ordinary GPS noise.")]
        public float minResyncIntervalS = 5f;

        [Tooltip("Seconds over which a re-sync correction is blended in.")]
        public float resyncBlendSeconds = 1.5f;

        [Header("Ground")]
        [Tooltip("Assumed eye height when no AR plane has been detected yet.")]
        public float assumedEyeHeightM = 1.55f;

        [Tooltip("Draw content this far above the detected ground.")]
        public float groundOffsetM = 0.02f;

        public LocalizationMode Mode { get; private set; } = LocalizationMode.GpsCompass;
        public bool IsReady { get; private set; }

        public double OriginLat { get; private set; }
        public double OriginLon { get; private set; }

        /// <summary>Unity position corresponding to the origin coordinate.</summary>
        public Vector3 OriginWorld { get; private set; }

        /// <summary>Estimated Unity y of the ground plane the user stands on.</summary>
        public float GroundY { get; private set; }

        /// <summary>
        /// True once a real AR plane has been hit at least once. Before this,
        /// GroundY is only a guess (camera height minus an assumed eye height).
        /// Content still renders against the guess rather than waiting —
        /// plane detection can be slow or fail outright on a busy, low-texture
        /// stone plaza, and showing nothing is worse than showing a path that
        /// is a few centimetres off the true floor height. This flag exists so
        /// GroundY can snap straight to the first real hit instead of slowly
        /// lerping in from the guess (see UpdateGroundHeight), and for
        /// diagnostics.
        /// </summary>
        public bool GroundEstablished { get; private set; }

        public int ResyncCount { get; private set; }
        public float LastResyncErrorM { get; private set; }

        public event Action OnOriginChanged;

        float _lastResyncTime = -999f;
        Vector3 _pendingCorrection;
        float _correctionRemaining;
        readonly List<ARRaycastHit> _hits = new List<ARRaycastHit>();

        void Start()
        {
            if (gps != null) gps.OnLocationUpdated += HandleLocation;
        }

        void OnDestroy()
        {
            if (gps != null) gps.OnLocationUpdated -= HandleLocation;
        }

        // ── Origin management ────────────────────────────────────────────────

        void HandleLocation(double lat, double lon, float accuracy)
        {
            if (!IsReady)
            {
                // Wait for heading too: an origin without a north reference
                // would place the pathway at a random rotation.
                if (heading == null || !heading.HasOffset) return;
                SetOrigin(lat, lon);
                IsReady = true;
                return;
            }

            MaybeResync(lat, lon, accuracy);
        }

        /// <summary>Bind a coordinate to the camera's current ground position.</summary>
        public void SetOrigin(double lat, double lon)
        {
            OriginLat = lat;
            OriginLon = lon;
            OriginWorld = CameraGroundPosition();
            _lastResyncTime = Time.time;
            ResyncCount++;
            OnOriginChanged?.Invoke();
        }

        /// <summary>
        /// Correct accumulated drift when GPS and AR disagree.
        ///
        /// The correction is applied to the origin rather than to the placed
        /// content, so every anchor moves consistently and the route keeps its
        /// shape — shifting objects individually would deform the pathway.
        /// </summary>
        void MaybeResync(double lat, double lon, float accuracy)
        {
            if (Time.time - _lastResyncTime < minResyncIntervalS) return;

            // Where AR thinks the user is, expressed geographically.
            var camGround = CameraGroundPosition();
            GeoFromWorld(camGround, out double arLat, out double arLon);

            double errorM = GeoUtils.Haversine(arLat, arLon, lat, lon);
            LastResyncErrorM = (float)errorM;

            // Do not chase a fix that is less certain than the disagreement:
            // a 20 m-accuracy reading cannot adjudicate a 10 m discrepancy.
            if (errorM < resyncThresholdM || errorM < accuracy) return;

            Vector3 target = WorldFromGeoUnsmoothed(lat, lon, camGround.y);
            _pendingCorrection = camGround - target;
            _correctionRemaining = resyncBlendSeconds;

            _lastResyncTime = Time.time;
            ResyncCount++;
        }

        void Update()
        {
            UpdateGroundHeight();

            // Blend in any pending drift correction.
            if (_correctionRemaining > 0f)
            {
                float step = Mathf.Min(Time.deltaTime, _correctionRemaining);
                float fraction = step / resyncBlendSeconds;
                OriginWorld += _pendingCorrection * fraction;
                _correctionRemaining -= step;
                if (_correctionRemaining <= 0f)
                {
                    _pendingCorrection = Vector3.zero;
                    OnOriginChanged?.Invoke();
                }
            }
        }

        // ── Ground estimation ────────────────────────────────────────────────

        void UpdateGroundHeight()
        {
            if (arCamera == null) return;

            float fallback = arCamera.transform.position.y - assumedEyeHeightM;

            if (raycastManager != null)
            {
                // Straight down from the camera onto any detected plane.
                var ray = new Ray(arCamera.transform.position, Vector3.down);
                if (raycastManager.Raycast(ray, _hits, TrackableType.PlaneWithinPolygon)
                    && _hits.Count > 0)
                {
                    float hitY = _hits[0].pose.position.y;

                    // Snap on the very first real hit rather than lerping in from
                    // the guess — easing in from a wrong height is what makes a
                    // ground-anchored path look like it briefly floats or drifts
                    // instead of simply appearing correctly placed.
                    GroundY = GroundEstablished ? Mathf.Lerp(GroundY, hitY, 0.1f) : hitY;
                    GroundEstablished = true;
                    return;
                }
            }

            GroundY = Mathf.Approximately(GroundY, 0f)
                ? fallback
                : Mathf.Lerp(GroundY, fallback, 0.02f);
        }

        Vector3 CameraGroundPosition()
        {
            if (arCamera == null) return Vector3.zero;
            var p = arCamera.transform.position;
            return new Vector3(p.x, GroundY, p.z);
        }

        // ── Conversions ──────────────────────────────────────────────────────

        Vector3 WorldFromGeoUnsmoothed(double lat, double lon, float y)
        {
            var enu = GeoUtils.ToEnu(lat, lon, OriginLat, OriginLon);
            float offset = heading != null ? heading.ArToNorthOffset : 0f;

            // Rotate the east/north vector into Unity's frame. Unity yaw 0
            // points at compass bearing `offset`, so geographic north sits at
            // yaw -offset.
            Vector3 local = Quaternion.Euler(0f, -offset, 0f) *
                            new Vector3((float)enu.x, 0f, (float)enu.y);

            return new Vector3(OriginWorld.x + local.x, y, OriginWorld.z + local.z);
        }

        /// <summary>Geographic coordinate → Unity world position on the ground.</summary>
        public Vector3 GeoToWorld(double lat, double lon)
        {
            return WorldFromGeoUnsmoothed(lat, lon, GroundY + groundOffsetM);
        }

        /// <summary>Geographic coordinate → Unity world position at a given height.</summary>
        public Vector3 GeoToWorld(double lat, double lon, float heightAboveGround)
        {
            return WorldFromGeoUnsmoothed(lat, lon, GroundY + heightAboveGround);
        }

        /// <summary>Alias for GeoToWorld for Google Maps Live View callers.</summary>
        public Vector3 LatLonToWorld(double lat, double lon, float heightAboveGround = 0.3f)
        {
            return GeoToWorld(lat, lon, heightAboveGround);
        }

        public int ActivePlaneCount => planeManager != null ? planeManager.trackables.count : 0;

        /// <summary>Unity world position → geographic coordinate.</summary>
        public void GeoFromWorld(Vector3 world, out double lat, out double lon)
        {
            float offset = heading != null ? heading.ArToNorthOffset : 0f;
            Vector3 local = new Vector3(world.x - OriginWorld.x, 0f,
                                        world.z - OriginWorld.z);

            // Inverse of the rotation applied in WorldFromGeoUnsmoothed.
            Vector3 enu = Quaternion.Euler(0f, offset, 0f) * local;
            GeoUtils.FromEnu(enu.x, enu.z, OriginLat, OriginLon, out lat, out lon);
        }

        /// <summary>Metres from the user to a coordinate, measured geographically.</summary>
        public double DistanceToUser(double lat, double lon)
        {
            if (gps == null || !gps.HasFix) return double.MaxValue;
            return GeoUtils.Haversine(gps.Latitude, gps.Longitude, lat, lon);
        }

        // ── Geospatial (optional) ────────────────────────────────────────────

#if HARIAR_GEOSPATIAL
        [Header("ARCore Geospatial")]
        [Tooltip("Horizontal accuracy below which Geospatial is preferred over GPS.")]
        public double geospatialAccuracyThresholdM = 3.0;

        Google.XR.ARCoreExtensions.AREarthManager _earthManager;

        void Awake()
        {
            _earthManager = FindFirstObjectByType<Google.XR.ARCoreExtensions.AREarthManager>();
        }

        /// <summary>
        /// Switch to Geospatial when the Earth subsystem is tracking with
        /// sufficient accuracy, and fall back the moment it degrades. VPS
        /// coverage over the Tirumala hilltop is not guaranteed, so this is
        /// checked continuously rather than assumed once.
        /// </summary>
        public void EvaluateGeospatialAvailability()
        {
            if (_earthManager == null) { Mode = LocalizationMode.GpsCompass; return; }

            if (_earthManager.EarthTrackingState != TrackingState.Tracking)
            {
                Mode = LocalizationMode.GpsCompass;
                return;
            }

            var pose = _earthManager.CameraGeospatialPose;
            Mode = pose.HorizontalAccuracy <= geospatialAccuracyThresholdM
                ? LocalizationMode.Geospatial
                : LocalizationMode.GpsCompass;
        }
#endif

        public string DiagnosticsSummary()
        {
            return $"mode={Mode} ready={IsReady} ground={GroundEstablished} resyncs={ResyncCount} " +
                   $"lastErr={LastResyncErrorM:0.0}m groundY={GroundY:0.00} " +
                   $"gpsAcc={(gps != null ? gps.AccuracyM : -1f):0.0}m " +
                   $"hdg={(heading != null ? heading.Heading : -1f):0}°";
        }
    }
}
