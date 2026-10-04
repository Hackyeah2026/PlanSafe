using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;
using PlanSafe.App.Services.Osm;

namespace PlanSafe.App.Simulation;

public sealed record PreparedMapSimulation(MapSimulationBounds Bounds, MapScenario Scenario, List<EvacuationTarget> Targets);

/// <summary>Converts geographic zones and terrain into the existing simulation raster.</summary>
public static class MapSimulationScenarioPreparer
{
    public static async Task<PreparedMapSimulation> PrepareAsync(
        MapSimulationBounds bounds,
        List<MapZoneItem> mapItems,
        List<MapZoneItem> evacuationZones,
        List<MapZoneItem> safeLocations,
        IOsmObstacleService terrainService,
        CancellationToken cancellationToken)
    {
        var (targets, targetShapes) = BuildTargets(bounds, safeLocations);
        double rasterCellSize = Math.Max(2.0, Math.Ceiling(Math.Max(bounds.WorldWidth, bounds.WorldHeight) / 600.0 * 2) / 2);
        var builder = new MapScenarioBuilder(bounds.WorldWidth, bounds.WorldHeight, cellSize: rasterCellSize);
        await terrainService.RasterizeTerrainAsync(builder, bounds.MinimumLatitude, bounds.MaximumLatitude,
            bounds.MinimumLongitude, bounds.MaximumLongitude, bounds.ToWorld, cancellationToken);
        ApplyBlockades(builder, mapItems, bounds, rasterCellSize);
        var mapExits = BuildExits(builder, bounds, targets, targetShapes, rasterCellSize);
        var mapSpawnZones = BuildSpawnZones(bounds, evacuationZones);
        var scenario = builder.Build(bounds.MaximumLatitude, bounds.MinimumLongitude,
            bounds.MetersPerDegreeLatitude, bounds.MetersPerDegreeLongitude,
            mapExits.ToArray(), mapSpawnZones.ToArray());
        return new PreparedMapSimulation(bounds, scenario, targets);
    }

    private static (List<EvacuationTarget> Targets, Dictionary<string, (double[] Xs, double[] Ys)> Shapes)
        BuildTargets(MapSimulationBounds bounds, List<MapZoneItem> safeLocations)
    {
        var toWorld = bounds.ToWorld;
        double worldWidth = bounds.WorldWidth, worldHeight = bounds.WorldHeight;
        var targets = new List<EvacuationTarget>();
        var targetShapes = new Dictionary<string, (double[] Xs, double[] Ys)>();
        foreach (var item in safeLocations)
        {
            switch (item)
            {
                case SafeCircleZoneItem circle when circle.Center is { Length: >= 2 }:
                    var (centerX, centerY) = toWorld(circle.Center[0], circle.Center[1]);
                    double radius = circle.Radius ?? 30.0;
                    targets.Add(new EvacuationTarget(item.Id, string.IsNullOrWhiteSpace(item.Name) ? "Schron" : item.Name, centerX - radius, centerY - radius, radius * 2, radius * 2, 1000, 0, true));
                    var circleXCoordinates = new double[32];
                    var circleYCoordinates = new double[32];
                    for (int vertexIndex = 0; vertexIndex < 32; vertexIndex++)
                    {
                        double angle = vertexIndex * 2.0 * Math.PI / 32.0;
                        circleXCoordinates[vertexIndex] = centerX + radius * Math.Cos(angle);
                        circleYCoordinates[vertexIndex] = centerY + radius * Math.Sin(angle);
                    }
                    targetShapes[item.Id] = (circleXCoordinates, circleYCoordinates);
                    break;
                case SafePolygonZoneItem polygon when polygon.Coordinates is { Count: >= 3 }:
                    var coordinates = polygon.Coordinates.Where(c => c is { Length: >= 2 }).ToList();
                    double polygonMinimumX = coordinates.Min(c => toWorld(c[0], c[1]).X);
                    double polygonMaximumX = coordinates.Max(c => toWorld(c[0], c[1]).X);
                    double polygonMinimumY = coordinates.Min(c => toWorld(c[0], c[1]).Y);
                    double polygonMaximumY = coordinates.Max(c => toWorld(c[0], c[1]).Y);
                    targets.Add(new EvacuationTarget(item.Id, string.IsNullOrWhiteSpace(item.Name) ? "Schron" : item.Name, polygonMinimumX, polygonMinimumY, Math.Max(10.0, polygonMaximumX - polygonMinimumX), Math.Max(10.0, polygonMaximumY - polygonMinimumY), 1000, 0, true));
                    targetShapes[item.Id] = (coordinates.Select(c => toWorld(c[0], c[1]).X).ToArray(), coordinates.Select(c => toWorld(c[0], c[1]).Y).ToArray());
                    break;
                case SafePointZoneItem point when point.Position is { Length: >= 2 }:
                    var (positionX, positionY) = toWorld(point.Position[0], point.Position[1]);
                    targets.Add(new EvacuationTarget(item.Id, string.IsNullOrWhiteSpace(item.Name) ? "Wyjście" : item.Name, positionX - 4.0, positionY - 4.0, 8.0, 8.0, 1000, 0, true));
                    break;
            }
        }
        if (targets.Count == 0)
        {
            targets.Add(new EvacuationTarget("shelter-east", "Wyjście Główne", worldWidth * 0.90, worldHeight * 0.45, worldWidth * 0.08, worldHeight * 0.10, 1000, 0, true));
        }
        return (targets, targetShapes);
    }

