// InstructionHUD.cs
// ---------------------------------------------------------------------------
// The 2D overlay.
//
// Layout, top to bottom:
//   • Destination card  — stop number, name, live distance, next landmark
//   • Instruction card  — the active turn, with the landmark it is anchored to
//   • Status pill       — connection, GPS and error messaging
//   • Mic button        — tap to toggle, with a listening pulse
//   • Type / Browse     — the fallback when speech fails (paper §4.2 reports
//                         STT errors on heritage nouns as the main failure mode)
//
// Direction is shown in AR, not here: RouteRenderer paints a continuous strip
// of forward-pointing chevrons flat on the ground toward the destination. A
// screen-space turn arrow was tried and dropped — it read the phone compass,
// which is unreliable enough (magnetic interference, no tilt compensation
// indoors) that the arrow pointed in a different, often wrong, direction from
// moment to moment. The ground path only ever appears once GeoAnchorManager
// has actually detected a real plane to sit on, so it cannot show at the
// wrong height/angle the way the compass arrow could point the wrong way.
//
// Built in code via UiKit, so there is still nothing to author or wire, but the
// result is rounded cards and a circular mic rather than flat blocks.
// ---------------------------------------------------------------------------

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using HariAR.Core;
using HariAR.Navigation;
using HariAR.Localization;
using HariAR.Voice;
using HariAR.AR;

namespace HariAR.UI
{
    public class InstructionHUD : MonoBehaviour
    {
        [Header("Dependencies")]
        public NavigationController nav;
        public SpeechInput speech;
        public TtsPlayer tts;
        public GpsProvider gps;
        public HeadingProvider heading;
        public GeoAnchorManager geoAnchors;
        public ArrowController arrowController;
        public ArContentManager content;
        public NavApiClient api;

        [Header("Options")]
        public bool speakInstructions = true;
        public bool showDiagnostics = false;

        Canvas _canvas;

        // Destination card
        RectTransform _destinationCard;
        Text _stopBadge, _destinationName, _destinationDistance;

        // Instruction card
        RectTransform _instructionCard;
        Text _instructionText, _landmarkText, _stepCounter;

        // Status
        RectTransform _statusPill;
        Text _statusText;
        Text _diagnosticsText;

        // Controls
        Button _micButton;
        Image _micRing;
        RectTransform _micRingRect;

        // Panels
        GameObject _searchPanel;
        RectTransform _searchContent;
        InputField _searchField;

        readonly List<GameObject> _resultRows = new List<GameObject>();
        float _pulse;
        float _fps;
        float _fpsAccumulator;
        int _fpsFrames;
        float _fpsTimeLeft;

        void Start()
        {
            _fpsTimeLeft = 0.5f;
            BuildUi();
            HookEvents();
            ShowStatus("Locating you…", UiKit.TextSecondary);
            SetRouteVisible(false);
        }

        // ── Construction ─────────────────────────────────────────────────────

        void BuildUi()
        {
            var canvasGo = new GameObject("HariAR_HUD");
            canvasGo.transform.SetParent(transform, false);

            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            BuildDestinationCard();
            BuildInstructionCard();
            BuildStatusPill();
            BuildControls();
            BuildSearchPanel();

            if (showDiagnostics) BuildDiagnostics();
        }

        void BuildDestinationCard()
        {
            _destinationCard = UiKit.Panel("DestinationCard", _canvas.transform,
                                           UiKit.Surface, 28);
            _destinationCard.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                                   new Vector2(0.5f, 1f), new Vector2(0f, -48f),
                                   new Vector2(980f, 148f));

            // Accent stripe marks the card as the active destination.
            var stripe = UiKit.Panel("Stripe", _destinationCard, UiKit.Accent, 8);
            stripe.Place(new Vector2(0f, 0f), new Vector2(0f, 1f),
                         new Vector2(0f, 0.5f), new Vector2(18f, 0f),
                         new Vector2(8f, -36f));

