using System;
using System.Collections.Generic;
using System.Linq;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;
using PlanSafe.Contracts.Simulation;

namespace PlanSafe.Api.Map;

/// <summary>
/// Rasterizes OpenStreetMap streets, buildings, barriers, and emergency elements
/// into a discrete walkability raster MapScenario for realistic street pathfinding.
/// </summary>
public sealed class MapScenarioFactory
{
    private readonly KrakowOsmService _osm;

    public const double MetersPerDegreeLatitude = 111_320.0;
    public const double MarginMeters = 160.0;
    public const double ExitRadiusMeters = 30.0;
    public const double RoadblockHalfWidthMeters = 3.0;
    private const double MinimumCellSize = 2.0;
    private const double PassageHalfWidthMeters = 1.0;
    public const double GateRadiusMeters = 1.5;
    public const double BarrierCrossingHalfWidthMeters = 2.5;
    public const double WaterwayCrossingHalfWidthMeters = 4.0;
    public const double RailwayCrossingHalfWidthMeters = 6.0;

    public MapScenarioFactory(KrakowOsmService osm)
    {
        _osm = osm;
    }

    public MapScenario? BuildForTargets(
        IReadOnlyList<EvacuationTarget> targets,
        IReadOnlyList<List<GeoCoordinate>>? roadblocks = null,
        IReadOnlyList<List<GeoCoordinate>>? zones = null,
        GeoCoordinate? citizenLocation = null)
    {
        if (!_osm.IsLoaded || targets == null || targets.Count == 0) return null;

        var allPoints = new List<(double Lat, double Lng)>();
        foreach (var t in targets)
        {
            allPoints.Add((t.Latitude ?? t.Y, t.Longitude ?? t.X));
        }
        if (citizenLocation.HasValue)
        {
            allPoints.Add((citizenLocation.Value.Latitude, citizenLocation.Value.Longitude));
        }
        if (roadblocks != null)
        {
            foreach (var line in roadblocks)
            {
                if (line == null) continue;
                foreach (var pt in line) allPoints.Add((pt.Latitude, pt.Longitude));
            }
        }
        if (zones != null)
        {
            foreach (var zone in zones)
            {
                if (zone == null) continue;
                foreach (var pt in zone) allPoints.Add((pt.Latitude, pt.Longitude));
            }
        }

        if (allPoints.Count == 0) return null;

        // Check if any point is in Krakow bounds
        if (!allPoints.Any(p => _osm.IsInsideKrakowBbox(p.Lat, p.Lng)))
        {
            return null;
        }

        double minLat = allPoints.Min(p => p.Lat);
        double maxLat = allPoints.Max(p => p.Lat);
        double minLng = allPoints.Min(p => p.Lng);
        double maxLng = allPoints.Max(p => p.Lng);

        // Clamp to OSM bbox
        minLat = Math.Max(KrakowOsmService.BboxSouth, minLat);
        maxLat = Math.Min(KrakowOsmService.BboxNorth, maxLat);
        minLng = Math.Max(KrakowOsmService.BboxWest, minLng);
        maxLng = Math.Min(KrakowOsmService.BboxEast, maxLng);

        double centerLat = (minLat + maxLat) / 2.0;
        double metersPerDegreeLongitude = MetersPerDegreeLatitude * Math.Cos(centerLat * Math.PI / 180.0);

        double north = maxLat + MarginMeters / MetersPerDegreeLatitude;
        double south = minLat - MarginMeters / MetersPerDegreeLatitude;
        double west = minLng - MarginMeters / metersPerDegreeLongitude;
        double east = maxLng + MarginMeters / metersPerDegreeLongitude;

        double width = Math.Clamp((east - west) * metersPerDegreeLongitude, 80.0, 6000.0);
        double height = Math.Clamp((north - south) * MetersPerDegreeLatitude, 80.0, 6000.0);

        double cellSize = Math.Max(MinimumCellSize, Math.Ceiling(Math.Max(width, height) / 600.0 * 2.0) / 2.0);

        (double X, double Y) ToWorld(double lat, double lng) =>
            ((lng - west) * metersPerDegreeLongitude, (north - lat) * MetersPerDegreeLatitude);

        (double[] X, double[] Y) ToWorldLine(IEnumerable<(double Lat, double Lng)> line)
        {
            var projected = line.Select(p => ToWorld(p.Lat, p.Lng)).ToArray();
            return (projected.Select(p => p.X).ToArray(), projected.Select(p => p.Y).ToArray());
        }

        var builder = new MapScenarioBuilder(width, height, cellSize);
        RasterizeStreets(builder, south, west, north, east, ToWorldLine);

        if (roadblocks != null)
        {
            foreach (var line in roadblocks)
            {
                if (line == null || line.Count < 2) continue;
                var (xs, ys) = ToWorldLine(line.Select(p => (p.Latitude, p.Longitude)));
                builder.SetCorridor(xs, ys, Math.Max(RoadblockHalfWidthMeters, cellSize), isWalkable: false);
            }
        }

        var exits = new List<MapExit>();
        foreach (var target in targets)
        {
            double tLat = target.Latitude ?? target.Y;
            double tLng = target.Longitude ?? target.X;
            var (x, y) = ToWorld(tLat, tLng);
            var snapped = builder.FindNearestWalkable(x, y, 120.0) ?? (x, y);
            builder.SetDisk(snapped.X, snapped.Y, Math.Max(2.0, cellSize), isWalkable: true);
            exits.Add(new MapExit(snapped.X, snapped.Y, ExitRadiusMeters, target.Capacity, target.CurrentOccupancy, TargetId: target.Id));
        }

        if (exits.Count == 0) return null;

        var spawnZone = new MapSpawnZone(
            [exits[0].X, exits[0].X + 5, exits[0].X],
            [exits[0].Y, exits[0].Y, exits[0].Y + 5],
            10
        );

        return builder.Build(
            north,
            west,
            MetersPerDegreeLatitude,
            metersPerDegreeLongitude,
            exits.ToArray(),
            [spawnZone]
        );
    }

