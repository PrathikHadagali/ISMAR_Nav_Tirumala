// NavigationController.cs
// ---------------------------------------------------------------------------
// The state machine that runs a navigation session end to end.
//
//   Idle ──Listening──▶ Routing ──▶ Guiding ──▶ Arrived
//     ▲                    │           │
//     └────────Error◀──────┴───────────┘
//
// While Guiding it posts the GPS fix to /navigate/update at ~1 Hz. That call
// is pure geometry on the server — no LLM, no retrieval — so it is cheap
// enough to run for the whole walk and is what makes off-route detection and
// step advancement work without re-planning.
//
// A confirmed off-route triggers exactly one re-plan, from the user's current
// position, reusing the session id so the backend's memory stays coherent.
// ---------------------------------------------------------------------------

using System;
using System.Collections;
using UnityEngine;
using HariAR.Core;
using HariAR.Localization;
using HariAR.AR;

namespace HariAR.Navigation
{
    public enum NavState
    {
        Idle,
        Listening,
        Routing,
        Guiding,
        Arrived,
        Error,
    }

    public class NavigationController : MonoBehaviour
    {
        [Header("Services")]
        public NavApiClient api;
        public GpsProvider gps;
        public HeadingProvider heading;
        public GeoAnchorManager geoAnchors;
        public RouteRenderer routeRenderer;
        public ArrowController arrowController;
        public ArContentManager content;

        [Header("Progress")]
        [Tooltip("Seconds between GPS heartbeats to the backend.")]
        public float progressIntervalS = 1.0f;

        [Tooltip("Re-plan automatically when the backend confirms off-route.")]
        public bool autoRerouteOnOffRoute = true;

        [Tooltip("Minimum seconds between automatic re-plans, so a pilgrim " +
                 "standing beside the path is not re-routed repeatedly.")]
        public float minRerouteIntervalS = 20f;

        public NavState State { get; private set; } = NavState.Idle;
        public NavResponse CurrentRoute { get; private set; }
        public ProgressResponse LastProgress { get; private set; }
        public string SessionId { get; private set; }
        public string LastError { get; private set; }
        public int RerouteCount { get; private set; }

        /// <summary>Index of the instruction the pilgrim is currently walking.</summary>
        public int CurrentStepIndex { get; private set; }

        public event Action<NavState> OnStateChanged;
        public event Action<NavResponse> OnRouteReceived;
        public event Action<ProgressResponse> OnProgress;
        public event Action<int, NavStep> OnStepChanged;
        public event Action<string> OnError;
        public event Action OnArrived;

        Coroutine _progressLoop;
        float _lastRerouteTime = -999f;
        string _pendingQuery;

        void SetState(NavState s)
        {
            if (State == s) return;
            State = s;
            OnStateChanged?.Invoke(s);
        }

        // ── Entry points ─────────────────────────────────────────────────────

        /// <summary>Begin navigating from a natural-language query.</summary>
        public void Navigate(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                Fail("I did not catch a destination. Please try again.");
                return;
            }

            if (gps == null || !gps.HasFix)
            {
                Fail("Waiting for a GPS fix. Please step into the open and try again.");
                return;
            }

            _pendingQuery = query;
            StopProgressLoop();
            SetState(NavState.Routing);
            StartCoroutine(RequestRoute(query));
        }

        public void SetListening(bool listening)
        {
            if (listening) SetState(NavState.Listening);
            else if (State == NavState.Listening) SetState(NavState.Idle);
        }

        public void Stop()
        {
            StopProgressLoop();
            if (!string.IsNullOrEmpty(SessionId) && api != null)
                StartCoroutine(api.EndSession(SessionId));

            CurrentRoute = null;
            LastProgress = null;
            CurrentStepIndex = 0;
            routeRenderer?.Clear();
            arrowController?.Clear();
            content?.ClearRoute();
            SetState(NavState.Idle);
        }

        // ── Routing ──────────────────────────────────────────────────────────

        IEnumerator RequestRoute(string query)
        {
            yield return api.Navigate(
                query, gps.Latitude, gps.Longitude, SessionId,
                onSuccess: route =>
                {
                    CurrentRoute = route;
                    SessionId = route.sessionId;
                    CurrentStepIndex = 0;

                    if (!route.HasPath)
                    {
                        Fail("The route came back empty. Please try a different destination.");
                        return;
                    }

                    routeRenderer?.SetRoute(route.path);
                    arrowController?.SetRoute(route.path);
                    content?.BuildRoute(route);
                    content?.SetCurrentStep(0);

                    OnRouteReceived?.Invoke(route);
                    if (route.steps != null && route.steps.Count > 0)
                        OnStepChanged?.Invoke(0, route.steps[0]);

                    SetState(NavState.Guiding);
                    StartProgressLoop();
                },
                onError: Fail);
        }

