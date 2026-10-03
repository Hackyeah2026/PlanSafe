using System;
using System.Collections.Generic;
using System.Linq;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Session;

namespace PlanSafe.App.Simulation;

public record CompiledSimulationScenario(
    string Name,
    string BranchName,
    SimulationGrid Grid,
    Action<CrowdSimulator, int> PopulateAgents,
    int RecommendedAgentCount,
    int SpawnCellCount,
    int ExitCellCount,
    int ObstacleCellCount
);

/// <summary>
/// Compiles a MapSession's geographic entities (zones, safe locations, blockades)
/// into a discrete 2D SimulationGrid with precomputed Dijkstra potential field and agent spawn delegates.
/// Bridges real-world GIS map sessions with the high-performance crowd simulation engine.
/// </summary>
public static class SessionSimulationCompiler
{
    private const double MetersPerDegreeLat = 111320.0;

    public static CompiledSimulationScenario Compile(MapSession session)
    {
        var config = session.SimulationConfig ?? new SimulationConfig();
        var items = session.Items ?? new List<MapZoneItem>();

        // 1. Calculate Geographic Bounding Box
        var (minLat, maxLat, minLng, maxLng) = ComputeBoundingBox(session, items);

        // Calculate metric dimensions
        double midLat = (minLat + maxLat) / 2.0;
        double metersPerDegreeLng = MetersPerDegreeLat * Math.Cos(midLat * Math.PI / 180.0);

        float widthMeters = Math.Max(16.0f, (float)((maxLng - minLng) * metersPerDegreeLng));
        float heightMeters = Math.Max(16.0f, (float)((maxLat - minLat) * MetersPerDegreeLat));

        // Limit maximum grid size for performant WASM computation (e.g. max 120m x 80m at 0.5m)
        widthMeters = Math.Min(120.0f, widthMeters);
        heightMeters = Math.Min(80.0f, heightMeters);
        float cellSize = Math.Clamp(config.CellSize, 0.25f, 1.0f);

        var grid = new SimulationGrid(widthMeters, heightMeters, cellSize);
        Array.Clear(grid.Cells, 0, grid.TotalCells);

        // Helper coordinate projection: GIS (lat, lng) -> Metric Local Grid (x, y)
        float ProjectX(double lng) =>
            Math.Clamp((float)((lng - minLng) / (maxLng - minLng) * widthMeters), 0f, widthMeters);

        float ProjectY(double lat) =>
            Math.Clamp((float)((lat - minLat) / (maxLat - minLat) * heightMeters), 0f, heightMeters);

        // 2. Rasterize Blockades (Obstacles)
        int obstacleCells = 0;
        foreach (var blockade in items.OfType<BlockadeZoneItem>())
        {
            if (blockade.StartPoint is { Length: >= 2 } sp && blockade.EndPoint is { Length: >= 2 } ep)
            {
                float x1 = ProjectX(sp[1]);
                float y1 = ProjectY(sp[0]);
                float x2 = ProjectX(ep[1]);
                float y2 = ProjectY(ep[0]);

                obstacleCells += RasterizeLine(grid, x1, y1, x2, y2, thicknessMeters: 1.5f, CellType.Obstacle);
            }
        }

        // 3. Rasterize Safe Locations (Exits)
        int exitCells = 0;
        foreach (var item in items.Where(i => i.Category == ZoneCategory.SafeLocation))
        {
            switch (item)
            {
                case SafeCircleZoneItem circle when circle.Center is { Length: >= 2 } && circle.Radius.HasValue:
                    {
                        float cx = ProjectX(circle.Center[1]);
                        float cy = ProjectY(circle.Center[0]);
                        float r = Math.Min((float)circle.Radius.Value, Math.Min(widthMeters, heightMeters) * 0.35f);
                        exitCells += RasterizeCircle(grid, cx, cy, Math.Max(1.0f, r), CellType.Exit);
                        break;
                    }
                case SafePolygonZoneItem poly when poly.Coordinates is { Count: >= 3 }:
                    {
                        var pts = poly.Coordinates.Select(c => (ProjectX(c[1]), ProjectY(c[0]))).ToList();
                        exitCells += RasterizePolygon(grid, pts, CellType.Exit);
                        break;
                    }
                case SafePointZoneItem pt when pt.Position is { Length: >= 2 }:
                    {
                        float px = ProjectX(pt.Position[1]);
                        float py = ProjectY(pt.Position[0]);
                        exitCells += RasterizeCircle(grid, px, py, 2.0f, CellType.Exit);
                        break;
                    }
            }
        }

        // 4. Rasterize Evacuation Zones (Spawns)
        int spawnCells = 0;
        var spawnCellIndices = new List<int>();

        foreach (var item in items.Where(i => i.Category == ZoneCategory.EvacuationZone))
        {
            switch (item)
            {
                case EvacCircleZoneItem circle when circle.Center is { Length: >= 2 } && circle.Radius.HasValue:
                    {
                        float cx = ProjectX(circle.Center[1]);
                        float cy = ProjectY(circle.Center[0]);
                        float r = Math.Min((float)circle.Radius.Value, Math.Min(widthMeters, heightMeters) * 0.35f);
                        spawnCells += RasterizeCircle(grid, cx, cy, Math.Max(1.0f, r), CellType.Spawn, spawnCellIndices);
                        break;
                    }
                case EvacPolygonZoneItem poly when poly.Coordinates is { Count: >= 3 }:
                    {
                        var pts = poly.Coordinates.Select(c => (ProjectX(c[1]), ProjectY(c[0]))).ToList();
                        spawnCells += RasterizePolygon(grid, pts, CellType.Spawn, spawnCellIndices);
                        break;
                    }
            }
        }

        // Fallbacks if no exits or spawns were placed on the map
        if (exitCells == 0)
        {
            // Default exit on right perimeter
            int rStart = (int)(grid.Rows * 0.4f);
            int rEnd = (int)(grid.Rows * 0.6f);
            for (int r = rStart; r <= rEnd; r++)
            {
                grid.SetCell(grid.Cols - 1, r, CellType.Exit);
                grid.SetCell(grid.Cols - 2, r, CellType.Exit);
                exitCells += 2;
            }
        }

        if (spawnCells == 0)
        {
            // Default spawn region on left perimeter
            int rStart = (int)(grid.Rows * 0.3f);
            int rEnd = (int)(grid.Rows * 0.7f);
            int cStart = 1;
            int cEnd = Math.Min(grid.Cols - 5, (int)(grid.Cols * 0.35f));
            for (int r = rStart; r <= rEnd; r++)
            {
                for (int c = cStart; c <= cEnd; c++)
                {
                    if (grid.GetCell(c, r) == CellType.Walkable)
                    {
                        grid.SetCell(c, r, CellType.Spawn);
                        spawnCellIndices.Add(grid.GetIndex(c, r));
                        spawnCells++;
                    }
                }
            }
        }

        // 5. Compute Dijkstra potential field towards exits
        grid.ComputeDijkstraField();

        // 6. Define PopulateAgents delegate
        Action<CrowdSimulator, int> populateAgents = (sim, agentCount) =>
        {
            sim.Reset();
            sim.AgentRadius = config.AgentRadius;
            var rng = new Random(42);

            if (spawnCellIndices.Count == 0) return;

            for (int i = 0; i < agentCount; i++)
            {
                int cellIdx = spawnCellIndices[rng.Next(spawnCellIndices.Count)];
                int c = cellIdx % grid.Cols;
                int r = cellIdx / grid.Cols;

                float x = (c + 0.1f + (float)rng.NextDouble() * 0.8f) * grid.CellSize;
                float y = (r + 0.1f + (float)rng.NextDouble() * 0.8f) * grid.CellSize;

                sim.SpawnAgent(x, y);
            }
        };

        return new CompiledSimulationScenario(
            Name: session.Name ?? session.BranchName,
            BranchName: session.BranchName,
            Grid: grid,
            PopulateAgents: populateAgents,
            RecommendedAgentCount: config.AgentCount,
            SpawnCellCount: spawnCells,
            ExitCellCount: exitCells,
            ObstacleCellCount: obstacleCells
        );
    }

