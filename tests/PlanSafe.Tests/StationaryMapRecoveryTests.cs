using System.Text.Json;
using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Simulation;
using Xunit;
using MapScenario = PlanSafe.Contracts.Simulation.MapScenario;
using MapScenarioBuilder = PlanSafe.Contracts.Simulation.MapScenarioBuilder;

namespace PlanSafe.Tests;

public class StationaryMapRecoveryTests
{
    private const int ThresholdTicks = 11250;

    [Fact]
    public async Task IsolatedAgentsRecoverAtThreeMinutesToNearestReachableStreet()
    {
        var references = new List<object>();
        foreach (int granulation in new[] { 1, 16 })
        {
            var scenario = CreateScenario();
            var engine = await CreateEngine(scenario, granulation);
            var snapshot = MapGpuSnapshot.Capture(engine);
            Advance(engine, ThresholdTicks - 1);
            Assert.Equal(10.5, engine.AgentPositionX[0], 3);
            Assert.Equal(20.5, engine.AgentPositionY[0], 3);
            engine.UpdatePhysics(0.016);
            Assert.Equal(37.5, engine.AgentPositionX[0]);
            Assert.Equal(20.5, engine.AgentPositionY[0]);
            Assert.Equal(0, engine.AgentVelocityX[0]);
            Assert.Equal(0, engine.AgentVelocityY[0]);
            Assert.Equal(0, engine.EvacuatedCount);
            Assert.Equal(1, engine.ActiveAgentCount);
            Assert.Equal(granulation, engine.AgentCount);
            references.Add(new { name = $"g{granulation}", snapshot, expectedX = 37.5, expectedY = 20.5 });
        }
        string? output = Environment.GetEnvironmentVariable("PLANSAFE_STATIONARY_REFERENCE");
        if (!string.IsNullOrEmpty(output))
            File.WriteAllText(output, JsonSerializer.Serialize(references,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    [Fact]
    public async Task MovementAndResetRestartTheThreeMinuteTimer()
    {
        var engine = await CreateEngine(CreateScenario(), 1);
        Advance(engine, ThresholdTicks - 1);
        engine.AgentPositionX[0] += 0.3;
        engine.UpdatePhysics(0.016);
        Advance(engine, ThresholdTicks - 1);
        Assert.Equal(10.8, engine.AgentPositionX[0], 3);
        engine.Reset();
        engine.AgentMaxSpeed[0] = 0.000001;
        Advance(engine, ThresholdTicks - 1);
        Assert.Equal(10.5, engine.AgentPositionX[0], 3);
        engine.UpdatePhysics(0.016);
        Assert.Equal(37.5, engine.AgentPositionX[0]);
    }

    [Fact]
    public async Task TinyJitterDoesNotKeepAnIsolatedAgentStuckForever()
    {
        var engine = await CreateEngine(CreateScenario(), 1);
        for (int tick = 0; tick < ThresholdTicks; tick++)
        {
            engine.AgentPositionX[0] = 10.5 + (tick % 2 == 0 ? 0.05 : -0.05);
            engine.UpdatePhysics(0.016);
        }
        Assert.Equal(37.5, engine.AgentPositionX[0]);
    }

    [Fact]
    public async Task WaitingNearOtherAgentsDoesNotTriggerQueueJumping()
    {
        var scenario = CreateScenario();
        var engine = await CrowdSimulationEngine.CreateMapAsync(scenario, [(10.5, 20.5), (11.5, 20.5)], socialRepulsionWeight: 0, seed: 42);
        engine.AgentMaxSpeed[0] = engine.AgentMaxSpeed[1] = 0.000001;
        Advance(engine, ThresholdTicks + 10);
        Assert.True(engine.AgentPositionX[0] < 15);
        Assert.True(engine.AgentPositionX[1] < 15);
    }

    [Fact]
    public async Task RecoveryDoesNotOverlapAnAgentAlreadyOnTheNearestStreet()
    {
        var engine = await CrowdSimulationEngine.CreateMapAsync(CreateScenario(), [(10.5, 20.5), (37.5, 20.5)], seed: 42);
        engine.AgentMaxSpeed[0] = engine.AgentMaxSpeed[1] = 0.000001;
        Advance(engine, ThresholdTicks);
        double dx = engine.AgentPositionX[0] - engine.AgentPositionX[1];
        double dy = engine.AgentPositionY[0] - engine.AgentPositionY[1];
        Assert.True(Math.Sqrt(dx * dx + dy * dy) >= engine.AgentRadius[0] + engine.AgentRadius[1] + 0.099);
        Assert.True(engine.AgentPositionX[0] > 30);
        Assert.Equal(2, engine.ActiveAgentCount);
    }

    [Fact]
    public async Task EvacuationCompactionPreservesTheRemainingAgentsTimer()
    {
        var scenario = CreateScenario();
        var engine = await CrowdSimulationEngine.CreateMapAsync(scenario, [(70.5, 20.5), (10.5, 20.5)], seed: 42);
        engine.AgentMaxSpeed[0] = engine.AgentMaxSpeed[1] = 0.000001;
        Advance(engine, ThresholdTicks - 1);
        Assert.Equal(1, engine.EvacuatedCount);
        Assert.Equal(10.5, engine.AgentPositionX[0], 3);
        engine.UpdatePhysics(0.016);
        Assert.Equal(37.5, engine.AgentPositionX[0]);
        Assert.Equal(1, engine.ActiveAgentCount);
        Assert.Equal(1, engine.EvacuatedCount);
    }

    [Fact]
    public async Task MissingStreetMetadataUsesWalkableCellsButEmptyStreetMetadataDoesNot()
    {
        var oldMap = CreateScenario();
        var legacy = new MapScenario(50, 20, 111320, 71555, 1, 80, 50, oldMap.Blocked,
            oldMap.Exits, oldMap.SpawnZones);
        var engine = await CreateEngine(legacy, 1);
        Advance(engine, ThresholdTicks);
        Assert.NotEqual((10.5, 20.5), (engine.AgentPositionX[0], engine.AgentPositionY[0]));
        var empty = new MapScenario(50, 20, 111320, 71555, 1, 80, 50, oldMap.Blocked,
            oldMap.Exits, oldMap.SpawnZones, new bool[80 * 50]);
        engine = await CreateEngine(empty, 1);
        Advance(engine, ThresholdTicks);
        Assert.Equal(10.5, engine.AgentPositionX[0], 3);
        Assert.Equal(20.5, engine.AgentPositionY[0], 3);
    }

    [Fact]
    public void StreetMetadataRoundTripsAndRejectsTruncatedData()
    {
        var scenario = CreateScenario();
        var bytes = scenario.Serialize();
        Assert.Equal(3, BitConverter.ToInt32(bytes, 5));
        var restored = MapScenario.Deserialize(bytes);
        Assert.Equal(scenario.StreetMask, restored.StreetMask);
        Assert.Equal(scenario.Blocked, restored.Blocked);
        Assert.Throws<ArgumentException>(() => MapScenario.Deserialize(bytes[..^1]));
    }

    private static MapScenario CreateScenario()
    {
        var builder = new MapScenarioBuilder(80, 50, 1);
        builder.Fill(true);
        // A blocked street and a closer, disconnected courtyard must lose to
        // the actual surface street connected to the exit.
        builder.SetPolygon([13, 18, 18, 13], [13, 13, 28, 28], false);
        builder.SetPolygon([20, 26, 26, 20], [14, 14, 27, 27], false);
        builder.SetPolygon([21, 25, 25, 21], [15, 15, 26, 26], true);
        var streets = new MapScenarioBuilder(80, 50, 1);
        streets.SetCorridor([15.5, 15.5], [15.5, 25.5], 1);
        streets.SetCorridor([22.5, 22.5], [16.5, 24.5], 0.5);
        streets.SetCorridor([40.5, 40.5], [5.5, 44.5], 3);
        builder.SetStreetMask(streets.ToMask());
        return builder.Build(50, 20, 111320, 71555,
            [new MapExit(70.5, 20.5, 1)], [new MapSpawnZone([5, 12, 12, 5], [5, 5, 40, 40], 16)]);
    }

    private static async Task<CrowdSimulationEngine> CreateEngine(MapScenario scenario, int granulation)
    {
        var engine = await CrowdSimulationEngine.CreateMapAsync(scenario,
            Enumerable.Repeat((10.5, 20.5), granulation).ToArray(), granulation, seed: 42);
        engine.AgentMaxSpeed[0] = 0.000001;
        return engine;
    }

    private static void Advance(CrowdSimulationEngine engine, int ticks)
    {
        for (int i = 0; i < ticks; i++) engine.UpdatePhysics(0.016);
    }
}
