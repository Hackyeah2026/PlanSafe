using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PlanSafe.App.Services.Gus;
using PlanSafe.Contracts.Models.Gus;
using PlanSafe.Contracts.Models.Map;
using Xunit;

namespace PlanSafe.Tests;

public class GusOccupantGeneratorTests
{
    [Fact]
    public async Task GenerateOccupants_StrictCount_MatchesCensusPopulation()
    {
        var service = new GusCensusService(null!);
        var cell = new GusGridCell
        {
            Id = "cell-101",
            Pop = 42,
            MinLat = 50.0600,
            MinLng = 19.9350,
            MaxLat = 50.0610,
            MaxLng = 19.9365,
            Lat = 50.0605,
            Lng = 19.93575
        };
        service.InitializeCells(new List<GusGridCell> { cell });

        var generator = new GusOccupantGenerator(service);

        // Zone covering the entire cell
        var zone = new EvacCircleZoneItem
        {
            Id = "evac-1",
            Center = new[] { cell.Lat, cell.Lng },
            Radius = 500
        };

        var occupants = generator.GenerateOccupants(new[] { zone }, randomSeed: 12345);
        var batched = await generator.GenerateOccupantsAsync(new[] { zone }, randomSeed: 12345);
        Assert.Equal(occupants.Select(a => (a.Latitude, a.Longitude, a.CellId, a.EvacZoneId)),
            batched.Select(a => (a.Latitude, a.Longitude, a.CellId, a.EvacZoneId)));

        // Strict 1:1 census count
        Assert.Equal(42, occupants.Count);

        // Every agent must have valid metadata
        foreach (var occupant in occupants)
        {
            Assert.StartsWith("occ-", occupant.Id);
            Assert.Equal("cell-101", occupant.CellId);
            Assert.Equal("evac-1", occupant.EvacZoneId);
        }
    }

    [Fact]
    public void GenerateOccupants_AllAgents_PlacedStrictlyInsideZoneBoundary()
    {
        var service = new GusCensusService(null!);
        var cell = new GusGridCell
        {
            Id = "cell-poly",
            Pop = 50,
            MinLat = 50.0600,
            MinLng = 19.9350,
            MaxLat = 50.0610,
            MaxLng = 19.9365,
            Lat = 50.0605,
            Lng = 19.93575
        };
        service.InitializeCells(new List<GusGridCell> { cell });

        var generator = new GusOccupantGenerator(service);

        // Evacuation polygon zone
        var zone = new EvacPolygonZoneItem
        {
            Id = "poly-zone",
            Coordinates = new List<double[]>
            {
                new[] { 50.0590, 19.9340 },
                new[] { 50.0620, 19.9340 },
                new[] { 50.0620, 19.9380 },
                new[] { 50.0590, 19.9380 }
            }
        };

        var occupants = generator.GenerateOccupants(new[] { zone }, randomSeed: 42);

        Assert.NotEmpty(occupants);
        foreach (var agent in occupants)
        {
            bool isInside = GeoSpatialMath.IsPointInZone(agent.Latitude, agent.Longitude, zone);
            Assert.True(isInside, $"Agent {agent.Id} at ({agent.Latitude}, {agent.Longitude}) is outside the evacuation zone!");
        }
    }

    [Fact]
    public void GenerateOccupants_IgnoresSafeZonesAndBlockades()
    {
        var service = new GusCensusService(null!);
        var cell = new GusGridCell
        {
            Id = "cell-1",
            Pop = 25,
            MinLat = 50.0600,
            MinLng = 19.9350,
            MaxLat = 50.0610,
            MaxLng = 19.9365,
            Lat = 50.0605,
            Lng = 19.93575
        };
        service.InitializeCells(new List<GusGridCell> { cell });

        var generator = new GusOccupantGenerator(service);

        var safeZone = new SafeCircleZoneItem
        {
            Id = "safe-1",
            Center = new[] { cell.Lat, cell.Lng },
            Radius = 500
        };

        // Only evacuation zones generate evacuee occupants
        var occupants = generator.GenerateOccupants(new[] { safeZone });
        Assert.Empty(occupants);
    }
}