        void Fail(string message)
        {
            LastError = message;
            SetState(NavState.Error);
            OnError?.Invoke(message);
        }

        // ── Progress heartbeat ───────────────────────────────────────────────

        void StartProgressLoop()
        {
            StopProgressLoop();
            _progressLoop = StartCoroutine(ProgressLoop());
        }

        void StopProgressLoop()
        {
            if (_progressLoop != null)
            {
                StopCoroutine(_progressLoop);
                _progressLoop = null;
            }
        }

        IEnumerator ProgressLoop()
        {
            var wait = new WaitForSeconds(progressIntervalS);

            while (State == NavState.Guiding)
            {
                if (gps != null && gps.HasFix && !string.IsNullOrEmpty(SessionId))
                {
                    yield return api.UpdateProgress(
                        SessionId, gps.Latitude, gps.Longitude,
                        heading != null ? heading.Heading : (float?)null,
                        gps.AccuracyM,
                        onSuccess: HandleProgress,
                        onError: err =>
                        {
                            // A dropped heartbeat is not fatal: the AR pathway is
                            // already placed and stays usable. Only surface it.
                            Debug.LogWarning($"[HARI-AR] Progress update failed: {err}");
                        });
                }

                yield return wait;
            }
        }

        void HandleProgress(ProgressResponse p)
        {
            if (p == null) return;
            LastProgress = p;
            OnProgress?.Invoke(p);

            if (p.currentStep != CurrentStepIndex)
            {
                CurrentStepIndex = p.currentStep;
                content?.SetCurrentStep(CurrentStepIndex);

                if (CurrentRoute?.steps != null &&
                    CurrentStepIndex < CurrentRoute.steps.Count)
                    OnStepChanged?.Invoke(CurrentStepIndex,
                                          CurrentRoute.steps[CurrentStepIndex]);
            }

            if (p.arrived)
            {
                HandleArrival();
                return;
            }

            if (p.offRoute && autoRerouteOnOffRoute)
                TryReroute();
        }

        void HandleArrival()
        {
            StopProgressLoop();
            routeRenderer?.MarkArrived();
            arrowController?.MarkArrived();
            SetState(NavState.Arrived);
            OnArrived?.Invoke();
        }

        void TryReroute()
        {
            if (Time.time - _lastRerouteTime < minRerouteIntervalS) return;
            if (string.IsNullOrEmpty(_pendingQuery)) return;

            _lastRerouteTime = Time.time;
            RerouteCount++;

            Debug.Log($"[HARI-AR] Off route (xte {LastProgress?.crossTrackErrorM:0.0} m) " +
                      $"— re-planning from current position.");

            StopProgressLoop();
            SetState(NavState.Routing);

            // Re-plan to the same destination. Reusing the resolved name rather
            // than the original utterance avoids a second LLM round-trip and
            // cannot re-resolve to somewhere different mid-walk.
            string target = CurrentRoute?.destination ?? _pendingQuery;
            StartCoroutine(RequestRoute(target));
        }

        // ── Queries for the UI ───────────────────────────────────────────────

        public NavStep CurrentStep =>
            CurrentRoute?.steps != null && CurrentStepIndex < CurrentRoute.steps.Count
                ? CurrentRoute.steps[CurrentStepIndex]
                : null;

        public NavStep NextStep =>
            CurrentRoute?.steps != null && CurrentStepIndex + 1 < CurrentRoute.steps.Count
                ? CurrentRoute.steps[CurrentStepIndex + 1]
                : null;

        /// <summary>Signed turn to face the current step's bearing: + right, - left.</summary>
        public float TurnToCurrentStep()
        {
            var step = CurrentStep;
            if (step == null || heading == null) return 0f;
            return heading.RelativeTurnTo(step.bearing);
        }

        /// <summary>Bearing from the user to the next waypoint, for the HUD arrow.</summary>
        public double BearingToNextWaypoint()
        {
            var step = NextStep ?? CurrentStep;
            if (step == null || gps == null || !gps.HasFix) return 0.0;
            return GeoUtils.Bearing(gps.Latitude, gps.Longitude, step.lat, step.lng);
        }
    }
}
