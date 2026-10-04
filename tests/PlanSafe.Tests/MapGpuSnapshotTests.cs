using MapScenarioBuilder = PlanSafe.Contracts.Simulation.MapScenarioBuilder;
using MapScenario = PlanSafe.Contracts.Simulation.MapScenario;
using MapExit = PlanSafe.Contracts.Simulation.MapExit;
using MapSpawnZone = PlanSafe.Contracts.Simulation.MapSpawnZone;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using PlanSafe.App.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class MapGpuSnapshotTests
{
    [Fact]
    public void PreparedMapStatePreservesAgentsFieldsAndRasterResolution()
    {
        var cases = new[] { MakeCase("two-exits", 200, 100), MakeCase("coarse-potential", 1000, 40) };
        var destination = Environment.GetEnvironmentVariable("PLANSAFE_MAP_GPU_REFERENCE");
        if (!string.IsNullOrEmpty(destination))
            File.WriteAllText(destination, JsonSerializer.Serialize(cases,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static object MakeCase(string name, int width, int height)
    {
        var blocked = new bool[width * height];
        for (int y = 0; y < height - 8; y++)
            for (int x = width / 2; x < width / 2 + 4; x++) blocked[y * width + x] = true;
        var exits = new[] { new MapExit(width - 10, 10, 4), new MapExit(width - 10, height - 10, 4) };
        var scenario = new MapScenario(50, 20, 111320, 71555, 1, width, height, blocked, exits,
            new[] { new MapSpawnZone(new double[] { 1, 40, 40, 1 }, new double[] { 1, 1, height - 1, height - 1 }, 100) });
        var engine = new CrowdSimulationEngine(width, height, 37) { Granulation = 3, IsMapMode = true };
        engine.LoadMapScenario(scenario);
        var positions = Enumerable.Range(0, 37).Select(i => (X: 12.0 + i % 8 * 0.3, Y: 12.0 + i / 8 * 0.3)).ToList();
        // This lies in the square's corner outside the inscribed circle.
        // Both engines must evacuate it using the map's square exit model.
        positions[0] = (exits[0].X + 3.8, exits[0].Y + 3.8);
        engine.InitializeAgentsWithPositions(positions);
        var snapshot = MapGpuSnapshot.Capture(engine);
        Assert.Equal(37, snapshot.Count);
        Assert.Equal(13 * 32, snapshot.Agents.Length);
        Assert.Equal(engine.PotentialFieldMap.TotalCells * 16, snapshot.Fields.Length);
        Assert.Equal(engine.PotentialFieldMap.TotalCells, snapshot.Blocked.Length);
        Assert.Equal(scenario.Blocked, MapScenario.Deserialize(snapshot.Scenario).Blocked);
        if (width > 600) Assert.True(snapshot.Columns < scenario.Columns);
        for (int i = 0; i < 13; i++)
        {
            Assert.Equal((float)engine.AgentPositionX[i], BitConverter.ToSingle(snapshot.Agents, i * 32));
            Assert.Equal((float)engine.AgentMaxSpeed[i], BitConverter.ToSingle(snapshot.Agents, i * 32 + 20));
            Assert.Equal(1, BitConverter.ToInt32(snapshot.Agents, i * 32 + 24));
        }
        engine.Step(0.016, 1);
        var afterOneTick = MapGpuSnapshot.Capture(engine);
        Assert.Equal(3, engine.EvacuatedCount);
        for (int i = 1; i < 64; i++) engine.Step(0.016, 1);
        return new { name, snapshot, afterOneTick, afterRefinement = MapGpuSnapshot.Capture(engine) };
    }
}
