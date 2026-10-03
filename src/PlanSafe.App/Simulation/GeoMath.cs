using System;
using System.Collections.Generic;
using System.Linq;
using PlanSafe.Contracts.Models.Simulation;

namespace PlanSafe.App.Simulation;

/// <summary>
/// Common geographic and geometric calculations shared across PlanSafe modules.
/// </summary>
public static class GeoMath
{
    public const double EarthRadiusMeters = 6371e3;

    /// <summary>
    /// Computes the great-circle distance between two geographic coordinates in meters using the Haversine formula.
    /// </summary>
    public static double CalculateDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        double phi1 = lat1 * Math.PI / 180.0;
        double phi2 = lat2 * Math.PI / 180.0;
        double deltaPhi = (lat2 - lat1) * Math.PI / 180.0;
        double deltaLambda = (lon2 - lon1) * Math.PI / 180.0;

        double a = Math.Sin(deltaPhi / 2) * Math.Sin(deltaPhi / 2) +
                   Math.Cos(phi1) * Math.Cos(phi2) *
                   Math.Sin(deltaLambda / 2) * Math.Sin(deltaLambda / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0.0, 1.0 - a)));

        return EarthRadiusMeters * c;
    }

    /// <summary>
    /// Computes initial geographic bearing from (lat1, lon1) to (lat2, lon2) in degrees [0, 360).
    /// </summary>
    public static double CalculateBearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        double phi1 = lat1 * Math.PI / 180.0;
        double phi2 = lat2 * Math.PI / 180.0;
        double deltaLambda = (lon2 - lon1) * Math.PI / 180.0;

        double y = Math.Sin(deltaLambda) * Math.Cos(phi2);
        double x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(deltaLambda);
        double rad = Math.Atan2(y, x);
        return Math.Round((rad * (180.0 / Math.PI) + 360.0) % 360.0);
    }

    /// <summary>
    /// Computes 2D Euclidean bearing from (x1, y1) to (x2, y2) in degrees [0, 360).
    /// </summary>
    public static double CalculateEuclideanBearingDegrees(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        double rad = Math.Atan2(dy, dx);
        return Math.Round((rad * (180.0 / Math.PI) + 360.0) % 360.0);
    }

    /// <summary>
    /// Determines whether two 2D line segments (p1-p2 and p3-p4) intersect.
    /// </summary>
    public static bool SegmentsIntersect(
        double p1x, double p1y, double p2x, double p2y,
        double p3x, double p3y, double p4x, double p4y)
    {
        double d1 = Cross(p4x - p3x, p4y - p3y, p1x - p3x, p1y - p3y);
        double d2 = Cross(p4x - p3x, p4y - p3y, p2x - p3x, p2y - p3y);
        double d3 = Cross(p2x - p1x, p2y - p1y, p3x - p1x, p3y - p1y);
        double d4 = Cross(p2x - p1x, p2y - p1y, p4x - p1x, p4y - p1y);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(double ax, double ay, double bx, double by) => (ax * by) - (ay * bx);

    /// <summary>
    /// Computes the perpendicular distance in meters from a geographic point (px, py) to a segment (x1, y1)-(x2, y2).
    /// </summary>
    public static double DistancePointToSegmentMeters(double px, double py, double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        double lenSq = (dx * dx) + (dy * dy);

        if (lenSq == 0)
        {
            return CalculateDistanceMeters(px, py, x1, y1);
        }

        double t = Math.Max(0, Math.Min(1, (((px - x1) * dx) + ((py - y1) * dy)) / lenSq));
        double projX = x1 + (t * dx);
        double projY = y1 + (t * dy);

        return CalculateDistanceMeters(px, py, projX, projY);
    }

    /// <summary>
    /// Checks whether a 2D line segment intersects a rectangular obstacle.
    /// </summary>
    public static bool LineIntersectsRect(double x1, double y1, double x2, double y2, double rx, double ry, double rw, double rh)
    {
        if (Math.Min(x1, x2) > rx + rw || Math.Max(x1, x2) < rx ||
            Math.Min(y1, y2) > ry + rh || Math.Max(y1, y2) < ry)
        {
            return false;
        }

        return SegmentsIntersect(x1, y1, x2, y2, rx, ry, rx + rw, ry) ||
               SegmentsIntersect(x1, y1, x2, y2, rx + rw, ry, rx + rw, ry + rh) ||
               SegmentsIntersect(x1, y1, x2, y2, rx + rw, ry + rh, rx, ry + rh) ||
               SegmentsIntersect(x1, y1, x2, y2, rx, ry + rh, rx, ry);
    }

    /// <summary>
    /// Generates waypoint path coordinates that route around rectangular obstacles.
    /// </summary>
    public static List<(double X, double Y)> GenerateObstacleAvoidingPath2D(
        double startX, double startY, double targetX, double targetY,
        IReadOnlyList<ObstacleDto>? obstacles,
        double margin = 6.0)
    {
        var path = new List<(double X, double Y)> { (startX, startY) };

        if (obstacles == null || obstacles.Count == 0)
        {
            path.Add((targetX, targetY));
            return path;
        }

        var hitObstacles = obstacles
            .Where(o => LineIntersectsRect(startX, startY, targetX, targetY, o.X, o.Y, o.Width, o.Height))
            .OrderBy(o => Math.Min(Math.Abs(startX - (o.X + (o.Width / 2))), Math.Abs(startY - (o.Y + (o.Height / 2)))))
            .ToList();

        if (hitObstacles.Count == 0)
        {
            path.Add((targetX, targetY));
            return path;
        }

        double currentX = startX;
        double currentY = startY;

        foreach (var obs in hitObstacles)
        {
            if (!LineIntersectsRect(currentX, currentY, targetX, targetY, obs.X, obs.Y, obs.Width, obs.Height))
            {
                continue;
            }

            double topY = Math.Max(0.0, obs.Y - margin);
            double bottomY = obs.Y + obs.Height + margin;
            double leftX = Math.Max(0.0, obs.X - margin);
            double rightX = obs.X + obs.Width + margin;

            double distTop = Math.Abs(currentY - topY) + Math.Abs(targetY - topY);
            double distBottom = Math.Abs(currentY - bottomY) + Math.Abs(targetY - bottomY);

            double bypassY = distTop <= distBottom ? topY : bottomY;

            if (currentX < obs.X)
            {
                path.Add((leftX, bypassY));
                path.Add((rightX, bypassY));
            }
            else
            {
                path.Add((rightX, bypassY));
                path.Add((leftX, bypassY));
            }

            currentX = path[^1].X;
            currentY = path[^1].Y;
        }

        path.Add((targetX, targetY));
        return path;
    }
}
