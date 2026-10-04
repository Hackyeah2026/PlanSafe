using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.IO;
using PlanSafe.Api.Map;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;
using PlanSafe.Contracts.Simulation;

namespace PlanSafe.Api.Services;

/// <summary>
/// Concurrency-safe in-memory state service managing evacuation plan sessions,
/// shelter capacities, real-time check-ins, and 1-hour inactivity expiration.
/// </summary>
public sealed class EvacuationStateService : IEvacuationStateService
{
    private readonly ConcurrentDictionary<string, EvacuationTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ObstacleDto> _obstacles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<List<GeoCoordinate>> _roadblocks = new();
    private readonly List<List<GeoCoordinate>> _evacuationZones = new();
    private readonly List<List<GeoCoordinate>> _safeZones = new();
    private readonly object _stateLock = new();

    private readonly KrakowOsmService? _osmService;
    private readonly MapScenarioFactory? _scenarioFactory;
    private MapPathfinder? _currentMapPathfinder;

    private string? _sessionId;
    private DateTime _createdAtUtc = DateTime.UtcNow;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private DateTime? _expiresAtUtc;
    private readonly TimeSpan _inactivityTimeout = TimeSpan.FromHours(1);

    private double _weightDistance = 0.5;
    private double _weightOccupancy = 0.5;
    private double? _mapCenterLat = 50.0614;
    private double? _mapCenterLng = 19.9366;
    private int? _zoomLevel = 14;

    private readonly Timer? _cleanupTimer;
    private bool _disposed;

    public EvacuationStateService()
    {
        InitializeDefaultEnvironment();

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string binPath = Path.Combine(baseDir, "Data", "OSM", "krakow_osm.bin");
        if (!File.Exists(binPath))
        {
            binPath = Path.Combine(Directory.GetCurrentDirectory(), "Data", "OSM", "krakow_osm.bin");
        }
        if (!File.Exists(binPath))
        {
            binPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "osm", "krakow_osm.bin");
        }

        _osmService = KrakowOsmService.TryLoad(binPath);
        if (_osmService != null)
        {
            _scenarioFactory = new MapScenarioFactory(_osmService);
            RebuildMapPathfinder();
        }

