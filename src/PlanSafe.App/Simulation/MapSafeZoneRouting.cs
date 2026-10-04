namespace PlanSafe.App.Simulation;

/// <summary>Routes to whole safe zones and balances their dispatched population, not their exit tiles.</summary>
public sealed class MapSafeZoneRouting
{
    public float[][] DistanceFields { get; }
    public Obstacle[][] ExitZones { get; }
    public int[] ExitTargets { get; }

    private MapSafeZoneRouting(float[][] distanceFields, Obstacle[][] exitZones, int[] exitTargets)
    {
        DistanceFields = distanceFields;
        ExitZones = exitZones;
        ExitTargets = exitTargets;
    }

    public static async Task<MapSafeZoneRouting> BuildAsync(MapScenario scenario, PotentialFieldGrid grid,
        CancellationToken cancellationToken)
    {
        var groups = scenario.Exits.Select((exit, index) => (Exit: exit, Index: index))
            .GroupBy(item => item.Exit.TargetId ?? $"exit-{item.Index}").ToArray();
        var fields = new float[groups.Length][];
        var zones = new Obstacle[groups.Length][];
        var exitTargets = new int[scenario.Exits.Length];
        var scratch = new PotentialFieldGrid(grid.CellSize, scenario.WorldWidth, scenario.WorldHeight);
        scratch.InitializeStaticMask(grid.ObstacleMask.ToArray());
        for (int target = 0; target < groups.Length; target++)
        {
            zones[target] = groups[target].Select(item => new Obstacle(item.Exit.X - item.Exit.Radius,
                item.Exit.Y - item.Exit.Radius, item.Exit.Radius * 2, item.Exit.Radius * 2)).ToArray();
            foreach (var item in groups[target]) exitTargets[item.Index] = target;
            await scratch.BuildStaticFieldAsync(zones[target], cancellationToken);
            fields[target] = scratch.StaticPotentialFieldMatrix.ToArray();
        }
        return new MapSafeZoneRouting(fields, zones, exitTargets);
    }

    public void Assign(int activeCount, double[] positionsX, double[] positionsY,
        int[] assignments, int[] evacuatedPerExit, PotentialFieldGrid grid, double distanceWeight,
        double occupancyWeight, double worldWidth, double worldHeight)
    {
        var dispatched = new int[DistanceFields.Length];
        for (int exit = 0; exit < evacuatedPerExit.Length; exit++)
            dispatched[ExitTargets[exit]] += evacuatedPerExit[exit];
        double population = Math.Max(1, activeCount + evacuatedPerExit.Sum());
        double distanceScale = Math.Max(1, Math.Sqrt(worldWidth * worldWidth + worldHeight * worldHeight));
        for (int agent = 0; agent < activeCount; agent++)
        {
            int column = Math.Clamp((int)(positionsX[agent] / grid.CellSize), 0, grid.ColumnCount - 1);
            int row = Math.Clamp((int)(positionsY[agent] / grid.CellSize), 0, grid.RowCount - 1);
            int cell = row * grid.ColumnCount + column;
            int selected = -1;
            double bestCost = double.PositiveInfinity;
            for (int target = 0; target < DistanceFields.Length; target++)
            {
                float distance = DistanceFields[target][cell];
                if (distance >= float.MaxValue * 0.5f) continue;
                double cost = distanceWeight * distance / distanceScale + occupancyWeight * dispatched[target] / population;
                if (cost < bestCost) { bestCost = cost; selected = target; }
            }
            assignments[agent] = selected;
            if (selected >= 0) dispatched[selected]++;
        }
    }
}