    private static void ApplyBlockades(MapScenarioBuilder builder, List<MapZoneItem> mapItems,
        MapSimulationBounds bounds, double rasterCellSize)
    {
        var toWorld = bounds.ToWorld;
        foreach (var blockade in mapItems.OfType<BlockadeZoneItem>())
        {
            if (blockade.StartPoint is { Length: >= 2 } startPoint && blockade.EndPoint is { Length: >= 2 } endPoint)
            {
                var startPosition = toWorld(startPoint[0], startPoint[1]);
                var endPosition = toWorld(endPoint[0], endPoint[1]);
                builder.SetCorridor([startPosition.X, endPosition.X], [startPosition.Y, endPosition.Y], halfWidth: Math.Max(1.5, rasterCellSize), isWalkable: false);
            }
        }
    }

    private static List<MapExit> BuildExits(MapScenarioBuilder builder, MapSimulationBounds bounds,
        List<EvacuationTarget> targets, Dictionary<string, (double[] Xs, double[] Ys)> targetShapes, double rasterCellSize)
    {
        double worldWidth = bounds.WorldWidth, worldHeight = bounds.WorldHeight;
        // Snap exit centers onto nearby walkable ground and
        // open only a small disk there, so the exit never cuts a hole through surrounding buildings.
        var mapExits = new List<MapExit>();
        foreach (var target in targets)
        {
            // Polygon / circle zones: cover the real shape with small square exit tiles. Only isBorderTile
            // tiles are needed (peoplePerZone enter from outside), which keeps the exit count small. The
            // engine's single-square exit model therefore follows the drawn outline closely instead
            // of catching agents insideShape a large bounding square far outside the zone.
            if (targetShapes.TryGetValue(target.Id, out var shape))
            {
                double exitTileSize = Math.Max(2.0 * rasterCellSize, 4.0);
                double shapeMinimumX = Math.Max(0, shape.Xs.Min()), shapeMaximumX = Math.Min(worldWidth, shape.Xs.Max());
                double shapeMinimumY = Math.Max(0, shape.Ys.Min()), shapeMaximumY = Math.Min(worldHeight, shape.Ys.Max());
                while (((shapeMaximumX - shapeMinimumX) / exitTileSize) * ((shapeMaximumY - shapeMinimumY) / exitTileSize) > 40000) exitTileSize *= 1.5;
                int tileColumns = Math.Max(1, (int)Math.Ceiling((shapeMaximumX - shapeMinimumX) / exitTileSize));
                int tileRows = Math.Max(1, (int)Math.Ceiling((shapeMaximumY - shapeMinimumY) / exitTileSize));
                var insideShape = new bool[tileColumns, tileRows];
                for (int column = 0; column < tileColumns; column++)
                    for (int row = 0; row < tileRows; row++)
                        insideShape[column, row] = MapScenario.IsPointInPolygon(shapeMinimumX + (column + 0.5) * exitTileSize, shapeMinimumY + (row + 0.5) * exitTileSize, shape.Xs, shape.Ys);
                int addedExitCount = 0;
                for (int column = 0; column < tileColumns; column++)
                {
                    for (int row = 0; row < tileRows; row++)
                    {
                        if (!insideShape[column, row]) continue;
                        bool isBorderTile = column == 0 || row == 0 || column == tileColumns - 1 || row == tileRows - 1 ||
                                      !insideShape[column - 1, row] || !insideShape[column + 1, row] || !insideShape[column, row - 1] || !insideShape[column, row + 1];
                        if (!isBorderTile) continue;
                        double tileCenterX = shapeMinimumX + (column + 0.5) * exitTileSize, tileCenterY = shapeMinimumY + (row + 0.5) * exitTileSize;
                        if (tileCenterX <= 0 || tileCenterY <= 0 || tileCenterX >= worldWidth || tileCenterY >= worldHeight) continue;
                        mapExits.Add(new MapExit(tileCenterX, tileCenterY, exitTileSize * 0.5));
                        addedExitCount++;
                    }
                }
                if (addedExitCount > 0) continue;
            }

            // Point zones (or degenerate shapes): one small exit snapped onto walkable ground,
            // opening only a small disk so it never cuts a hole through surrounding buildings.
            double centerX = target.X + target.Width * 0.5;
            double centerY = target.Y + target.Height * 0.5;
            double radius = Math.Max(4.0, Math.Min(target.Width, target.Height) * 0.5);
            int exitColumn = Math.Clamp((int)(centerX / rasterCellSize), 0, builder.Columns - 1);
            int exitRow = Math.Clamp((int)(centerY / rasterCellSize), 0, builder.Rows - 1);
            if (!builder.IsWalkable(exitColumn, exitRow) && builder.FindNearestWalkable(centerX, centerY, Math.Max(30.0, radius)) is { } snapped)
            {
                centerX = snapped.X;
                centerY = snapped.Y;
            }
            mapExits.Add(new MapExit(centerX, centerY, radius));
            builder.SetDisk(centerX, centerY, Math.Max(2.0, rasterCellSize), isWalkable: true);
        }
        return mapExits;
    }