            _stopBadge = UiKit.Label("Badge", _destinationCard, "#1", 34,
                                     UiKit.Accent, TextAnchor.MiddleLeft,
                                     FontStyle.Bold);
            _stopBadge.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f),
                                           new Vector2(0f, 1f), new Vector2(46f, -22f),
                                           new Vector2(90f, 44f));

            _destinationName = UiKit.Label("Name", _destinationCard, "", 42,
                                           UiKit.TextPrimary, TextAnchor.UpperLeft,
                                           FontStyle.Bold);
            _destinationName.rectTransform.Place(new Vector2(0f, 1f), new Vector2(1f, 1f),
                                                 new Vector2(0f, 1f), new Vector2(140f, -20f),
                                                 new Vector2(-320f, 52f));

            _destinationDistance = UiKit.Label("Distance", _destinationCard, "", 40,
                                               UiKit.Accent, TextAnchor.MiddleRight,
                                               FontStyle.Bold);
            _destinationDistance.rectTransform.Place(new Vector2(1f, 1f), new Vector2(1f, 1f),
                                                     new Vector2(1f, 1f), new Vector2(-28f, -22f),
                                                     new Vector2(240f, 48f));

            _stepCounter = UiKit.Label("StepCounter", _destinationCard, "", 26,
                                       UiKit.TextSecondary, TextAnchor.LowerLeft);
            _stepCounter.rectTransform.Place(new Vector2(0f, 0f), new Vector2(1f, 0f),
                                             new Vector2(0f, 0f), new Vector2(46f, 18f),
                                             new Vector2(-80f, 34f));
        }

        void BuildInstructionCard()
        {
            _instructionCard = UiKit.Panel("InstructionCard", _canvas.transform,
                                           UiKit.Surface, 28);
            _instructionCard.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                                   new Vector2(0.5f, 1f), new Vector2(0f, -212f),
                                   new Vector2(980f, 190f));

            _instructionText = UiKit.Label("Instruction", _instructionCard,
                                           "Tap the microphone and say where you want to go.",
                                           38, UiKit.TextPrimary, TextAnchor.UpperLeft);
            _instructionText.rectTransform.Place(new Vector2(0f, 1f), new Vector2(1f, 1f),
                                                 new Vector2(0f, 1f), new Vector2(30f, -24f),
                                                 new Vector2(-60f, 110f));

            _landmarkText = UiKit.Label("Landmark", _instructionCard, "", 32,
                                        UiKit.Warning, TextAnchor.LowerLeft);
            _landmarkText.rectTransform.Place(new Vector2(0f, 0f), new Vector2(1f, 0f),
                                              new Vector2(0f, 0f), new Vector2(30f, 20f),
                                              new Vector2(-60f, 44f));
        }

        void BuildStatusPill()
        {
            _statusPill = UiKit.Panel("StatusPill", _canvas.transform,
                                      new Color(0f, 0f, 0f, 0.75f), 26);
            _statusPill.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                              new Vector2(0.5f, 0f), new Vector2(0f, 430f),
                              new Vector2(940f, 68f));

            _statusText = UiKit.Label("Status", _statusPill, "", 28,
                                      UiKit.TextSecondary, TextAnchor.MiddleCenter);
            _statusText.rectTransform.Stretch(18f);
        }

        void BuildControls()
        {
            // Listening pulse sits behind the button.
            var ringGo = new GameObject("MicRing", typeof(RectTransform), typeof(Image));
            ringGo.transform.SetParent(_canvas.transform, false);
            _micRingRect = ringGo.GetComponent<RectTransform>();
            _micRingRect.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                               new Vector2(0.5f, 0.5f), new Vector2(0f, 210f),
                               new Vector2(260f, 260f));
            _micRing = ringGo.GetComponent<Image>();
            _micRing.sprite = UiKit.Ring();
            _micRing.color = new Color(UiKit.Danger.r, UiKit.Danger.g, UiKit.Danger.b, 0f);
            _micRing.raycastTarget = false;

            _micButton = UiKit.CircleButton("MicButton", _canvas.transform,
                                            UiKit.Danger, 200f, UiKit.MicIcon());
            _micButton.GetComponent<RectTransform>()
                      .Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                             new Vector2(0.5f, 0.5f), new Vector2(0f, 210f),
                             new Vector2(200f, 200f));
            _micButton.onClick.AddListener(OnMicPressed);

            var hint = UiKit.Label("MicHint", _canvas.transform, "Tap to speak", 26,
                                   UiKit.TextSecondary, TextAnchor.MiddleCenter);
            hint.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                                     new Vector2(0.5f, 0.5f), new Vector2(0f, 84f),
                                     new Vector2(420f, 36f));

            MakePillButton("SearchButton", "Search", new Vector2(-300f, 210f),
                           () => ToggleSearchPanel(true));
            MakePillButton("StopButton", "Stop", new Vector2(300f, 210f),
                           () => nav?.Stop()).gameObject.name = "StopButton";
        }

        Button MakePillButton(string name, string label, Vector2 position,
                              UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image),
                                    typeof(Button));
            go.transform.SetParent(_canvas.transform, false);

            go.GetComponent<RectTransform>()
              .Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                     new Vector2(0.5f, 0.5f), position, new Vector2(210f, 96f));

            var image = go.GetComponent<Image>();
            image.sprite = UiKit.RoundedRect(24);
            image.type = Image.Type.Sliced;
            image.color = UiKit.SurfaceLight;

            var text = UiKit.Label("Label", go.transform, label, 30,
                                   UiKit.TextPrimary, TextAnchor.MiddleCenter);
            text.rectTransform.Stretch();

            var button = go.GetComponent<Button>();
            button.onClick.AddListener(onClick);
            return button;
        }

        void BuildSearchPanel()
        {
            var panel = UiKit.Panel("SearchPanel", _canvas.transform,
                                    new Color(0.04f, 0.05f, 0.07f, 0.98f), 36);
            panel.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                        new Vector2(0.5f, 0.5f), Vector2.zero,
                        new Vector2(1000f, 1500f));
            _searchPanel = panel.gameObject;

            var title = UiKit.Label("Title", panel, "Where do you want to go?", 40,
                                    UiKit.TextPrimary, TextAnchor.MiddleCenter,
                                    FontStyle.Bold);
            title.rectTransform.Place(new Vector2(0f, 1f), new Vector2(1f, 1f),
                                      new Vector2(0.5f, 1f), new Vector2(0f, -40f),
                                      new Vector2(-80f, 60f));

            // Text entry — the escape hatch when speech recognition fails.
            var fieldRect = UiKit.Panel("Field", panel, UiKit.SurfaceLight, 20);
            fieldRect.Place(new Vector2(0f, 1f), new Vector2(1f, 1f),
                            new Vector2(0.5f, 1f), new Vector2(0f, -122f),
                            new Vector2(-80f, 100f));

            var placeholder = UiKit.Label("Placeholder", fieldRect,
                                          "Type a place name…", 32,
                                          new Color(1f, 1f, 1f, 0.35f));
            placeholder.rectTransform.Stretch(24f);

            var fieldText = UiKit.Label("Text", fieldRect, "", 32, UiKit.TextPrimary);
            fieldText.rectTransform.Stretch(24f);

            _searchField = fieldRect.gameObject.AddComponent<InputField>();
            _searchField.textComponent = fieldText;
            _searchField.placeholder = placeholder;
            _searchField.targetGraphic = fieldRect.GetComponent<Image>();
            _searchField.onValueChanged.AddListener(OnSearchChanged);
            _searchField.onSubmit.AddListener(OnSearchSubmit);

            MakePanelButton(panel, "Go", new Vector2(0f, -238f), UiKit.Accent,
                            () => OnSearchSubmit(_searchField.text));

            // Close is anchored to the panel's BOTTOM. Anchoring it to the top
            // like the other buttons and then patching anchorMin afterwards
            // left anchorMin/anchorMax mismatched, which stretches the rect
            // across the whole panel — that is why it appeared floating in the
            // middle of the results list.
            var close = MakePanelButton(panel, "Close", Vector2.zero,
                                        UiKit.SurfaceLight,
                                        () => ToggleSearchPanel(false));
            close.GetComponent<RectTransform>()
                 .Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                        new Vector2(0.5f, 0f), new Vector2(0f, 36f),
                        new Vector2(300f, 88f));

            // Scrollable results. The viewport is inset from the panel so it
            // cannot overlap the Go button above or Close below.
            var viewportGo = new GameObject("Viewport", typeof(RectTransform),
                                            typeof(Image), typeof(RectMask2D));
            viewportGo.transform.SetParent(panel, false);
            var viewport = viewportGo.GetComponent<RectTransform>();
            viewport.anchorMin = new Vector2(0f, 0f);
            viewport.anchorMax = new Vector2(1f, 1f);
            viewport.offsetMin = new Vector2(40f, 140f);   // clear of Close
            viewport.offsetMax = new Vector2(-40f, -300f); // clear of Go
            viewportGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.03f);

            var contentGo = new GameObject("Content", typeof(RectTransform),
                                           typeof(VerticalLayoutGroup),
                                           typeof(ContentSizeFitter));
            contentGo.transform.SetParent(viewportGo.transform, false);
            _searchContent = contentGo.GetComponent<RectTransform>();

            // Stretch horizontally, pinned to the top: the layout group grows
            // it downward. Leaving the default centre pivot is what made the
            // rows drift sideways while scrolling.
            _searchContent.anchorMin = new Vector2(0f, 1f);
            _searchContent.anchorMax = new Vector2(1f, 1f);
            _searchContent.pivot = new Vector2(0.5f, 1f);
            _searchContent.offsetMin = new Vector2(0f, 0f);
            _searchContent.offsetMax = new Vector2(0f, 0f);
            _searchContent.anchoredPosition = Vector2.zero;

            var layout = contentGo.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.padding = new RectOffset(12, 12, 12, 12);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandHeight = false;
            layout.childControlHeight = true;    // honour each row's LayoutElement
            layout.childForceExpandWidth = true;
            layout.childControlWidth = true;     // rows fill the viewport width

            contentGo.GetComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;

            var scroll = viewportGo.AddComponent<ScrollRect>();
            scroll.content = _searchContent;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Elastic;
            scroll.scrollSensitivity = 40f;

            _searchPanel.SetActive(false);
        }

        Button MakePanelButton(Transform parent, string label, Vector2 position,
                               Color color, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(label + "Button", typeof(RectTransform),
                                    typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);

            go.GetComponent<RectTransform>()
              .Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                     new Vector2(0.5f, 1f), position, new Vector2(300f, 88f));

            var image = go.GetComponent<Image>();
            image.sprite = UiKit.RoundedRect(24);
            image.type = Image.Type.Sliced;
            image.color = color;

            var text = UiKit.Label("Label", go.transform, label, 32,
                                   UiKit.TextPrimary, TextAnchor.MiddleCenter,
                                   FontStyle.Bold);
            text.rectTransform.Stretch();

            go.GetComponent<Button>().onClick.AddListener(onClick);
            return go.GetComponent<Button>();
        }

        void BuildDiagnostics()
        {
            var panel = UiKit.Panel("Diagnostics", _canvas.transform,
                                    new Color(0f, 0f, 0f, 0.6f), 16);
            panel.Place(new Vector2(0f, 0f), new Vector2(0f, 0f),
                        new Vector2(0f, 0f), new Vector2(20f, 520f),
                        new Vector2(700f, 180f));

            _diagnosticsText = UiKit.Label("Text", panel, "", 22,
                                           new Color(0.55f, 1f, 0.65f, 0.9f),
                                           TextAnchor.UpperLeft);
            _diagnosticsText.rectTransform.Stretch(14f);
        }

        // ── Events ───────────────────────────────────────────────────────────

        void HookEvents()
        {
            if (nav != null)
            {
                nav.OnStateChanged += HandleStateChanged;
                nav.OnRouteReceived += HandleRoute;
                nav.OnProgress += HandleProgress;
                nav.OnStepChanged += HandleStepChanged;
                nav.OnError += HandleError;
                nav.OnArrived += HandleArrived;
            }

            if (speech != null)
            {
                speech.OnResult += HandleTranscript;
                speech.OnPartialResult += p => ShowStatus($"“{p}”", UiKit.TextPrimary);
                speech.OnFailed += HandleError;
                speech.OnListeningStarted += () =>
                    ShowStatus("Listening… tap again to stop", UiKit.Accent);
                speech.OnListeningStopped += () => { };
            }

            if (gps != null)
                gps.OnStatusChanged += s => ShowStatus(DescribeGps(s), UiKit.TextSecondary);
        }

        void OnDestroy()
        {
            if (nav == null) return;
            nav.OnStateChanged -= HandleStateChanged;
            nav.OnRouteReceived -= HandleRoute;
            nav.OnProgress -= HandleProgress;
            nav.OnStepChanged -= HandleStepChanged;
            nav.OnError -= HandleError;
            nav.OnArrived -= HandleArrived;
        }

        void OnMicPressed()
        {
            if (speech == null) return;
            speech.Toggle();
        }

        void HandleTranscript(string transcript)
        {
            ShowStatus($"Finding “{transcript}”…", UiKit.TextPrimary);
            nav?.Navigate(transcript);
        }

        void HandleStateChanged(NavState state)
        {
            switch (state)
            {
                case NavState.Routing:
                    ShowStatus("Planning your route…", UiKit.Accent);
                    break;
                case NavState.Guiding:
                    SetRouteVisible(true);
                    ShowStatus("", UiKit.TextSecondary);
                    break;
                case NavState.Idle:
                    SetRouteVisible(false);
                    _instructionText.text =
                        "Tap the microphone and say where you want to go.";
                    _landmarkText.text = "";
                    break;
            }
        }

        void HandleRoute(NavResponse route)
        {
            SetRouteVisible(true);

            _destinationName.text = route.destination ?? "";
            _stopBadge.text = route.multiTarget && route.stops != null
                ? $"1/{route.stops.Count}" : "#1";
            _destinationDistance.text = GeoUtils.FormatDistance(route.totalDistanceM);
            _stepCounter.text = route.steps != null
                ? $"{route.steps.Count} steps · ~{route.estimatedWalkMinutes:0} min"
                : "";

            if (route.needsConfirmation)
                ShowStatus($"Did you mean {route.destination}? Tap Search to change.",
                           UiKit.Warning);
            else if (route.safetyWarnings is { Count: > 0 })
                ShowStatus(route.safetyWarnings[0], UiKit.Warning);
            else
                ShowStatus("", UiKit.TextSecondary);

            if (speakInstructions && tts != null)
            {
                tts.Speak(!string.IsNullOrEmpty(route.memoryNote)
                    ? route.memoryNote
                    : $"Navigating to {route.destination}. " +
                      $"{GeoUtils.FormatDistance(route.totalDistanceM)}.");
            }
        }

        void HandleStepChanged(int index, NavStep step)
        {
            if (step == null) return;

            _instructionText.text = step.text;
            _landmarkText.text = step.HasLandmark
                ? $"◆  {step.landmark}" +
                  (step.landmarkDistM.HasValue ? $"   {step.landmarkDistM.Value:0} m" : "")
                : "";

            var route = nav?.CurrentRoute;
            if (route?.steps != null)
                _stepCounter.text = $"Step {index + 1} of {route.steps.Count}";

            if (speakInstructions && tts != null) tts.Speak(step.text);
        }

        void HandleProgress(ProgressResponse p)
        {
            _destinationDistance.text = p.distanceToNextStepM.HasValue
                ? GeoUtils.FormatDistance(p.distanceToNextStepM.Value)
                : GeoUtils.FormatDistance(p.distanceToDestinationM);

            if (p.offRoute)
                ShowStatus($"Off the path ({p.crossTrackErrorM:0} m) — re-planning…",
                           UiKit.Warning);
        }

        void HandleArrived()
        {
            _instructionText.text = "You have arrived.";
            _landmarkText.text = "";
            _destinationDistance.text = "0 m";
            ShowStatus("Journey complete", UiKit.Success);
            if (speakInstructions && tts != null) tts.Speak("You have arrived.");
        }

        void HandleError(string message)
        {
            ShowStatus(message, UiKit.Danger);
            if (speakInstructions && tts != null) tts.Speak(message);
        }

        // ── Per-frame ────────────────────────────────────────────────────────

        void Update()
        {
            UpdateMicPulse();
            UpdateFps();

            if (_diagnosticsText != null)
                _diagnosticsText.text = DiagnosticsText();
        }

        void UpdateFps()
        {
            _fpsAccumulator += Time.timeScale / Time.deltaTime;
            _fpsFrames++;
            _fpsTimeLeft -= Time.deltaTime;

            if (_fpsTimeLeft <= 0.0f)
            {
                _fps = _fpsAccumulator / _fpsFrames;
                _fpsTimeLeft = 0.5f;
                _fpsAccumulator = 0.0f;
                _fpsFrames = 0;
            }
        }

        void UpdateMicPulse()
        {
            if (_micRing == null) return;

            bool listening = speech != null && speech.IsListening;
            if (listening)
            {
                _pulse = Mathf.Repeat(_pulse + Time.deltaTime * 1.4f, 1f);
                float scale = Mathf.Lerp(1.0f, 1.45f, _pulse);
                _micRingRect.sizeDelta = new Vector2(260f * scale, 260f * scale);
                _micRing.color = new Color(UiKit.Danger.r, UiKit.Danger.g,
                                           UiKit.Danger.b, 1f - _pulse);
            }
            else if (_micRing.color.a > 0f)
            {
                _pulse = 0f;
                _micRingRect.sizeDelta = new Vector2(260f, 260f);
                _micRing.color = new Color(UiKit.Danger.r, UiKit.Danger.g,
                                           UiKit.Danger.b, 0f);
            }
        }

        /// <summary>
        /// On-device status readout.
        ///
        /// There is no Console on a phone, and the three things that actually
        /// break — AR session state, backend reachability and GPS lock — all
        /// present as the same symptom: nothing happens. This makes each of
        /// them individually visible.
        /// </summary>
        string DiagnosticsText()
        {
            var sb = new System.Text.StringBuilder();

            // FPS & AR session
            var session = FindFirstObjectByType<UnityEngine.XR.ARFoundation.ARSession>();
            string arState = session == null
                ? "no ARSession"
                : UnityEngine.XR.ARFoundation.ARSession.state.ToString();
            sb.AppendLine($"FPS  {_fps:0.0} | AR {arState}");

            if (gps != null)
                sb.AppendLine($"GPS  {gps.Status} acc={gps.AccuracyM:0.0}m fix={gps.HasFix} " +
                              $"lat={gps.Latitude:0.00000} lng={gps.Longitude:0.00000}");
            if (heading != null)
                sb.AppendLine($"HDG  {heading.Heading:0}° ok={heading.IsReliable} " +
                              $"off={heading.ArToNorthOffset:0}°");
            if (geoAnchors != null)
                sb.AppendLine($"PLN  count={geoAnchors.ActivePlaneCount} ground={geoAnchors.GroundEstablished} y={geoAnchors.GroundY:0.00}");
            if (nav != null)
            {
                sb.AppendLine($"NAV  {nav.State} step={nav.CurrentStepIndex} " +
                              $"brng={nav.BearingToNextWaypoint():0}°");
            }
            if (arrowController != null)
            {
                int poolTotal = arrowController.arrowPool != null ? arrowController.arrowPool.TotalCount : 0;
                sb.AppendLine($"ARW  active={arrowController.ActiveArrowCount} pool={poolTotal} nodes={arrowController.TotalSampledNodes}");
            }
            else if (content != null)
            {
                sb.AppendLine($"ARR  markers={content.MarkerCount} visible={content.VisibleMarkerCount}");
            }

            if (speech != null)
                sb.AppendLine($"MIC  avail={speech.IsAvailable} listening={speech.IsListening}");

            return sb.ToString();
        }

        // ── Search panel ─────────────────────────────────────────────────────

        void ToggleSearchPanel(bool show)
        {
            _searchPanel.SetActive(show);
            if (show)
            {
                _searchField.text = "";
                StartCoroutine(LoadDestinations(null));
            }
        }

        void OnSearchChanged(string query)
        {
            StopCoroutine(nameof(DebouncedSearch));
            StartCoroutine(nameof(DebouncedSearch), query);
        }

        IEnumerator DebouncedSearch(string query)
        {
            // Wait out the typing burst rather than firing a request per keystroke.
            yield return new WaitForSeconds(0.35f);
            yield return LoadDestinations(query);
        }

        void OnSearchSubmit(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            ToggleSearchPanel(false);
            nav?.Navigate(query);
        }

        IEnumerator LoadDestinations(string search)
        {
            if (api == null) yield break;

            foreach (var row in _resultRows) Destroy(row);
            _resultRows.Clear();

            yield return api.GetDestinations(
                list =>
                {
                    if (list?.destinations == null) return;
                    int shown = 0;
                    foreach (var d in list.destinations)
                    {
                        if (shown++ >= 50) break;
                        AddResultRow(d);
                    }
                },
                err => ShowStatus(err, UiKit.Danger),
                search);
        }

        void AddResultRow(Core.Destination d)
        {
            var row = UiKit.Panel($"Row_{d.name}", _searchContent, UiKit.SurfaceLight, 18);

            // The VerticalLayoutGroup owns this rect's position and width, so
            // the row must declare its height through LayoutElement and leave
            // anchoring alone. Setting sizeDelta directly, as before, fought
            // the layout group and produced rows sliding out of the viewport.
            var layoutElement = row.gameObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 104f;
            layoutElement.minHeight = 104f;
            layoutElement.flexibleWidth = 1f;

            // Children stretch to the row, so they follow it wherever the
            // layout group puts it.
            var name = UiKit.Label("Name", row, d.name, 32, UiKit.TextPrimary);
            name.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            name.rectTransform.anchorMax = new Vector2(1f, 1f);
            name.rectTransform.offsetMin = new Vector2(26f, 0f);
            name.rectTransform.offsetMax = new Vector2(-26f, -12f);

            string subtitle = string.IsNullOrEmpty(d.type) || d.type == "unknown"
                ? "Point of interest" : d.type.Replace('_', ' ');
            var type = UiKit.Label("Type", row, subtitle, 24, UiKit.TextSecondary);
            type.rectTransform.anchorMin = new Vector2(0f, 0f);
            type.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            type.rectTransform.offsetMin = new Vector2(26f, 12f);
            type.rectTransform.offsetMax = new Vector2(-26f, 0f);

            var button = row.gameObject.AddComponent<Button>();
            button.targetGraphic = row.GetComponent<Image>();
            string destination = d.name;
            button.onClick.AddListener(() =>
            {
                ToggleSearchPanel(false);
                nav?.Navigate(destination);
            });

            _resultRows.Add(row.gameObject);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        void SetRouteVisible(bool visible)
        {
            _destinationCard.gameObject.SetActive(visible);
        }

        void ShowStatus(string message, Color color)
        {
            bool has = !string.IsNullOrEmpty(message);
            _statusPill.gameObject.SetActive(has);
            if (!has) return;
            _statusText.text = message;
            _statusText.color = color;
        }

        static string DescribeGps(GpsStatus s) => s switch
        {
            GpsStatus.Initializing => "Locating you…",
            GpsStatus.Running => "",
            GpsStatus.PermissionDenied => "Location permission is required to navigate.",
            GpsStatus.Disabled => "Please turn on location services.",
            GpsStatus.Failed => "Could not determine your location.",
            _ => "",
        };

    }
}
