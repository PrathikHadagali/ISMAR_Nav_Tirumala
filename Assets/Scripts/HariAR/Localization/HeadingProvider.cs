// HeadingProvider.cs
// ---------------------------------------------------------------------------
// True-north heading for the device.
//
// Heading is what decides whether "turn left" points left. It is also the
// weakest signal in the whole system: a magnetometer near the iron railings
// and dense crowds of a temple corridor can be off by 20–30°.
//
// Strategy:
//   • Primary   — Input.compass.trueHeading, circularly smoothed.
//   • Secondary — the AR camera's yaw, which is gyro-driven and far steadier
//                 short-term but has no absolute reference and drifts.
//   The two are fused: the compass supplies absolute truth slowly, the AR pose
//   supplies responsiveness. This is the standard complementary filter, and it
//   is why the arrow stays stable while you walk yet still points north.
// ---------------------------------------------------------------------------

using System;
using UnityEngine;

namespace HariAR.Localization
{
    public class HeadingProvider : MonoBehaviour
    {
        [Header("Sources")]
        [Tooltip("Camera driven by the AR session. Supplies gyro-grade yaw.")]
        public Transform arCamera;

        [Header("Fusion")]
        [Tooltip("How strongly the compass corrects AR yaw drift, per second. " +
                 "Low = smooth but slow to correct; high = accurate but jittery.")]
        [Range(0.01f, 1f)] public float compassCorrectionRate = 0.08f;

        [Tooltip("Reject compass readings with accuracy worse than this (degrees). " +
                 "Negative headingAccuracy means the sensor is uncalibrated.")]
        public float maxCompassErrorDeg = 30f;

        [Tooltip("Editor fallback when no magnetometer exists.")]
        public bool useSimulatedHeading = false;
        public float simulatedHeading = 0f;

        /// <summary>Fused true-north heading in [0, 360).</summary>
        public float Heading { get; private set; }

        /// <summary>Raw compass reading, for logging and diagnostics.</summary>
        public float CompassHeading { get; private set; }

        public bool IsReliable { get; private set; }
        public float CompassAccuracyDeg { get; private set; } = -1f;

        /// <summary>
        /// Offset between AR yaw and true north. Once stable this is what lets
        /// the app place geographic bearings into Unity world space.
        /// </summary>
        public float ArToNorthOffset { get; private set; }

        public bool HasOffset { get; private set; }

        float _smoothedCompass;
        bool _compassInitialised;

        void OnEnable()
        {
            // Input.compass is safe under "Input System Package (New)": the
            // compass and location services are explicitly not covered by the
            // new Input System, and Input.compass.trueHeading has no equivalent
            // there (AttitudeSensor reports orientation, not true north).
            Input.compass.enabled = true;

            // Input.gyro is deliberately NOT touched. Gyroscope *is* covered by
            // the new Input System, so reading it through the legacy class
            // throws InvalidOperationException under this project's setting.
            // Nothing here needs it anyway — device attitude reaches us through
            // the AR camera pose, which ARCore drives via TrackedPoseDriver.
        }

        void Update()
        {
            if (useSimulatedHeading)
            {
                Heading = CompassHeading = Mathf.Repeat(simulatedHeading, 360f);
                IsReliable = true;
                HasOffset = true;
                ArToNorthOffset = arCamera != null
                    ? Mathf.DeltaAngle(arCamera.eulerAngles.y, Heading)
                    : 0f;
                return;
            }

            UpdateCompass();
            FuseWithArPose();
        }

        void UpdateCompass()
        {
            CompassAccuracyDeg = Input.compass.headingAccuracy;
            float raw = Input.compass.trueHeading;

            // headingAccuracy is negative when the magnetometer is uncalibrated.
            IsReliable = Input.compass.enabled &&
                         CompassAccuracyDeg >= 0f &&
                         CompassAccuracyDeg <= maxCompassErrorDeg;

            if (!Input.compass.enabled) return;

            // trueHeading is documented as undefined until the location
            // service is actually running — GpsProvider's startup (permission
            // prompt, service init) takes real time, so for the first several
            // seconds `raw` here can be a meaningless placeholder (commonly
            // reads as a flat 0°), not just an imprecise reading. Bootstrapping
            // the whole heading fusion from that placeholder was worse than
            // waiting: it latches ArToNorthOffset onto an arbitrary rotation
            // immediately, and every AR-anchored arrow ends up rotated off to
            // wherever that rotation happens to point — usually well outside
            // the camera's current view, which looks identical to "nothing is
            // rendering" even though everything is placed and visible, just
            // not in front of the user.
            bool locationLive = Input.location.status == LocationServiceStatus.Running;

            if (!_compassInitialised)
            {
                // Bootstrap from the first reading once it is at least a real
                // (location-backed) value, reliable or not — still not
                // waiting for maxCompassErrorDeg specifically, since that
                // figure routinely never clears 30° near metal structures or
                // in a crowd. GeoToWorld re-reads ArToNorthOffset every frame,
                // so a rough-but-real initial fix self-corrects smoothly via
                // the nudge below once a reliable reading arrives.
                if (!locationLive) return;
                _smoothedCompass = raw;
                _compassInitialised = true;
            }
            else if (IsReliable)
            {
                // Circular smoothing. Naive lerp across the 359°→1° wrap would
                // swing the arrow the long way round. Only reliable readings
                // are folded in here, so a single bad sample beside an iron
                // gate cannot swing an already-decent fix.
                float delta = Mathf.DeltaAngle(_smoothedCompass, raw);
                _smoothedCompass = Mathf.Repeat(_smoothedCompass + delta * 0.15f, 360f);
            }

            CompassHeading = _smoothedCompass;
        }

        void FuseWithArPose()
        {
            if (arCamera == null)
            {
                Heading = CompassHeading;
                HasOffset = _compassInitialised;
                ArToNorthOffset = 0f;
                return;
            }

            float arYaw = arCamera.eulerAngles.y;

            if (!_compassInitialised)
            {
                // No absolute reference yet — report AR yaw so the UI still moves.
                Heading = Mathf.Repeat(arYaw, 360f);
                return;
            }

            float instantOffset = Mathf.DeltaAngle(arYaw, CompassHeading);

            if (!HasOffset)
            {
                ArToNorthOffset = instantOffset;
                HasOffset = true;
            }
            else if (IsReliable)
            {
                // Nudge the offset toward the compass. Slow, so a single bad
                // reading beside an iron gate cannot swing the pathway.
                float correction = Mathf.DeltaAngle(ArToNorthOffset, instantOffset);
                ArToNorthOffset = Mathf.Repeat(
                    ArToNorthOffset + correction * compassCorrectionRate * Time.deltaTime * 10f,
                    360f);
            }

            Heading = Mathf.Repeat(arYaw + ArToNorthOffset, 360f);
        }

        /// <summary>
        /// Convert a geographic bearing into a Unity world-space yaw.
        /// The inverse of the offset the fusion maintains.
        /// </summary>
        public float BearingToUnityYaw(double bearing)
        {
            return Mathf.Repeat((float)bearing - ArToNorthOffset, 360f);
        }

        /// <summary>Signed turn the user must make to face a bearing: + right, - left.</summary>
        public float RelativeTurnTo(double bearing)
        {
            return Mathf.DeltaAngle(Heading, (float)bearing);
        }

        /// <summary>Force re-acquisition of the north offset, e.g. after a tracking loss.</summary>
        public void ResetOffset()
        {
            HasOffset = false;
            _compassInitialised = false;
        }
    }
}
