using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlanSafe.Api.Map;

public struct OsmNode
{
    public float Lat;
    public float Lng;
}

public enum OsmAreaKind : byte
{
    Building = 1,
    Water = 2
}

/// <summary>A building or water surface: one or more rings (outer and inner, filled even-odd).</summary>
public sealed class OsmArea
{
    public long Id;
    public OsmAreaKind Kind;
    public OsmNode[][] Rings = [];

    // Pre-computed bounding box for fast spatial filtering
    public float MinLat, MaxLat, MinLng, MaxLng;

    public void ComputeBbox()
    {
        MinLat = MinLng = float.MaxValue;
        MaxLat = MaxLng = float.MinValue;
        foreach (var ring in Rings)
        {
            foreach (var n in ring)
            {
                if (n.Lat < MinLat) MinLat = n.Lat;
                if (n.Lat > MaxLat) MaxLat = n.Lat;
                if (n.Lng < MinLng) MinLng = n.Lng;
                if (n.Lng > MaxLng) MaxLng = n.Lng;
            }
        }
    }

    public bool Contains(double lat, double lng)
    {
        if (lat < MinLat || lat > MaxLat || lng < MinLng || lng > MaxLng) return false;
        bool inside = false;
        foreach (var ring in Rings)
        {
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                double yi = ring[i].Lng, yj = ring[j].Lng, xi = ring[i].Lat, xj = ring[j].Lat;
                if ((yi > lng) != (yj > lng) && lat < (xj - xi) * (lng - yi) / (yj - yi) + xi) inside = !inside;
            }
        }
        return inside;
    }
}

[Flags]
public enum OsmRoadFlags : byte
{
    None = 0,
    Bridge = 1,
    /// <summary>Underground: tunnels and culverts do not open the ground above them.</summary>
    Tunnel = 2,
    /// <summary>A mapped passage through a building (tunnel=building_passage, covered ways).</summary>
    Passage = 4
}

/// <summary>A road / path polyline from OpenStreetMap.</summary>
public sealed class OsmRoad
{
    public long Id;
    public string HighwayType = "";
    public OsmRoadFlags Flags;
    public OsmNode[] Nodes = [];
}

public enum OsmLineKind : byte
{
    Waterway = 1,
    Railway = 2,
    Barrier = 3
}

/// <summary>A linear obstacle: stream, railway track, wall or fence, blocking HalfWidth meters around it.</summary>
public sealed class OsmLine
{
    public long Id;
    public OsmLineKind Kind;
    public float HalfWidth;
    public OsmNode[] Nodes = [];
}

/// <summary>
/// Kraków terrain from OpenStreetMap, loaded from a binary cache (krakow_osm.bin).
/// Pedestrians may walk on open ground; obstacles are buildings, water, railway tracks and walls/fences.
/// Roads keep their bridge, tunnel and passage flags so passages through buildings are open.
/// </summary>
public sealed class KrakowOsmService
{
    // Kraków city-centre bounding box (≈ 10 km × 9 km)
    public const double BboxSouth = 50.0200;
    public const double BboxWest = 19.8700;
    public const double BboxNorth = 50.1100;
    public const double BboxEast = 20.0200;

    // Spatial-index grid (≈ 100 m cells)
    private const double BucketDeg = 0.001;

    // Loaded geometry
    private OsmArea[] _areas = [];
    private OsmRoad[] _roads = [];
    private OsmLine[] _lines = [];
    private OsmNode[] _gates = [];

    private readonly Dictionary<(int, int), List<int>> _areaIndex = new();
    private readonly Dictionary<(int, int), List<int>> _roadIndex = new();
    private readonly Dictionary<(int, int), List<int>> _lineIndex = new();
    private readonly Dictionary<(int, int), List<int>> _gateIndex = new();

    private volatile bool _isLoaded;

    public bool IsLoaded => _isLoaded;
    public int BuildingCount => _areas.Count(a => a.Kind == OsmAreaKind.Building);
    public int RoadCount => _roads.Length;
    public bool HasUsableData => _areas.Any(a => a.Kind == OsmAreaKind.Building) && _roads.Length > 0;

    private static readonly byte[] Magic = "KOSMV3\0"u8.ToArray();

    public static KrakowOsmService? TryLoad(string binPath)
    {
        try
        {
            if (!File.Exists(binPath)) return null;
            var service = new KrakowOsmService();
            if (!service.LoadFromBinary(binPath) || !service.HasUsableData) return null;
            service.BuildSpatialIndex();
            service._isLoaded = true;
            return service;
        }
        catch
        {
            return null;
        }
    }

