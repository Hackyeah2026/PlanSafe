using PlanSafe.App.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class CrowdSimulatorTests
{
    [Fact]
    public void CrowdSimulator_AgentMovesTowardsExit_AndEvacuatesUponArrival()
    {
        var grid = new SimulationGrid(width: 6f, height: 4f, cellSize: 0.5f);
        // Exit at right
        for (int r = 0; r < grid.Rows; r++)
        {
            grid.SetCell(grid.Cols - 1, r, CellType.Exit);
        }
        grid.ComputeDijkstraField();

        var sim = new CrowdSimulator(grid, maxAgents: 10);
        sim.SpawnAgent(x: 1.0f, y: 2.0f);

        Assert.Equal(1, sim.CurrentBuffer.Count);
        Assert.Equal(0, sim.EvacuatedCount);

        float initialX = sim.CurrentBuffer.PosX[0];

        // Step simulation forward
        for (int step = 0; step < 100; step++)
        {
            sim.Step(0.05f);
            if (sim.EvacuatedCount > 0) break;
        }

        // Agent should have either advanced significantly towards exit or evacuated
        Assert.True(sim.EvacuatedCount == 1 || sim.CurrentBuffer.PosX[0] > initialX + 1.0f,
            $"Expected evacuation or advance, got evacuated={sim.EvacuatedCount}, PosX={sim.CurrentBuffer.PosX[0]}");
    }

    [Fact]
    public void CrowdSimulator_AgentBehind_SlowsDownBeforeAgentInFront()
    {
        var grid = new SimulationGrid(width: 10f, height: 4f, cellSize: 0.5f);
        for (int r = 0; r < grid.Rows; r++)
        {
            grid.SetCell(grid.Cols - 1, r, CellType.Exit);
        }
        grid.ComputeDijkstraField();

        var sim = new CrowdSimulator(grid, maxAgents: 10);

        // Leader agent placed ahead
        sim.SpawnAgent(x: 3.0f, y: 2.0f);
        // Follower agent placed 0.8m behind
        sim.SpawnAgent(x: 2.2f, y: 2.0f);

        // Force front agent to be slow / stopped
        sim.CurrentBuffer.VelX[0] = 0.05f;
        sim.CurrentBuffer.Speed[0] = 0.05f;

        // Step once
        sim.Step(0.03f);

        // Follower agent (index 1) should have its target speed capped due to front agent
        float followerSpeed = sim.CurrentBuffer.Speed[1];
        Assert.True(followerSpeed < WeidmannModel.DefaultFreeSpeed,
            $"Follower agent should brake before front agent. Expected < {WeidmannModel.DefaultFreeSpeed}, got {followerSpeed}");
    }

    [Fact]
    public void CrowdSimulator_TwoObstaclesNarrowCorridorPreset_InitializesCorrectly()
    {
        var preset = MapPresets.CreateTwoObstaclesNarrowCorridor();
        var grid = new SimulationGrid(preset.Width, preset.Height, preset.CellSize);
        preset.SetupGrid(grid);

        var sim = new CrowdSimulator(grid, maxAgents: 100);
        preset.PopulateAgents(sim, 50);

        Assert.Equal(50, sim.CurrentBuffer.Count);
        Assert.Equal(0, sim.EvacuatedCount);

        // Corridor center at (14m, 8m) should be passable
        int corrCol = (int)(14f / grid.CellSize);
        int corrRow = (int)(8f / grid.CellSize);
        Assert.True(grid.IsPassable(corrCol, corrRow));

        // Obstacles at (14m, 3m) and (14m, 12m) should be impassable
        int obs1Row = (int)(3f / grid.CellSize);
        int obs2Row = (int)(12f / grid.CellSize);
        Assert.False(grid.IsPassable(corrCol, obs1Row));
        Assert.False(grid.IsPassable(corrCol, obs2Row));
    }
}
