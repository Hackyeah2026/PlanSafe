using System.Collections.Generic;
using PlanSafe.App.Services.Gus;
using PlanSafe.Contracts.Models.Gus;
using PlanSafe.Contracts.Models.Map;
using Xunit;

namespace PlanSafe.Tests;

public class GusCensusServiceTests
{
    private static GusGridCell CreateSampleCell(string id, int pop, double minLat, double minLng, double maxLat, double maxLng)
    {
        return new GusGridCell
        {
            Id = id,
            Pop = pop,
            MinLat = minLat,
            MinLng = minLng,
            MaxLat = maxLat,
            MaxLng = maxLng,
            Lat = (minLat + maxLat) / 2.0,
            Lng = (minLng + maxLng) / 2.0
        };
    }

    [Fact]
    public void SpatialMath_DistanceInMeters_ComputesCorrectDistance()
    {
        // Distance between Krakow Main Square and Wawel Castle is ~900m
        double krakowRynekLat = 50.0614, krakowRynekLng = 19.9366;
        double wawelLat = 50.0540, wawelLng = 19.9354;

        double dist = GeoSpatialMath.DistanceInMeters(krakowRynekLat, krakowRynekLng, wawelLat, wawelLng);

        Assert.InRange(dist, 800.0, 950.0);
    }

    [Fact]
    public void SpatialMath_IsPointInPolygon_AccuratelyTestsInclusion()
    {
        var polygon = new List<GeoCoordinate>
        {
            new(50.0, 20.0),
            new(50.0, 20.1),
            new(50.1, 20.1),
            new(50.1, 20.0)
        };

        Assert.True(GeoSpatialMath.IsPointInPolygon(50.05, 20.05, polygon));
        Assert.False(GeoSpatialMath.IsPointInPolygon(50.15, 20.05, polygon));
        Assert.False(GeoSpatialMath.IsPointInPolygon(49.95, 20.05, polygon));
    }

    [Fact]
    public void GusCensusService_CalculateZonePopulation_AreaWeightedCircle()
    {
        var service = new GusCensusService(null!);
        // Create a 125m cell at Krakow center with population 100
        var cell = CreateSampleCell("cell-1", 100, 50.0600, 19.9350, 50.0610, 19.9365);
        service.InitializeCells(new List<GusGridCell> { cell });

        // Circle zone that fully encompasses the cell
        var largeZone = new EvacCircleZoneItem
        {
            Id = "zone-large",
            Center = new[] { cell.Lat, cell.Lng },
            Radius = 500 // 500m radius completely encloses 125m cell
        };

        int popFull = service.CalculateZonePopulation(largeZone);
        Assert.Equal(100, popFull);

        // Far away zone: 0 overlap
        var farZone = new EvacCircleZoneItem
        {
            Id = "zone-far",
            Center = new[] { 50.1000, 20.0000 },
            Radius = 50
        };

        int popZero = service.CalculateZonePopulation(farZone);
        Assert.Equal(0, popZero);
    }

    [Fact]
    public void GusCensusService_CalculateEvacuationPopulation_PreventsDoubleCountingAcrossOverlappingZones()
    {
        var service = new GusCensusService(null!);
        var cell = CreateSampleCell("cell-1", 100, 50.0600, 19.9350, 50.0610, 19.9365);
        service.InitializeCells(new List<GusGridCell> { cell });

        // Two overlapping evacuation zones that both cover the exact same cell
        var zone1 = new EvacCircleZoneItem
        {
            Id = "zone-1",
            Center = new[] { cell.Lat, cell.Lng },
            Radius = 300
        };
        var zone2 = new EvacCircleZoneItem
        {
            Id = "zone-2",
            Center = new[] { cell.Lat, cell.Lng },
            Radius = 400
        };

        var (totalPop, affectedCells) = service.CalculateEvacuationPopulation(new[] { zone1, zone2 });

        // Even though both zones cover the cell, total population must remain 100 (1:1 union)
        Assert.Equal(100, totalPop);
        Assert.Equal(1, affectedCells);
    }
}
