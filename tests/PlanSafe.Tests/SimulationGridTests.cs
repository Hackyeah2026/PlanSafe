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

    [Fact]
    public void SimulationGrid_DesiredDirection_NeverPointsAwayFromExit_EvenWithDenseCrowd()
    {
        var grid = new SimulationGrid(width: 10f, height: 6f, cellSize: 0.5f);
        for (int r = 0; r < grid.Rows; r++)
        {
            grid.SetCell(grid.Cols - 1, r, CellType.Exit);
        }
        grid.ComputeDijkstraField();

        // Place a heavy crowd in the middle (col 10 to 12, row 4 to 8)
        for (int r = 4; r <= 8; r++)
        {
            for (int c = 10; c <= 12; c++)
            {
                grid.DensityGrid[grid.GetIndex(c, r)] = 5.0f; // Jam density
            }
        }
        grid.UpdateDynamicPotential();

        // Check cells to the left of the crowd (e.g. col 2 to 9)
        for (int r = 4; r <= 8; r++)
        {
            for (int c = 2; c <= 9; c++)
            {
                float x = (c + 0.5f) * grid.CellSize;
                float y = (r + 0.5f) * grid.CellSize;
                grid.SampleDesiredDirection(x, y, out float dirX, out float dirY);

                // Exit is at x = 10 (to the right). dirX must NOT be negative (pointing away from exit)!
                Assert.True(dirX >= 0f,
                    $"Desired direction at ({x}, {y}) points backwards away from exit! dirX={dirX}, dirY={dirY}");
            }
        }
    }
}
