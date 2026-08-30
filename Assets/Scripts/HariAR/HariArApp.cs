// HariArApp.cs
// ---------------------------------------------------------------------------
// Composition root.
//
// Builds and wires the entire client at runtime — AR session, localization,
// rendering, navigation, voice, UI, and the study harness — from a single
// component. Drop one HariArApp into an empty scene and the app is complete.
//
// Everything is constructed in code rather than authored in the scene so that
// the configuration is reviewable in source control, cannot be broken by an
// accidental inspector edit during a study session, and can be rebuilt from
// scratch on any machine.
// ---------------------------------------------------------------------------

using UnityEngine;
using UnityEngine.XR.ARFoundation;
using HariAR.Core;
using HariAR.Localization;
using HariAR.AR;
using HariAR.Navigation;
using HariAR.UI;
using HariAR.Voice;
using HariAR.Study;

namespace HariAR
{
    public class HariArApp : MonoBehaviour
    {
        [Header("Backend")]
        [Tooltip("Address used in DEVICE builds. Deployed backend (Railway) — " +
                 "reachable over any mobile network, not just the dev LAN. " +
                 "Swap to a LAN IP (http://<ip>:8000) only for local-uvicorn testing.")]
        public string backendUrl = "https://hari-ar-backend-production.up.railway.app";

        [Tooltip("Address used when running in the Editor. The Editor runs on " +
                 "the same machine as the backend, so loopback is both correct " +
                 "and immune to VPN or LAN routing problems.")]
        public string editorBackendUrl = "http://localhost:8000";

        /// <summary>
        /// The address actually used.
        ///
        /// Editor Play mode and the device need different hosts, and having one
        /// field for both means it is always wrong for whichever you are not
        /// currently testing — and the failure looks identical to the server
        /// being down.
        /// </summary>
        public string ActiveBackendUrl =>
            Application.isEditor && !string.IsNullOrWhiteSpace(editorBackendUrl)
                ? editorBackendUrl
                : backendUrl;

        [Header("Language")]
        [Tooltip("en | te | hi — passed to the backend and the speech recogniser.")]
        public string language = "en";

        [Header("Location testing")]
        [Tooltip("Simulate a GPS fix when running in the Unity Editor.")]
        public bool simulateLocationInEditor = true;

        [Tooltip("Simulate a GPS fix ON THE DEVICE TOO. Turn this on to test " +
                 "the full AR pathway away from Tirumala — the real fix would " +
                 "be hundreds of kilometres from the navigation graph and every " +
                 "route would be rejected. MUST be off for real trials.")]
        public bool simulateLocationOnDevice = false;

        [Tooltip("Coordinate used when simulating. Defaults to GNC Tollgate, " +
                 "the starting point for all four study tasks.")]
        public double simulatedLat = 13.6729;    // GNC Tollgate
        public double simulatedLng = 79.3512;

        [Header("Google Maps Live View 3D Arrows")]
        [Tooltip("Enable large floating 3D blue arrows along the path.")]
        public bool enableLiveViewArrows = true;

        [Tooltip("Distance along path between consecutive arrows (metres).")]
        public float arrowSpacing = 3.2f;

        [Tooltip("Height above detected ground plane (metres).")]
        public float arrowHeightAboveGround = 0.30f;

        [Tooltip("Pre-warmed pool size for 3D arrows.")]
        public int arrowPoolSize = 50;

        [Header("Study mode")]
        public bool enableStudyMode = false;
        public int participantId = 1;
        public int taskIndex = 1;
        public StudyCondition condition = StudyCondition.HariAr;

        [Header("Debug")]
        [Tooltip("On-screen readout of AR session, GPS, heading, mic and backend " +
                 "state. Leave ON until the app is confirmed working on device — " +
                 "a phone has no Console, and every failure mode otherwise looks " +
                 "identical to 'nothing happened'.")]
        public bool showDiagnostics = true;

        // Built references, exposed for inspection at runtime.
        public ARSession Session { get; private set; }
        public Camera ArCamera { get; private set; }
        public NavApiClient Api { get; private set; }
        public GpsProvider Gps { get; private set; }
        public HeadingProvider Heading { get; private set; }
        public GeoAnchorManager Anchors { get; private set; }
        public RouteRenderer Route { get; private set; }
        public ArrowPool ArrowPool { get; private set; }
        public ArrowController ArrowController { get; private set; }
        public ArContentManager Content { get; private set; }
        public NavigationController Nav { get; private set; }
        public SpeechInput Speech { get; private set; }
        public TtsPlayer Tts { get; private set; }
        public InstructionHUD Hud { get; private set; }
        public StudyController Study { get; private set; }
        public StudyLogger Logger { get; private set; }

        void Awake()
        {
            // A pilgrim following a route must not have the screen lock.
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            Application.targetFrameRate = 60;

            BuildArRig();
            BuildServices();
            BuildRendering();
            BuildNavigation();
            BuildUi();
            BuildStudy();
        }

        // ── AR rig ───────────────────────────────────────────────────────────

