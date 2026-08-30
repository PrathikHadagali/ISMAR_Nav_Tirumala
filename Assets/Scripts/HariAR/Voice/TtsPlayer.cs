// TtsPlayer.cs
// ---------------------------------------------------------------------------
// Spoken guidance.
//
// Two sources, preferred in order:
//   1. Android's on-device TextToSpeech — instant, works offline, and speaks
//      each instruction exactly when the pilgrim reaches it.
//   2. The backend's /voice/audio/{session} MP3 — used when on-device TTS is
//      missing a voice for the requested language.
//
// Instructions are spoken as they become active rather than all at once: a
// pilgrim at the first junction does not need to hear the last one.
// ---------------------------------------------------------------------------

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace HariAR.Voice
{
    [RequireComponent(typeof(AudioSource))]
    public class TtsPlayer : MonoBehaviour
    {
        [Header("Language")]
        public string localeTag = "en-IN";

        [Range(0.5f, 2f)] public float speechRate = 0.95f;
        [Range(0.5f, 2f)] public float pitch = 1.0f;

        [Header("Behaviour")]
        [Tooltip("Never repeat the same line within this many seconds.")]
        public float repeatSuppressionS = 8f;

        public bool IsAvailable { get; private set; }
        public bool IsSpeaking { get; private set; }

        AudioSource _audio;
        string _lastSpoken;
        float _lastSpokenTime = -999f;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject _tts;
#endif

        void Awake()
        {
            _audio = GetComponent<AudioSource>();
            _audio.playOnAwake = false;
            InitialiseNativeTts();
        }

        void InitialiseNativeTts()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = player.GetStatic<AndroidJavaObject>("currentActivity");

                // The two-arg constructor needs an OnInitListener; passing null
                // is legal and the engine still initialises asynchronously.
                _tts = new AndroidJavaObject("android.speech.tts.TextToSpeech",
                                             activity, null);

                using var locale = new AndroidJavaObject("java.util.Locale", "en", "IN");
                _tts.Call<int>("setLanguage", locale);
                _tts.Call<int>("setSpeechRate", speechRate);
                _tts.Call<int>("setPitch", pitch);

                IsAvailable = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HARI-AR] Native TTS unavailable: {e.Message}");
                IsAvailable = false;
            }
#else
            IsAvailable = false;
#endif
        }

        /// <summary>Speak a line, interrupting whatever is currently playing.</summary>
        public void Speak(string text, bool interrupt = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // Suppress accidental repeats — the progress loop can report the
            // same active step many times in a row.
            if (text == _lastSpoken && Time.time - _lastSpokenTime < repeatSuppressionS)
                return;

            _lastSpoken = text;
            _lastSpokenTime = Time.time;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (IsAvailable && _tts != null)
            {
                try
                {
                    // QUEUE_FLUSH = 0, QUEUE_ADD = 1
                    _tts.Call<int>("speak", text, interrupt ? 0 : 1, null, "hariar");
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[HARI-AR] TTS speak failed: {e.Message}");
                }
            }
#endif
            Debug.Log($"[HARI-AR][TTS] {text}");
        }

        public void Stop()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { _tts?.Call<int>("stop"); } catch { /* engine may be shutting down */ }
#endif
            if (_audio.isPlaying) _audio.Stop();
            IsSpeaking = false;
        }

        /// <summary>
        /// Fallback path: stream the backend's rendered MP3.
        /// Used when the device has no voice for the requested language.
        /// </summary>
        public IEnumerator PlayFromUrl(string url, Action<string> onError = null)
        {
            if (string.IsNullOrEmpty(url)) yield break;

            using var req = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG);
            req.timeout = 20;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"Could not fetch audio: {req.error}");
                yield break;
            }

            var clip = DownloadHandlerAudioClip.GetContent(req);
            if (clip == null)
            {
                onError?.Invoke("Audio response was empty.");
                yield break;
            }

            _audio.clip = clip;
            _audio.Play();
            IsSpeaking = true;

            while (_audio.isPlaying) yield return null;
            IsSpeaking = false;
        }

        void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                _tts?.Call("stop");
                _tts?.Call("shutdown");
            }
            catch { /* activity already gone */ }
#endif
        }
    }
}
