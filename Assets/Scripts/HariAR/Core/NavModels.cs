// NavModels.cs
// ---------------------------------------------------------------------------
// Data contract with the HARI-AR backend.
//
// Field names mirror the JSON produced by app/agents/response.py exactly.
// Newtonsoft is used rather than JsonUtility because the payload contains
// nullable numbers (landmark_dist_m is null when no landmark was found) and
// JsonUtility silently turns those into 0 — which would place an AR label at
// a landmark that does not exist.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace HariAR.Core
{
    // ── Requests ─────────────────────────────────────────────────────────────

    [Serializable]
    public class NavRequest
    {
        [JsonProperty("user_query")] public string userQuery;
        [JsonProperty("source_lat")] public double sourceLat;
        [JsonProperty("source_lng")] public double sourceLng;
        [JsonProperty("session_id", NullValueHandling = NullValueHandling.Ignore)]
        public string sessionId;
        [JsonProperty("user_id", NullValueHandling = NullValueHandling.Ignore)]
        public string userId;
        [JsonProperty("lang")] public string lang = "en";
    }

    [Serializable]
    public class ProgressRequest
    {
        [JsonProperty("session_id")] public string sessionId;
        [JsonProperty("lat")] public double lat;
        [JsonProperty("lng")] public double lng;
        [JsonProperty("heading", NullValueHandling = NullValueHandling.Ignore)]
        public float? heading;
        [JsonProperty("accuracy_m", NullValueHandling = NullValueHandling.Ignore)]
        public float? accuracyM;
    }

    // ── Route payload ────────────────────────────────────────────────────────

    /// <summary>One point on the dense AR pathway ribbon (~3 m spacing).</summary>
    [Serializable]
    public class PathPoint
    {
        [JsonProperty("lat")] public double lat;
        [JsonProperty("lng")] public double lng;
        [JsonProperty("heading")] public double? heading;
    }

    /// <summary>
    /// A sparse anchor (~15 m spacing, capped at 60 by the backend).
    /// These become real AR anchors; ribbon points do not.
    /// </summary>
    [Serializable]
    public class RouteAnchor
    {
        [JsonProperty("index")] public int index;
        [JsonProperty("lat")] public double lat;
        [JsonProperty("lng")] public double lng;
        [JsonProperty("heading")] public double? heading;
        [JsonProperty("is_destination")] public bool isDestination;
    }

    /// <summary>
    /// One turn-by-turn instruction with its landmark annotation.
    /// <see cref="landmark"/> is the RQ2 payload: the label the client anchors
    /// at the junction so the pilgrim sees "Dhwaja Sthambam" in the world.
    /// </summary>
    [Serializable]
    public class NavStep
    {
        [JsonProperty("index")] public int index;
        [JsonProperty("text")] public string text;
        [JsonProperty("type")] public string type;          // right | left | arrive | …
        [JsonProperty("distance_m")] public double distanceM;
        [JsonProperty("bearing")] public double bearing;
        [JsonProperty("turn_angle")] public double turnAngle;
        [JsonProperty("lat")] public double lat;
        [JsonProperty("lng")] public double lng;
        [JsonProperty("landmark")] public string landmark;
        [JsonProperty("landmark_dist_m")] public double? landmarkDistM;
        [JsonProperty("landmark_type")] public string landmarkType;

        public bool HasLandmark => !string.IsNullOrEmpty(landmark);
        public bool IsArrival => type == "arrive";

        /// <summary>True for turns the pilgrim must physically make.</summary>
        public bool IsTurn => !IsArrival && type != "straight";
    }

    [Serializable]
    public class Stop
    {
        [JsonProperty("name")] public string name;
        [JsonProperty("lat")] public double lat;
        [JsonProperty("lng")] public double lng;
        [JsonProperty("type")] public string type;
    }

    [Serializable]
    public class Alternative
    {
        [JsonProperty("name")] public string name;
        [JsonProperty("score")] public double score;
        [JsonProperty("type")] public string type;
        [JsonProperty("distance_m")] public double? distanceM;
    }

    /// <summary>Where the route was planned from, and how far that was from a walkway.</summary>
    [Serializable]
    public class RouteOrigin
    {
        [JsonProperty("lat")] public double lat;
        [JsonProperty("lng")] public double lng;
        [JsonProperty("snap_distance_m")] public double? snapDistanceM;
    }

    [Serializable]
    public class RouteLeg
    {
        [JsonProperty("from")] public Stop from;
        [JsonProperty("to")] public Stop to;
        [JsonProperty("distance_m")] public double distanceM;
        [JsonProperty("algorithm")] public string algorithm;
        [JsonProperty("dest_snap_offset_m")] public double destSnapOffsetM;
    }

    /// <summary>The full /navigate response.</summary>
    [Serializable]
    public class NavResponse
    {
        [JsonProperty("status")] public string status;
        [JsonProperty("api_version")] public string apiVersion;
        [JsonProperty("session_id")] public string sessionId;

        [JsonProperty("query")] public string query;
        [JsonProperty("understood_as")] public List<string> understoodAs;
        [JsonProperty("query_source")] public string querySource;
        [JsonProperty("referential")] public bool referential;
        [JsonProperty("memory_note")] public string memoryNote;

        [JsonProperty("destination")] public string destination;
        [JsonProperty("destination_lat")] public double? destinationLat;
        [JsonProperty("destination_lng")] public double? destinationLng;
        [JsonProperty("destination_type")] public string destinationType;
        [JsonProperty("semantic_score")] public double? semanticScore;
        [JsonProperty("match_source")] public string matchSource;
        [JsonProperty("needs_confirmation")] public bool needsConfirmation;
        [JsonProperty("alternatives")] public List<Alternative> alternatives;
        [JsonProperty("stops")] public List<Stop> stops;
        [JsonProperty("multi_target")] public bool multiTarget;
        /// <summary>True when the pilgrim imposed a visiting order ("A then B").</summary>
        [JsonProperty("ordered")] public bool ordered;

        [JsonProperty("origin")] public RouteOrigin origin;
        [JsonProperty("total_distance_m")] public double totalDistanceM;
        [JsonProperty("estimated_walk_minutes")] public double estimatedWalkMinutes;
        [JsonProperty("routing_algorithm")] public string routingAlgorithm;
        [JsonProperty("legs")] public List<RouteLeg> legs;

        [JsonProperty("path")] public List<PathPoint> path;
        [JsonProperty("anchors")] public List<RouteAnchor> anchors;
        [JsonProperty("path_point_count")] public int pathPointCount;
        [JsonProperty("anchor_count")] public int anchorCount;

        [JsonProperty("instructions")] public List<string> instructions;
        [JsonProperty("steps")] public List<NavStep> steps;
        [JsonProperty("landmark_coverage")] public double? landmarkCoverage;
        [JsonProperty("llm_enhanced")] public bool llmEnhanced;
        [JsonProperty("safety_warnings")] public List<string> safetyWarnings;

        /// <summary>Per-agent server latency. Logged by the study harness.</summary>
        [JsonProperty("timings_ms")] public Dictionary<string, double> timingsMs;

        [JsonProperty("arrival_radius_m")] public float arrivalRadiusM = 12f;
        [JsonProperty("waypoint_advance_m")] public float waypointAdvanceM = 10f;
        [JsonProperty("audio_url")] public string audioUrl;

        // Error shape
        [JsonProperty("message")] public string message;
        [JsonProperty("failed_node")] public string failedNode;

        public bool IsSuccess => status == "success";
        public bool HasPath => path != null && path.Count >= 2;
    }

    /// <summary>The /navigate/update response — pure geometry, no LLM.</summary>
    [Serializable]
    public class ProgressResponse
    {
        [JsonProperty("status")] public string status;
        [JsonProperty("session_id")] public string sessionId;
        [JsonProperty("arrived")] public bool arrived;
        [JsonProperty("off_route")] public bool offRoute;
        [JsonProperty("off_route_streak")] public int offRouteStreak;
        [JsonProperty("cross_track_error_m")] public double crossTrackErrorM;
        [JsonProperty("current_step")] public int currentStep;
        [JsonProperty("current_instruction")] public string currentInstruction;
        [JsonProperty("current_landmark")] public string currentLandmark;
        [JsonProperty("next_instruction")] public string nextInstruction;
        [JsonProperty("distance_to_next_step_m")] public double? distanceToNextStepM;
        [JsonProperty("distance_to_destination_m")] public double distanceToDestinationM;
        [JsonProperty("remaining_route_m")] public double remainingRouteM;
        [JsonProperty("path_index")] public int pathIndex;
        [JsonProperty("destination")] public string destination;
    }

    // ── Catalogue ────────────────────────────────────────────────────────────

    [Serializable]
    public class Destination
    {
        [JsonProperty("name")] public string name;
        [JsonProperty("type")] public string type;
        [JsonProperty("lat")] public double lat;
        [JsonProperty("lng")] public double lng;
        [JsonProperty("score")] public double? score;
    }

    [Serializable]
    public class DestinationList
    {
        [JsonProperty("count")] public int count;
        [JsonProperty("destinations")] public List<Destination> destinations;
    }

    [Serializable]
    public class HealthResponse
    {
        [JsonProperty("status")] public string status;
        [JsonProperty("version")] public string version;
        [JsonProperty("graph_loaded")] public bool graphLoaded;
        [JsonProperty("poi_index_loaded")] public bool poiIndexLoaded;

        public bool IsReady => status == "ok" && graphLoaded && poiIndexLoaded;
    }
}
