// GpsProvider.cs
// ---------------------------------------------------------------------------
// Device location with the drift handling the deployment actually needs.
//
// The paper's §1 and §5 name GPS quantization drift of 3–9 m as the defining
// problem of this site, worsening in narrow temple corridors with partial sky
// occlusion. Feeding Input.location.lastData straight into the AR anchoring
// makes the whole pathway twitch by several metres every second.
//
// Three defences, in order of importance:
//   1. Reject fixes whose reported accuracy is worse than a threshold.
//   2. Reject fixes implying an impossible walking speed (a classic multipath
//      symptom: the position jumps 40 m and comes straight back).
//   3. Smooth what survives with an adaptive low-pass filter whose strength
//      follows the reported accuracy — trust good fixes, damp poor ones.
// ---------------------------------------------------------------------------

using System;
using System.Collections;
using UnityEngine;

namespace HariAR.Localization
{
    public enum GpsStatus
    {
        Idle,
        Initializing,
        Running,
        PermissionDenied,
        Disabled,
        Failed,
    }

    public class GpsProvider : MonoBehaviour
    {
        [Header("Accuracy")]
        [Tooltip("Metres of desired accuracy requested from the OS.")]
        public float desiredAccuracyM = 5f;

        [Tooltip("Metres of movement before the OS reports a new fix.")]
        public float updateDistanceM = 1f;

        [Tooltip("Discard fixes reporting worse accuracy than this.")]
        public float maxAcceptableAccuracyM = 30f;

        [Header("Filtering")]
        [Tooltip("Fastest plausible pilgrim speed. Faster implies a bad fix.")]
        public float maxWalkingSpeedMps = 3.0f;

        [Tooltip("Smoothing at good accuracy. 0 = raw, 1 = frozen.")]
        [Range(0f, 0.95f)] public float baseSmoothing = 0.6f;

        [Header("Editor testing")]
        [Tooltip("Feed a fixed coordinate so the app can be exercised without a device.")]
        public bool useSimulatedLocation = false;
        public double simulatedLat = 13.6729;   // GNC Tollgate
        public double simulatedLng = 79.3512;

        public GpsStatus Status { get; private set; } = GpsStatus.Idle;

        /// <summary>Filtered position — what the rest of the app should use.</summary>
        public double Latitude { get; private set; }
        public double Longitude { get; private set; }

        /// <summary>Unfiltered device position, for logging and diagnostics.</summary>
        public double RawLatitude { get; private set; }
        public double RawLongitude { get; private set; }

        public float AccuracyM { get; private set; } = 999f;
        public bool HasFix { get; private set; }
        public int RejectedFixCount { get; private set; }
        public string LastError { get; private set; }

        /// <summary>Raised on every accepted fix.</summary>
        public event Action<double, double, float> OnLocationUpdated;
        public event Action<GpsStatus> OnStatusChanged;

        double _lastAcceptedTime;
        bool _initialised;

        void OnEnable()
        {
            if (!useSimulatedLocation) StartCoroutine(StartLocationService());
            else SimulateFix();
        }

        void OnDisable()
        {
            if (!useSimulatedLocation && Input.location.status == LocationServiceStatus.Running)
                Input.location.Stop();
        }

        void SetStatus(GpsStatus s)
        {
            if (Status == s) return;
            Status = s;
            OnStatusChanged?.Invoke(s);
        }

        void SimulateFix()
        {
            Latitude = RawLatitude = simulatedLat;
            Longitude = RawLongitude = simulatedLng;
            AccuracyM = 3f;
            HasFix = true;
            SetStatus(GpsStatus.Running);
            OnLocationUpdated?.Invoke(Latitude, Longitude, AccuracyM);
        }

        IEnumerator StartLocationService()
        {
            SetStatus(GpsStatus.Initializing);

#if UNITY_ANDROID && !UNITY_EDITOR
            // Android 6+ requires an explicit runtime grant; without it
            // Input.location.Start() silently never starts.
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.FineLocation))
            {
                UnityEngine.Android.Permission.RequestUserPermission(
                    UnityEngine.Android.Permission.FineLocation);

                float waited = 0f;
                while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                           UnityEngine.Android.Permission.FineLocation) && waited < 30f)
                {
                    waited += Time.deltaTime;
                    yield return null;
                }

                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                        UnityEngine.Android.Permission.FineLocation))
                {
                    LastError = "Location permission denied.";
                    SetStatus(GpsStatus.PermissionDenied);
                    yield break;
                }
            }
#endif

            if (!Input.location.isEnabledByUser)
            {
                LastError = "Location services are turned off on this device.";
                SetStatus(GpsStatus.Disabled);
                yield break;
            }

            Input.location.Start(desiredAccuracyM, updateDistanceM);

            int guard = 20;      // seconds
            while (Input.location.status == LocationServiceStatus.Initializing && guard > 0)
            {
                yield return new WaitForSeconds(1f);
                guard--;
            }

            if (guard <= 0)
            {
                LastError = "Timed out waiting for a GPS fix.";
                SetStatus(GpsStatus.Failed);
                yield break;
            }

            if (Input.location.status == LocationServiceStatus.Failed)
            {
                LastError = "Unable to determine device location.";
                SetStatus(GpsStatus.Failed);
                yield break;
            }

            SetStatus(GpsStatus.Running);
            Input.compass.enabled = true;
        }

        void Update()
        {
            if (useSimulatedLocation) return;
            if (Input.location.status != LocationServiceStatus.Running) return;

            var data = Input.location.lastData;
            ProcessFix(data.latitude, data.longitude,
                       Mathf.Max(data.horizontalAccuracy, 1f));
        }

        /// <summary>Exposed so recorded traces can be replayed for study analysis.</summary>
        public void ProcessFix(double lat, double lon, float accuracy)
        {
            RawLatitude = lat;
            RawLongitude = lon;
            AccuracyM = accuracy;

            // 1. accuracy gate
            if (accuracy > maxAcceptableAccuracyM)
            {
                RejectedFixCount++;
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;

            if (!_initialised)
            {
                Latitude = lat;
                Longitude = lon;
                _initialised = true;
                HasFix = true;
                _lastAcceptedTime = now;
                OnLocationUpdated?.Invoke(Latitude, Longitude, accuracy);
                return;
            }

            // 2. speed gate — reject physically impossible jumps
            double dt = Math.Max(now - _lastAcceptedTime, 0.05);
            double jump = Core.GeoUtils.Haversine(Latitude, Longitude, lat, lon);
            if (jump / dt > maxWalkingSpeedMps * 4.0 && jump > 15.0)
            {
                RejectedFixCount++;
                // Never reject forever: a genuine teleport (bus ride, or the
                // first fix after a tunnel) must eventually be accepted.
                if (now - _lastAcceptedTime < 5.0) return;
            }

            // 3. adaptive smoothing — a 3 m fix is trusted, a 25 m fix is damped
            float t = Mathf.InverseLerp(3f, maxAcceptableAccuracyM, accuracy);
            double alpha = Mathf.Lerp(1f - baseSmoothing, 0.08f, t);

            Latitude += (lat - Latitude) * alpha;
            Longitude += (lon - Longitude) * alpha;

            _lastAcceptedTime = now;
            HasFix = true;
            OnLocationUpdated?.Invoke(Latitude, Longitude, accuracy);
        }

        /// <summary>Drop the filter state — call after a long stationary pause.</summary>
        public void ResetFilter()
        {
            _initialised = false;
            RejectedFixCount = 0;
        }
    }
}