    private static (double minLat, double maxLat, double minLng, double maxLng) ComputeBoundingBox(
        MapSession session,
        List<MapZoneItem> items)
    {
        var lats = new List<double>();
        var lngs = new List<double>();

        foreach (var item in items)
        {
            switch (item)
            {
                case CircleMapZoneItem circle when circle.Center is { Length: >= 2 }:
                    double rDegLat = (circle.Radius ?? 50.0) / MetersPerDegreeLat;
                    double rDegLng = rDegLat / Math.Cos(circle.Center[0] * Math.PI / 180.0);
                    lats.Add(circle.Center[0] - rDegLat);
                    lats.Add(circle.Center[0] + rDegLat);
                    lngs.Add(circle.Center[1] - rDegLng);
                    lngs.Add(circle.Center[1] + rDegLng);
                    break;

                case PolygonMapZoneItem poly when poly.Coordinates != null:
                    foreach (var c in poly.Coordinates.Where(c => c is { Length: >= 2 }))
                    {
                        lats.Add(c[0]);
                        lngs.Add(c[1]);
                    }
                    break;

                case LineMapZoneItem line:
                    if (line.StartPoint is { Length: >= 2 }) { lats.Add(line.StartPoint[0]); lngs.Add(line.StartPoint[1]); }
                    if (line.EndPoint is { Length: >= 2 }) { lats.Add(line.EndPoint[0]); lngs.Add(line.EndPoint[1]); }
                    break;

                case PointMapZoneItem pt when pt.Position is { Length: >= 2 }:
                    lats.Add(pt.Position[0]);
                    lngs.Add(pt.Position[1]);
                    break;
            }
        }

        if (lats.Count < 2)
        {
            // Center around session map center with ~300m radius
            double cLat = session.MapCenter[0];
            double cLng = session.MapCenter[1];
            double margin = 0.003; // ~330m
            return (cLat - margin, cLat + margin, cLng - margin, cLng + margin);
        }

        double minLat = lats.Min();
        double maxLat = lats.Max();
        double minLng = lngs.Min();
        double maxLng = lngs.Max();

        // Add 25% boundary padding
        double padLat = Math.Max(0.0008, (maxLat - minLat) * 0.15);
        double padLng = Math.Max(0.0008, (maxLng - minLng) * 0.15);

        return (minLat - padLat, maxLat + padLat, minLng - padLng, maxLng + padLng);
    }

