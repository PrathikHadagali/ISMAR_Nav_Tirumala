// StudyLogger.cs
// ---------------------------------------------------------------------------
// CSV telemetry for the N=24 evaluation.
//
// Without this file there is no way to compute the numbers §4.4 reports:
// wayfinding errors per session, task completion, or the per-junction turn
// outcomes behind the 21/24 vs 14/24 comparison. Everything is written to
// Application.persistentDataPath so it survives the app closing and can be
// pulled off the device with adb.
//
// Two streams per session:
//   *_track.csv   one row per GPS fix — the walked trajectory
//   *_events.csv  one row per discrete event — turns, errors, task boundaries
//
// Rows are flushed immediately. A phone that dies mid-session must not take
// the participant's data with it.
// ---------------------------------------------------------------------------

using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using HariAR.Core;
using HariAR.Localization;
using HariAR.Navigation;

namespace HariAR.Study
{
    public class StudyLogger : MonoBehaviour
    {
        [Header("Sources")]
        public GpsProvider gps;
        public HeadingProvider heading;
        public NavigationController nav;
        public GeoAnchorManager geoAnchors;

        [Header("Sampling")]
        [Tooltip("Seconds between trajectory samples.")]
        public float trackIntervalS = 1.0f;

        public bool IsLogging { get; private set; }
        public string SessionName { get; private set; }
        public int WayfindingErrorCount { get; private set; }
        public string OutputDirectory => Path.Combine(Application.persistentDataPath, "study");

        StreamWriter _track;
        StreamWriter _events;
        float _nextSample;
        float _sessionStart;
        double _distanceWalked;
        double _lastLat, _lastLon;
        bool _hasLast;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public void BeginSession(int participantId, StudyCondition condition, int taskIndex)
        {
            EndSession();

            Directory.CreateDirectory(OutputDirectory);
            SessionName = $"P{participantId:00}_T{taskIndex}_{condition}_" +
                          $"{DateTime.Now:yyyyMMdd_HHmmss}";

            try
            {
                _track = new StreamWriter(
                    Path.Combine(OutputDirectory, SessionName + "_track.csv"), false, Encoding.UTF8);
                _track.WriteLine("t_s,lat,lon,raw_lat,raw_lon,accuracy_m,heading_deg," +
                                 "compass_deg,nav_state,step_index,xte_m,dist_to_dest_m," +
                                 "distance_walked_m,gps_rejected,resyncs");

                _events = new StreamWriter(
                    Path.Combine(OutputDirectory, SessionName + "_events.csv"), false, Encoding.UTF8);
                _events.WriteLine("t_s,timestamp,event,detail,lat,lon,heading_deg,step_index");

                _track.AutoFlush = true;
                _events.AutoFlush = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[HARI-AR][Study] Cannot open log files: {e.Message}");
                return;
            }

            _sessionStart = Time.time;
            _distanceWalked = 0.0;
            _hasLast = false;
            WayfindingErrorCount = 0;
            IsLogging = true;

            LogEvent("session_start",
                     $"participant={participantId};task={taskIndex};condition={condition}");
            Debug.Log($"[HARI-AR][Study] Logging to {OutputDirectory}/{SessionName}_*.csv");

            HookNav();
        }

        public void EndSession()
        {
            if (!IsLogging) return;

            LogEvent("session_end",
                     $"errors={WayfindingErrorCount};" +
                     $"walked_m={_distanceWalked:0.0};" +
                     $"duration_s={Time.time - _sessionStart:0.0}");

            UnhookNav();

            _track?.Flush(); _track?.Dispose(); _track = null;
            _events?.Flush(); _events?.Dispose(); _events = null;
            IsLogging = false;
        }

        void HookNav()
        {
            if (nav == null) return;
            nav.OnStateChanged += HandleState;
            nav.OnStepChanged += HandleStep;
            nav.OnRouteReceived += HandleRoute;
            nav.OnArrived += HandleArrived;
            nav.OnError += HandleNavError;
        }

