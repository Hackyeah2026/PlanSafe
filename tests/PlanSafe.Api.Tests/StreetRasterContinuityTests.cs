using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;
using Xunit;

namespace PlanSafe.Api.Tests;

public class StreetRasterContinuityTests
{
    [Fact]
    public void ServerStreetRasterKeepsStreetOpenAndStillHonorsExplicitRoadblocks()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? osmPath = null;
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "data", "osm", "krakow_osm.bin");
            if (File.Exists(candidate)) { osmPath = candidate; break; }
            directory = directory.Parent;
        }
        Assert.NotNull(osmPath);
        var osm = PlanSafe.Api.Map.KrakowOsmService.TryLoad(osmPath);
        Assert.NotNull(osm);
        var factory = new PlanSafe.Api.Map.MapScenarioFactory(osm);
        EvacuationTarget[] targets = [new("exit", "Exit", 19.9335, 50.0578, 10, 10, 1000, 0, true, 50.0578, 19.9335)];
        List<List<GeoCoordinate>> zones = [[new(50.055, 19.902), new(50.077, 19.945), new(50.055, 19.945)]];
        var scenario = factory.BuildForTargets(targets, zones: zones);
        Assert.NotNull(scenario);
        var point = scenario.ToWorld(50.07263, 19.91233);
        Assert.False(scenario.IsBlockedAt(point.X, point.Y));

        List<List<GeoCoordinate>> roadblocks = [[new(50.07273, 19.91233), new(50.07253, 19.91233)]];
        var closed = factory.BuildForTargets(targets, roadblocks: roadblocks, zones: zones);
        Assert.NotNull(closed);
        point = closed.ToWorld(50.07263, 19.91233);
        Assert.True(closed.IsBlockedAt(point.X, point.Y));
    }
}
