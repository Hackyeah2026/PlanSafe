using PlanSafe.App.Services.Osm;
using PlanSafe.Contracts.Simulation;
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
}