        void UnhookNav()
        {
            if (nav == null) return;
            nav.OnStateChanged -= HandleState;
            nav.OnStepChanged -= HandleStep;
            nav.OnRouteReceived -= HandleRoute;
            nav.OnArrived -= HandleArrived;
            nav.OnError -= HandleNavError;
        }

        void HandleState(NavState s) => LogEvent("nav_state", s.ToString());

        void HandleStep(int index, NavStep step) =>
            LogEvent("step_changed",
                     $"{index}|{step?.type}|{step?.landmark}|{step?.text}");

        void HandleRoute(NavResponse r) =>
            LogEvent("route_received",
                     $"dest={r.destination};dist_m={r.totalDistanceM:0};" +
                     $"steps={r.steps?.Count};coverage={r.landmarkCoverage:0.00};" +
                     $"source={r.matchSource};session={r.sessionId}");

        void HandleArrived() => LogEvent("arrived", "");

        void HandleNavError(string message) => LogEvent("nav_error", message);

        void Update()
        {
            if (!IsLogging || Time.time < _nextSample) return;
            _nextSample = Time.time + trackIntervalS;
            SampleTrack();
        }

        void SampleTrack()
        {
            if (_track == null || gps == null || !gps.HasFix) return;

            if (_hasLast)
            {
                double step = GeoUtils.Haversine(_lastLat, _lastLon,
                                                 gps.Latitude, gps.Longitude);
                // Ignore sub-metre jitter so a stationary participant does not
                // accumulate phantom walking distance.
                if (step > 0.8) _distanceWalked += step;
            }
            _lastLat = gps.Latitude;
            _lastLon = gps.Longitude;
            _hasLast = true;

            var p = nav?.LastProgress;

            _track.WriteLine(string.Join(",",
                F(Time.time - _sessionStart),
                F(gps.Latitude, 7), F(gps.Longitude, 7),
                F(gps.RawLatitude, 7), F(gps.RawLongitude, 7),
                F(gps.AccuracyM),
                F(heading != null ? heading.Heading : -1f),
                F(heading != null ? heading.CompassHeading : -1f),
                nav != null ? nav.State.ToString() : "",
                nav != null ? nav.CurrentStepIndex.ToString() : "",
                F(p?.crossTrackErrorM ?? -1.0),
                F(p?.distanceToDestinationM ?? -1.0),
                F(_distanceWalked),
                gps.RejectedFixCount.ToString(),
                geoAnchors != null ? geoAnchors.ResyncCount.ToString() : "0"));
        }

        public void LogEvent(string eventName, string detail)
        {
            if (_events == null) return;

            _events.WriteLine(string.Join(",",
                F(Time.time - _sessionStart),
                DateTime.Now.ToString("HH:mm:ss.fff", Inv),
                Escape(eventName),
                Escape(detail),
                F(gps != null && gps.HasFix ? gps.Latitude : 0.0, 7),
                F(gps != null && gps.HasFix ? gps.Longitude : 0.0, 7),
                F(heading != null ? heading.Heading : -1f),
                nav != null ? nav.CurrentStepIndex.ToString() : ""));
        }

        /// <summary>
        /// Record a wrong turn. This is the raw material for §4.4's
        /// "1.8 errors per session (baseline) vs 0.6 (HARI-AR)".
        /// </summary>
        public void LogWayfindingError(string note = "")
        {
            WayfindingErrorCount++;
            LogEvent("wayfinding_error", $"#{WayfindingErrorCount}|{note}");
        }

        static string F(double v, int decimals = 3) =>
            v.ToString("F" + decimals, Inv);

        /// <summary>Quote a field so instruction text containing commas stays one column.</summary>
        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\"", "'").Replace("\n", " ").Replace("\r", " ");
            return s.Contains(",") ? $"\"{s}\"" : s;
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) { _track?.Flush(); _events?.Flush(); }
        }

        void OnDestroy() => EndSession();
    }
}
