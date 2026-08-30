// NavApiClient.cs
// ---------------------------------------------------------------------------
// HTTP transport to the HARI-AR backend.
//
// Every call is a coroutine with an explicit timeout and an error callback.
// A pilgrim standing in the sun must never see the app hang: if the server is
// unreachable the UI has to say so and offer the offline destination list,
// not spin forever.
// ---------------------------------------------------------------------------

using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace HariAR.Core
{
    public class NavApiClient : MonoBehaviour
    {
        [Header("Server")]
        [Tooltip("Backend base URL. Must be the LAN IP of the machine running " +
                 "uvicorn — 'localhost' resolves to the phone itself.")]
        public string baseUrl = "http://192.168.1.100:8000";

        [Tooltip("Seconds before a routing request is abandoned.")]
        public int requestTimeoutSeconds = 30;

        [Tooltip("Progress pings are tiny and frequent; they fail fast.")]
        public int progressTimeoutSeconds = 8;

        /// <summary>Stable per-install id enabling the backend's long-term memory.</summary>
        public string UserId { get; private set; }

        const string UserIdKey = "hariar.user_id";

        static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
        };

        void Awake()
        {
            UserId = PlayerPrefs.GetString(UserIdKey, null);
            if (string.IsNullOrEmpty(UserId))
            {
                // Random, not the device id: this keys navigation history, and
                // a hardware identifier would be personal data we do not need.
                UserId = Guid.NewGuid().ToString("N").Substring(0, 16);
                PlayerPrefs.SetString(UserIdKey, UserId);
                PlayerPrefs.Save();
            }
        }

        string Url(string path) => $"{baseUrl.TrimEnd('/')}{path}";

        // ── Health ───────────────────────────────────────────────────────────

        public IEnumerator CheckHealth(Action<HealthResponse> onSuccess,
                                       Action<string> onError)
        {
            yield return Get("/health", 8, onSuccess, onError);
        }

        // ── Routing ──────────────────────────────────────────────────────────

        /// <summary>Natural-language query + GPS → full AR route.</summary>
        public IEnumerator Navigate(string query, double lat, double lng,
                                    string sessionId,
                                    Action<NavResponse> onSuccess,
                                    Action<string> onError,
                                    string lang = "en")
        {
            var body = new NavRequest
            {
                userQuery = query,
                sourceLat = lat,
                sourceLng = lng,
                sessionId = sessionId,
                userId = UserId,
                lang = lang,
            };

            yield return Post("/navigate", body, requestTimeoutSeconds,
                (NavResponse r) =>
                {
                    if (r != null && r.IsSuccess) onSuccess?.Invoke(r);
                    else onError?.Invoke(r?.message ?? "Navigation failed.");
                },
                onError);
        }

        /// <summary>
        /// GPS heartbeat while walking. Pure geometry on the server, so this is
        /// safe to call at ~1 Hz for the whole journey.
        /// </summary>
        public IEnumerator UpdateProgress(string sessionId, double lat, double lng,
                                          float? heading, float? accuracy,
                                          Action<ProgressResponse> onSuccess,
                                          Action<string> onError)
        {
            var body = new ProgressRequest
            {
                sessionId = sessionId,
                lat = lat,
                lng = lng,
                heading = heading,
                accuracyM = accuracy,
            };

            yield return Post("/navigate/update", body, progressTimeoutSeconds,
                              onSuccess, onError);
        }

        // ── Catalogue (fallback when speech recognition fails) ───────────────

        public IEnumerator GetDestinations(Action<DestinationList> onSuccess,
                                           Action<string> onError,
                                           string search = null, int limit = 200)
        {
            var path = $"/destinations?limit={limit}";
            if (!string.IsNullOrEmpty(search))
                path += $"&q={UnityWebRequest.EscapeURL(search)}";
            yield return Get(path, requestTimeoutSeconds, onSuccess, onError);
        }

        public IEnumerator EndSession(string sessionId)
        {
            using var req = UnityWebRequest.Delete(Url($"/navigate/session/{sessionId}"));
            req.timeout = progressTimeoutSeconds;
            yield return req.SendWebRequest();
        }

        // ── Transport ────────────────────────────────────────────────────────

        IEnumerator Get<T>(string path, int timeout,
                           Action<T> onSuccess, Action<string> onError)
        {
            using var req = UnityWebRequest.Get(Url(path));
            req.timeout = timeout;
            yield return req.SendWebRequest();
            Handle(req, onSuccess, onError);
        }

        IEnumerator Post<T>(string path, object body, int timeout,
                            Action<T> onSuccess, Action<string> onError)
        {
            string json = JsonConvert.SerializeObject(body, JsonSettings);

            // UnityWebRequest.Post URL-encodes the body; the raw-upload form is
            // the only one that sends valid JSON.
            using var req = new UnityWebRequest(Url(path), UnityWebRequest.kHttpVerbPOST);
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = timeout;

            yield return req.SendWebRequest();
            Handle(req, onSuccess, onError);
        }

        void Handle<T>(UnityWebRequest req, Action<T> onSuccess, Action<string> onError)
        {
            if (req.result == UnityWebRequest.Result.ConnectionError ||
                req.result == UnityWebRequest.Result.DataProcessingError)
            {
                onError?.Invoke($"Cannot reach the navigation server ({req.error}). " +
                                $"Check that the backend is running and that {baseUrl} " +
                                $"is reachable from this device.");
                return;
            }

            var text = req.downloadHandler?.text;

            if (req.result == UnityWebRequest.Result.ProtocolError)
            {
                // The backend returns a structured payload even on failure, so
                // prefer its message over the bare HTTP status.
                string detail = ExtractError(text) ?? $"HTTP {req.responseCode}";
                onError?.Invoke(detail);
                return;
            }

            if (string.IsNullOrEmpty(text))
            {
                onError?.Invoke("Server returned an empty response.");
                return;
            }

            try
            {
                onSuccess?.Invoke(JsonConvert.DeserializeObject<T>(text, JsonSettings));
            }
            catch (Exception e)
            {
                Debug.LogError($"[HARI-AR] Failed to parse response: {e.Message}\n{text}");
                onError?.Invoke("Could not understand the server's reply.");
            }
        }

        /// <summary>Pull a human-readable message out of an error body.</summary>
        static string ExtractError(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                var obj = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(text);

                var message = obj["message"]?.ToString();
                if (!string.IsNullOrEmpty(message)) return message;

                // FastAPI validation errors arrive as {"detail": [{msg, loc}, …]}
                var detail = obj["detail"];
                if (detail == null) return null;
                if (detail.Type == Newtonsoft.Json.Linq.JTokenType.String)
                    return detail.ToString();

                var first = detail.First;
                return first?["msg"]?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
