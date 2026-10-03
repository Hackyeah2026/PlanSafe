using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Models.Map;

namespace PlanSafe.App.Services.Osm;

public interface IOsmObstacleService
{
    bool IsLoaded { get; }
    Task EnsureLoadedAsync(CancellationToken cancellationToken = default);
    Task<List<BuildingObstacle>> GetBuildingsAsync(
        double minLat,
        double maxLat,
        double minLng,
        double maxLng,
        CancellationToken cancellationToken = default);
    Task<List<List<double[]>>> GetPassagesAsync(
        double minLat,
        double maxLat,
        double minLng,
        double maxLng,
        CancellationToken cancellationToken = default);
    Task RasterizeTerrainAsync(
        MapScenarioBuilder builder,
        double minLat,
        double maxLat,
        double minLng,
        double maxLng,
        Func<double, double, (double X, double Y)> toWorld,
        CancellationToken cancellationToken = default);
}

