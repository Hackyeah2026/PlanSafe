using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PlanSafe.Contracts.Models.Gus;
using PlanSafe.Contracts.Models.Map;

namespace PlanSafe.App.Services.Gus;

public class GusCensusService : IGusCensusService
{
    private const double BucketSize = 0.02; // ~2km spatial hash bucket
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private List<GusGridCell> _cells = new();
    private Dictionary<(int X, int Y), List<GusGridCell>> _spatialGrid = new();
    private int _totalCensusPopulation = 0;
    private bool _isLoaded = false;

    public GusCensusService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public bool IsLoaded => _isLoaded;
    public int CellCount => _cells.Count;
    public int TotalCensusPopulation => _totalCensusPopulation;

    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded) return;

        await _loadLock.WaitAsync();
        try
        {
            if (_isLoaded) return;

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var cells = await _httpClient.GetFromJsonAsync<List<GusGridCell>>("data/gus_krakow_125m.json", options);

            if (cells != null)
            {
                InitializeCells(cells);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GusCensusService] Error loading GUS census data: {ex.Message}");
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public void InitializeCells(List<GusGridCell> cells)
    {
        _cells = cells ?? new List<GusGridCell>();
        _spatialGrid.Clear();
        _totalCensusPopulation = 0;

        foreach (var cell in _cells)
        {
            _totalCensusPopulation += cell.Pop;
            int bucketX = (int)Math.Floor(cell.Lng / BucketSize);
            int bucketY = (int)Math.Floor(cell.Lat / BucketSize);
            var key = (bucketX, bucketY);

            if (!_spatialGrid.TryGetValue(key, out var list))
            {
                list = new List<GusGridCell>();
                _spatialGrid[key] = list;
            }
            list.Add(cell);
        }

        _isLoaded = true;
    }

    public IReadOnlyList<GusGridCell> GetAllCells() => _cells;

    public IReadOnlyList<(GusGridCell Cell, double OverlapRatio)> GetIntersectingCells(MapZoneItem zone)
    {
        if (!_isLoaded || zone == null)
            return Array.Empty<(GusGridCell, double)>();

        if (!GeoSpatialMath.GetZoneBoundingBox(zone, out double zMinLat, out double zMinLng, out double zMaxLat, out double zMaxLng))
            return Array.Empty<(GusGridCell, double)>();

        var candidateCells = GetCandidateCellsInBBox(zMinLat, zMinLng, zMaxLat, zMaxLng);
        var results = new List<(GusGridCell, double)>();

        foreach (var cell in candidateCells)
        {
            if (!GeoSpatialMath.DoBoundingBoxesOverlap(cell.MinLat, cell.MinLng, cell.MaxLat, cell.MaxLng, zMinLat, zMinLng, zMaxLat, zMaxLng))
                continue;

            double ratio = GeoSpatialMath.CalculateCellOverlapRatio(cell, zone);
            if (ratio > 0.0)
            {
                results.Add((cell, ratio));
            }
        }

        return results;
    }

    public int CalculateZonePopulation(MapZoneItem zone)
    {
        var intersecting = GetIntersectingCells(zone);
        int total = 0;
        foreach (var (cell, ratio) in intersecting)
        {
            total += (int)Math.Round(cell.Pop * ratio);
        }
        return total;
    }

    public (int TotalPopulation, int AffectedCellsCount) CalculateEvacuationPopulation(IEnumerable<MapZoneItem> zones)
    {
        if (!_isLoaded || zones == null)
            return (0, 0);

        var evacZones = zones.Where(z => z.Category == ZoneCategory.EvacuationZone).ToList();
        if (evacZones.Count == 0)
            return (0, 0);

        // Find all unique candidate cells across all evacuation zones
        var cellCandidates = new HashSet<GusGridCell>();
        foreach (var zone in evacZones)
        {
            if (GeoSpatialMath.GetZoneBoundingBox(zone, out double zMinLat, out double zMinLng, out double zMaxLat, out double zMaxLng))
            {
                foreach (var cell in GetCandidateCellsInBBox(zMinLat, zMinLng, zMaxLat, zMaxLng))
                {
                    if (GeoSpatialMath.DoBoundingBoxesOverlap(cell.MinLat, cell.MinLng, cell.MaxLat, cell.MaxLng, zMinLat, zMinLng, zMaxLat, zMaxLng))
                    {
                        cellCandidates.Add(cell);
                    }
                }
            }
        }

        int totalPopulation = 0;
        int affectedCellsCount = 0;
        const int subGridSteps = 4;
        const int totalSubPoints = subGridSteps * subGridSteps;

        foreach (var cell in cellCandidates)
        {
            int insideCount = 0;
            double dLat = (cell.MaxLat - cell.MinLat) / subGridSteps;
            double dLng = (cell.MaxLng - cell.MinLng) / subGridSteps;

            for (int i = 0; i < subGridSteps; i++)
            {
                double sampleLat = cell.MinLat + (i + 0.5) * dLat;
                for (int j = 0; j < subGridSteps; j++)
                {
                    double sampleLng = cell.MinLng + (j + 0.5) * dLng;
                    // Union: inside if it falls in ANY of the evacuation zones
                    for (int z = 0; z < evacZones.Count; z++)
                    {
                        if (GeoSpatialMath.IsPointInZone(sampleLat, sampleLng, evacZones[z]))
                        {
                            insideCount++;
                            break;
                        }
                    }
                }
            }

            if (insideCount > 0)
            {
                affectedCellsCount++;
                double ratio = (double)insideCount / totalSubPoints;
                totalPopulation += (int)Math.Round(cell.Pop * ratio);
            }
        }

        return (totalPopulation, affectedCellsCount);
    }

    private HashSet<GusGridCell> GetCandidateCellsInBBox(double minLat, double minLng, double maxLat, double maxLng)
    {
        var candidates = new HashSet<GusGridCell>();
        int minBx = (int)Math.Floor(minLng / BucketSize);
        int maxBx = (int)Math.Floor(maxLng / BucketSize);
        int minBy = (int)Math.Floor(minLat / BucketSize);
        int maxBy = (int)Math.Floor(maxLat / BucketSize);

        for (int bx = minBx; bx <= maxBx; bx++)
        {
            for (int by = minBy; by <= maxBy; by++)
            {
                if (_spatialGrid.TryGetValue((bx, by), out var list))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        candidates.Add(list[i]);
                    }
                }
            }
        }

        return candidates;
    }
}
