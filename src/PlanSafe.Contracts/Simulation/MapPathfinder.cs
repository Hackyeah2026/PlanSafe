using System;
using System.Collections.Generic;
using System.Linq;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;

namespace PlanSafe.Contracts.Simulation;

/// <summary>
/// High-performance street raster pathfinder and target evaluator operating directly on a MapScenario.
/// Uses the exact OpenStreetMap raster potential field to compute true walkable distances,
/// evaluate weighted costs balancing distance and occupancy, and trace realistic street polylines.
/// </summary>
public sealed class MapPathfinder
{
    private readonly MapScenario _scenario;
    private readonly float[][] _exitDistanceFields;
    private readonly double _dMax;

    public MapScenario Scenario => _scenario;
    public float[]? GetExitField(int index) => (index >= 0 && index < _exitDistanceFields.Length) ? _exitDistanceFields[index] : null;

    public MapPathfinder(MapScenario scenario)
    {
        _scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
        _exitDistanceFields = new float[scenario.Exits.Length][];
        for (int i = 0; i < scenario.Exits.Length; i++)
        {
            _exitDistanceFields[i] = ComputeExitDistanceField(scenario, scenario.Exits[i]);
        }
        _dMax = Math.Max(100.0, Math.Max(scenario.WorldWidth, scenario.WorldHeight));
    }

