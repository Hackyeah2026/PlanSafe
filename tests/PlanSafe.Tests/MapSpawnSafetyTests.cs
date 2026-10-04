using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class MapSpawnSafetyTests
{
    private static MapScenario Scenario(int width = 80)
    {
        var blocked = new bool[width * 80];
        for (int y = 0; y < 65; y++)
            for (int x = 50; x < 57; x++) blocked[y * width + x] = true;
        for (int y = 10; y <= 25; y++)
            for (int x = 10; x <= 25; x++)
                if (x is 10 or 25 || y is 10 or 25) blocked[y * width + x] = true;
        return new MapScenario(50, 20, 111320, 71555, 1, width, 80, blocked,
            [new MapExit(width - 8, 72, 3)],
            [new MapSpawnZone([1, 50, 50, 1], [1, 1, 79, 79], 800)]);
    }

    private static void AssertSafe(CrowdSimulationEngine engine)
    {
        var scenario = engine.MapScenario!;
        for (int i = 0; i < engine.ActiveAgentCount; i++)
        {
            double x = engine.AgentPositionX[i], y = engine.AgentPositionY[i];
            double clearance = engine.AgentRadius[i] + 0.6;
            Assert.True(engine.IsReachable(x, y), $"Spawn {i} is disconnected from the exits.");
            Assert.InRange(x, clearance, scenario.WorldWidth - clearance);
            Assert.InRange(y, clearance, scenario.WorldHeight - clearance);
            // Measure distance to each wall box independently of the spawn validator.
            for (int row = 0; row < scenario.Rows; row++)
                for (int col = 0; col < scenario.Columns; col++)
                {
                    if (!scenario.Blocked[row * scenario.Columns + col]) continue;
                    double cell = scenario.CellSize;
                    double dx = Math.Max(Math.Max(col * cell - x, x - (col + 1) * cell), 0);
                    double dy = Math.Max(Math.Max(row * cell - y, y - (row + 1) * cell), 0);
                    Assert.True(Math.Sqrt(dx * dx + dy * dy) >= clearance - 1e-8,
                        $"Spawn {i} at ({x}, {y}) is too close to wall ({col}, {row}).");
                }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    public void RandomSpawnsAndResetAvoidWallsCornersAndDisconnectedCourtyards(int granulation)
    {
        var engine = new CrowdSimulationEngine(80, 80, 800) { Granulation = granulation };
        engine.LoadMapScenario(Scenario());
        engine.InitializeAgents(seed: 42);
        AssertSafe(engine);
        for (int i = 0; i < engine.ActiveAgentCount; i++)
            Assert.True(MapScenario.IsPointInPolygon(engine.AgentPositionX[i], engine.AgentPositionY[i],
                engine.MapScenario!.SpawnZones[0].X, engine.MapScenario.SpawnZones[0].Y));
        engine.Reset();
        AssertSafe(engine);
    }

    [Theory]
    [InlineData(80, 1)]
    [InlineData(1000, 25)]
    public async Task PrecomputedPositionsAreRelocatedBeforeGpuSnapshotAndReset(int width, int granulation)
    {
        var positions = Enumerable.Range(0, granulation * 5)
            .Select(i => (i / granulation) switch
            {
                0 => (49.9, 40.0), // Wall edge.
                1 => (49.9, 65.1), // Wall corner.
                2 => (0.1, 0.1), // Map boundary.
                3 => (18.0, 18.0), // Disconnected courtyard.
                _ => (35.0, 35.0) // Safe coordinates should stay exact.
            }).ToArray();
        var engine = await CrowdSimulationEngine.CreateMapAsync(Scenario(width), positions, granulation, seed: 42);
        AssertSafe(engine);
        Assert.Equal(35, engine.AgentPositionX[4]);
        Assert.Equal(35, engine.AgentPositionY[4]);
        var snapshot = MapGpuSnapshot.Capture(engine);
        for (int i = 0; i < engine.ActiveAgentCount; i++)
            Assert.Equal((float)engine.AgentPositionX[i], BitConverter.ToSingle(snapshot.Agents, i * 32));
        var xs = engine.AgentPositionX.Take(engine.ActiveAgentCount).ToArray();
        var ys = engine.AgentPositionY.Take(engine.ActiveAgentCount).ToArray();
        engine.Reset();
        Assert.Equal(xs, engine.AgentPositionX.Take(engine.ActiveAgentCount));
        Assert.Equal(ys, engine.AgentPositionY.Take(engine.ActiveAgentCount));
        AssertSafe(engine);
    }

    [Fact]
    public void GranulationChangeRebuildsSpawnCellsForLargerRadius()
    {
        const int width = 80;
        var blocked = Enumerable.Repeat(true, width * width).ToArray();
        for (int y = 2; y < 78; y++)
            for (int x = 10; x < 15; x++) blocked[y * width + x] = false;
        for (int y = 60; y < 78; y++)
            for (int x = 10; x < 78; x++) blocked[y * width + x] = false;
        var scenario = new MapScenario(50, 20, 111320, 71555, 0.5, width, width, blocked,
            [new MapExit(35, 35, 2)],
            [new MapSpawnZone([5, 7.5, 7.5, 5], [5, 5, 20, 20], 100),
             new MapSpawnZone([15, 35, 35, 15], [32, 32, 37, 37], 100)]);
        var engine = new CrowdSimulationEngine(40, 40, 200);
        engine.LoadMapScenario(scenario);
        Assert.Contains(engine.AgentPositionY.Take(engine.ActiveAgentCount), y => y < 20);
        engine.SetGranulation(25);
        Assert.All(engine.AgentPositionY.Take(engine.ActiveAgentCount), y => Assert.True(y >= 32));
        AssertSafe(engine);
    }

    [Fact]
    public async Task NoSafeSpawnFailsInsteadOfKeepingUnsafePosition()
    {
        var blocked = Enumerable.Repeat(true, 20 * 20).ToArray();
        for (int x = 0; x < 20; x++) blocked[10 * 20 + x] = false;
        var scenario = new MapScenario(50, 20, 111320, 71555, 1, 20, 20, blocked,
            [new MapExit(18, 10.5, 1)],
            [new MapSpawnZone([1, 5, 5, 1], [10, 10, 11, 11], 1)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CrowdSimulationEngine.CreateMapAsync(scenario, [(2.5, 10.5)]));
        var engine = new CrowdSimulationEngine(20, 20, 1);
        Assert.Throws<InvalidOperationException>(() => engine.LoadMapScenario(scenario));
    }
}
