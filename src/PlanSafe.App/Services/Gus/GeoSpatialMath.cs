using System;
using System.Collections.Generic;
using System.Linq;
using PlanSafe.Contracts.Models.Gus;
using PlanSafe.Contracts.Models.Map;

namespace PlanSafe.App.Services.Gus;

/// <summary>
/// High-performance geospatial utilities for distance calculations,
/// point-in-shape testing, and cell intersection evaluation.
/// </summary>
public static class GeoSpatialMath
{
    private const double EarthRadiusMeters = 6371000.0;
    private const double MetersPerDegreeLat = 111320.0;

    /// <summary>
    /// Haversine distance in meters between two WGS84 geographic coordinates.
    /// </summary>
    public static double DistanceInMeters(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double rad1 = lat1 * Math.PI / 180.0;
        double rad2 = lat2 * Math.PI / 180.0;

        double a = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0) +
                   Math.Cos(rad1) * Math.Cos(rad2) *
                   Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);

        double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0.0, 1.0 - a)));
        return EarthRadiusMeters * c;
    }

    /// <summary>
    /// Determines whether a point is inside a polygon using ray-casting.
    /// </summary>
    public static bool IsPointInPolygon(double lat, double lng, IReadOnlyList<GeoCoordinate> polygon)
    {
        if (polygon == null || polygon.Count < 3) return false;

        bool inside = false;
        int j = polygon.Count - 1;
        for (int i = 0; i < polygon.Count; j = i++)
        {
            double piLat = polygon[i].Latitude;
            double piLng = polygon[i].Longitude;
            double pjLat = polygon[j].Latitude;
            double pjLng = polygon[j].Longitude;

            if (((piLat > lat) != (pjLat > lat)) &&
                (lng < (pjLng - piLng) * (lat - piLat) / (pjLat - piLat + 1e-15) + piLng))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// Determines whether a point is within a circular area.
    /// </summary>
    public static bool IsPointInCircle(double lat, double lng, double centerLat, double centerLng, double radiusMeters)
    {
        return DistanceInMeters(lat, lng, centerLat, centerLng) <= radiusMeters;
    }

    /// <summary>
    /// Tests if a point is inside an evacuation or safe zone item.
    /// </summary>
    public static bool IsPointInZone(double lat, double lng, MapZoneItem zone)
    {
        if (zone is CircleMapZoneItem circle && circle.CenterCoordinate.HasValue && circle.Radius.HasValue)
        {
            var center = circle.CenterCoordinate.Value;
            return IsPointInCircle(lat, lng, center.Latitude, center.Longitude, circle.Radius.Value);
        }

        if (zone is PolygonMapZoneItem polygon)
        {
            var vertices = polygon.Vertices.ToList();
            return IsPointInPolygon(lat, lng, vertices);
        }

        return false;
    }

    /// <summary>
    /// Computes the geographic bounding box of a map zone item.
    /// </summary>
    public static bool GetZoneBoundingBox(MapZoneItem zone, out double minLat, out double minLng, out double maxLat, out double maxLng)
    {
        if (zone is CircleMapZoneItem circle && circle.CenterCoordinate.HasValue && circle.Radius.HasValue)
        {
            var c = circle.CenterCoordinate.Value;
            double rMeters = circle.Radius.Value;
            double dLat = rMeters / MetersPerDegreeLat;
            double dLng = rMeters / (MetersPerDegreeLat * Math.Cos(c.Latitude * Math.PI / 180.0));

            minLat = c.Latitude - dLat;
            maxLat = c.Latitude + dLat;
            minLng = c.Longitude - dLng;
            maxLng = c.Longitude + dLng;
            return true;
        }

        if (zone is PolygonMapZoneItem polygon)
        {
            var vertices = polygon.Vertices.ToList();
            if (vertices.Count >= 3)
            {
                minLat = vertices.Min(v => v.Latitude);
                maxLat = vertices.Max(v => v.Latitude);
                minLng = vertices.Min(v => v.Longitude);
                maxLng = vertices.Max(v => v.Longitude);
                return true;
            }
        }

        minLat = minLng = maxLat = maxLng = 0;
        return false;
    }

    /// <summary>
    /// Checks if two axis-aligned bounding boxes overlap.
    /// </summary>
    public static bool DoBoundingBoxesOverlap(
        double minLat1, double minLng1, double maxLat1, double maxLng1,
        double minLat2, double minLng2, double maxLat2, double maxLng2)
    {
        return !(minLat1 > maxLat2 || maxLat1 < minLat2 || minLng1 > maxLng2 || maxLng1 < minLng2);
    }

    /// <summary>
    /// Computes the area overlap ratio (0.0 to 1.0) between a 125m GUS cell and a map zone
    /// by sampling an internal sub-grid of test points.
    /// </summary>
    public static double CalculateCellOverlapRatio(GusGridCell cell, MapZoneItem zone, int subGridSteps = 4)
    {
        int insideCount = 0;
        int totalPoints = subGridSteps * subGridSteps;

        double dLat = (cell.MaxLat - cell.MinLat) / subGridSteps;
        double dLng = (cell.MaxLng - cell.MinLng) / subGridSteps;

        for (int i = 0; i < subGridSteps; i++)
        {
            double sampleLat = cell.MinLat + (i + 0.5) * dLat;
            for (int j = 0; j < subGridSteps; j++)
            {
                double sampleLng = cell.MinLng + (j + 0.5) * dLng;
                if (IsPointInZone(sampleLat, sampleLng, zone))
                {
                    insideCount++;
                }
            }
        }

        return (double)insideCount / totalPoints;
    }
}