    public TargetAssignmentResponse EvaluateAssignment(
        double citizenLat,
        double citizenLng,
        IReadOnlyList<EvacuationTarget> candidateTargets,
        double weightDistance = 0.5,
        double weightOccupancy = 0.5,
        string? currentTargetId = null,
        bool ignoreHysteresis = false)
    {
        if (candidateTargets == null || candidateTargets.Count == 0 || _scenario.Exits.Length == 0)
        {
            return new TargetAssignmentResponse(
                Target: null,
                Distance: 0.0,
                OccupancyRatio: 0.0,
                Instructions: "No evacuation points available in the map scenario.",
                CalculatedCost: double.PositiveInfinity,
                IsSimulationEngineBased: true);
        }

        var (origX, origY) = _scenario.ToWorld(citizenLat, citizenLng);
        double px = origX;
        double py = origY;

        int col = Math.Clamp((int)(px / _scenario.CellSize), 0, _scenario.Columns - 1);
        int row = Math.Clamp((int)(py / _scenario.CellSize), 0, _scenario.Rows - 1);
        int startCellIdx = row * _scenario.Columns + col;

        // Check startCellIdx walkability and exit connectivity
        int connectedExits = _scenario.Blocked[startCellIdx] ? 0 :
            _exitDistanceFields.Count(field => field[startCellIdx] < float.MaxValue - 1000f);

        // If not connected, or if not connected to all exits, look for a nearby cell on the main network
        if (connectedExits < _exitDistanceFields.Length)
        {
            var connected = FindNearestReachableCell(px, py, 200.0);
            if (connected.HasValue)
            {
                int newCol = Math.Clamp((int)(connected.Value.X / _scenario.CellSize), 0, _scenario.Columns - 1);
                int newRow = Math.Clamp((int)(connected.Value.Y / _scenario.CellSize), 0, _scenario.Rows - 1);
                int newIdx = newRow * _scenario.Columns + newCol;
                int newConnected = _exitDistanceFields.Count(field => field[newIdx] < float.MaxValue - 1000f);
                if (newConnected > connectedExits)
                {
                    px = connected.Value.X;
                    py = connected.Value.Y;
                    col = newCol;
                    row = newRow;
                    startCellIdx = newIdx;
                }
            }
        }

        double snapOffset = Math.Sqrt((origX - px) * (origX - px) + (origY - py) * (origY - py));

        double wDist = Math.Max(0.0, weightDistance);
        double wOcc = Math.Max(0.0, weightOccupancy);

        bool hasCapacity = candidateTargets.Any(t => t.CurrentOccupancy < t.Capacity);
        EvacuationTarget? bestTarget = null;
        int bestExitIndex = -1;
        double minCost = double.PositiveInfinity;
        double bestDistance = double.PositiveInfinity;
        double bestOccRatio = 0.0;
        var evaluations = new List<TargetEvaluationDto>();

        EvacuationTarget? currentTargetCandidate = null;
        int currentTargetExitIdx = -1;
        double currentTargetCost = double.PositiveInfinity;
        double currentTargetDistance = double.PositiveInfinity;
        double currentTargetOccRatio = 0.0;
        bool currentTargetIsFull = false;
        bool currentTargetUnreachable = false;

        for (int i = 0; i < candidateTargets.Count; i++)
        {
            var target = candidateTargets[i];
            int exitIdx = FindMatchingExitIndex(target);
            float walkableDist = float.MaxValue;

            if (exitIdx >= 0 && exitIdx < _exitDistanceFields.Length)
            {
                walkableDist = _exitDistanceFields[exitIdx][startCellIdx];
            }

            bool unreachable = walkableDist >= float.MaxValue - 1000f;
            double effectiveDist = unreachable ? 999999.0 : (walkableDist + snapOffset);
            double normDist = Math.Clamp(effectiveDist / _dMax, 0.0, 10.0);

            int cap = Math.Max(1, target.Capacity);

            // Self-occupancy discount: if the citizen is already allocated to this target,
            // discount their own reservation (-1) so it does not penalize their current target.
            bool isCurrentTarget = !string.IsNullOrEmpty(currentTargetId)
                && string.Equals(target.Id, currentTargetId, StringComparison.OrdinalIgnoreCase);
            int evaluatedOccupancy = target.CurrentOccupancy;
            if (isCurrentTarget && evaluatedOccupancy > 0)
            {
                evaluatedOccupancy -= 1;
            }

            double occRatio = (double)evaluatedOccupancy / cap;

            double distCost = wDist * normDist;
            double occCost = wOcc * occRatio;
            // Distance tie-breaker when occupancies are identical or wDist is 0
            double tieBreaker = 0.0001 * normDist;
            double cost = distCost + occCost + tieBreaker;

            bool isFull = evaluatedOccupancy >= cap;
            if (isFull && hasCapacity)
            {
                cost += TargetSelector.CapacityOverflowPenalty;
            }
            if (unreachable)
            {
                cost += TargetSelector.RoadblockBlockagePenalty * 2.0;
            }

            if (isCurrentTarget)
            {
                currentTargetCandidate = target;
                currentTargetExitIdx = exitIdx;
                currentTargetCost = cost;
                currentTargetDistance = effectiveDist;
                currentTargetOccRatio = occRatio;
                currentTargetIsFull = isFull;
                currentTargetUnreachable = unreachable;
            }

            if (cost < minCost - 1e-9 || (Math.Abs(cost - minCost) <= 1e-9 && effectiveDist < bestDistance))
            {
                minCost = cost;
                bestTarget = target;
                bestExitIndex = exitIdx;
                bestDistance = effectiveDist;
                bestOccRatio = occRatio;
            }

            evaluations.Add(new TargetEvaluationDto(
                TargetId: target.Id,
                TargetName: target.Name,
                WalkableDistance: Math.Round(effectiveDist, 1),
                NormalizedDistance: Math.Round(normDist, 3),
                OccupancyRatio: Math.Round(occRatio, 3),
                DistanceCost: Math.Round(wDist > 0 ? distCost : tieBreaker, 4),
                OccupancyCost: Math.Round(occCost, 4),
                TotalCost: Math.Round(cost, 4),
                IsFull: isFull,
                IsSelected: false));
        }

        // Apply hysteresis: if citizen already has a valid, non-full, reachable target,
        // stick with it unless an alternative target is significantly better (>15% lower cost).
        // If occupancy costs are equal/tied (e.g. both shelters empty with wDist=0), distance decides.
        const double HysteresisMargin = 0.15;
        if (!ignoreHysteresis
            && currentTargetCandidate != null
            && !currentTargetUnreachable
            && !currentTargetIsFull
            && currentTargetCandidate.IsActive
            && bestTarget != null
            && !string.Equals(bestTarget.Id, currentTargetCandidate.Id, StringComparison.OrdinalIgnoreCase))
        {
            bool switchTarget = false;

            // 1. Alternate target is significantly cheaper in total cost
            if (minCost < currentTargetCost * (1.0 - HysteresisMargin) || (currentTargetCost - minCost > 0.02))
            {
                switchTarget = true;
            }
            // 2. If costs are virtually tied / equal (e.g. equal occupancy or wDist near 0):
            // Distance must be the decisive tie-breaker! Switch if alternate is at least 15% closer or >= 25m closer.
            else if (minCost <= currentTargetCost + 1e-4)
            {
                if (bestDistance < currentTargetDistance * (1.0 - HysteresisMargin)
                    || bestDistance < currentTargetDistance - 25.0)
                {
                    switchTarget = true;
                }
            }

            if (!switchTarget)
            {
                // Alternate is only marginally better or equal - stick with current target to prevent flapping
                bestTarget = currentTargetCandidate;
                bestExitIndex = currentTargetExitIdx;
                minCost = currentTargetCost;
                bestDistance = currentTargetDistance;
                bestOccRatio = currentTargetOccRatio;
            }
        }

        if (bestTarget != null)
        {
            var selectedIdx = evaluations.FindIndex(e => e.TargetId == bestTarget.Id);
            if (selectedIdx >= 0)
            {
                evaluations[selectedIdx] = evaluations[selectedIdx] with { IsSelected = true };
            }
        }
        else
        {
            bestTarget = candidateTargets[0];
            bestExitIndex = 0;
            bestDistance = 0.0;
            bestOccRatio = bestTarget.OccupancyRatio;
        }

        List<GeoCoordinate> routePath = new();
        if (bestExitIndex >= 0 && bestExitIndex < _scenario.Exits.Length && bestDistance < 999990.0)
        {
            routePath = TraceRoutePath(citizenLat, citizenLng, px, py, bestExitIndex);
        }
        else
        {
            routePath.Add(new GeoCoordinate(citizenLat, citizenLng));
            if (bestTarget.Latitude.HasValue && bestTarget.Longitude.HasValue)
            {
                routePath.Add(new GeoCoordinate(bestTarget.Latitude.Value, bestTarget.Longitude.Value));
            }
        }

        string instructions = $"Head to the evacuation point: {bestTarget.Name} (walking distance: {Math.Round(bestDistance):F0} m).";

        return new TargetAssignmentResponse(
            Target: bestTarget,
            Distance: Math.Round(bestDistance, 1),
            OccupancyRatio: Math.Round(bestOccRatio, 3),
            Instructions: instructions,
            CalculatedCost: Math.Round(minCost, 4),
            BearingDegrees: null,
            FlowDirectionX: null,
            FlowDirectionY: null,
            IsSimulationEngineBased: true,
            TargetEvaluations: evaluations,
            RoutePath: routePath);
    }

