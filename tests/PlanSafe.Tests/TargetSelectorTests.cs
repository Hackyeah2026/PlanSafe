using System;
using System.Collections.Generic;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;
using PlanSafe.Contracts.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public sealed class TargetSelectorTests
{
    [Fact]
    public void SelectGeoTarget_CalculatesHaversineDistance_AndPicksClosestWhenPureDistance()
    {
        // Citizen at Krakow Main Square
        double citizenLat = 50.0614;
        double citizenLng = 19.9366;

        // Shelter A close (Park Krakowski, ~800m)
        var shelterA = new EvacuationTarget("shelter-a", "Park Krakowski", 19.9266, 50.0684, 20, 20, 500, 0, true, 50.0684, 19.9266);
        // Shelter B farther (Nowa Huta, ~8km)
        var shelterB = new EvacuationTarget("shelter-b", "Nowa Huta", 20.0350, 50.0720, 20, 20, 500, 0, true, 50.0720, 20.0350);

        var result = TargetSelector.SelectGeoTarget(
            citizenLat: citizenLat,
            citizenLng: citizenLng,
            targets: new[] { shelterA, shelterB },
            weightDistance: 1.0,
            weightOccupancy: 0.0
        );

        Assert.NotNull(result.Target);
        Assert.Equal("shelter-a", result.Target.Id);
        Assert.True(result.Distance < 1500.0, "Expected distance to Park Krakowski to be < 1.5km");
        Assert.True(result.BearingDegrees >= 0.0 && result.BearingDegrees < 360.0);
    }

    [Fact]
    public void SelectGeoTarget_MultiCriteria_BalancesDistanceAndOccupancy()
    {
        double citizenLat = 50.0614;
        double citizenLng = 19.9366;

        // Shelter A is closer but 98% full
        var shelterA = new EvacuationTarget("shelter-close", "Bliski Przepełniony", 19.9380, 50.0630, 20, 20, 100, 98, true, 50.0630, 19.9380);
        // Shelter B is slightly farther but completely empty
        var shelterB = new EvacuationTarget("shelter-far", "Dalszy Pusty", 19.9250, 50.0670, 20, 20, 500, 5, true, 50.0670, 19.9250);

        // Heavy occupancy weight (w_dist = 0.1, w_occ = 0.9)
        var result = TargetSelector.SelectGeoTarget(
            citizenLat: citizenLat,
            citizenLng: citizenLng,
            targets: new[] { shelterA, shelterB },
            weightDistance: 0.1,
            weightOccupancy: 0.9
        );

        Assert.NotNull(result.Target);
        Assert.Equal("shelter-far", result.Target.Id);
    }

    [Fact]
    public void SelectGeoTarget_AppliesOverflowPenalty_WhenOtherSheltersHaveCapacity()
    {
        double citizenLat = 50.0614;
        double citizenLng = 19.9366;

        // Shelter A is closer but 100% full (capacity exceeded)
        var shelterA = new EvacuationTarget("shelter-full", "Schron Pełny", 19.9370, 50.0620, 20, 20, 100, 100, true, 50.0620, 19.9370);
        // Shelter B is farther but has available capacity
        var shelterB = new EvacuationTarget("shelter-open", "Schron Wolny", 19.9200, 50.0650, 20, 20, 100, 20, true, 50.0650, 19.9200);

        var result = TargetSelector.SelectGeoTarget(
            citizenLat: citizenLat,
            citizenLng: citizenLng,
            targets: new[] { shelterA, shelterB },
            weightDistance: 0.9,
            weightOccupancy: 0.1
        );

        Assert.NotNull(result.Target);
        Assert.Equal("shelter-open", result.Target.Id);
        Assert.True(result.CalculatedCost < TargetSelector.CapacityOverflowPenalty);
    }

    [Fact]
    public void SelectGeoTarget_BypassesOverflowPenalty_WhenAllSheltersAreFull()
    {
        double citizenLat = 50.0614;
        double citizenLng = 19.9366;

        // Both shelters are 100% full (catastrophic mass-evacuation event)
        var shelterClose = new EvacuationTarget("shelter-close", "Bliski Pełny", 19.9370, 50.0620, 20, 20, 100, 100, true, 50.0620, 19.9370);
        var shelterFar = new EvacuationTarget("shelter-far", "Daleki Pełny", 19.9100, 50.0700, 20, 20, 100, 100, true, 50.0700, 19.9100);

        var result = TargetSelector.SelectGeoTarget(
            citizenLat: citizenLat,
            citizenLng: citizenLng,
            targets: new[] { shelterClose, shelterFar },
            weightDistance: 1.0,
            weightOccupancy: 0.0
        );

        // Penalty is bypassed; citizen is routed to closest shelter
        Assert.NotNull(result.Target);
        Assert.Equal("shelter-close", result.Target.Id);
        Assert.True(result.CalculatedCost < TargetSelector.CapacityOverflowPenalty);
    }

    [Fact]
    public void SelectGeoTarget_DetectsRoadblock_AndAppliesPenalty()
    {
        double citizenLat = 50.0600;
        double citizenLng = 19.9300;

        // Shelter East
        var shelterEast = new EvacuationTarget("shelter-east", "Wschód", 19.9500, 50.0600, 20, 20, 500, 0, true, 50.0600, 19.9500);
        // Shelter West
        var shelterWest = new EvacuationTarget("shelter-west", "Zachód", 19.9100, 50.0600, 20, 20, 500, 0, true, 50.0600, 19.9100);

        // Roadblock intersecting the path to shelter-east (line from (50.055, 19.940) to (50.065, 19.940))
        var roadblocks = new List<IReadOnlyList<GeoCoordinate>>
        {
            new List<GeoCoordinate>
            {
                new GeoCoordinate(50.0550, 19.9400),
                new GeoCoordinate(50.0650, 19.9400)
            }
        };

        var result = TargetSelector.SelectGeoTarget(
            citizenLat: citizenLat,
            citizenLng: citizenLng,
            targets: new[] { shelterEast, shelterWest },
            weightDistance: 1.0,
            weightOccupancy: 0.0,
            roadblocks: roadblocks
        );

        Assert.NotNull(result.Target);
        // Path to East crosses the barrier; must divert to West
        Assert.Equal("shelter-west", result.Target.Id);
    }

    [Fact]
    public void SelectGeoTarget_AppliesHysteresis_SelfOccupancyDiscount()
    {
        double citizenLat = 50.0614;
        double citizenLng = 19.9366;

        // Shelter A capacity 10, current occupancy 10 (full, but citizen is already assigned here!)
        var shelterA = new EvacuationTarget("shelter-a", "Schron A", 19.9366, 50.0614, 20, 20, 10, 10, true, 50.0614, 19.9366);
        var shelterB = new EvacuationTarget("shelter-b", "Schron B", 19.9400, 50.0650, 20, 20, 10, 2, true, 50.0650, 19.9400);

        var result = TargetSelector.SelectGeoTarget(
            citizenLat: citizenLat,
            citizenLng: citizenLng,
            targets: new[] { shelterA, shelterB },
            weightDistance: 0.8,
            weightOccupancy: 0.2,
            currentTargetId: "shelter-a"
        );

        // Due to self-discount (10 - 1 = 9 < 10), shelter A is not treated as full for this citizen!
        Assert.NotNull(result.Target);
        Assert.Equal("shelter-a", result.Target.Id);
    }

    [Fact]
    public void SelectGeoTarget_AppliesHysteresis_15PercentSwitchingBarrier()
    {
        double citizenLat = 50.0600;
        double citizenLng = 19.9300;

        // Current shelter A: cost ~ 0.50
        var shelterA = new EvacuationTarget("shelter-curr", "Aktualny Schron", 19.9300, 50.0650, 20, 20, 100, 20, true, 50.0650, 19.9300);
        // Alternative shelter B: only slightly better (5% cheaper, does not exceed 15% threshold)
        var shelterB = new EvacuationTarget("shelter-alt", "Alternatywa", 19.9300, 50.0645, 20, 20, 100, 18, true, 50.0645, 19.9300);

        var result = TargetSelector.SelectGeoTarget(
            citizenLat: citizenLat,
            citizenLng: citizenLng,
            targets: new[] { shelterA, shelterB },
            weightDistance: 0.5,
            weightOccupancy: 0.5,
            currentTargetId: "shelter-curr"
        );

        // Anti-flapping barrier prevents jittery route switching
        Assert.NotNull(result.Target);
        Assert.Equal("shelter-curr", result.Target.Id);
    }

    [Fact]
    public void SelectGeoTarget_BypassesHysteresis_WhenIgnoreHysteresisIsTrue()
    {
        double citizenLat = 50.0600;
        double citizenLng = 19.9300;

        var shelterA = new EvacuationTarget("shelter-curr", "Aktualny Schron", 19.9300, 50.0650, 20, 20, 100, 20, true, 50.0650, 19.9300);
        var shelterB = new EvacuationTarget("shelter-alt", "Alternatywa", 19.9300, 50.0645, 20, 20, 100, 18, true, 50.0645, 19.9300);

        var result = TargetSelector.SelectGeoTarget(
            citizenLat: citizenLat,
            citizenLng: citizenLng,
            targets: new[] { shelterA, shelterB },
            weightDistance: 0.5,
            weightOccupancy: 0.5,
            currentTargetId: "shelter-curr",
            ignoreHysteresis: true // Manual relocation bypass
        );

        // With ignoreHysteresis true, even marginally cheaper alternative is immediately accepted
        Assert.NotNull(result.Target);
        Assert.Equal("shelter-alt", result.Target.Id);
    }

    [Fact]
    public void SelectGeoTarget_Geofence_ReturnsOutsideZoneWhenCitizenOutsidePolygon()
    {
        // Citizen far away (Warsaw: ~52.2, 21.0)
        double citizenLat = 52.2297;
        double citizenLng = 21.0122;

        var shelter = new EvacuationTarget("shelter-krk", "Krakow Shelter", 19.9366, 50.0614, 20, 20, 500, 0, true, 50.0614, 19.9366);

        // Krakow Evacuation Zone bounding polygon
        var evacZone = new List<IReadOnlyList<GeoCoordinate>>
        {
            new List<GeoCoordinate>
            {
                new GeoCoordinate(50.0500, 19.9200),
                new GeoCoordinate(50.0700, 19.9200),
                new GeoCoordinate(50.0700, 19.9500),
                new GeoCoordinate(50.0500, 19.9500)
            }
        };

        var result = TargetSelector.SelectGeoTarget(
            citizenLat: citizenLat,
            citizenLng: citizenLng,
            targets: new[] { shelter },
            evacuationZones: evacZone
        );

        Assert.True(result.IsOutsideZone);
        Assert.Null(result.Target);
        Assert.Contains("outside the designated evacuation zone", result.Instructions);
    }

    [Fact]
    public void SelectGeoTarget_NonStrictGeofence_AssignsTargetAndRouteWhenCitizenOutsidePolygon()
    {
        // Citizen in Warsaw (far outside Krakow zone)
        double citizenLat = 52.2297;
        double citizenLng = 21.0122;

        var shelter = new EvacuationTarget("shelter-krk", "Krakow Shelter", 19.9366, 50.0614, 20, 20, 500, 0, true, 50.0614, 19.9366);

        var evacZone = new List<IReadOnlyList<GeoCoordinate>>
        {
            new List<GeoCoordinate>
            {
                new GeoCoordinate(50.0500, 19.9200),
                new GeoCoordinate(50.0700, 19.9200),
                new GeoCoordinate(50.0700, 19.9500),
                new GeoCoordinate(50.0500, 19.9500)
            }
        };

        var result = TargetSelector.SelectGeoTarget(
            citizenLat: citizenLat,
            citizenLng: citizenLng,
            targets: new[] { shelter },
            evacuationZones: evacZone,
            strictGeofence: false
        );

        Assert.True(result.IsOutsideZone);
        Assert.NotNull(result.Target);
        Assert.Equal("shelter-krk", result.Target.Id);
        Assert.NotNull(result.RoutePath);
        Assert.True(result.RoutePath.Count >= 2);
    }

    [Fact]
    public void SelectGeoTarget_Geofence_AssignsTargetWhenCitizenInsidePolygon()
    {
        // Citizen inside Krakow zone
        double citizenLat = 50.0614;
        double citizenLng = 19.9366;

        var shelter = new EvacuationTarget("shelter-krk", "Krakow Shelter", 19.9366, 50.0614, 20, 20, 500, 0, true, 50.0614, 19.9366);

        var evacZone = new List<IReadOnlyList<GeoCoordinate>>
        {
            new List<GeoCoordinate>
            {
                new GeoCoordinate(50.0500, 19.9200),
                new GeoCoordinate(50.0700, 19.9200),
                new GeoCoordinate(50.0700, 19.9500),
                new GeoCoordinate(50.0500, 19.9500)
            }
        };

        var result = TargetSelector.SelectGeoTarget(
            citizenLat: citizenLat,
            citizenLng: citizenLng,
            targets: new[] { shelter },
            evacuationZones: evacZone
        );

        Assert.False(result.IsOutsideZone);
        Assert.NotNull(result.Target);
        Assert.Equal("shelter-krk", result.Target.Id);
    }

    [Fact]
    public void GeoMath_PointInPolygon_RayCasting_AccuratelyTestsGeometry()
    {
        var polygon = new List<GeoCoordinate>
        {
            new GeoCoordinate(10.0, 10.0),
            new GeoCoordinate(20.0, 10.0),
            new GeoCoordinate(20.0, 20.0),
            new GeoCoordinate(10.0, 20.0)
        };

        Assert.True(GeoMath.IsPointInPolygon(15.0, 15.0, polygon));
        Assert.False(GeoMath.IsPointInPolygon(5.0, 5.0, polygon));
        Assert.False(GeoMath.IsPointInPolygon(25.0, 25.0, polygon));
    }
}
