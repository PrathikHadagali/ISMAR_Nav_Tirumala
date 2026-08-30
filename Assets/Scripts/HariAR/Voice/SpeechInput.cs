// SpeechInput.cs
// ---------------------------------------------------------------------------
// Android speech recognition via RecognizerIntent (paper §3.7 puts STT on the
// client). Falls back to the backend's Whisper endpoint conceptually, and to
// typed text in the editor.
//
// Interaction design note, straight from the paper's own findings: §4.4 names
// "press-and-hold voice input was uncomfortable for older participants during
// outdoor walking" as the primary usability issue. This implementation is
// therefore **tap-to-toggle** — tap to start, tap again or stay silent to
// stop. No sustained press is ever required.
//
// The Java plumbing runs through a small companion activity-free bridge built
// on AndroidJavaProxy, so no custom Java or AAR is needed in the project.
// ---------------------------------------------------------------------------

using System;
using System.Collections;
using UnityEngine;

namespace HariAR.Voice
{
    public class SpeechInput : MonoBehaviour
    {
        [Header("Language")]
        [Tooltip("BCP-47 tag handed to Android. te-IN and hi-IN cover the " +
                 "vernacular cases the paper targets.")]
        public string localeTag = "en-IN";

        [Header("Editor fallback")]
        [Tooltip("Text used when Run in the editor, where no recogniser exists.")]
        public string editorTestQuery = "Take me to Ladoo Counter and then Anna Prasadam";

        public bool IsListening { get; private set; }
        public bool IsAvailable { get; private set; }
        public string LastTranscript { get; private set; }

        public event Action OnListeningStarted;
        public event Action OnListeningStopped;
        public event Action<string> OnResult;
        public event Action<string> OnFailed;
        /// <summary>Live partial transcript, for on-screen feedback while speaking.</summary>
        public event Action<string> OnPartialResult;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject _recognizer;
        AndroidJavaObject _activity;
        RecognitionBridge _bridge;
#endif

        void Start()
        {
            StartCoroutine(Initialise());
        }

        IEnumerator Initialise()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(
                    UnityEngine.Android.Permission.Microphone);

                float waited = 0f;
                while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                           UnityEngine.Android.Permission.Microphone) && waited < 30f)
                {
                    waited += Time.deltaTime;
                    yield return null;
                }
            }

            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.Microphone))
            {
                IsAvailable = false;
                OnFailed?.Invoke("Microphone permission denied.");
                yield break;
            }

            // C# forbids `yield` inside a try block that has a catch, so the
            // outcome is captured here and acted on after the block.
            string initError = null;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                _activity = player.GetStatic<AndroidJavaObject>("currentActivity");

                using var recognizerClass =
                    new AndroidJavaClass("android.speech.SpeechRecognizer");
                IsAvailable = recognizerClass.CallStatic<bool>(
                    "isRecognitionAvailable", _activity);

                if (!IsAvailable)
                    initError = "Speech recognition is not available on this device.";
            }
            catch (Exception e)
            {
                IsAvailable = false;
                initError = $"Speech init failed: {e.Message}";
            }

            if (initError != null)
            {
                OnFailed?.Invoke(initError);
                yield break;
            }
#else
            IsAvailable = true;   // editor: simulated
            yield return null;
#endif
        }

        /// <summary>Tap-to-toggle entry point. Never requires a sustained press.</summary>
        public void Toggle()
        {
            if (IsListening) StopListening();
            else StartListening();
        }

        public void StartListening()
        {
            if (IsListening) return;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!IsAvailable)
            {
                OnFailed?.Invoke("Speech recognition unavailable.");
                return;
            }

            IsListening = true;
            OnListeningStarted?.Invoke();

            // The recogniser is main-thread affine on Android.
            _activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                try
                {
                    using var recognizerClass =
                        new AndroidJavaClass("android.speech.SpeechRecognizer");
                    _recognizer = recognizerClass.CallStatic<AndroidJavaObject>(
                        "createSpeechRecognizer", _activity);

                    _bridge = new RecognitionBridge(this);
                    _recognizer.Call("setRecognitionListener", _bridge);

                    using var intent = new AndroidJavaObject(
                        "android.content.Intent",
                        "android.speech.action.RECOGNIZE_SPEECH");

                    intent.Call<AndroidJavaObject>("putExtra",
                        "android.speech.extra.LANGUAGE_MODEL", "free_form");
                    intent.Call<AndroidJavaObject>("putExtra",
                        "android.speech.extra.LANGUAGE", localeTag);
                    intent.Call<AndroidJavaObject>("putExtra",
                        "android.speech.extra.PARTIAL_RESULTS", true);
                    intent.Call<AndroidJavaObject>("putExtra",
                        "android.speech.extra.MAX_RESULTS", 3);
                    // Let Android end the utterance on silence, so the pilgrim
                    // never has to hold anything down.
                    intent.Call<AndroidJavaObject>("putExtra",
                        "android.speech.extras.SPEECH_INPUT_COMPLETE_SILENCE_LENGTH_MILLIS",
                        1500L);

                    _recognizer.Call("startListening", intent);
                }
                catch (Exception e)
                {
                    IsListening = false;
                    OnFailed?.Invoke($"Could not start listening: {e.Message}");
                }
            }));