    private bool LoadFromBinary(string binPath)
    {
        using var fs = File.OpenRead(binPath);
        using var reader = new BinaryReader(fs);

        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.SequenceEqual(Magic)) return false;

        _areas = new OsmArea[reader.ReadInt32()];
        for (int i = 0; i < _areas.Length; i++)
        {
            var area = new OsmArea { Id = reader.ReadInt64(), Kind = (OsmAreaKind)reader.ReadByte() };
            area.Rings = new OsmNode[reader.ReadInt32()][];
            for (int r = 0; r < area.Rings.Length; r++) area.Rings[r] = ReadNodes(reader);
            area.ComputeBbox();
            _areas[i] = area;
        }

        _roads = new OsmRoad[reader.ReadInt32()];
        for (int i = 0; i < _roads.Length; i++)
            _roads[i] = new OsmRoad { Id = reader.ReadInt64(), HighwayType = reader.ReadString(), Flags = (OsmRoadFlags)reader.ReadByte(), Nodes = ReadNodes(reader) };

        _lines = new OsmLine[reader.ReadInt32()];
        for (int i = 0; i < _lines.Length; i++)
            _lines[i] = new OsmLine { Id = reader.ReadInt64(), Kind = (OsmLineKind)reader.ReadByte(), HalfWidth = reader.ReadSingle(), Nodes = ReadNodes(reader) };

        _gates = ReadNodes(reader);
        return true;
    }

    private static OsmNode[] ReadNodes(BinaryReader reader)
    {
        var nodes = new OsmNode[reader.ReadInt32()];
        for (int i = 0; i < nodes.Length; i++) nodes[i] = new OsmNode { Lat = reader.ReadSingle(), Lng = reader.ReadSingle() };
        return nodes;
    }

    private void BuildSpatialIndex()
    {
        _areaIndex.Clear();
        for (int i = 0; i < _areas.Length; i++)
        {
            var a = _areas[i];
            int minGx = BucketX(Math.Max(a.MinLng, BboxWest)), maxGx = BucketX(Math.Min(a.MaxLng, BboxEast));
            int minGy = BucketY(Math.Max(a.MinLat, BboxSouth)), maxGy = BucketY(Math.Min(a.MaxLat, BboxNorth));
            for (int gx = minGx; gx <= maxGx; gx++)
                for (int gy = minGy; gy <= maxGy; gy++)
                    IndexAdd(_areaIndex, (gx, gy), i);
        }

        IndexPolylines(_roadIndex, _roads.Select(r => r.Nodes));
        IndexPolylines(_lineIndex, _lines.Select(l => l.Nodes));

        _gateIndex.Clear();
        for (int i = 0; i < _gates.Length; i++) IndexAdd(_gateIndex, (BucketX(_gates[i].Lng), BucketY(_gates[i].Lat)), i);
    }

    private static void IndexPolylines(Dictionary<(int, int), List<int>> index, IEnumerable<OsmNode[]> polylines)
    {
        index.Clear();
        int i = 0;
        foreach (var nodes in polylines)
        {
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
        if (!idx.TryGetValue(key, out var list)) idx[key] = list = [];
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

    public IReadOnlyList<OsmArea> GetAreasInBbox(double south, double west, double north, double east) =>
        _isLoaded ? Query(_areaIndex, _areas, south, west, north, east) : [];

    public IReadOnlyList<OsmRoad> GetRoadsInBbox(double south, double west, double north, double east) =>
        _isLoaded ? Query(_roadIndex, _roads, south, west, north, east) : [];

    public IReadOnlyList<OsmLine> GetLinesInBbox(double south, double west, double north, double east) =>
        _isLoaded ? Query(_lineIndex, _lines, south, west, north, east) : [];

    public IReadOnlyList<OsmNode> GetGatesInBbox(double south, double west, double north, double east) =>
        _isLoaded ? Query(_gateIndex, _gates, south, west, north, east) : [];

    public OsmAreaKind? AreaAt(double lat, double lng)
    {
        if (!_isLoaded || !_areaIndex.TryGetValue((BucketX(lng), BucketY(lat)), out var candidates)) return null;
        OsmAreaKind? found = null;
        foreach (int idx in candidates)
        {
            if (!_areas[idx].Contains(lat, lng)) continue;
            if (_areas[idx].Kind == OsmAreaKind.Building) return OsmAreaKind.Building;
            found = OsmAreaKind.Water;
        }
        return found;
    }

    public bool IsPointInsideBuilding(double lat, double lng) => AreaAt(lat, lng) == OsmAreaKind.Building;
    public bool IsInsideKrakowBbox(double lat, double lng) =>
        lat >= BboxSouth && lat <= BboxNorth && lng >= BboxWest && lng <= BboxEast;
}