    private static List<MapSpawnZone> BuildSpawnZones(MapSimulationBounds bounds, List<MapZoneItem> evacuationZones)
    {
        var toWorld = bounds.ToWorld;
        double worldWidth = bounds.WorldWidth, worldHeight = bounds.WorldHeight;
        var mapSpawnZones = new List<MapSpawnZone>();
        foreach (var zone in evacuationZones)
        {
            int peoplePerZone = 100;
            switch (zone)
            {
                case PolygonMapZoneItem polygon when polygon.Coordinates is { Count: >= 3 }:
                    var coordinates = polygon.Coordinates.Where(c => c is { Length: >= 2 }).ToList();
                    var xCoordinates = coordinates.Select(c => toWorld(c[0], c[1]).X).ToArray();
                    var yCoordinates = coordinates.Select(c => toWorld(c[0], c[1]).Y).ToArray();
                    mapSpawnZones.Add(new MapSpawnZone(xCoordinates, yCoordinates, peoplePerZone));
                    break;
                case CircleMapZoneItem circle when circle.Center is { Length: >= 2 }:
                    var (centerX, centerY) = toWorld(circle.Center[0], circle.Center[1]);
                    double radius = circle.Radius ?? 30.0;
                    var circleXCoordinates = new double[16];
                    var circleYCoordinates = new double[16];
                    for (int vertexIndex = 0; vertexIndex < 16; vertexIndex++)
                    {
                        double angle = vertexIndex * 2.0 * Math.PI / 16.0;
                        circleXCoordinates[vertexIndex] = centerX + radius * Math.Cos(angle);
                        circleYCoordinates[vertexIndex] = centerY + radius * Math.Sin(angle);
                    }
                    mapSpawnZones.Add(new MapSpawnZone(circleXCoordinates, circleYCoordinates, peoplePerZone));
                    break;
                case PointMapZoneItem point when point.Position is { Length: >= 2 }:
                    var (positionX, positionY) = toWorld(point.Position[0], point.Position[1]);
                    mapSpawnZones.Add(new MapSpawnZone([positionX - 5, positionX + 5, positionX + 5, positionX - 5], [positionY - 5, positionY - 5, positionY + 5, positionY + 5], peoplePerZone));
                    break;
            }
        }
        if (mapSpawnZones.Count == 0)
        {
            mapSpawnZones.Add(new MapSpawnZone([0, worldWidth, worldWidth, 0], [0, 0, worldHeight, worldHeight], 100));
        }
        return mapSpawnZones;
    }
}
