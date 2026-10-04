using System.Text.Json;
using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Simulation;
using Xunit;
using MapScenario = PlanSafe.Contracts.Simulation.MapScenario;
using MapScenarioBuilder = PlanSafe.Contracts.Simulation.MapScenarioBuilder;

namespace PlanSafe.Tests;

public class ContinuousMapCollisionTests
{
    [Fact]
    public async Task MovementCannotJumpAcrossThinBuildingWallsAndRecoversEmbeddedAgents()
    {
        var fixtures = new List<object>();
        foreach (var (cell, granulation, embedded) in new[]
        {
            (0.25, 1, false), (0.25, 16, false), (1.0, 1, false), (2.5, 16, false), (1.0, 1, true), (1.0, 16, true)
        })
        {
            var builder = new MapScenarioBuilder(60, 40, cell);
            builder.Fill(true);
            double wallWidth = embedded && granulation > 1 ? 10 : cell;
            builder.SetPolygon([30, 30 + wallWidth, 30 + wallWidth, 30], [5, 5, 35, 35], false);
            var scenario = builder.Build(50, 20, 111320, 71555,
                [new MapExit(55, 20, 2)], [new MapSpawnZone([5, 25, 25, 5], [5, 5, 35, 35], granulation)]);
            var positions = Enumerable.Repeat((X: 20.0, Y: 20.0), granulation).ToArray();
            var engine = await CrowdSimulationEngine.CreateMapAsync(scenario, positions, granulation, seed: 42);
            double radius = engine.AgentRadius[0];
            engine.AgentPositionX[0] = embedded ? 30 + wallWidth / 2 : 30 - radius - 0.05;
            engine.AgentPositionY[0] = 20 + cell / 2;
            engine.AgentVelocityX[0] = embedded ? 0 : 500;
            engine.AgentVelocityY[0] = 0;
            double fromX = engine.AgentPositionX[0], fromY = engine.AgentPositionY[0];
            var snapshot = MapGpuSnapshot.Capture(engine);
            engine.UpdatePhysics(0.016);
            Assert.False(scenario.IsBlockedAt(engine.AgentPositionX[0], engine.AgentPositionY[0]));
            if (!embedded)
            {
                Assert.False(scenario.CrossesBlockedCell(fromX, fromY, engine.AgentPositionX[0], engine.AgentPositionY[0]));
                Assert.True(engine.AgentPositionX[0] < 30, "An open final cell on the far side of the wall is still an illegal crossing.");
            }
            fixtures.Add(new { name = $"cell-{cell}-g{granulation}-embedded-{embedded}", embedded, snapshot });
        }
        string? output = Environment.GetEnvironmentVariable("PLANSAFE_MAP_COLLISION_REFERENCE");
        if (!string.IsNullOrEmpty(output))
            File.WriteAllText(output, JsonSerializer.Serialize(fixtures, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    [Fact]
    public void SegmentTestRejectsCornerCutsAndAllowsMovementAwayFromWall()
    {
        var blocked = new bool[10 * 10];
        blocked[5 * 10 + 5] = true;
        var scenario = new MapScenario(50, 20, 111320, 71555, 1, 10, 10, blocked,
            [new MapExit(9, 9, 1)], [new MapSpawnZone([1, 2, 2, 1], [1, 1, 2, 2], 1)]);
        Assert.True(scenario.CrossesBlockedCell(4.5, 5.5, 6.5, 5.5));
        Assert.True(scenario.CrossesBlockedCell(4.5, 5.5, 5.5, 4.5));
        Assert.False(scenario.CrossesBlockedCell(6, 5.5, 7, 5.5));
        Assert.False(scenario.CrossesBlockedCell(4, 4, 4, 7));
    }
}