    private void RasterizeStreets(
        MapScenarioBuilder builder,
        double south,
        double west,
        double north,
        double east,
        Func<IEnumerable<(double Lat, double Lng)>, (double[] X, double[] Y)> toWorldLine)
    {
        double cellSize = builder.CellSize;
        double minimumHalfWidth = cellSize * 0.75;
        (double[] X, double[] Y) Line(OsmNode[] nodes) => toWorldLine(nodes.Select(n => ((double)n.Lat, (double)n.Lng)));

        var areas = _osm.GetAreasInBbox(south, west, north, east);
        var lines = _osm.GetLinesInBbox(south, west, north, east);
        var roads = _osm.GetRoadsInBbox(south, west, north, east)
            .Where(road => !road.Flags.HasFlag(OsmRoadFlags.Tunnel))
            .Select(road => (Road: road, Geometry: Line(road.Nodes)))
            .ToList();

        void SetAreas(MapScenarioBuilder target, OsmAreaKind kind, bool isWalkable)
        {
            foreach (var area in areas.Where(a => a.Kind == kind))
            {
                var rings = area.Rings.Select(Line).ToArray();
                target.SetRings(rings.Select(r => r.X).ToArray(), rings.Select(r => r.Y).ToArray(), isWalkable);
                foreach (var (xs, ys) in rings)
                {
                    if (MeanWidth(xs, ys) < 3 * cellSize)
                        target.SetCorridor([.. xs, xs[0]], [.. ys, ys[0]], minimumHalfWidth, isWalkable);
                }
            }
        }

        builder.Fill(isWalkable: true);
        SetAreas(builder, OsmAreaKind.Water, isWalkable: false);

        // Keep raster-width fences beside mapped streets from closing their centre
        // lane. Match the browser terrain raster without reopening water or railways.
        var streetCells = new MapScenarioBuilder(builder.Columns * cellSize, builder.Rows * cellSize, cellSize);
        foreach (var (_, (xs, ys)) in roads)
            streetCells.SetCorridor(xs, ys, Math.Max(PassageHalfWidthMeters, minimumHalfWidth));
        var outsideStreets = streetCells.ToMask();
        for (int i = 0; i < outsideStreets.Length; i++) outsideStreets[i] = !outsideStreets[i];

        var obstacles = new SegmentIndex(20.0);
        foreach (var line in lines)
        {
            var (xs, ys) = Line(line.Nodes);
            builder.SetCorridor(xs, ys, Math.Max(line.HalfWidth, minimumHalfWidth), isWalkable: false,
                onlyWhere: line.Kind == OsmLineKind.Barrier ? outsideStreets : null);
            for (int i = 0; i + 1 < xs.Length; i++) obstacles.Add(xs[i], ys[i], xs[i + 1], ys[i + 1], line.Kind);
        }
        foreach (var gate in _osm.GetGatesInBbox(south, west, north, east))
        {
            var (xs, ys) = toWorldLine([(gate.Lat, gate.Lng)]);
            builder.SetDisk(xs[0], ys[0], Math.Max(GateRadiusMeters, cellSize), isWalkable: true);
        }
        foreach (var (road, (xs, ys)) in roads)
        {
            double roadHalfWidth = HalfWidth(road.HighwayType);
            for (int i = 0; i + 1 < xs.Length; i++)
            {
                foreach (var (x, y, kind) in obstacles.Crossings(xs[i], ys[i], xs[i + 1], ys[i + 1]))
                {
                    double limit = kind switch
                    {
                        OsmLineKind.Railway => RailwayCrossingHalfWidthMeters,
                        OsmLineKind.Waterway => WaterwayCrossingHalfWidthMeters,
                        _ => BarrierCrossingHalfWidthMeters
                    };
                    builder.SetDisk(x, y, Math.Max(Math.Min(roadHalfWidth, limit), minimumHalfWidth), isWalkable: true);
                }
            }
            if (road.Flags.HasFlag(OsmRoadFlags.Bridge))
                builder.SetCorridor(xs, ys, Math.Max(roadHalfWidth, minimumHalfWidth));
        }

        // Ways reopen only the building cells they pass through: gateways, arcades, passages.
        var buildingCells = new MapScenarioBuilder(builder.Columns * cellSize, builder.Rows * cellSize, cellSize);
        SetAreas(buildingCells, OsmAreaKind.Building, isWalkable: true);
        var insideBuildings = buildingCells.ToMask();
        SetAreas(builder, OsmAreaKind.Building, isWalkable: false);
        foreach (var (_, (xs, ys)) in roads)
            builder.SetCorridor(xs, ys, Math.Max(PassageHalfWidthMeters, minimumHalfWidth), isWalkable: true, onlyWhere: insideBuildings);
        var recoveryStreets = new MapScenarioBuilder(builder.Columns * cellSize, builder.Rows * cellSize, cellSize);
        foreach (var (road, (xs, ys)) in roads)
            recoveryStreets.SetCorridor(xs, ys, Math.Max(HalfWidth(road.HighwayType), minimumHalfWidth));
        builder.SetStreetMask(recoveryStreets.ToMask());
    }

