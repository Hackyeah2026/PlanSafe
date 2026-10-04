using MapScenario = PlanSafe.Contracts.Simulation.MapScenario;
using MapExit = PlanSafe.Contracts.Simulation.MapExit;
using MapSpawnZone = PlanSafe.Contracts.Simulation.MapSpawnZone;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlanSafe.App.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class MapPreparationTests
{
    [Fact]
    public async Task CancelledPreparationDoesNotStartAnEngine()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CrowdSimulationEngine.CreateMapAsync(Scenario(), [(12, 12)], cancellationToken: cancellation.Token));
    }

    private static MapScenario Scenario()
    {
        var blocked = new bool[120 * 80];
        for (int y = 0; y < 65; y++)
            for (int x = 50; x < 57; x++) blocked[y * 120 + x] = true;
        // Disconnected courtyard, plus the main wall with a passage around its bottom.
        for (int y = 10; y <= 25; y++)
            for (int x = 10; x <= 25; x++)
                if (x is 10 or 25 || y is 10 or 25) blocked[y * 120 + x] = true;
        return new MapScenario(50, 20, 111320, 71555, 1, 120, 80, blocked,
            [new MapExit(112, 12, 3), new MapExit(112, 72, 3)],
            [new MapSpawnZone([1, 40, 40, 1], [1, 1, 79, 79], 100)]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(16)]
    public async Task BatchedPreparationMatchesSynchronousStateAndReset(int granulation)
    {
        var scenario = Scenario();
        var positions = Enumerable.Range(0, 173)
            .Select(i => (X: 12.0 + i % 9, Y: 12.0 + i % 40)).ToArray();
        var expected = new CrowdSimulationEngine(120, 80, positions.Length) { Granulation = granulation, IsMapMode = true };
        expected.LoadMapScenario(scenario);
        expected.InitializeAgentsWithPositions(positions, seed: 42);
        var actual = await CrowdSimulationEngine.CreateMapAsync(scenario, positions, granulation, seed: 42);
        Assert.Equal(expected.AgentPositionX.Take(expected.SimulatedAgentCount), actual.AgentPositionX.Take(actual.SimulatedAgentCount));
        Assert.Equal(expected.AgentPositionY.Take(expected.SimulatedAgentCount), actual.AgentPositionY.Take(actual.SimulatedAgentCount));
        Assert.Equal(expected.AgentMaxSpeed.Take(expected.SimulatedAgentCount), actual.AgentMaxSpeed.Take(actual.SimulatedAgentCount));
        Assert.Equal(expected.PotentialFieldMap.StaticPotentialFieldMatrix, actual.PotentialFieldMap.StaticPotentialFieldMatrix);
        Assert.Equal(expected.PotentialFieldMap.DynamicPotentialFieldMatrix, actual.PotentialFieldMap.DynamicPotentialFieldMatrix);
        Assert.Equal(expected.PotentialFieldMap.SmoothedDensity.ToArray(), actual.PotentialFieldMap.SmoothedDensity.ToArray());
        var initialX = actual.AgentPositionX.Take(actual.SimulatedAgentCount).ToArray();
        actual.Step(0.016, 2);
        actual.Reset();
        Assert.Equal(initialX, actual.AgentPositionX.Take(actual.SimulatedAgentCount).ToArray());
        Assert.Equal(0, actual.SimulationTime);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task MapRouteFieldStaysPreparedThroughCongestionAndPlayback(int granulation)
    {
        var positions = Enumerable.Range(0, 37).Select(i => (X: 35.0 + i % 5 * 0.01, Y: 35.0 + i / 5 * 0.01)).ToArray();
        var engine = await CrowdSimulationEngine.CreateMapAsync(Scenario(), positions, granulation, seed: 42);
        var initial = engine.PotentialFieldMap.StaticPotentialFieldMatrix.ToArray();
        engine.Step(0.016, 80);
        Assert.Equal(initial, engine.PotentialFieldMap.DynamicPotentialFieldMatrix);
        Assert.All(engine.PotentialFieldMap.DynamicCrowdPenalty, penalty => Assert.Equal(0, penalty));
        Assert.True(engine.SimulationTime > 1);
        Assert.Contains(engine.AgentPositionX.Take(engine.ActiveAgentCount), x => x != 35);
    }

    [Fact]
    public void NearestReachableSearchMatchesExhaustiveSearchIncludingTies()
    {
        var engine = new CrowdSimulationEngine(120, 80, 1);
        engine.LoadMapScenario(Scenario());
        var rng = new Random(42);
        for (int i = 0; i < 250; i++)
        {
            double px = i < 120 ? i : rng.NextDouble() * 120;
            double py = i < 120 ? 18 : rng.NextDouble() * 80;
            double bestDistance = 50 * 50;
            (double X, double Y)? expected = null;
            for (int row = 0; row < 80; row++)
                for (int col = 0; col < 120; col++)
                {
                    double x = col + 0.5, y = row + 0.5;
                    if (!engine.IsReachable(x, y)) continue;
                    double distance = (x - px) * (x - px) + (y - py) * (y - py);
                    if (distance < bestDistance) { bestDistance = distance; expected = (x, y); }
                }
            Assert.Equal(expected, engine.FindNearestReachable(px, py, 50));
        }
    }
}
