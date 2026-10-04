using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Simulation;
using Xunit;
using MapScenario = PlanSafe.Contracts.Simulation.MapScenario;

namespace PlanSafe.Tests;

public class MapSafeZoneRoutingTests
{
    private static MapScenario Scenario(bool disconnected = false, bool extraTiles = false)
    {
        var exits = new List<MapExit> { new(10, 30, 2, TargetId: "near"), new(90, 30, 2, TargetId: "far") };
        if (extraTiles) exits.AddRange([new(90, 28, 2, TargetId: "far"), new(90, 32, 2, TargetId: "far")]);
        var blocked = new bool[100 * 60];
        if (disconnected) for (int row = 0; row < 60; row++) blocked[row * 100 + 50] = true;
        return new MapScenario(50, 20, 111320, 71555, 1, 100, 60, blocked, exits.ToArray(),
            [new MapSpawnZone([2, 45, 45, 2], [2, 2, 58, 58], 100)]);
    }

    private static Task<CrowdSimulationEngine> Engine(MapScenario scenario, int count = 100) =>
        CrowdSimulationEngine.CreateMapAsync(scenario,
            Enumerable.Range(0, count).Select(index => (20.0, 5.0 + index % 45)).ToArray(), seed: 42);

    [Fact]
    public async Task OccupancyOnlySplitsPopulationEquallyByZoneNotByTile()
    {
        var engine = await Engine(Scenario(extraTiles: true));
        await engine.ConfigureMapRoutingAsync(0, 1);
        Assert.Equal(2, engine.MapRouting!.DistanceFields.Length);
        Assert.Equal(50, engine.AgentTargetIndex[..100].Count(target => target == 0));
        Assert.Equal(50, engine.AgentTargetIndex[..100].Count(target => target == 1));
        engine.Reset();
        Assert.Equal(50, engine.AgentTargetIndex[..100].Count(target => target == 1));
    }

    [Fact]
    public async Task IncreasingOccupancyWeightGraduallyIncreasesDispatchToTheFarZone()
    {
        var engine = await Engine(Scenario());
        var farCounts = new List<int>();
        foreach (double occupancyWeight in new[] { 0.1, 0.5, 0.8, 1.0 })
        {
            await engine.ConfigureMapRoutingAsync(1 - occupancyWeight, occupancyWeight);
            farCounts.Add(engine.AgentTargetIndex[..100].Count(target => target == 1));
        }
        Assert.Equal(0, farCounts[0]);
        Assert.InRange(farCounts[1], 1, 49);
        Assert.True(farCounts[2] > farCounts[1]);
        Assert.Equal(50, farCounts[3]);
        await engine.ConfigureMapRoutingAsync(1, 0);
        Assert.Null(engine.MapRouting);
        Assert.True(engine.PotentialFieldMap.GetFlowDirection(20, 30).dx < 0);
    }

    [Fact]
    public async Task UnreachableSafeZonesAreExcludedEvenWithOccupancyOnly()
    {
        var engine = await Engine(Scenario(disconnected: true));
        await engine.ConfigureMapRoutingAsync(0, 1);
        Assert.All(engine.AgentTargetIndex[..100], target => Assert.Equal(0, target));
    }

    [Fact]
    public async Task AssignedAgentsPassOtherSafeZonesAndRetargetAfterMapEdits()
    {
        var engine = await CrowdSimulationEngine.CreateMapAsync(Scenario(), [(20.0, 30.0), (20.0, 45.0)], seed: 42);
        await engine.ConfigureMapRoutingAsync(0, 1);
        Assert.Equal(1, engine.AgentTargetIndex[1]);
        engine.AgentPositionX[1] = 10;
        engine.AgentPositionY[1] = 30;
        engine.Step(0.016, 1);
        Assert.Equal(0, engine.EvacuatedCount);
        Assert.True(engine.AgentVelocityX[1] > 0);
        await engine.UpdateMapAsync(Scenario(disconnected: true));
        Assert.All(engine.AgentTargetIndex[..2], target => Assert.Equal(0, target));
    }

    [Fact]
    public async Task ExportWeightedRoutingReference()
    {
        string? path = Environment.GetEnvironmentVariable("PLANSAFE_ROUTING_REFERENCE");
        if (string.IsNullOrEmpty(path)) return;
        var references = new List<object>();
        foreach (double occupancyWeight in new[] { 0.0, 0.5, 1.0 })
        {
            var engine = await Engine(Scenario(extraTiles: true), 20);
            await engine.ConfigureMapRoutingAsync(1 - occupancyWeight, occupancyWeight);
            var snapshot = MapGpuSnapshot.Capture(engine);
            var assignments = engine.AgentTargetIndex[..20];
            engine.Step(0.016, 1);
            references.Add(new { snapshot, assignments, after = MapGpuSnapshot.Capture(engine) });
        }
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(references,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
    }
}