    private static double MeanWidth(double[] xs, double[] ys)
    {
        double area = 0, perimeter = 0;
        for (int i = 0, j = xs.Length - 1; i < xs.Length; j = i++)
        {
            area += xs[j] * ys[i] - xs[i] * ys[j];
            perimeter += Math.Sqrt((xs[i] - xs[j]) * (xs[i] - xs[j]) + (ys[i] - ys[j]) * (ys[i] - ys[j]));
        }
        return perimeter > 0 ? Math.Abs(area) / perimeter : 0;
    }

    public static double HalfWidth(string highwayType) => highwayType.Replace("_link", "", StringComparison.Ordinal) switch
    {
        "motorway" or "trunk" or "primary" => 9.0,
        "secondary" => 8.0,
        "tertiary" => 7.0,
        "residential" or "unclassified" or "road" => 6.0,
        "living_street" or "pedestrian" => 5.0,
        "service" => 3.5,
        "track" => 2.5,
        "footway" or "path" or "cycleway" or "bridleway" or "steps" or "corridor" or "sidewalk" or "crossing" => 1.8,
        _ => 3.0
    };

    private sealed class SegmentIndex(double bucketSize)
    {
        private readonly Dictionary<(int, int), List<(double Ax, double Ay, double Bx, double By, OsmLineKind Kind)>> buckets = new();

        public void Add(double ax, double ay, double bx, double by, OsmLineKind kind)
        {
            var segment = (ax, ay, bx, by, kind);
            foreach (var key in Keys(ax, ay, bx, by))
            {
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
                list.Add(segment);
            }
        }

        public IEnumerable<(double X, double Y, OsmLineKind Kind)> Crossings(double ax, double ay, double bx, double by)
        {
            var seen = new HashSet<(double, double, double, double)>();
            foreach (var key in Keys(ax, ay, bx, by))
            {
                if (!buckets.TryGetValue(key, out var list)) continue;
                foreach (var s in list)
                {
                    if (!seen.Add((s.Ax, s.Ay, s.Bx, s.By))) continue;
                    if (Intersect(ax, ay, bx, by, s.Ax, s.Ay, s.Bx, s.By) is { } point) yield return (point.X, point.Y, s.Kind);
                }
            }
        }

        private IEnumerable<(int, int)> Keys(double ax, double ay, double bx, double by)
        {
            int minX = (int)Math.Floor(Math.Min(ax, bx) / bucketSize), maxX = (int)Math.Floor(Math.Max(ax, bx) / bucketSize);
            int minY = (int)Math.Floor(Math.Min(ay, by) / bucketSize), maxY = (int)Math.Floor(Math.Max(ay, by) / bucketSize);
            for (int x = minX; x <= maxX; x++)
                for (int y = minY; y <= maxY; y++)
                    yield return (x, y);
        }

        private static (double X, double Y)? Intersect(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
        {
            double rx = bx - ax, ry = by - ay, sx = dx - cx, sy = dy - cy;
            double denominator = rx * sy - ry * sx;
            if (Math.Abs(denominator) < 1e-9) return null;
            double t = ((cx - ax) * sy - (cy - ay) * sx) / denominator;
            double u = ((cx - ax) * ry - (cy - ay) * rx) / denominator;
            return t is >= 0 and <= 1 && u is >= 0 and <= 1 ? (ax + t * rx, ay + t * ry) : null;
        }
    }
}