        _cleanupTimer = new Timer(_ => CleanIfInactive(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void RebuildMapPathfinder(GeoCoordinate? citizenLoc = null)
    {
        if (_scenarioFactory == null) return;
        try
        {
            var targets = _targets.Values.ToList();
            if (targets.Count == 0) return;
            var scenario = _scenarioFactory.BuildForTargets(targets, _roadblocks, _evacuationZones, citizenLoc);
            if (scenario != null)
            {
                _currentMapPathfinder = new MapPathfinder(scenario);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EvacuationStateService] RebuildMapPathfinder error: {ex.Message}");
        }
    }

    public string? CurrentSessionId
    {
        get { lock (_stateLock) return _sessionId; }
    }

    public DateTime LastActivityUtc
    {
        get { lock (_stateLock) return _lastActivityUtc; }
    }

    public DateTime? ExpiresAtUtc
    {
        get { lock (_stateLock) return _expiresAtUtc; }
    }

    private void Touch()
    {
        lock (_stateLock)
        {
            _lastActivityUtc = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(_sessionId))
            {
                _expiresAtUtc = _lastActivityUtc.Add(_inactivityTimeout);
            }
        }
    }

    public PublishPlanResponse PublishPlan(PublishPlanRequest request)
    {
        Touch();
        lock (_stateLock)
        {
            _sessionId = Guid.NewGuid().ToString("N")[..8];
            _createdAtUtc = DateTime.UtcNow;
            _expiresAtUtc = _createdAtUtc.Add(_inactivityTimeout);

            _targets.Clear();
            if (request.Targets != null)
            {
                foreach (var t in request.Targets)
                {
                    _targets[t.Id] = t;
                }
            }

            _obstacles.Clear();
            if (request.Obstacles != null)
            {
                foreach (var o in request.Obstacles)
                {
                    string id = o.Id ?? Guid.NewGuid().ToString("N");
                    _obstacles[id] = o with { Id = id };
                }
            }

            _roadblocks.Clear();
            if (request.Roadblocks != null)
            {
                foreach (var rb in request.Roadblocks)
                {
                    if (rb != null && rb.Count >= 2)
                    {
                        _roadblocks.Add(rb.ToList());
                    }
                }
            }

            _evacuationZones.Clear();
            if (request.EvacuationZones != null)
            {
                foreach (var ez in request.EvacuationZones)
                {
                    if (ez != null && ez.Count >= 3)
                    {
                        _evacuationZones.Add(ez.ToList());
                    }
                }
            }

            _safeZones.Clear();
            if (request.SafeZones != null)
            {
                foreach (var sz in request.SafeZones)
                {
                    if (sz != null && sz.Count >= 3)
                    {
                        _safeZones.Add(sz.ToList());
                    }
                }
            }

            _weightDistance = Math.Clamp(request.WeightDistance, 0.0, 1.0);
            _weightOccupancy = Math.Clamp(request.WeightOccupancy, 0.0, 1.0);
            _mapCenterLat = request.MapCenterLat ?? 50.0614;
            _mapCenterLng = request.MapCenterLng ?? 19.9366;
            _zoomLevel = request.ZoomLevel ?? 14;

            string baseUrl = request.BaseUrl?.TrimEnd('/') ?? "http://127.0.0.1:5171";
            string publicEvacuateUrl = $"{baseUrl}/evacuate?session={_sessionId}&wdist={_weightDistance.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}&wocc={_weightOccupancy.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}";
            string qrSvg = QrCodeSvgGenerator.GenerateSvg(publicEvacuateUrl);

            RebuildMapPathfinder();

            return new PublishPlanResponse(
                SessionId: _sessionId,
                EvacuateUrl: publicEvacuateUrl,
                QrCodeSvg: qrSvg,
                CreatedAtUtc: _createdAtUtc,
                ExpiresAtUtc: _expiresAtUtc
            );
        }
    }

    public TargetAssignmentResponse AssignTarget(TargetAssignmentRequest request)
    {
        Touch();

        IReadOnlyList<EvacuationTarget> candidateTargets;
        IReadOnlyList<List<GeoCoordinate>> roadblocks;
        IReadOnlyList<List<GeoCoordinate>> evacuationZones;
        double wDist;
        double wOcc;

        lock (_stateLock)
        {
            candidateTargets = request.CustomTargets ?? _targets.Values.ToList();
            roadblocks = _roadblocks.ToList();
            evacuationZones = _evacuationZones.ToList();
            wDist = request.WeightDistance ?? _weightDistance;
            wOcc = request.WeightOccupancy ?? _weightOccupancy;
        }

        // Real-world GIS (WGS84 Lat/Lng) Mode
        if (request.Latitude.HasValue && request.Longitude.HasValue)
        {
            double reqLat = request.Latitude.Value;
            double reqLng = request.Longitude.Value;

            // If OSM is available and coordinates are in Krakow area, evaluate via street raster pathfinder
            if (_osmService != null && _osmService.IsInsideKrakowBbox(reqLat, reqLng))
            {
                lock (_stateLock)
                {
                    if (_currentMapPathfinder == null)
                    {
                        RebuildMapPathfinder(new GeoCoordinate(reqLat, reqLng));
                    }
                    else
                    {
                        var (px, py) = _currentMapPathfinder.Scenario.ToWorld(reqLat, reqLng);
                        if (px < 40 || px > _currentMapPathfinder.Scenario.WorldWidth - 40 ||
                            py < 40 || py > _currentMapPathfinder.Scenario.WorldHeight - 40)
                        {
                            RebuildMapPathfinder(new GeoCoordinate(reqLat, reqLng));
                        }
                    }
                }

                if (_currentMapPathfinder != null)
                {
                    try
                    {
                        var pathfinderAssignment = _currentMapPathfinder.EvaluateAssignment(
                            citizenLat: reqLat,
                            citizenLng: reqLng,
                            candidateTargets: candidateTargets,
                            weightDistance: wDist,
                            weightOccupancy: wOcc,
                            currentTargetId: request.CurrentTargetId,
                            ignoreHysteresis: request.IgnoreHysteresis
                        );

                        if (pathfinderAssignment?.Target != null && pathfinderAssignment.RoutePath != null && pathfinderAssignment.RoutePath.Count >= 2)
                        {
                            bool outside = evacuationZones.Count > 0 &&
                                           !evacuationZones.Any(z => z.Count >= 3 && GeoMath.IsPointInPolygon(reqLat, reqLng, z));

                            return pathfinderAssignment with { IsOutsideZone = outside };
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[EvacuationStateService] MapPathfinder evaluation error: {ex.Message}");
                    }
                }
            }

            return TargetSelector.SelectGeoTarget(
                citizenLat: reqLat,
                citizenLng: reqLng,
                targets: candidateTargets,
                weightDistance: wDist,
                weightOccupancy: wOcc,
                maxDistanceMeters: request.MaxDistance,
                roadblocks: roadblocks,
                evacuationZones: evacuationZones,
                currentTargetId: request.CurrentTargetId,
                ignoreHysteresis: request.IgnoreHysteresis,
                strictGeofence: request.StrictGeofence
            );
        }

        // 2D Cartesian Mode
        IReadOnlyList<ObstacleDto> obstacles;
        lock (_stateLock)
        {
            obstacles = _obstacles.Values.ToList();
        }

        return TargetSelector.SelectTarget(
            px: request.X,
            py: request.Y,
            targets: candidateTargets,
            weightDistance: wDist,
            weightOccupancy: wOcc,
            maxDistance: request.MaxDistance,
            obstacles: obstacles,
            currentTargetId: request.CurrentTargetId,
            ignoreHysteresis: request.IgnoreHysteresis
        );
    }

    public CheckInResponse CheckIn(CheckInRequest request)
    {
        Touch();
        if (string.IsNullOrWhiteSpace(request.TargetId))
        {
            return new CheckInResponse(false, "Shelter ID is required.");
        }

        if (_targets.TryGetValue(request.TargetId, out var existing))
        {
            int updatedOccupancy = Math.Max(0, existing.CurrentOccupancy + request.Count);
            var updated = existing with { CurrentOccupancy = updatedOccupancy };
            _targets[request.TargetId] = updated;

            return new CheckInResponse(
                Success: true,
                Message: updated.HasCapacityLimit
                    ? $"Occupancy updated: {updated.CurrentOccupancy}/{updated.Capacity}"
                    : $"Occupancy updated: {updated.CurrentOccupancy}",
                Target: updated
            );
        }

        return new CheckInResponse(false, $"Evacuation point '{request.TargetId}' was not found.");
    }

    public IReadOnlyList<EvacuationTarget> GetTargets()
    {
        Touch();
        return _targets.Values.OrderBy(t => t.Name).ToList();
    }

    public EvacuationPlanConfig? GetConfig()
    {
        Touch();
        lock (_stateLock)
        {
            return new EvacuationPlanConfig(
                SessionId: _sessionId ?? "default",
                Targets: _targets.Values.OrderBy(t => t.Name).ToList(),
                Obstacles: _obstacles.Values.ToList(),
                Roadblocks: _roadblocks.ToList(),
                EvacuationZones: _evacuationZones.ToList(),
                WeightDistance: _weightDistance,
                WeightOccupancy: _weightOccupancy,
                MapCenterLat: _mapCenterLat,
                MapCenterLng: _mapCenterLng,
                ZoomLevel: _zoomLevel,
                CreatedAtUtc: _createdAtUtc,
                ExpiresAtUtc: _expiresAtUtc,
                SafeZones: _safeZones.ToList()
            );
        }
    }

    public IReadOnlyList<ObstacleDto> GetObstacles()
    {
        Touch();
        return _obstacles.Values.ToList();
    }

    public bool UpdateTargetOccupancy(string targetId, int occupancy)
    {
        Touch();
        if (_targets.TryGetValue(targetId, out var existing))
        {
            _targets[targetId] = existing with { CurrentOccupancy = Math.Max(0, occupancy) };
            return true;
        }
        return false;
    }

    public void Reset()
    {
        lock (_stateLock)
        {
            _sessionId = null;
            _createdAtUtc = DateTime.UtcNow;
            _lastActivityUtc = DateTime.UtcNow;
            _expiresAtUtc = null;
            InitializeDefaultEnvironment();
        }
    }

    public bool CleanIfInactive()
    {
        lock (_stateLock)
        {
            if (string.IsNullOrEmpty(_sessionId)) return false;

            if (DateTime.UtcNow - _lastActivityUtc >= _inactivityTimeout)
            {
                _sessionId = null;
                _expiresAtUtc = null;
                InitializeDefaultEnvironment();
                return true;
            }
            return false;
        }
    }

    private void InitializeDefaultEnvironment()
    {
        _targets.Clear();
        _obstacles.Clear();
        _roadblocks.Clear();
        _evacuationZones.Clear();
        _safeZones.Clear();

        // Baseline Kraków gathering points
        var shelter1 = new EvacuationTarget(
            Id: "shelter-krakow-1",
            Name: "North Assembly Point (Krakowski Park)",
            X: 19.9266,
            Y: 50.0684,
            Width: 20.0,
            Height: 20.0,
            Capacity: 500,
            CurrentOccupancy: 45,
            IsActive: true,
            Latitude: 50.0684,
            Longitude: 19.9266
        );

        var shelter2 = new EvacuationTarget(
            Id: "shelter-krakow-2",
            Name: "South Assembly Point (Planty Park)",
            X: 19.9380,
            Y: 50.0570,
            Width: 20.0,
            Height: 20.0,
            Capacity: 400,
            CurrentOccupancy: 20,
            IsActive: true,
            Latitude: 50.0570,
            Longitude: 19.9380
        );

        _targets[shelter1.Id] = shelter1;
        _targets[shelter2.Id] = shelter2;

        _weightDistance = 0.5;
        _weightOccupancy = 0.5;
        _mapCenterLat = 50.0614;
        _mapCenterLng = 19.9366;
        _zoomLevel = 14;

        RebuildMapPathfinder();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer?.Dispose();
    }
}
