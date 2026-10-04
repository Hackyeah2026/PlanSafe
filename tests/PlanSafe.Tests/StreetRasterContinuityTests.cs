using PlanSafe.App.Services.Osm;
using PlanSafe.Contracts.Simulation;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class StreetRasterContinuityTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task ParallelFenceDoesNotCloseMappedStreetAtCityScale(double cellSize)
    {
        var osm = new OsmObstacleService(new HttpClient { BaseAddress = new Uri("http://localhost/") });
        const double south = 50.055, north = 50.077, west = 19.902, east = 19.945;
        const double metersLat = 111320;
        double metersLng = metersLat * Math.Cos((north + south) / 2 * Math.PI / 180);
        (double X, double Y) Project(double lat, double lng) =>
            ((lng - west) * metersLng, (north - lat) * metersLat);
        var builder = new MapScenarioBuilder((east - west) * metersLng, (north - south) * metersLat, cellSize);
        await osm.RasterizeTerrainAsync(builder, south, north, west, east, Project);

        // OSM residential way 182922095 runs beside fence 249019524. Inflating the
        // fence to 0.75 cells used to close these street-centre cells repeatedly.
        var start = Project(50.07264300755092, 19.912275586809432);
        var end = Project(50.072610271220306, 19.912416497055364);
        for (int i = 0; i <= 20; i++)
        {
            double x = start.X + (end.X - start.X) * i / 20;
            double y = start.Y + (end.Y - start.Y) * i / 20;
            Assert.True(builder.IsWalkable((int)(x / cellSize), (int)(y / cellSize)),
                $"Mapped street centre is blocked at ({x:F2}, {y:F2}).");
        }
    }

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