        void BuildArRig()
        {
            var sessionGo = new GameObject("AR Session");
            sessionGo.transform.SetParent(transform, false);
            Session = sessionGo.AddComponent<ARSession>();

            // XROrigin expects Origin ▸ Camera Offset ▸ Camera. Collapsing the
            // offset object breaks floor-relative tracking on some devices.
            var originGo = new GameObject("XR Origin");
            originGo.transform.SetParent(transform, false);

            var offsetGo = new GameObject("Camera Offset");
            offsetGo.transform.SetParent(originGo.transform, false);

            var cameraGo = new GameObject("AR Camera");
            cameraGo.transform.SetParent(offsetGo.transform, false);

            ArCamera = cameraGo.AddComponent<Camera>();
            ArCamera.clearFlags = CameraClearFlags.SolidColor;
            ArCamera.backgroundColor = Color.black;
            ArCamera.nearClipPlane = 0.1f;
            ArCamera.farClipPlane = 400f;      // route content is capped well inside this
            ArCamera.tag = "MainCamera";

            cameraGo.AddComponent<AudioListener>();
            cameraGo.AddComponent<ARCameraManager>();
            cameraGo.AddComponent<ARCameraBackground>();
            AddPoseDriver(cameraGo);

            var origin = originGo.AddComponent<Unity.XR.CoreUtils.XROrigin>();
            origin.Camera = ArCamera;
            origin.CameraFloorOffsetObject = offsetGo;

            // Plane detection gives us the ground height the pathway sits on.
            var planeManager = originGo.AddComponent<ARPlaneManager>();
            planeManager.requestedDetectionMode = UnityEngine.XR.ARSubsystems
                                                       .PlaneDetectionMode.Horizontal;
            originGo.AddComponent<ARRaycastManager>();
            originGo.AddComponent<ARAnchorManager>();
        }

        /// <summary>
        /// Drive the camera transform from the XR device pose.
        ///
        /// The Input System's TrackedPoseDriver needs explicit bindings — added
        /// without them the component exists but the camera never moves, which
        /// looks exactly like a broken AR session.
        /// </summary>
        static void AddPoseDriver(GameObject cameraGo)
        {
            var driver = cameraGo.AddComponent<
                UnityEngine.InputSystem.XR.TrackedPoseDriver>();

            driver.trackingType = UnityEngine.InputSystem.XR
                .TrackedPoseDriver.TrackingType.RotationAndPosition;
            driver.updateType = UnityEngine.InputSystem.XR
                .TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;

            var position = new UnityEngine.InputSystem.InputAction(
                "AR Camera Position", UnityEngine.InputSystem.InputActionType.Value,
                "<XRHMD>/centerEyePosition", expectedControlType: "Vector3");
            var rotation = new UnityEngine.InputSystem.InputAction(
                "AR Camera Rotation", UnityEngine.InputSystem.InputActionType.Value,
                "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion");
            var tracked = new UnityEngine.InputSystem.InputAction(
                "AR Camera Is Tracked", UnityEngine.InputSystem.InputActionType.Button,
                "<XRHMD>/isTracked");

            driver.positionInput = new UnityEngine.InputSystem.InputActionProperty(position);
            driver.rotationInput = new UnityEngine.InputSystem.InputActionProperty(rotation);
            driver.trackingStateInput = new UnityEngine.InputSystem.InputActionProperty(tracked);

            // Properties assigned after Awake do not take effect until the
            // component is cycled.
            driver.enabled = false;
            driver.enabled = true;
        }

        // ── Services ─────────────────────────────────────────────────────────

        void BuildServices()
        {
            Api = gameObject.AddComponent<NavApiClient>();
            Api.baseUrl = ActiveBackendUrl;
            Debug.Log($"[HARI-AR] Backend endpoint: {Api.baseUrl}");

            Gps = gameObject.AddComponent<GpsProvider>();
            Gps.useSimulatedLocation =
                Application.isEditor ? simulateLocationInEditor : simulateLocationOnDevice;
            Gps.simulatedLat = simulatedLat;
            Gps.simulatedLng = simulatedLng;

            Heading = gameObject.AddComponent<HeadingProvider>();
            Heading.arCamera = ArCamera.transform;
            // Heading is only simulated in the editor. On device the real
            // compass is used even with a simulated position, so the AR
            // pathway still orients to the world the user is standing in.
            Heading.useSimulatedHeading = Application.isEditor && simulateLocationInEditor;

            Anchors = gameObject.AddComponent<GeoAnchorManager>();
            Anchors.gps = Gps;
            Anchors.heading = Heading;
            Anchors.arCamera = ArCamera;
            Anchors.raycastManager = FindFirstObjectByType<ARRaycastManager>();
            Anchors.planeManager = FindFirstObjectByType<ARPlaneManager>();

            Speech = gameObject.AddComponent<SpeechInput>();
            Speech.localeTag = LocaleFor(language);

            // TtsPlayer carries [RequireComponent(typeof(AudioSource))], so the
            // AudioSource is added for us — adding one here too would leave the
            // object with two.
            Tts = gameObject.AddComponent<TtsPlayer>();
            Tts.localeTag = LocaleFor(language);
        }

