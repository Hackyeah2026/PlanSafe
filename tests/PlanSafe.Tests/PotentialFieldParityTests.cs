using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Models.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class PotentialFieldParityTests
{
    [Fact]
    public void ExportReferenceFieldsForWebGpuParity()
    {
        var cases = new[]
        {
            MakeCase("unlimited-zones", 200, 200, 2, 1, 0, 1, unlimited: true),
            MakeCase("two-shelters", 200, 200, 2, 1, 0.5, 0.5),
            MakeCase("uncongested", 200, 200, 2, 1, 1, 0, crowded: false),
            MakeCase("macro-density", 60, 40, 2, 16, 0.2, 0.8),
            MakeCase("thin-obstacle", 60, 40, 2, 1, 1, 0, thin: true),
            MakeCase("full-shelter", 200, 200, 2, 1, 0.5, 0.5, full: true),
            MakeCase("inactive-shelter", 200, 200, 2, 1, 1, 0, inactive: true),
            MakeCase("single-shelter", 60, 40, 2, 1, 0.5, 0.5, single: true),
            MakeCase("long-detour", 200, 200, 2, 1, 1, 0, maze: true),
            MakeCase("occupied-exits", 60, 40, 2, 16, 0.5, 0.5, atExits: true),
        };
        // The Node parity runner requests these outputs and compares the actual
        // production TypeScript and GPU fields against every C# cell.
        var destination = Environment.GetEnvironmentVariable("PLANSAFE_FIELD_REFERENCE");
        if (!string.IsNullOrEmpty(destination))
            File.WriteAllText(destination, JsonSerializer.Serialize(cases, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static object MakeCase(string name, double width, double height, double cellSize,
        int granulation, double weightDistance, double weightOccupancy, bool crowded = true,
        bool thin = false, bool full = false, bool inactive = false, bool single = false,
        bool maze = false, bool atExits = false, bool unlimited = false)
    {
        var obstacles = new[]
        {
            new Obstacle(width * 0.35, height * 0.18, thin ? 0.2 : width * 0.12, height * 0.28),
            new Obstacle(width * 0.35, height * 0.54, width * 0.12, height * 0.28)
        };
        var targets = new[]
        {
            new EvacuationTarget("north", "North", width * 0.9, height * 0.12, width * 0.08, height * 0.2, 300, full ? 300 : 35),
            new EvacuationTarget("south", "South", width * 0.9, height * 0.68, width * 0.08, height * 0.2, 300, 15, !inactive)
        };
        if (unlimited) targets = targets.Select(t => t with { Capacity = 0 }).ToArray();
        if (single) targets = targets.Take(1).ToArray();
        if (maze)
        {
            obstacles = new[] { new Obstacle(70, 0, 4, 180), new Obstacle(130, 20, 4, 180) };
            targets = new[] { new EvacuationTarget("exit", "Exit", 184, 80, 12, 40, 300, 0) };
        }
        var grid = new PotentialFieldGrid(cellSize, width, height);
        grid.InitializeStaticObstacles(obstacles);
        grid.BuildStaticField(obstacles, targets, weightDistance, weightOccupancy, width);
        var count = crowded ? 120 : 1;
        var xs = Enumerable.Range(0, count).Select(i => width * 0.32 + (i % 8) * 0.04).ToArray();
        var ys = Enumerable.Range(0, count).Select(i => height * 0.5 + (i % 5) * 0.04).ToArray();
        if (atExits)
        {
            xs = Enumerable.Range(0, count).Select(i => targets[i % targets.Length].CenterX).ToArray();
            ys = Enumerable.Range(0, count).Select(i => targets[i % targets.Length].CenterY).ToArray();
        }
        grid.UpdateDynamicDensity(count, xs, ys, granulation);
        grid.RefineDynamicField();
        // InitializeAgents updates density once; the first UpdatePhysics does
        // so again before positions move. Exercise that same EMA history.
        grid.UpdateDynamicDensity(count, xs, ys, granulation);
        grid.RefineDynamicField();
        Assert.Contains(grid.StaticPotentialFieldMatrix, p => p < float.MaxValue);
        if (crowded && !atExits) Assert.Contains(grid.DynamicCrowdPenalty, p => p > 0.5f);
        else Assert.Equal(grid.StaticPotentialFieldMatrix, grid.DynamicPotentialFieldMatrix);
        var samples = Enumerable.Range(1, 12).Select(i => new[] { width * 0.32, height * i / 13.0 })
            .Concat(targets.Where(t => t.IsActive).Select(t => new[] { t.X - 0.1, t.CenterY })).ToArray();
        var flows = samples.Select(p => { var (x, y) = grid.GetFlowDirection(p[0], p[1]); return new[] { x, y }; }).ToArray();
        return new
        {
            name,
            width,
            height,
            cellSize,
            granulation,
            weightDistance,
            weightOccupancy,
            cols = grid.ColumnCount,
            rows = grid.RowCount,
            obstacles = obstacles.Select(o => new[] { o.X, o.Y, o.Width, o.Height }),
            targets,
            xs,
            ys,
            blocked = Enumerable.Range(0, grid.TotalCells).Select(i => grid.IsObstacleCell(i % grid.ColumnCount, i / grid.ColumnCount) ? 1 : 0),
            staticField = grid.StaticPotentialFieldMatrix,
            dynamicField = grid.DynamicPotentialFieldMatrix,
            density = grid.GetSmoothedDensitiesCopy(),
            penalty = grid.DynamicCrowdPenalty,
            samples,
            flows
        };
    }
}
