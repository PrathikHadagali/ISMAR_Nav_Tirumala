// GeoUtils.cs
// ---------------------------------------------------------------------------
// Geodesy for the AR client — a function-for-function mirror of the backend's
// app/core/geo.py. Both ends must agree on distance, bearing and the local
// projection, or the AR pathway drifts away from the route the server planned.
//
// Convention (identical to the backend):
//   • distances in metres
//   • bearings in degrees clockwise from true north, in [0, 360)
//   • ENU east → Unity +x, ENU north → Unity +z
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace HariAR.Core
{
    public static class GeoUtils
    {
        public const double EarthRadiusM = 6371000.0;

        static readonly string[] Cardinals =
        {
            "north", "northeast", "east", "southeast",
            "south", "southwest", "west", "northwest"
        };

        /// <summary>Great-circle distance between two coordinates, in metres.</summary>
        public static double Haversine(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Mathf.Deg2Rad;
            double p2 = lat2 * Mathf.Deg2Rad;
            double dp = (lat2 - lat1) * Mathf.Deg2Rad;
            double dl = (lon2 - lon1) * Mathf.Deg2Rad;

            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) +
                       Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * EarthRadiusM * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        /// <summary>Initial compass bearing from point 1 to point 2, in [0, 360).</summary>
        public static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Mathf.Deg2Rad;
            double p2 = lat2 * Mathf.Deg2Rad;
            double dl = (lon2 - lon1) * Mathf.Deg2Rad;

            double x = Math.Sin(dl) * Math.Cos(p2);
            double y = Math.Cos(p1) * Math.Sin(p2) -
                       Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            return (Math.Atan2(x, y) * Mathf.Rad2Deg + 360.0) % 360.0;
        }

        public static string BearingToCardinal(double bearing)
        {
            int idx = (int)Math.Round(bearing / 45.0) % 8;
            if (idx < 0) idx += 8;
            return Cardinals[idx];
        }

        /// <summary>Signed smallest angle between bearings: + right, - left, in (-180, 180].</summary>
        public static double BearingDelta(double from, double to)
        {
            double d = (to - from + 360.0) % 360.0;
            return d > 180.0 ? d - 360.0 : d;
        }

        /// <summary>
        /// Local East-North-Up projection in metres relative to an origin.
        /// Equirectangular — sub-centimetre over the few-kilometre extent of the
        /// Tirumala complex, and directly usable as Unity world coordinates.
        /// </summary>
        public static Vector2d ToEnu(double lat, double lon,
                                     double originLat, double originLon)
        {
            double latRad = (lat + originLat) / 2.0 * Mathf.Deg2Rad;
            double east = (lon - originLon) * Mathf.Deg2Rad * EarthRadiusM * Math.Cos(latRad);
            double north = (lat - originLat) * Mathf.Deg2Rad * EarthRadiusM;
            return new Vector2d(east, north);
        }

        public static void FromEnu(double east, double north,
                                   double originLat, double originLon,
                                   out double lat, out double lon)
        {
            lat = originLat + north / EarthRadiusM * Mathf.Rad2Deg;
            double latRad = (lat + originLat) / 2.0 * Mathf.Deg2Rad;
            lon = originLon + east / (EarthRadiusM * Math.Cos(latRad)) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Geographic coordinate → Unity world position relative to a session
        /// origin. <paramref name="y"/> is supplied by the caller because ground
        /// height comes from AR plane detection, not from the geodesy.
        /// </summary>
        public static Vector3 ToUnity(double lat, double lon,
                                      double originLat, double originLon,
                                      float y = 0f)
        {
            var enu = ToEnu(lat, lon, originLat, originLon);
            return new Vector3((float)enu.x, y, (float)enu.y);
        }

        /// <summary>
        /// Perpendicular distance in metres from P to segment AB, and the
        /// projection parameter t in [0,1] along AB. Used for cross-track error.
        /// </summary>
        public static double PointSegmentDistance(
            double plat, double plon,
            double alat, double alon,
            double blat, double blon,
            out double t)
        {
            var b = ToEnu(blat, blon, alat, alon);
            var p = ToEnu(plat, plon, alat, alon);

            double dx = b.x, dy = b.y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-9)
            {
                t = 0.0;
                return Math.Sqrt(p.x * p.x + p.y * p.y);
            }

            t = Math.Max(0.0, Math.Min(1.0, (p.x * dx + p.y * dy) / lenSq));
            double cx = t * dx, cy = t * dy;
            return Math.Sqrt((p.x - cx) * (p.x - cx) + (p.y - cy) * (p.y - cy));
        }

        /// <summary>Human-readable distance, matching the backend's phrasing.</summary>
        public static string FormatDistance(double metres)
        {
            if (metres < 1000.0) return $"{Mathf.RoundToInt((float)metres)} m";
            return $"{metres / 1000.0:0.0} km";
        }

        /// <summary>
        /// Shortest signed turn the user must make to face <paramref name="targetBearing"/>,
        /// given their current heading. Drives the on-screen arrow.
        /// </summary>
        public static float RelativeTurn(float currentHeading, double targetBearing)
        {
            return (float)BearingDelta(currentHeading, targetBearing);
        }

        /// <summary>Drop points closer than <paramref name="minSpacing"/> to their kept predecessor.</summary>
        public static List<T> SimplifyBySpacing<T>(IList<T> points, double minSpacing,
                                                   Func<T, (double lat, double lon)> selector)
        {
            var kept = new List<T>();
            if (points == null || points.Count == 0) return kept;

            kept.Add(points[0]);
            if (points.Count <= 2)
            {
                if (points.Count == 2) kept.Add(points[1]);
                return kept;
            }

            for (int i = 1; i < points.Count - 1; i++)
            {
                var (klat, klon) = selector(kept[kept.Count - 1]);
                var (plat, plon) = selector(points[i]);
                if (Haversine(klat, klon, plat, plon) >= minSpacing)
                    kept.Add(points[i]);
            }
            kept.Add(points[points.Count - 1]);
            return kept;
        }
    }

    /// <summary>
    /// Double-precision 2D vector. Unity's Vector2 is float, which quantises
    /// latitude to roughly a metre — unusable for a system whose whole problem
    /// statement is 3–9 m of GPS error.
    /// </summary>
    [Serializable]
    public struct Vector2d
    {
        public double x;
        public double y;

        public Vector2d(double x, double y)
        {
            this.x = x;
            this.y = y;
        }

        public double Magnitude => Math.Sqrt(x * x + y * y);

        public override string ToString() => $"({x:0.##}, {y:0.##})";
    }
}