    private static int RasterizeLine(SimulationGrid grid, float x1, float y1, float x2, float y2, float thicknessMeters, CellType cellType)
    {
        int count = 0;
        float dist = MathF.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        int steps = Math.Max(2, (int)(dist / (grid.CellSize * 0.5f)));

        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            float px = x1 + (x2 - x1) * t;
            float py = y1 + (y2 - y1) * t;

            count += RasterizeCircle(grid, px, py, thicknessMeters * 0.5f, cellType);
        }

        return count;
    }

    private static int RasterizeCircle(SimulationGrid grid, float cx, float cy, float radius, CellType cellType, List<int>? indices = null)
    {
        int count = 0;
        int minC = Math.Max(0, (int)((cx - radius) / grid.CellSize));
        int maxC = Math.Min(grid.Cols - 1, (int)((cx + radius) / grid.CellSize));
        int minR = Math.Max(0, (int)((cy - radius) / grid.CellSize));
        int maxR = Math.Min(grid.Rows - 1, (int)((cy + radius) / grid.CellSize));

        float rSq = radius * radius;

        for (int r = minR; r <= maxR; r++)
        {
            float py = (r + 0.5f) * grid.CellSize;
            for (int c = minC; c <= maxC; c++)
            {
                float px = (c + 0.5f) * grid.CellSize;
                float dSq = (px - cx) * (px - cx) + (py - cy) * (py - cy);
                if (dSq <= rSq)
                {
                    grid.SetCell(c, r, cellType);
                    indices?.Add(grid.GetIndex(c, r));
                    count++;
                }
            }
        }

        return count;
    }

    private static int RasterizePolygon(SimulationGrid grid, List<(float X, float Y)> vertices, CellType cellType, List<int>? indices = null)
    {
        int count = 0;
        float minX = vertices.Min(v => v.X);
        float maxX = vertices.Max(v => v.X);
        float minY = vertices.Min(v => v.Y);
        float maxY = vertices.Max(v => v.Y);

        int minC = Math.Max(0, (int)(minX / grid.CellSize));
        int maxC = Math.Min(grid.Cols - 1, (int)(maxX / grid.CellSize));
        int minR = Math.Max(0, (int)(minY / grid.CellSize));
        int maxR = Math.Min(grid.Rows - 1, (int)(maxY / grid.CellSize));

        for (int r = minR; r <= maxR; r++)
        {
            float py = (r + 0.5f) * grid.CellSize;
            for (int c = minC; c <= maxC; c++)
            {
                float px = (c + 0.5f) * grid.CellSize;
                if (PointInPolygon(px, py, vertices))
                {
                    grid.SetCell(c, r, cellType);
                    indices?.Add(grid.GetIndex(c, r));
                    count++;
                }
            }
        }

        return count;
    }

    private static bool PointInPolygon(float x, float y, List<(float X, float Y)> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if (((poly[i].Y > y) != (poly[j].Y > y)) &&
                (x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X))
            {
                inside = !inside;
            }
        }
        return inside;
    }
}