        static string LocaleFor(string lang) => lang switch
        {
            "te" => "te-IN",
            "hi" => "hi-IN",
            _ => "en-IN",
        };

        // ── Rendering ────────────────────────────────────────────────────────

        void BuildRendering()
        {
            // 1. Google Maps Live View 3D Arrow Pooling & Controller
            var poolGo = new GameObject("ArrowPool");
            poolGo.transform.SetParent(transform, false);
            ArrowPool = poolGo.AddComponent<ArrowPool>();
            ArrowPool.poolSize = arrowPoolSize;

            var arrowCtrlGo = new GameObject("ArrowController");
            arrowCtrlGo.transform.SetParent(transform, false);
            ArrowController = arrowCtrlGo.AddComponent<ArrowController>();
            ArrowController.anchors = Anchors;
            ArrowController.gps = Gps;
            ArrowController.heading = Heading;
            ArrowController.arrowPool = ArrowPool;
            ArrowController.arCamera = ArCamera;
            ArrowController.arrowSpacingM = arrowSpacing;
            ArrowController.heightAboveGroundM = arrowHeightAboveGround;

            // 2. Route Ribbon (with Live View 3D Arrows delegation)
            var ribbonGo = new GameObject("RouteRibbon",
                                          typeof(MeshFilter), typeof(MeshRenderer));
            ribbonGo.transform.SetParent(transform, false);

            Route = ribbonGo.AddComponent<RouteRenderer>();
            Route.anchors = Anchors;
            Route.gps = Gps;
            Route.arrowController = ArrowController;
            Route.render3DArrows = enableLiveViewArrows;
            Route.renderRibbonMesh = !enableLiveViewArrows;

            // 3. Content Manager (Destination Beacon & Landmark Labels)
            Content = gameObject.AddComponent<ArContentManager>();
            Content.anchors = Anchors;
            Content.gps = Gps;
            Content.arCamera = ArCamera;
            Content.renderWaypointChevrons = false; // Dense Live View arrows handled by ArrowController
        }

        // ── Navigation ───────────────────────────────────────────────────────

        void BuildNavigation()
        {
            Nav = gameObject.AddComponent<NavigationController>();
            Nav.api = Api;
            Nav.gps = Gps;
            Nav.heading = Heading;
            Nav.geoAnchors = Anchors;
            Nav.routeRenderer = Route;
            Nav.arrowController = ArrowController;
            Nav.content = Content;
        }

        // ── UI ───────────────────────────────────────────────────────────────

        void BuildUi()
        {
            var hudGo = new GameObject("HUD");
            hudGo.transform.SetParent(transform, false);

            Hud = hudGo.AddComponent<InstructionHUD>();
            Hud.nav = Nav;
            Hud.speech = Speech;
            Hud.tts = Tts;
            Hud.gps = Gps;
            Hud.heading = Heading;
            Hud.geoAnchors = Anchors;
            Hud.arrowController = ArrowController;
            Hud.content = Content;
            Hud.api = Api;
            Hud.showDiagnostics = showDiagnostics;

            // The scene needs an EventSystem for buttons to receive input, and
            // building one here keeps the "empty scene" promise intact.
            //
            // InputSystemUIInputModule, not StandaloneInputModule: this project
            // has Active Input Handling set to "Input System Package (New)", and
            // the legacy module reads UnityEngine.Input.mousePosition, which
            // throws InvalidOperationException under that setting.
            if (FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.transform.SetParent(transform, false);
                es.AddComponent<UnityEngine.EventSystems.EventSystem>();
                es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            }
        }

        // ── Study ────────────────────────────────────────────────────────────

        void BuildStudy()
        {
            Logger = gameObject.AddComponent<StudyLogger>();
            Logger.gps = Gps;
            Logger.heading = Heading;
            Logger.nav = Nav;
            Logger.geoAnchors = Anchors;

            Study = gameObject.AddComponent<StudyController>();
            Study.nav = Nav;
            Study.content = Content;
            Study.routeRenderer = Route;
            Study.arrowController = ArrowController;
            Study.hud = Hud;
            Study.logger = Logger;
            Study.studyModeEnabled = enableStudyMode;
            Study.participantId = participantId;
            Study.taskIndex = taskIndex;
            Study.condition = condition;
        }

        // ── Convenience ──────────────────────────────────────────────────────

        /// <summary>Navigate to a destination by name or free-text query.</summary>
        public void Navigate(string query) => Nav?.Navigate(query);

        void Start()
        {
            StartCoroutine(Api.CheckHealth(
                health =>
                {
                    if (health.IsReady)
                        Debug.Log($"[HARI-AR] Backend {health.version} ready.");
                    else
                        Debug.LogWarning("[HARI-AR] Backend reachable but still warming up.");
                },
                error => Debug.LogError($"[HARI-AR] Backend unreachable: {error}")));
        }
    }
}
