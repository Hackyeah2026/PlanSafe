using PlanSafe.App.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class SimulationGridTests
{
    [Fact]
    public void DijkstraField_ExitsHaveZeroPotential_AndDistancesIncreaseAwayFromExit()
    {
        var grid = new SimulationGrid(width: 10f, height: 6f, cellSize: 0.5f);

        // Place exit at rightmost column
        for (int r = 0; r < grid.Rows; r++)
        {
            grid.SetCell(grid.Cols - 1, r, CellType.Exit);
        }

        grid.ComputeDijkstraField();

        // Exit cells must have potential 0
        for (int r = 0; r < grid.Rows; r++)
        {
            float pot = grid.BasePotential[grid.GetIndex(grid.Cols - 1, r)];
            Assert.Equal(0f, pot);
        }

        // Further left cells must have strictly positive and increasing potential
        float potNearExit = grid.BasePotential[grid.GetIndex(grid.Cols - 2, grid.Rows / 2)];
        float potFarLeft = grid.BasePotential[grid.GetIndex(1, grid.Rows / 2)];

        Assert.True(potNearExit > 0f);
        Assert.True(potFarLeft > potNearExit);
    }

    [Fact]
    public void DijkstraField_ObstacleCells_AreImpassable()
    {
        var grid = new SimulationGrid(width: 10f, height: 6f, cellSize: 0.5f);
        grid.SetCell(grid.Cols - 1, grid.Rows / 2, CellType.Exit);

        // Put an obstacle wall
        grid.SetRect(4, 1, 2, 4, CellType.Obstacle);

        grid.ComputeDijkstraField();

        int obsIdx = grid.GetIndex(4, 2);
        Assert.True(grid.BasePotential[obsIdx] >= SimulationGrid.ImpassablePotential);
    }

    [Fact]
    public void SampleDesiredDirection_PointsTowardsExit()
    {
        var grid = new SimulationGrid(width: 10f, height: 6f, cellSize: 0.5f);

        // Exit at right
        for (int r = 0; r < grid.Rows; r++)
        {
            grid.SetCell(grid.Cols - 1, r, CellType.Exit);
        }

        grid.ComputeDijkstraField();

        // Sample on left side
        grid.SampleDesiredDirection(2.0f, 3.0f, out float dirX, out float dirY);

        // Must point predominantly to the right (+X direction towards exit)
        Assert.True(dirX > 0.8f, $"Expected dirX > 0.8 pointing east towards exit, got {dirX}");
    }
}
