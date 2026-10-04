using PlanSafe.Contracts.Models.Simulation;
using PlanSafe.App.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class SafeZoneCapacityTests
{
    [Fact]
    public void OpenSafeZoneCountsPeopleWithoutBecomingFull()
    {
        var zone = new EvacuationTarget("safe", "Safe zone", 10, 10, 5, 5, 0, 10000);
        Assert.Equal(10000, zone.CurrentOccupancy);
        Assert.False(zone.IsFull);
        Assert.Null(zone.AvailableCapacity);
        Assert.Equal(0, zone.OccupancyRatio);
        var farther = new EvacuationTarget("far", "Far zone", 80, 80, 5, 5, 0, 0);
        var result = TargetSelector.SelectTarget(0, 0, new[] { zone, farther }, 1.0, 0.0);
        Assert.Equal(zone.Id, result.Target!.Id);
        Assert.DoesNotContain("%", result.Instructions);
    }

    [Fact]
    public void SimulationDefaultsHaveNoCapacityLimit()
    {
        var engine = new CrowdSimulationEngine(100, 100, 1001);
        engine.SetMultiTargetEnabled(true);
        Assert.Equal(2, engine.Targets.Count);
        Assert.All(engine.Targets, target => Assert.False(target.HasCapacityLimit));
        engine.UpdateTargetOccupancy(engine.Targets[0].Id, 10000);
        Assert.Equal(10000, engine.Targets[0].CurrentOccupancy);
        Assert.False(engine.Targets[0].IsFull);
    }

    [Fact]
    public void GeographicRoutingBalancesOpenZonesWithoutMarkingThemFull()
    {
        var near = new EvacuationTarget("near", "Near", 19.94, 50.06, 5, 5, 0, 10000);
        var far = new EvacuationTarget("far", "Far", 19.96, 50.08, 5, 5, 0, 0);
        var result = PlanSafe.Contracts.Simulation.TargetSelector.SelectGeoTarget(
            50.06, 19.94, new[] { near, far }, 0.0, 1.0);
        Assert.Equal(far.Id, result.Target!.Id);
        Assert.True(result.TargetEvaluations!.Single(e => e.TargetId == near.Id).OccupancyCost > 0);
        Assert.All(result.TargetEvaluations!, evaluation =>
        {
            Assert.False(evaluation.IsFull);

        });
    }

    [Fact]
    public void LimitedShelterStillReportsFull()
    {
        var shelter = new EvacuationTarget("shelter", "Shelter", 0, 0, 5, 5, 100, 100);
        Assert.True(shelter.IsFull);
        Assert.Equal(0, shelter.AvailableCapacity);
    }
}
