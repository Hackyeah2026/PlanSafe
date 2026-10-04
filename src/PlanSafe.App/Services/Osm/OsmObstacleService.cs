using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Models.Map;

namespace PlanSafe.App.Services.Osm;

public class OsmObstacleService : IOsmObstacleService
{
    public const double BboxSouth = 50.0200;
    public const double BboxWest = 19.8700;
    public const double BboxNorth = 50.1100;
    public const double BboxEast = 20.0200;
    private const double BucketDeg = 0.001; // ~100m spatial hash bucket

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private OsmArea[] _areas = Array.Empty<OsmArea>();
    private OsmRoad[] _roads = Array.Empty<OsmRoad>();
    private OsmLine[] _lines = Array.Empty<OsmLine>();
    private (float Lat, float Lng)[] _gates = Array.Empty<(float, float)>();

    private readonly Dictionary<(int, int), List<int>> _areaIndex = new();
    private readonly Dictionary<(int, int), List<int>> _roadIndex = new();
    private readonly Dictionary<(int, int), List<int>> _lineIndex = new();
    private readonly Dictionary<(int, int), List<int>> _gateIndex = new();

    private bool _isLoaded;
    public bool IsLoaded => _isLoaded;

    public OsmObstacleService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_isLoaded) return;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_isLoaded) return;
            var scheduler = new PreparationScheduler();

            byte[]? binData = null;

            // 1. Try local file system first (useful in server/desktop and tests)
            string localBin = Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "krakow_osm.bin");
            if (!File.Exists(localBin))
            {
                localBin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "osm", "krakow_osm.bin"));
            }
            if (!File.Exists(localBin))
            {
                localBin = Path.Combine(Directory.GetCurrentDirectory(), "data", "osm", "krakow_osm.bin");
            }
            if (!File.Exists(localBin))
            {
                localBin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "osm", "krakow_osm.bin"));
            }

            if (File.Exists(localBin))
            {
                binData = await File.ReadAllBytesAsync(localBin, cancellationToken);
            }
            else
            {
                try
                {
                    binData = await _httpClient.GetByteArrayAsync("data/krakow_osm.bin", cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"[OsmObstacleService] Could not fetch data/krakow_osm.bin: {ex.Message}");
                }
            }

            if (binData != null && binData.Length > 16)
            {
                using var ms = new MemoryStream(binData);
                using var reader = new BinaryReader(ms);

                var magic = reader.ReadBytes(7);
                if (magic.Length == 7 &&
                    magic[0] == (byte)'K' && magic[1] == (byte)'O' && magic[2] == (byte)'S' &&
                    magic[3] == (byte)'M' && magic[4] == (byte)'V' && magic[5] == (byte)'3' && magic[6] == 0)
                {
                    int areaCount = reader.ReadInt32();
                    var areas = new OsmArea[areaCount];
                    for (int i = 0; i < areaCount; i++)
                    {
                        if (i % 64 == 0) await scheduler.YieldAsync(cancellationToken);
                        long id = reader.ReadInt64();
                        byte kind = reader.ReadByte();
                        int ringCount = reader.ReadInt32();

                        double bMinLat = double.MaxValue, bMaxLat = double.MinValue;
                        double bMinLng = double.MaxValue, bMaxLng = double.MinValue;

                        var rings = new (float Lat, float Lng)[ringCount][];
                        for (int r = 0; r < ringCount; r++)
                        {
                            int nodeCount = reader.ReadInt32();
                            var ring = new (float Lat, float Lng)[nodeCount];
                            for (int n = 0; n < nodeCount; n++)
                            {
                                float lat = reader.ReadSingle();
                                float lng = reader.ReadSingle();
                                ring[n] = (lat, lng);
                                if (r == 0)
                                {
                                    if (lat < bMinLat) bMinLat = lat;
                                    if (lat > bMaxLat) bMaxLat = lat;
                                    if (lng < bMinLng) bMinLng = lng;
                                    if (lng > bMaxLng) bMaxLng = lng;
                                }
                            }
                            rings[r] = ring;
                        }

                        areas[i] = new OsmArea
                        {
                            Id = id,
                            Kind = (OsmAreaKind)kind,
                            MinLat = bMinLat,
                            MaxLat = bMaxLat,
                            MinLng = bMinLng,
                            MaxLng = bMaxLng,
                            Rings = rings
                        };
                    }

                    int roadCount = reader.ReadInt32();
                    var roads = new OsmRoad[roadCount];
                    for (int i = 0; i < roadCount; i++)
                    {
                        if (i % 64 == 0) await scheduler.YieldAsync(cancellationToken);
                        long id = reader.ReadInt64();
                        string hw = reader.ReadString();
                        byte flags = reader.ReadByte();
                        int nc = reader.ReadInt32();
                        var nodes = new (float Lat, float Lng)[nc];
                        for (int n = 0; n < nc; n++)
                        {
                            nodes[n] = (reader.ReadSingle(), reader.ReadSingle());
                        }
                        roads[i] = new OsmRoad
                        {
                            Id = id,
                            HighwayType = hw,
                            Flags = (OsmRoadFlags)flags,
                            Nodes = nodes
                        };
                    }

                    int lineCount = reader.ReadInt32();
                    var lines = new OsmLine[lineCount];
                    for (int i = 0; i < lineCount; i++)
                    {
                        if (i % 64 == 0) await scheduler.YieldAsync(cancellationToken);
                        long id = reader.ReadInt64();
                        byte kind = reader.ReadByte();
                        float halfWidth = reader.ReadSingle();
                        int nc = reader.ReadInt32();
                        var nodes = new (float Lat, float Lng)[nc];
                        for (int n = 0; n < nc; n++)
                        {
                            nodes[n] = (reader.ReadSingle(), reader.ReadSingle());
                        }
                        lines[i] = new OsmLine
                        {
                            Id = id,
                            Kind = (OsmLineKind)kind,
                            HalfWidth = halfWidth,
                            Nodes = nodes
                        };
                    }

                    int gateCount = reader.ReadInt32();
                    var gates = new (float Lat, float Lng)[gateCount];
                    for (int i = 0; i < gateCount; i++)
                    {
                        if (i % 64 == 0) await scheduler.YieldAsync(cancellationToken);
                        gates[i] = (reader.ReadSingle(), reader.ReadSingle());
                    }

                    if (areas.Length == 0 && lines.Length == 0)
                        throw new InvalidDataException("The bundled map terrain contains no obstacles.");

                    _areas = areas;
                    _roads = roads;
                    _lines = lines;
                    _gates = gates;
                    await BuildSpatialIndexAsync(cancellationToken);
                    _isLoaded = true;
                    return;
                }
            }

            // Fallback: load buildings_krakow.json if binary not available
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var list = await _httpClient.GetFromJsonAsync<List<BuildingObstacle>>("data/buildings_krakow.json", options, cancellationToken);
            if (list != null && list.Count > 0)
            {
                var areas = new List<OsmArea>(list.Count);
                foreach (var b in list)
                {
                    if (b.Polygon == null || b.Polygon.Count < 3) continue;
                    var rings = new List<(float Lat, float Lng)[]>();
                    rings.Add(b.Polygon.Select(p => ((float)p[0], (float)p[1])).ToArray());
                    if (b.InnerPolygons != null)
                    {
                        foreach (var inner in b.InnerPolygons)
                        {
                            if (inner.Count >= 3)
                                rings.Add(inner.Select(p => ((float)p[0], (float)p[1])).ToArray());
                        }
                    }
                    areas.Add(new OsmArea
                    {
                        Id = b.Id.GetHashCode(),
                        Kind = b.BuildingType == "water" ? OsmAreaKind.Water : OsmAreaKind.Building,
                        MinLat = b.MinLat,
                        MaxLat = b.MaxLat,
                        MinLng = b.MinLng,
                        MaxLng = b.MaxLng,
                        Rings = rings.ToArray()
                    });
                }
                if (areas.Count == 0)
                    throw new InvalidDataException("The fallback map terrain contains no usable obstacles.");

                _areas = areas.ToArray();
                await BuildSpatialIndexAsync(cancellationToken);
                _isLoaded = true;
            }
            else
            {
                throw new InvalidDataException("The fallback map terrain is empty.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Map terrain could not be loaded. Simulation cannot start without obstacle data.", ex);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task BuildSpatialIndexAsync(CancellationToken cancellationToken)
    {
        var scheduler = new PreparationScheduler();
        _areaIndex.Clear();
        for (int i = 0; i < _areas.Length; i++)
        {
            if (i % 64 == 0) await scheduler.YieldAsync(cancellationToken);
            var a = _areas[i];
            int minGx = BucketX(Math.Max(a.MinLng, BboxWest)), maxGx = BucketX(Math.Min(a.MaxLng, BboxEast));
            int minGy = BucketY(Math.Max(a.MinLat, BboxSouth)), maxGy = BucketY(Math.Min(a.MaxLat, BboxNorth));
            for (int gx = minGx; gx <= maxGx; gx++)
                for (int gy = minGy; gy <= maxGy; gy++)
                    IndexAdd(_areaIndex, (gx, gy), i);
        }

        await IndexPolylinesAsync(_roadIndex, _roads.Select(r => r.Nodes), cancellationToken);
        await IndexPolylinesAsync(_lineIndex, _lines.Select(l => l.Nodes), cancellationToken);

        _gateIndex.Clear();
        for (int i = 0; i < _gates.Length; i++)
        {
            if (i % 64 == 0) await scheduler.YieldAsync(cancellationToken);
            IndexAdd(_gateIndex, (BucketX(_gates[i].Lng), BucketY(_gates[i].Lat)), i);
        }
    }

    private static async Task IndexPolylinesAsync(Dictionary<(int, int), List<int>> index, IEnumerable<(float Lat, float Lng)[]> polylines, CancellationToken cancellationToken)
    {
        var scheduler = new PreparationScheduler();
        index.Clear();
        int i = 0;
        foreach (var nodes in polylines)
        {
            if (i % 32 == 0) await scheduler.YieldAsync(cancellationToken);
            var seen = new HashSet<(int, int)>();
            for (int k = 0; k < nodes.Length; k++)
            {
                var a = nodes[k];
                var b = k + 1 < nodes.Length ? nodes[k + 1] : a;
                int minGx = BucketX(Math.Min(a.Lng, b.Lng)), maxGx = BucketX(Math.Max(a.Lng, b.Lng));
                int minGy = BucketY(Math.Min(a.Lat, b.Lat)), maxGy = BucketY(Math.Max(a.Lat, b.Lat));
                if ((maxGx - minGx + 1) * (maxGy - minGy + 1) > 400) { minGx = maxGx = BucketX(a.Lng); minGy = maxGy = BucketY(a.Lat); }
                for (int gx = minGx; gx <= maxGx; gx++)
                    for (int gy = minGy; gy <= maxGy; gy++)
                        if (seen.Add((gx, gy))) IndexAdd(index, (gx, gy), i);
            }
            i++;
        }
    }

    private static void IndexAdd(Dictionary<(int, int), List<int>> idx, (int, int) key, int value)
    {
        if (!idx.TryGetValue(key, out var list)) idx[key] = list = new List<int>();
        list.Add(value);
    }

    private static int BucketX(double lng) => (int)Math.Floor(lng / BucketDeg);
    private static int BucketY(double lat) => (int)Math.Floor(lat / BucketDeg);

    private static List<T> Query<T>(Dictionary<(int, int), List<int>> index, T[] items, double south, double west, double north, double east)
    {
        var result = new List<T>();
        var seen = new HashSet<int>();
        for (int gx = BucketX(west); gx <= BucketX(east); gx++)
            for (int gy = BucketY(south); gy <= BucketY(north); gy++)
                if (index.TryGetValue((gx, gy), out var list))
                    foreach (int idx in list)
                        if (seen.Add(idx)) result.Add(items[idx]);
        return result;
    }

    public async Task RasterizeTerrainAsync(
        MapScenarioBuilder builder,
        double minLat,
        double maxLat,
        double minLng,
        double maxLng,
        Func<double, double, (double X, double Y)> toWorld,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var scheduler = new PreparationScheduler();

        double cellSize = builder.CellSize;
        double minimumHalfWidth = cellSize * 0.75; // a thin obstacle or opening never leaves a diagonal gap

        (double[] X, double[] Y) Line((float Lat, float Lng)[] nodes)
        {
            var xs = new double[nodes.Length];
            var ys = new double[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                var (x, y) = toWorld(nodes[i].Lat, nodes[i].Lng);
                xs[i] = x;
                ys[i] = y;
            }
            return (xs, ys);
        }

        var areas = Query(_areaIndex, _areas, minLat, minLng, maxLat, maxLng);
        var lines = Query(_lineIndex, _lines, minLat, minLng, maxLat, maxLng);
        var roads = Query(_roadIndex, _roads, minLat, minLng, maxLat, maxLng)
            .Where(road => !road.Flags.HasFlag(OsmRoadFlags.Tunnel))
            .Select(road => (Road: road, Geometry: Line(road.Nodes)))
            .ToList();

        async Task SetAreasAsync(MapScenarioBuilder target, OsmAreaKind kind, bool isWalkable)
        {
            foreach (var area in areas.Where(a => a.Kind == kind))
            {
                await scheduler.YieldAsync(cancellationToken);
                var rings = area.Rings.Select(Line).ToArray();
                target.SetRings(rings.Select(r => r.X).ToArray(), rings.Select(r => r.Y).ToArray(), isWalkable);
                // Thin shapes (city walls mapped as buildings, narrow channels) would fall between cell
                // centers; their outline keeps them continuous without growing ordinary buildings.
                foreach (var (xs, ys) in rings)
                {
                    if (MeanWidth(xs, ys) < 3 * cellSize)
                        target.SetCorridor([.. xs, xs[0]], [.. ys, ys[0]], minimumHalfWidth, isWalkable);
                }
            }
        }

        builder.Fill(isWalkable: true);
        await SetAreasAsync(builder, OsmAreaKind.Water, isWalkable: false);

        // Raster-width fences beside a street can cover its centre even though the
        // geometries never intersect. Preserve a narrow lane along mapped ground
        // ways; water, railways and buildings still use their own obstacle rules.
        var streetCells = new MapScenarioBuilder(builder.Columns * cellSize, builder.Rows * cellSize, cellSize);
        foreach (var (_, (xs, ys)) in roads)
        {
            await scheduler.YieldAsync(cancellationToken);
            streetCells.SetCorridor(xs, ys, Math.Max(1.0, minimumHalfWidth));
        }
        var outsideStreets = streetCells.ToMask();
        for (int i = 0; i < outsideStreets.Length; i++) outsideStreets[i] = !outsideStreets[i];

        var obstacles = new SegmentIndex(20.0);
        foreach (var line in lines)
        {
            await scheduler.YieldAsync(cancellationToken);
            var (xs, ys) = Line(line.Nodes);
            builder.SetCorridor(xs, ys, Math.Max(line.HalfWidth, minimumHalfWidth), isWalkable: false,
                onlyWhere: line.Kind == OsmLineKind.Barrier ? outsideStreets : null);
            for (int i = 0; i + 1 < xs.Length; i++) obstacles.Add(xs[i], ys[i], xs[i + 1], ys[i + 1], line.Kind);
        }
        foreach (var gate in Query(_gateIndex, _gates, minLat, minLng, maxLat, maxLng))
        {
            await scheduler.YieldAsync(cancellationToken);
            var (gx, gy) = toWorld(gate.Lat, gate.Lng);
            builder.SetDisk(gx, gy, Math.Max(1.5, cellSize), isWalkable: true);
        }
        foreach (var (road, (xs, ys)) in roads)
        {
            await scheduler.YieldAsync(cancellationToken);
            double roadHalfWidth = HalfWidth(road.HighwayType);
            for (int i = 0; i + 1 < xs.Length; i++)
            {
                foreach (var (x, y, kind) in obstacles.Crossings(xs[i], ys[i], xs[i + 1], ys[i + 1]))
                {
                    double limit = kind switch
                    {
                        OsmLineKind.Railway => 6.0,
                        OsmLineKind.Waterway => 4.0,
                        _ => 2.5
                    };
                    builder.SetDisk(x, y, Math.Max(Math.Min(roadHalfWidth, limit), minimumHalfWidth), isWalkable: true);
                }
            }
            if (road.Flags.HasFlag(OsmRoadFlags.Bridge))
                builder.SetCorridor(xs, ys, Math.Max(roadHalfWidth, minimumHalfWidth));
        }

        // Ways reopen only the building cells they pass through: gateways, arcades, passages.
        var buildingCells = new MapScenarioBuilder(builder.Columns * cellSize, builder.Rows * cellSize, cellSize);
        await SetAreasAsync(buildingCells, OsmAreaKind.Building, isWalkable: true);
        var insideBuildings = buildingCells.ToMask();
        await SetAreasAsync(builder, OsmAreaKind.Building, isWalkable: false);
        foreach (var (_, (xs, ys)) in roads)
        {
            await scheduler.YieldAsync(cancellationToken);
            builder.SetCorridor(xs, ys, Math.Max(1.0, minimumHalfWidth), isWalkable: true, onlyWhere: insideBuildings);
        }
        var recoveryStreets = new MapScenarioBuilder(builder.Columns * cellSize, builder.Rows * cellSize, cellSize);
        foreach (var (road, (xs, ys)) in roads)
        {
            await scheduler.YieldAsync(cancellationToken);
            recoveryStreets.SetCorridor(xs, ys, Math.Max(HalfWidth(road.HighwayType), minimumHalfWidth));
        }
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

    public Task<List<BuildingObstacle>> GetBuildingsAsync(
        double minLat,
        double maxLat,
        double minLng,
        double maxLng,
        CancellationToken cancellationToken = default)
    {
        if (!_isLoaded) return Task.FromResult(new List<BuildingObstacle>());

        var areas = Query(_areaIndex, _areas, minLat, minLng, maxLat, maxLng);
        var result = new List<BuildingObstacle>(areas.Count);
        foreach (var a in areas)
        {
            if (a.Rings.Length == 0) continue;
            var outer = a.Rings[0].Select(n => new double[] { n.Lat, n.Lng }).ToList();
            List<List<double[]>>? inners = null;
            if (a.Rings.Length > 1)
            {
                inners = new List<List<double[]>>(a.Rings.Length - 1);
                for (int r = 1; r < a.Rings.Length; r++)
                {
                    inners.Add(a.Rings[r].Select(n => new double[] { n.Lat, n.Lng }).ToList());
                }
            }
            result.Add(new BuildingObstacle
            {
                Id = a.Kind == OsmAreaKind.Water ? $"osm_water_{a.Id}" : $"osm_bld_{a.Id}",
                BuildingType = a.Kind == OsmAreaKind.Water ? "water" : "building",
                MinLat = a.MinLat,
                MaxLat = a.MaxLat,
                MinLng = a.MinLng,
                MaxLng = a.MaxLng,
                Polygon = outer,
                InnerPolygons = inners
            });
        }
        return Task.FromResult(result);
    }

    public Task<List<List<double[]>>> GetPassagesAsync(
        double minLat,
        double maxLat,
        double minLng,
        double maxLng,
        CancellationToken cancellationToken = default)
    {
        if (!_isLoaded) return Task.FromResult(new List<List<double[]>>());

        var roads = Query(_roadIndex, _roads, minLat, minLng, maxLat, maxLng);
        var passages = roads
            .Where(r => r.Flags.HasFlag(OsmRoadFlags.Passage))
            .Select(r => r.Nodes.Select(n => new double[] { n.Lat, n.Lng }).ToList())
            .ToList();
        return Task.FromResult(passages);
    }

    private sealed class SegmentIndex
    {
        private readonly double _bucketSize;
        private readonly Dictionary<(int, int), List<(double Ax, double Ay, double Bx, double By, OsmLineKind Kind)>> _buckets = new();

        public SegmentIndex(double bucketSize)
        {
            _bucketSize = bucketSize;
        }

        public void Add(double ax, double ay, double bx, double by, OsmLineKind kind)
        {
            var segment = (ax, ay, bx, by, kind);
            foreach (var key in Keys(ax, ay, bx, by))
            {
                if (!_buckets.TryGetValue(key, out var list)) _buckets[key] = list = new List<(double, double, double, double, OsmLineKind)>();
                list.Add(segment);
            }
        }

        public IEnumerable<(double X, double Y, OsmLineKind Kind)> Crossings(double ax, double ay, double bx, double by)
        {
            var seen = new HashSet<(double, double, double, double)>();
            foreach (var key in Keys(ax, ay, bx, by))
            {
                if (!_buckets.TryGetValue(key, out var list)) continue;
                foreach (var s in list)
                {
                    if (!seen.Add((s.Ax, s.Ay, s.Bx, s.By))) continue;
                    if (Intersect(ax, ay, bx, by, s.Ax, s.Ay, s.Bx, s.By) is { } point) yield return (point.X, point.Y, s.Kind);
                }
            }
        }

        private IEnumerable<(int, int)> Keys(double ax, double ay, double bx, double by)
        {
            int minX = (int)Math.Floor(Math.Min(ax, bx) / _bucketSize), maxX = (int)Math.Floor(Math.Max(ax, bx) / _bucketSize);
            int minY = (int)Math.Floor(Math.Min(ay, by) / _bucketSize), maxY = (int)Math.Floor(Math.Max(ay, by) / _bucketSize);
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

public enum OsmAreaKind : byte { Building = 1, Water = 2 }

[Flags]
public enum OsmRoadFlags : byte { None = 0, Bridge = 1, Tunnel = 2, Passage = 4 }

public enum OsmLineKind : byte { Waterway = 1, Railway = 2, Barrier = 3 }

public sealed class OsmArea
{
    public long Id { get; init; }
    public OsmAreaKind Kind { get; init; }
    public double MinLat { get; set; }
    public double MaxLat { get; set; }
    public double MinLng { get; set; }
    public double MaxLng { get; set; }
    public (float Lat, float Lng)[][] Rings { get; init; } = Array.Empty<(float, float)[]>();
}

public sealed class OsmRoad
{
    public long Id { get; init; }
    public string HighwayType { get; init; } = "";
    public OsmRoadFlags Flags { get; init; }
    public (float Lat, float Lng)[] Nodes { get; init; } = Array.Empty<(float, float)>();
}

public sealed class OsmLine
{
    public long Id { get; init; }
    public OsmLineKind Kind { get; init; }
    public float HalfWidth { get; init; }
    public (float Lat, float Lng)[] Nodes { get; init; } = Array.Empty<(float, float)>();
}
