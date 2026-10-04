using System;

namespace PlanSafe.App.Simulation;

/// <summary>Collects nearby raster walls without allocating per-agent obstacles.</summary>
internal sealed class SimulationObstacleQuery
{
    private readonly Obstacle[] _obstacleBuffer = CreateObstacleBuffer(64);

    private static Obstacle[] CreateObstacleBuffer(int length)
    {
        var buffer = new Obstacle[length];
        for (int i = 0; i < length; i++) buffer[i] = new Obstacle(0, 0, 0, 0);
        return buffer;
    }

    /// <summary>
    /// Preset mode returns the static obstacles unchanged. Map mode turns blocked raster cells near the
    /// agent into small boxes. For forces, contactsOnly keeps the nearest wall plus at most one wall facing
    /// another way, so a straight wall made of many cells pushes like a single wall.
    /// </summary>
    public int Collect(MapScenario? scenario, Obstacle[] presetObstacles, double positionX, double positionY, double range, bool contactsOnly, out Obstacle[] obstacles)
    {
        if (scenario is null)
        {
            obstacles = presetObstacles;
            return presetObstacles.Length;
        }

        obstacles = _obstacleBuffer;
        double cellSize = scenario.CellSize;
        int columns = scenario.Columns;
        bool[] blocked = scenario.Blocked;
        int minimumColumn = Math.Max(0, (int)Math.Floor((positionX - range) / cellSize));
        int maximumColumn = Math.Min(columns - 1, (int)Math.Floor((positionX + range) / cellSize));
        int minimumRow = Math.Max(0, (int)Math.Floor((positionY - range) / cellSize));
        int maximumRow = Math.Min(scenario.Rows - 1, (int)Math.Floor((positionY + range) / cellSize));

        if (!contactsOnly)
        {
            int count = 0;
            for (int row = minimumRow; row <= maximumRow; row++)
            {
                for (int column = minimumColumn; column <= maximumColumn; column++)
                {
                    if (!blocked[row * columns + column]) continue;
                    SetBox(_obstacleBuffer[count++], column, row, cellSize);
                    if (count == _obstacleBuffer.Length) return count;
                }
            }
            return count;
        }

        double rangeSquared = range * range;
        int nearestColumn = -1, nearestRow = -1;
        double nearestDistanceSquared = double.MaxValue, nearestNormalX = 0, nearestNormalY = 0;
        for (int row = minimumRow; row <= maximumRow; row++)
        {
            for (int column = minimumColumn; column <= maximumColumn; column++)
            {
                if (!blocked[row * columns + column]) continue;
                double closestX = Math.Clamp(positionX, column * cellSize, (column + 1) * cellSize);
                double closestY = Math.Clamp(positionY, row * cellSize, (row + 1) * cellSize);
                double distanceSquared = (positionX - closestX) * (positionX - closestX) + (positionY - closestY) * (positionY - closestY);
                if (distanceSquared <= 0.000001 || distanceSquared >= rangeSquared || distanceSquared >= nearestDistanceSquared) continue;
                nearestDistanceSquared = distanceSquared;
                nearestColumn = column;
                nearestRow = row;
                double distance = Math.Sqrt(distanceSquared);
                nearestNormalX = (positionX - closestX) / distance;
                nearestNormalY = (positionY - closestY) / distance;
            }
        }
        if (nearestColumn < 0) return 0;
        SetWallBox(_obstacleBuffer[0], scenario, nearestColumn, nearestRow, Math.Abs(nearestNormalX) >= Math.Abs(nearestNormalY));

        int secondColumn = -1, secondRow = -1;
        double secondDistanceSquared = double.MaxValue;
        bool secondNormalIsHorizontal = false;
        for (int row = minimumRow; row <= maximumRow; row++)
        {
            for (int column = minimumColumn; column <= maximumColumn; column++)
            {
                if (!blocked[row * columns + column] || (column == nearestColumn && row == nearestRow)) continue;
                double closestX = Math.Clamp(positionX, column * cellSize, (column + 1) * cellSize);
                double closestY = Math.Clamp(positionY, row * cellSize, (row + 1) * cellSize);
                double distanceSquared = (positionX - closestX) * (positionX - closestX) + (positionY - closestY) * (positionY - closestY);
                if (distanceSquared <= 0.000001 || distanceSquared >= rangeSquared || distanceSquared >= secondDistanceSquared) continue;
                double distance = Math.Sqrt(distanceSquared);
                if ((positionX - closestX) / distance * nearestNormalX + (positionY - closestY) / distance * nearestNormalY > 0.5) continue;
                secondDistanceSquared = distanceSquared;
                secondColumn = column;
                secondRow = row;
                secondNormalIsHorizontal = Math.Abs(positionX - closestX) >= Math.Abs(positionY - closestY);
            }
        }
        if (secondColumn < 0) return 1;
        SetWallBox(_obstacleBuffer[1], scenario, secondColumn, secondRow, secondNormalIsHorizontal);
        return 2;
    }

    /// <summary>
    /// Grows a contact cell along the wall it belongs to, so the box center used to pick the bypass
    /// side stays stable while an agent slides along a building instead of flipping between cells.
    /// </summary>
    private static void SetWallBox(Obstacle box, MapScenario scenario, int column, int row, bool normalIsHorizontal)
    {
        const int maximumRun = 24;
        int columns = scenario.Columns;
        bool[] blocked = scenario.Blocked;
        double cellSize = scenario.CellSize;
        if (normalIsHorizontal)
        {
            int top = row, bottom = row;
            while (top > 0 && row - top < maximumRun && blocked[(top - 1) * columns + column]) top--;
            while (bottom < scenario.Rows - 1 && bottom - row < maximumRun && blocked[(bottom + 1) * columns + column]) bottom++;
            box.X = column * cellSize;
            box.Y = top * cellSize;
            box.Width = cellSize;
            box.Height = (bottom - top + 1) * cellSize;
        }
        else
        {
            int left = column, right = column;
            while (left > 0 && column - left < maximumRun && blocked[row * columns + left - 1]) left--;
            while (right < columns - 1 && right - column < maximumRun && blocked[row * columns + right + 1]) right++;
            box.X = left * cellSize;
            box.Y = row * cellSize;
            box.Width = (right - left + 1) * cellSize;
            box.Height = cellSize;
        }
    }

    private static void SetBox(Obstacle box, int column, int row, double cellSize)
    {
        box.X = column * cellSize;
        box.Y = row * cellSize;
        box.Width = cellSize;
        box.Height = cellSize;
    }
}
