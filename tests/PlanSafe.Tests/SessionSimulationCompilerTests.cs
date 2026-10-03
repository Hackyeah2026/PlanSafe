using System.Collections.Generic;
using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Session;
using Xunit;

namespace PlanSafe.Tests;

public class SessionSimulationCompilerTests
{
    [Fact]
    public void Compile_ValidMapSession_ProducesFunctionalSimulationGridAndDijkstraField()
    {
        var session = MapSession.CreateDefaultRoot("scenario/market-evac");

        // Evac Hazard Zone (Spawn)
        session.Items.Add(new EvacCircleZoneItem
        {
            Name = "Hazard Spawn",
            Center = [50.0614, 19.9366],
            Radius = 60
        });

        // Safe Location (Exit)
        session.Items.Add(new SafeCircleZoneItem
        {
            Name = "Park Safe Zone",
            Center = [50.0630, 19.9400],
            Radius = 50
        });

        // Blockade (Obstacle)
        session.Items.Add(new BlockadeZoneItem
        {
            Name = "Sienna Street Block",
            StartPoint = [50.0620, 19.9370],
            EndPoint = [50.0620, 19.9390]
        });

        // Act
        var compiled = SessionSimulationCompiler.Compile(session);

        // Assert
        Assert.NotNull(compiled);
        Assert.Equal("scenario/market-evac", compiled.Name);
        Assert.Equal("scenario/market-evac", compiled.BranchName);
        Assert.True(compiled.Grid.Cols > 10);
        Assert.True(compiled.Grid.Rows > 10);
        Assert.True(compiled.ExitCellCount > 0, "Should have rasterized exit cells");
        Assert.True(compiled.SpawnCellCount > 0, "Should have rasterized spawn cells");
        Assert.True(compiled.ObstacleCellCount > 0, "Should have rasterized obstacle cells");

        // Verify Dijkstra field is not all infinity
        bool hasReachableCells = false;
        for (int i = 0; i < compiled.Grid.TotalCells; i++)
        {
            if (compiled.Grid.BasePotential[i] < SimulationGrid.ImpassablePotential)
            {
                hasReachableCells = true;
                break;
            }
        }
        Assert.True(hasReachableCells, "Dijkstra field should have reachable paths towards exits");

        // Verify PopulateAgents works on CrowdSimulator
        var sim = new CrowdSimulator(compiled.Grid, maxAgents: 500);
        compiled.PopulateAgents(sim, 100);
        Assert.Equal(100, sim.CurrentBuffer.Count);
    }
}