#else
            IsListening = true;
            OnListeningStarted?.Invoke();
            StartCoroutine(SimulateRecognition());
#endif
        }

        public void StopListening()
        {
            if (!IsListening) return;
            IsListening = false;
            OnListeningStopped?.Invoke();

#if UNITY_ANDROID && !UNITY_EDITOR
            if (_recognizer == null) return;
            _activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                try { _recognizer.Call("stopListening"); }
                catch (Exception e) { Debug.LogWarning($"[HARI-AR] stopListening: {e.Message}"); }
            }));
#endif
        }

        IEnumerator SimulateRecognition()
        {
            yield return new WaitForSeconds(1.2f);
            if (!IsListening) yield break;
            IsListening = false;
            OnListeningStopped?.Invoke();
            HandleResult(editorTestQuery);
        }

        // ── Main-thread marshalling ──────────────────────────────────────────
        // RecognitionListener callbacks arrive on Android's main *Java* thread,
        // which is not Unity's scripting thread. Touching Unity objects from
        // there — as every one of these handlers ultimately does, by starting a
        // navigation coroutine — is undefined behaviour and crashes in release
        // builds. Callbacks are queued and drained in Update instead.
        readonly System.Collections.Generic.Queue<Action> _mainThreadQueue = new();

        void Enqueue(Action action)
        {
            lock (_mainThreadQueue) _mainThreadQueue.Enqueue(action);
        }

        void Update()
        {
            while (true)
            {
                Action action;
                lock (_mainThreadQueue)
                {
                    if (_mainThreadQueue.Count == 0) break;
                    action = _mainThreadQueue.Dequeue();
                }
                action?.Invoke();
            }
        }

        // Called from the Java listener thread via the bridge.
        internal void HandleResult(string transcript) => Enqueue(() =>
        {
            LastTranscript = transcript;
            IsListening = false;
            OnListeningStopped?.Invoke();
            OnResult?.Invoke(transcript);
        });

        internal void HandlePartial(string partial) =>
            Enqueue(() => OnPartialResult?.Invoke(partial));

        internal void HandleError(string message) => Enqueue(() =>
        {
            IsListening = false;
            OnListeningStopped?.Invoke();
            OnFailed?.Invoke(message);
        });

        void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { _recognizer?.Call("destroy"); } catch { /* activity may be gone */ }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>
        /// Implements android.speech.RecognitionListener in C#.
        /// Every abstract method must exist or Android throws on registration.
        /// </summary>
        class RecognitionBridge : AndroidJavaProxy
        {
            readonly SpeechInput _owner;

            public RecognitionBridge(SpeechInput owner)
                : base("android.speech.RecognitionListener")
            {
                _owner = owner;
            }

            void onResults(AndroidJavaObject results)
            {
                var list = results.Call<AndroidJavaObject>(
                    "getStringArrayList", "results_recognition");
                if (list == null || list.Call<int>("size") == 0)
                {
                    _owner.HandleError("I did not catch that. Please try again.");
                    return;
                }
                _owner.HandleResult(list.Call<string>("get", 0));
            }

            void onPartialResults(AndroidJavaObject partial)
            {
                var list = partial.Call<AndroidJavaObject>(
                    "getStringArrayList", "results_recognition");
                if (list != null && list.Call<int>("size") > 0)
                    _owner.HandlePartial(list.Call<string>("get", 0));
            }

            void onError(int error)
            {
                _owner.HandleError(DescribeError(error));
            }

            static string DescribeError(int code) => code switch
            {
                1 => "Network timed out.",
                2 => "No network connection.",
                3 => "Microphone error.",
                4 => "Speech service error.",
                5 => "Recogniser error.",
                6 => "I did not hear anything. Please try again.",
                7 => "I did not catch that. Please try again.",
                8 => "Recogniser busy — please wait a moment.",
                9 => "Microphone permission denied.",
                _ => "Speech recognition failed.",
            };

            // Required no-ops.
            void onReadyForSpeech(AndroidJavaObject p) { }
            void onBeginningOfSpeech() { }
            void onRmsChanged(float rms) { }
            void onBufferReceived(AndroidJavaObject buffer) { }
            void onEndOfSpeech() { }
            void onEvent(int type, AndroidJavaObject p) { }
        }
#endif
    }
}
