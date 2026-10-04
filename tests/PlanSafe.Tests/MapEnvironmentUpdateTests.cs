using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Models.Map;
using MapScenario = PlanSafe.Contracts.Simulation.MapScenario;
using MapExit = PlanSafe.Contracts.Simulation.MapExit;
using MapSpawnZone = PlanSafe.Contracts.Simulation.MapSpawnZone;
using Xunit;

namespace PlanSafe.Tests;

public class MapEnvironmentUpdateTests
{
    [Fact]
    public async Task UpdatingTerrainPreservesAgentsAndEvacuationProgress()
    {
        var original = MakeScenario(50, 50, [new MapExit(40, 10, 2)]);
        var engine = await CrowdSimulationEngine.CreateMapAsync(original, [(40, 10), (5, 15), (7, 20)], seed: 42);
        var initialSnapshot = MapGpuSnapshot.Capture(engine);
        engine.Step(0.016, 1);
        Assert.Equal(1, engine.EvacuatedCount);
        var positionsX = engine.AgentPositionX[..3];
        var positionsY = engine.AgentPositionY[..3];
        var velocities = engine.AgentVelocityX[..3];
        var radii = engine.AgentRadius[..3];
        var before = engine.GetMapEvacuationStatus();
        int frame = engine.FrameCounter;
        var changed = MakeScenario(100, 80, [new MapExit(50, 15, 2), new MapExit(15, 65, 2)]);
        for (int row = 0; row < 50; row++) changed.Blocked[row * 100 + 30] = row != 25;

        await engine.UpdateMapAsync(changed, 10, 5);

        Assert.Equal(before.SimulationSeconds, engine.SimulationTime);
        Assert.Equal(frame, engine.FrameCounter);
        Assert.Equal(1, engine.EvacuatedCount);
        Assert.Equal(before.RemainingAgents, engine.ActiveAgentCount);
        Assert.Equal(velocities, engine.AgentVelocityX[..3]);
        Assert.Equal(radii, engine.AgentRadius[..3]);
        for (int index = 0; index < 3; index++)
        {
            Assert.Equal(positionsX[index] + 10, engine.AgentPositionX[index]);
            Assert.Equal(positionsY[index] + 5, engine.AgentPositionY[index]);
        }
        Assert.Equal(1, engine.GetMapEvacuationStatus().EvacuatedPerExit[0]);
        Assert.True(engine.PotentialFieldMap.ObstacleMask[10 * 100 + 30]);
        Assert.Equal(0, engine.PotentialFieldMap.SamplePotential(15, 65));
        var referencePath = Environment.GetEnvironmentVariable("PLANSAFE_MAP_UPDATE_REFERENCE");
        if (!string.IsNullOrEmpty(referencePath))
            File.WriteAllText(referencePath, System.Text.Json.JsonSerializer.Serialize(new
            {
                before = initialSnapshot,
                after = MapGpuSnapshot.Capture(engine),
                offsetX = 10,
                offsetY = 5
            }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        engine.Step(0.016, 1);
        Assert.True(engine.SimulationTime > before.SimulationSeconds);
    }

    [Fact]
    public async Task CancelledUpdateKeepsThePreviousEnvironment()
    {
        var original = MakeScenario(50, 50, [new MapExit(40, 10, 2)]);
        var engine = await CrowdSimulationEngine.CreateMapAsync(original, [(5, 15)], seed: 42);
        var field = engine.PotentialFieldMap;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.UpdateMapAsync(
            MakeScenario(100, 80, [new MapExit(90, 10, 2)]), cancellationToken: cancellation.Token));
        Assert.Same(original, engine.MapScenario);
        Assert.Same(field, engine.PotentialFieldMap);
    }

    [Fact]
    public void ExpandingBoundsKeepsTheExistingGeographicProjection()
    {
        var items = new List<MapZoneItem>
        {
            new SafePointZoneItem { Id = "first", Position = [50.07, 19.9] },
            new SafePointZoneItem { Id = "second", Position = [50.07, 19.901] }
        };
        var original = new MapSimulationBounds(items, null);
        items.Add(new SafePointZoneItem { Id = "north-west", Position = [50.08, 19.88] });
        var expanded = new MapSimulationBounds(items, null, original);
        var before = original.ToWorld(50.07, 19.9);
        var after = expanded.ToWorld(50.07, 19.9);
        Assert.Equal(original.MetersPerDegreeLongitude, expanded.MetersPerDegreeLongitude);
        Assert.True(expanded.WorldWidth > original.WorldWidth);
        Assert.Equal(before.X + (original.MinimumLongitude - expanded.MinimumLongitude) * expanded.MetersPerDegreeLongitude, after.X, 8);
        Assert.Equal(before.Y + (expanded.MaximumLatitude - original.MaximumLatitude) * expanded.MetersPerDegreeLatitude, after.Y, 8);
    }

    private static MapScenario MakeScenario(int columns, int rows, MapExit[] exits) =>
        new(50, 20, 111320, 71555, 1, columns, rows, new bool[columns * rows], exits,
            [new MapSpawnZone([1, 20, 20, 1], [1, 1, 25, 25], 3)]);
}