    private (double X, double Y)? FindNearestReachableCell(double x, double y, double maxDistance)
    {
        double cellSize = _scenario.CellSize;
        int centerColumn = Math.Clamp((int)(x / cellSize), 0, _scenario.Columns - 1);
        int centerRow = Math.Clamp((int)(y / cellSize), 0, _scenario.Rows - 1);
        int reach = (int)Math.Ceiling(maxDistance / cellSize);
        double bestDistSq = maxDistance * maxDistance;
        (double X, double Y)? best = null;
        int maxReachableExits = 0;

        for (int r = Math.Max(0, centerRow - reach); r <= Math.Min(_scenario.Rows - 1, centerRow + reach); r++)
        {
            for (int c = Math.Max(0, centerColumn - reach); c <= Math.Min(_scenario.Columns - 1, centerColumn + reach); c++)
            {
                int idx = r * _scenario.Columns + c;
                if (_scenario.Blocked[idx]) continue;

                int reachableCount = 0;
                for (int e = 0; e < _exitDistanceFields.Length; e++)
                {
                    if (_exitDistanceFields[e][idx] < float.MaxValue - 1000f)
                    {
                        reachableCount++;
                    }
                }
                if (reachableCount == 0) continue;

                double cx = (c + 0.5) * cellSize;
                double cy = (r + 0.5) * cellSize;
                double dSq = (cx - x) * (cx - x) + (cy - y) * (cy - y);

                // Prefer cells that connect to more exits (the main street network) over isolated pockets.
                // Within the same connectivity level, pick the closest cell.
                if (reachableCount > maxReachableExits || (reachableCount == maxReachableExits && dSq < bestDistSq))
                {
                    maxReachableExits = reachableCount;
                    bestDistSq = dSq;
                    best = (cx, cy);
                }
            }
        }
        return best;
    }

    public int FindMatchingExit(EvacuationTarget target) => FindMatchingExitIndex(target);

