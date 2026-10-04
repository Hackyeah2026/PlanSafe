using System;
using System.Collections.Generic;
using System.Linq;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;

namespace PlanSafe.Contracts.Simulation;

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
    /// Ray-casting point-in-polygon test (WGS84 lat/lng).
    /// </summary>
    public static bool IsPointInPolygon(double lat, double lng, IReadOnlyList<GeoCoordinate> polygon)
    {
        if (polygon == null || polygon.Count < 3) return false;
        bool inside = false;
        int j = polygon.Count - 1;
        for (int i = 0; i < polygon.Count; i++)
        {
            if (((polygon[i].Lat > lat) != (polygon[j].Lat > lat)) &&
                (lng < (polygon[j].Lng - polygon[i].Lng) * (lat - polygon[i].Lat) /
                       (polygon[j].Lat - polygon[i].Lat) + polygon[i].Lng))
            {
                inside = !inside;
            }
            j = i;
        }
        return inside;
    }

    /// <summary>
    /// Returns the polygon centroid, or an interior midpoint when a concave polygon excludes it.
    /// Returns null for polygons without a usable interior.
    /// </summary>
    public static GeoCoordinate? GetInteriorCenter(IReadOnlyList<GeoCoordinate> polygon)
    {
        if (polygon.Count < 3 || polygon.Any(p => !double.IsFinite(p.Latitude) || !double.IsFinite(p.Longitude)))
            return null;

        // Translate coordinates before accumulating area to avoid cancellation for small zones.
        var origin = polygon[0];
        double twiceArea = 0, latSum = 0, lngSum = 0;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            double ax = polygon[j].Longitude - origin.Longitude;
            double ay = polygon[j].Latitude - origin.Latitude;
            double bx = polygon[i].Longitude - origin.Longitude;
            double by = polygon[i].Latitude - origin.Latitude;
            double cross = ax * by - bx * ay;
            twiceArea += cross;
            lngSum += (ax + bx) * cross;
            latSum += (ay + by) * cross;
        }
        if (twiceArea == 0) return null;
        double lat = origin.Latitude + latSum / (3 * twiceArea);
        double lng = origin.Longitude + lngSum / (3 * twiceArea);
        if (IsPointInPolygon(lat, lng, polygon)) return new GeoCoordinate(lat, lng);

        lat = (polygon.Min(p => p.Latitude) + polygon.Max(p => p.Latitude)) / 2;

        // Pair scan-line intersections to find interior spans at the central latitude.
        var intersections = new List<double>();
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[j];
            var b = polygon[i];
            if ((a.Latitude > lat) != (b.Latitude > lat))
                intersections.Add(a.Longitude + (lat - a.Latitude) *
                    (b.Longitude - a.Longitude) / (b.Latitude - a.Latitude));
        }
        intersections.Sort();
        GeoCoordinate? center = null;
        double widest = 0;
        for (int i = 0; i + 1 < intersections.Count; i += 2)
        {
            double width = intersections[i + 1] - intersections[i];
            double midpoint = (intersections[i] + intersections[i + 1]) / 2;
            if (width > widest && IsPointInPolygon(lat, midpoint, polygon))
            {
                widest = width;
                center = new GeoCoordinate(lat, midpoint);
            }
        }
        return center;
    }

    /// <summary>
    /// Tests whether point (lat, lng) is within circular buffer of radius in meters.
    /// </summary>
    public static bool IsPointInCircle(double lat, double lng, double centerLat, double centerLng, double radiusMeters)
    {
        return CalculateDistanceMeters(lat, lng, centerLat, centerLng) <= radiusMeters;
    }

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

    /// <summary>
    /// Simplifies a polyline of geographic coordinates using the Ramer-Douglas-Peucker (RDP) algorithm.
    /// </summary>
    public static List<GeoCoordinate> SimplifyPath(IReadOnlyList<GeoCoordinate> points, double epsilonMeters = 3.5)
    {
        if (points == null || points.Count < 3)
        {
            return points != null ? points.ToList() : new List<GeoCoordinate>();
        }

        var result = new List<GeoCoordinate>();
        RdpRecursive(points, 0, points.Count - 1, epsilonMeters, result);
        result.Add(points[^1]);
        return result;
    }

    private static void RdpRecursive(
        IReadOnlyList<GeoCoordinate> points,
        int startIndex,
        int endIndex,
        double epsilonMeters,
        List<GeoCoordinate> result)
    {
        double maxDist = 0.0;
        int maxIndex = startIndex;

        double startLat = points[startIndex].Lat;
        double startLng = points[startIndex].Lng;
        double endLat = points[endIndex].Lat;
        double endLng = points[endIndex].Lng;

        for (int i = startIndex + 1; i < endIndex; i++)
        {
            double d = DistancePointToSegmentMeters(
                points[i].Lat, points[i].Lng,
                startLat, startLng,
                endLat, endLng);

            if (d > maxDist)
            {
                maxDist = d;
                maxIndex = i;
            }
        }

        if (maxDist > epsilonMeters)
        {
            RdpRecursive(points, startIndex, maxIndex, epsilonMeters, result);
            RdpRecursive(points, maxIndex, endIndex, epsilonMeters, result);
        }
        else
        {
            result.Add(points[startIndex]);
        }
    }

    /// <summary>
    /// Computes a collision-avoiding route using the crowd simulation's Dijkstra potential field wavefront.
    /// If no roadblocks obstruct the direct path, returns direct segment.
    /// If roadblocks obstruct the path, builds a local grid, marks roadblock barriers,
    /// propagates Dijkstra wavefront from target, and traces down the gradient.
    /// </summary>
    public static List<GeoCoordinate> CalculateWavefrontRoute(
        double startLat, double startLng,
        double targetLat, double targetLng,
        IReadOnlyList<IReadOnlyList<GeoCoordinate>>? roadblocks,
        double cellSize = 4.0)
    {
        var directPath = new List<GeoCoordinate>
        {
            new(startLat, startLng),
            new(targetLat, targetLng)
        };

        if (roadblocks == null || roadblocks.Count == 0)
        {
            return directPath;
        }

        // Check if any roadblock intersects the direct path
        bool anyIntersection = false;
        foreach (var rb in roadblocks)
        {
            if (rb == null || rb.Count < 2) continue;
            for (int i = 0; i + 1 < rb.Count; i++)
            {
                if (SegmentsIntersect(startLat, startLng, targetLat, targetLng,
                    rb[i].Latitude, rb[i].Longitude, rb[i + 1].Latitude, rb[i + 1].Longitude))
                {
                    anyIntersection = true;
                    break;
                }
            }
            if (anyIntersection) break;
        }

        if (!anyIntersection)
        {
            return directPath;
        }

        // Roadblock obstruction detected: build local wavefront potential field grid
        double minLat = Math.Min(startLat, targetLat);
        double maxLat = Math.Max(startLat, targetLat);
        double minLng = Math.Min(startLng, targetLng);
        double maxLng = Math.Max(startLng, targetLng);

        foreach (var rb in roadblocks)
        {
            if (rb == null) continue;
            foreach (var pt in rb)
            {
                minLat = Math.Min(minLat, pt.Latitude);
                maxLat = Math.Max(maxLat, pt.Latitude);
                minLng = Math.Min(minLng, pt.Longitude);
                maxLng = Math.Max(maxLng, pt.Longitude);
            }
        }

        // Add 100 meters padding around the bounding box
        double padMeters = 100.0;
        double padLat = padMeters / 111320.0;
        double padLng = padMeters / (111320.0 * Math.Cos(minLat * Math.PI / 180.0));

        minLat -= padLat;
        maxLat += padLat;
        minLng -= padLng;
        maxLng += padLng;

        double originLat = maxLat;
        double originLng = minLng;
        double metersPerDegLat = 111320.0;
        double metersPerDegLng = 111320.0 * Math.Cos(originLat * Math.PI / 180.0);

        double worldWidth = Math.Max(20.0, (maxLng - minLng) * metersPerDegLng);
        double worldHeight = Math.Max(20.0, (maxLat - minLat) * metersPerDegLat);

        int cols = Math.Clamp((int)Math.Ceiling(worldWidth / cellSize), 10, 500);
        int rows = Math.Clamp((int)Math.Ceiling(worldHeight / cellSize), 10, 500);
        int totalCells = cols * rows;

        bool[] blocked = new bool[totalCells];

        // Rasterize roadblock segments into grid with safety margin
        foreach (var rb in roadblocks)
        {
            if (rb == null || rb.Count < 2) continue;
            for (int i = 0; i + 1 < rb.Count; i++)
            {
                double x1 = (rb[i].Longitude - originLng) * metersPerDegLng;
                double y1 = (originLat - rb[i].Latitude) * metersPerDegLat;
                double x2 = (rb[i + 1].Longitude - originLng) * metersPerDegLng;
                double y2 = (originLat - rb[i + 1].Latitude) * metersPerDegLat;

                int c1 = Math.Clamp((int)(x1 / cellSize), 0, cols - 1);
                int r1 = Math.Clamp((int)(y1 / cellSize), 0, rows - 1);
                int c2 = Math.Clamp((int)(x2 / cellSize), 0, cols - 1);
                int r2 = Math.Clamp((int)(y2 / cellSize), 0, rows - 1);

                RasterizeLineWithThickness(blocked, cols, rows, c1, r1, c2, r2, thickness: 1);
            }
        }

        double startX = (startLng - originLng) * metersPerDegLng;
        double startY = (originLat - startLat) * metersPerDegLat;
        double targetX = (targetLng - originLng) * metersPerDegLng;
        double targetY = (originLat - targetLat) * metersPerDegLat;

        int startCol = Math.Clamp((int)(startX / cellSize), 0, cols - 1);
        int startRow = Math.Clamp((int)(startY / cellSize), 0, rows - 1);
        int targetCol = Math.Clamp((int)(targetX / cellSize), 0, cols - 1);
        int targetRow = Math.Clamp((int)(targetY / cellSize), 0, rows - 1);

        // If target cell is blocked, find nearest open cell
        if (blocked[targetRow * cols + targetCol])
        {
            var open = FindNearestOpenCell(blocked, cols, rows, targetCol, targetRow, 25);
            if (open.HasValue) { targetCol = open.Value.Col; targetRow = open.Value.Row; }
        }

        // If start cell is blocked, find nearest open cell
        if (blocked[startRow * cols + startCol])
        {
            var open = FindNearestOpenCell(blocked, cols, rows, startCol, startRow, 25);
            if (open.HasValue) { startCol = open.Value.Col; startRow = open.Value.Row; }
        }

        // Dijkstra Wavefront Propagation from target (0 potential)
        float[] potential = new float[totalCells];
        Array.Fill(potential, float.MaxValue);

        var pq = new PriorityQueue<int, float>(cols + rows);
        int targetIdx = targetRow * cols + targetCol;
        potential[targetIdx] = 0f;
        pq.Enqueue(targetIdx, 0f);

        int[] dCol = { 1, -1, 0, 0, 1, 1, -1, -1 };
        int[] dRow = { 0, 0, 1, -1, 1, -1, 1, -1 };
        float stepCard = (float)cellSize;
        float stepDiag = (float)(cellSize * 1.41421356);
        float[] costs = { stepCard, stepCard, stepCard, stepCard, stepDiag, stepDiag, stepDiag, stepDiag };

        int startIdx = startRow * cols + startCol;

        while (pq.Count > 0)
        {
            pq.TryDequeue(out int currIdx, out float currDist);
            if (currDist > potential[currIdx]) continue;

            if (currIdx == startIdx && currDist < float.MaxValue - 1000f)
            {
                break;
            }

            int currRow = currIdx / cols;
            int currCol = currIdx % cols;

            for (int d = 0; d < 8; d++)
            {
                int nc = currCol + dCol[d];
                int nr = currRow + dRow[d];

                if (nc >= 0 && nc < cols && nr >= 0 && nr < rows)
                {
                    int nIdx = nr * cols + nc;
                    if (blocked[nIdx]) continue;

                    if (d >= 4)
                    {
                        if (blocked[currRow * cols + nc] && blocked[nr * cols + currCol])
                            continue;
                    }

                    float newDist = currDist + costs[d];
                    if (newDist < potential[nIdx])
                    {
                        potential[nIdx] = newDist;
                        pq.Enqueue(nIdx, newDist);
                    }
                }
            }
        }

        if (potential[startIdx] >= float.MaxValue - 1000f)
        {
            return directPath;
        }

        // Trace path down the gradient from start to target
        var rawPath = new List<GeoCoordinate> { new(startLat, startLng) };
        int traceCol = startCol;
        int traceRow = startRow;
        int maxSteps = (cols + rows) * 2;
        int step = 0;

        while ((traceCol != targetCol || traceRow != targetRow) && step++ < maxSteps)
        {
            float bestDist = potential[traceRow * cols + traceCol];
            int nextCol = traceCol;
            int nextRow = traceRow;

            for (int d = 0; d < 8; d++)
            {
                int nc = traceCol + dCol[d];
                int nr = traceRow + dRow[d];
                if (nc >= 0 && nc < cols && nr >= 0 && nr < rows)
                {
                    int nIdx = nr * cols + nc;
                    if (potential[nIdx] < bestDist)
                    {
                        bestDist = potential[nIdx];
                        nextCol = nc;
                        nextRow = nr;
                    }
                }
            }

            if (nextCol == traceCol && nextRow == traceRow)
            {
                break;
            }

            traceCol = nextCol;
            traceRow = nextRow;

            double cellLat = originLat - ((traceRow + 0.5) * cellSize / metersPerDegLat);
            double cellLng = originLng + ((traceCol + 0.5) * cellSize / metersPerDegLng);
            rawPath.Add(new GeoCoordinate(cellLat, cellLng));
        }

        rawPath.Add(new GeoCoordinate(targetLat, targetLng));

        return SimplifyPath(rawPath, 3.5);
    }

    private static void RasterizeLineWithThickness(bool[] grid, int cols, int rows, int x0, int y0, int x1, int y1, int thickness)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        int cx = x0;
        int cy = y0;

        while (true)
        {
            for (int ty = -thickness; ty <= thickness; ty++)
            {
                for (int tx = -thickness; tx <= thickness; tx++)
                {
                    int nx = cx + tx;
                    int ny = cy + ty;
                    if (nx >= 0 && nx < cols && ny >= 0 && ny < rows)
                    {
                        grid[ny * cols + nx] = true;
                    }
                }
            }

            if (cx == x1 && cy == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                cx += sx;
            }
            if (e2 < dx)
            {
                err += dx;
                cy += sy;
            }
        }
    }

    private static (int Col, int Row)? FindNearestOpenCell(bool[] blocked, int cols, int rows, int startCol, int startRow, int maxRadius)
    {
        for (int r = 1; r <= maxRadius; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    int c = startCol + dx;
                    int row = startRow + dy;
                    if (c >= 0 && c < cols && row >= 0 && row < rows)
                    {
                        if (!blocked[row * cols + c]) return (c, row);
                    }
                }
            }
        }
        return null;
    }
}
