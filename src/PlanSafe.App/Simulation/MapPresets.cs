namespace PlanSafe.App.Simulation;

public static class MapPresets
{
    public record MapDefinition(
        string Name,
        string Description,
        float Width,
        float Height,
        float CellSize,
        Action<SimulationGrid> SetupGrid,
        Action<CrowdSimulator, int> PopulateAgents);

    public static MapDefinition CreateTwoObstaclesNarrowCorridor()
    {
        return new MapDefinition(
            Name: "Two Obstacles with Narrow Corridor",
            Description: "A large crowd evacuates towards an exit through a narrow 2-meter corridor flanked by two massive rectangular obstacles.",
            Width: 30f,
            Height: 16f,
            CellSize: 0.5f,
            SetupGrid: (grid) =>
            {
                // Clear grid to walkable
                Array.Clear(grid.Cells, 0, grid.TotalCells);

                // Exit on the right wall (cols 58-59, rows 6 to 26)
                for (int r = 6; r <= 26; r++)
                {
                    grid.SetCell(grid.Cols - 1, r, CellType.Exit);
                    grid.SetCell(grid.Cols - 2, r, CellType.Exit);
                }

                // Top obstacle block: x = 12m to 16m (cols 24 to 32), y = 0 to 7m (rows 0 to 14)
                grid.SetRect(24, 0, 9, 14, CellType.Obstacle);

                // Bottom obstacle block: x = 12m to 16m (cols 24 to 32), y = 9m to 16m (rows 18 to 32)
                grid.SetRect(24, 18, 9, 14, CellType.Obstacle);

                // Between rows 14 and 18 (y = 7.0m to 9.0m) is a 2-meter narrow corridor!

                // Mark spawn zone visually (cols 2 to 14, rows 4 to 28)
                grid.SetRect(2, 4, 13, 24, CellType.Spawn);

                // Compute Dijkstra field from exit
                grid.ComputeDijkstraField();
            },
            PopulateAgents: (sim, agentCount) =>
            {
                sim.Reset();
                var rng = new Random(42);

                // Spawn agents uniformly within the spawn zone (x: 1.5m to 7.5m, y: 2.5m to 13.5m)
                float minX = 1.5f;
                float maxX = 7.5f;
                float minY = 2.5f;
                float maxY = 13.5f;

                for (int i = 0; i < agentCount; i++)
                {
                    float x = minX + (float)rng.NextDouble() * (maxX - minX);
                    float y = minY + (float)rng.NextDouble() * (maxY - minY);
                    sim.SpawnAgent(x, y);
                }
            });
    }

    public static MapDefinition CreateOpenArena()
    {
        return new MapDefinition(
            Name: "Open Arena Evacuation",
            Description: "Unobstructed crowd movement towards a centered exit door.",
            Width: 24f,
            Height: 16f,
            CellSize: 0.5f,
            SetupGrid: (grid) =>
            {
                Array.Clear(grid.Cells, 0, grid.TotalCells);

                // Exit on the right center (cols 46-47, rows 13 to 19)
                for (int r = 13; r <= 19; r++)
                {
                    grid.SetCell(grid.Cols - 1, r, CellType.Exit);
                    grid.SetCell(grid.Cols - 2, r, CellType.Exit);
                }

                // Spawn zone on the left
                grid.SetRect(2, 4, 10, 24, CellType.Spawn);

                grid.ComputeDijkstraField();
            },
            PopulateAgents: (sim, agentCount) =>
            {
                sim.Reset();
                var rng = new Random(42);

                float minX = 1.5f;
                float maxX = 5.5f;
                float minY = 2.5f;
                float maxY = 13.5f;

                for (int i = 0; i < agentCount; i++)
                {
                    float x = minX + (float)rng.NextDouble() * (maxX - minX);
                    float y = minY + (float)rng.NextDouble() * (maxY - minY);
                    sim.SpawnAgent(x, y);
                }
            });
    }
}