    private int FindMatchingExitIndex(EvacuationTarget target)
    {
        if (!string.IsNullOrEmpty(target.Id))
        {
            for (int i = 0; i < _scenario.Exits.Length; i++)
            {
                if (string.Equals(_scenario.Exits[i].TargetId, target.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        double? tLat = target.Latitude ?? (target.Y != 0 ? target.Y : null);
        double? tLng = target.Longitude ?? (target.X != 0 ? target.X : null);

        if (tLat.HasValue && tLng.HasValue)
        {
            var (tx, ty) = _scenario.ToWorld(tLat.Value, tLng.Value);
            double bestDistSq = double.MaxValue;
            int bestIdx = -1;
            for (int i = 0; i < _scenario.Exits.Length; i++)
            {
                var exit = _scenario.Exits[i];
                double dx = exit.X - tx;
                double dy = exit.Y - ty;
                double dSq = dx * dx + dy * dy;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestIdx = i;
                }
            }
            if (bestIdx >= 0) return bestIdx;
        }
        return 0;
    }

    private List<GeoCoordinate> TraceRoutePath(
        double startLat, double startLng,
        double startX, double startY,
        int exitIndex)
    {
        var rawWaypoints = new List<GeoCoordinate> { new(startLat, startLng) };
        var (snapLat, snapLng) = _scenario.ToGeo(startX, startY);
        if (GeoMath.CalculateDistanceMeters(startLat, startLng, snapLat, snapLng) > 3.0)
        {
            rawWaypoints.Add(new GeoCoordinate(snapLat, snapLng));
        }

        var field = _exitDistanceFields[exitIndex];
        var exit = _scenario.Exits[exitIndex];

        double currentX = startX;
        double currentY = startY;
        double cellSize = _scenario.CellSize;
        int cols = _scenario.Columns;
        int rows = _scenario.Rows;

        int[] dCols = { 0, 1, 1, 1, 0, -1, -1, -1 };
        int[] dRows = { -1, -1, 0, 1, 1, 1, 0, -1 };

        int maxSteps = Math.Max(2000, (cols + rows) * 2);
        int step = 0;

        while (step++ < maxSteps)
        {
            double distToExitCenter = Math.Sqrt((currentX - exit.X) * (currentX - exit.X) + (currentY - exit.Y) * (currentY - exit.Y));
            if (distToExitCenter <= Math.Max(exit.Radius, cellSize * 1.5))
            {
                break;
            }

            int col = Math.Clamp((int)(currentX / cellSize), 0, cols - 1);
            int row = Math.Clamp((int)(currentY / cellSize), 0, rows - 1);
            int currentIdx = row * cols + col;
            float currentDist = field[currentIdx];

            float bestNeighbourDist = currentDist;
            int bestNeighbourCol = col;
            int bestNeighbourRow = row;

            for (int d = 0; d < 8; d++)
            {
                int nc = col + dCols[d];
                int nr = row + dRows[d];
                if (nc >= 0 && nc < cols && nr >= 0 && nr < rows)
                {
                    int nIdx = nr * cols + nc;
                    float nd = field[nIdx];
                    if (nd < bestNeighbourDist)
                    {
                        bestNeighbourDist = nd;
                        bestNeighbourCol = nc;
                        bestNeighbourRow = nr;
                    }
                }
            }

            if (bestNeighbourCol == col && bestNeighbourRow == row)
            {
                // If local plateau/minimum is encountered, search 5x5 neighborhood for lower cell
                bool foundLower = false;
                for (int dr = -2; dr <= 2 && !foundLower; dr++)
                {
                    for (int dc = -2; dc <= 2; dc++)
                    {
                        int nc = col + dc;
                        int nr = row + dr;
                        if (nc >= 0 && nc < cols && nr >= 0 && nr < rows)
                        {
                            int nIdx = nr * cols + nc;
                            float nd = field[nIdx];
                            if (nd < bestNeighbourDist)
                            {
                                bestNeighbourDist = nd;
                                bestNeighbourCol = nc;
                                bestNeighbourRow = nr;
                                foundLower = true;
                            }
                        }
                    }
                }
                if (!foundLower)
                {
                    break;
                }
            }

            currentX = (bestNeighbourCol + 0.5) * cellSize;
            currentY = (bestNeighbourRow + 0.5) * cellSize;

            var (geoLat, geoLng) = _scenario.ToGeo(currentX, currentY);
            rawWaypoints.Add(new GeoCoordinate(geoLat, geoLng));
        }

        var (exitLat, exitLng) = _scenario.ToGeo(exit.X, exit.Y);
        rawWaypoints.Add(new GeoCoordinate(exitLat, exitLng));

        return SimplifyPath(rawWaypoints, 3.5);
    }

    public static List<GeoCoordinate> SimplifyPath(IReadOnlyList<GeoCoordinate> points, double epsilonMeters)
    {
        if (points == null || points.Count < 3)
        {
            return points != null ? points.ToList() : new List<GeoCoordinate>();
        }

        var result = new List<GeoCoordinate>();
        RdpRecursive(points, 0, points.Count - 1, epsilonMeters, result);
        result.Add(points[^1]);
        return result;
    }

    private static void RdpRecursive(
        IReadOnlyList<GeoCoordinate> points,
        int startIndex,
        int endIndex,
        double epsilonMeters,
        List<GeoCoordinate> result)
    {
        double maxDist = 0.0;
        int maxIndex = startIndex;

        double startLat = points[startIndex].Lat;
        double startLng = points[startIndex].Lng;
        double endLat = points[endIndex].Lat;
        double endLng = points[endIndex].Lng;

        for (int i = startIndex + 1; i < endIndex; i++)
        {
            double d = GeoMath.DistancePointToSegmentMeters(
                points[i].Lat, points[i].Lng,
                startLat, startLng,
                endLat, endLng);

            if (d > maxDist)
            {
                maxDist = d;
                maxIndex = i;
            }
        }

        if (maxDist > epsilonMeters)
        {
            RdpRecursive(points, startIndex, maxIndex, epsilonMeters, result);
            RdpRecursive(points, maxIndex, endIndex, epsilonMeters, result);
        }
        else
        {
            result.Add(points[startIndex]);
        }
    }

    private static float[] ComputeExitDistanceField(MapScenario scenario, MapExit exit)
    {
        int cols = scenario.Columns;
        int rows = scenario.Rows;
        int total = cols * rows;
        var field = new float[total];
        Array.Fill(field, float.MaxValue);

        double cellSize = scenario.CellSize;
        float stepCard = (float)cellSize;
        float stepDiag = (float)(cellSize * 1.41421356);

        int minCol = Math.Clamp((int)Math.Floor((exit.X - exit.Radius) / cellSize), 0, cols - 1);
        int maxCol = Math.Clamp((int)Math.Floor((exit.X + exit.Radius) / cellSize), 0, cols - 1);
        int minRow = Math.Clamp((int)Math.Floor((exit.Y - exit.Radius) / cellSize), 0, rows - 1);
        int maxRow = Math.Clamp((int)Math.Floor((exit.Y + exit.Radius) / cellSize), 0, rows - 1);

        PriorityQueue<int, float> pq = new(512);

        for (int r = minRow; r <= maxRow; r++)
        {
            for (int c = minCol; c <= maxCol; c++)
            {
                int idx = r * cols + c;
                if (!scenario.Blocked[idx])
                {
                    field[idx] = 0f;
                    pq.Enqueue(idx, 0f);
                }
            }
        }

        if (pq.Count == 0)
        {
            var nearest = scenario.NearestOpenCell(exit.X, exit.Y, 200.0);
            if (nearest.HasValue)
            {
                int c = Math.Clamp((int)(nearest.Value.X / cellSize), 0, cols - 1);
                int r = Math.Clamp((int)(nearest.Value.Y / cellSize), 0, rows - 1);
                int idx = r * cols + c;
                field[idx] = 0f;
                pq.Enqueue(idx, 0f);
            }
        }

        int[] dCols = { 0, 1, 1, 1, 0, -1, -1, -1 };
        int[] dRows = { -1, -1, 0, 1, 1, 1, 0, -1 };
        float[] costs = { stepCard, stepDiag, stepCard, stepDiag, stepCard, stepDiag, stepCard, stepDiag };

        while (pq.Count > 0)
        {
            pq.TryDequeue(out int currIdx, out float currDist);
            if (currDist > field[currIdx]) continue;

            int currRow = currIdx / cols;
            int currCol = currIdx % cols;

            for (int d = 0; d < 8; d++)
            {
                int nc = currCol + dCols[d];
                int nr = currRow + dRows[d];
                if (nc >= 0 && nc < cols && nr >= 0 && nr < rows)
                {
                    int nIdx = nr * cols + nc;
                    if (scenario.Blocked[nIdx]) continue;

                    float newDist = currDist + costs[d];
                    if (newDist < field[nIdx])
                    {
                        field[nIdx] = newDist;
                        pq.Enqueue(nIdx, newDist);
                    }
                }
            }
        }

        return field;
    }
}
