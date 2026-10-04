using System;
using System.Collections.Generic;
using System.Linq;
using PlanSafe.Contracts.Models.Simulation;

namespace PlanSafe.App.Simulation;

public class PotentialFieldGrid
{
    public readonly double CellSize;
    public readonly double InvCellSize;
    public readonly int ColumnCount;
    public readonly int RowCount;
    public readonly int TotalCells;
    public readonly float[] StaticPotentialFieldMatrix;
    public readonly float[] DynamicPotentialFieldMatrix;
    private readonly bool[] _obstacleMask;
    public readonly float[] DynamicCrowdPenalty;
    private readonly float[] _rawCellDensity;
    private readonly float[] _smoothedCellDensity;
    public ReadOnlySpan<bool> ObstacleMask => _obstacleMask;
    public ReadOnlySpan<float> SmoothedDensity => _smoothedCellDensity;
    private readonly PriorityQueue<int, float> _dynamicFieldFrontier = new(4096);
    private Obstacle[] _cachedExitZones = Array.Empty<Obstacle>();
    private readonly bool[] _protectedSinkMask;

    public PotentialFieldGrid(double cellSize, double width, double height)
    {
        CellSize = Math.Max(1.0, cellSize);
        InvCellSize = 1.0 / CellSize;
        ColumnCount = (int)Math.Max(5, Math.Ceiling(width / CellSize));
        RowCount = (int)Math.Max(5, Math.Ceiling(height / CellSize));
        TotalCells = ColumnCount * RowCount;

        StaticPotentialFieldMatrix = new float[TotalCells];
        DynamicPotentialFieldMatrix = new float[TotalCells];
        _obstacleMask = new bool[TotalCells];
        DynamicCrowdPenalty = new float[TotalCells];
        _rawCellDensity = new float[TotalCells];
        _smoothedCellDensity = new float[TotalCells];
        _protectedSinkMask = new bool[TotalCells];
    }

    public void UpdateDynamicDensity(int activeAgentCount, double[] agentPosX, double[] agentPosY, int granulation = 1, bool updatePenalty = true)
    {
        Array.Clear(_rawCellDensity, 0, TotalCells);
        double cellArea = CellSize * CellSize;
        float invCellArea = (float)(1.0 / cellArea);

        if (granulation >= 16)
        {
            float m = invCellArea * granulation;
            for (int i = 0; i < activeAgentCount; i++)
            {
                double u = agentPosX[i] * InvCellSize - 0.5;
                double v = agentPosY[i] * InvCellSize - 0.5;
                int c0 = (int)Math.Floor(u);
                int r0 = (int)Math.Floor(v);
                float s = (float)Math.Clamp(u - c0, 0.0, 1.0);
                float t = (float)Math.Clamp(v - r0, 0.0, 1.0);

                float w00 = (1.0f - s) * (1.0f - t);
                float w10 = s * (1.0f - t);
                float w01 = (1.0f - s) * t;
                float w11 = s * t;

                if (c0 >= 0 && c0 < ColumnCount && r0 >= 0 && r0 < RowCount) _rawCellDensity[r0 * ColumnCount + c0] += m * w00;
                if (c0 + 1 >= 0 && c0 + 1 < ColumnCount && r0 >= 0 && r0 < RowCount) _rawCellDensity[r0 * ColumnCount + c0 + 1] += m * w10;
                if (c0 >= 0 && c0 < ColumnCount && r0 + 1 >= 0 && r0 + 1 < RowCount) _rawCellDensity[(r0 + 1) * ColumnCount + c0] += m * w01;
                if (c0 + 1 >= 0 && c0 + 1 < ColumnCount && r0 + 1 >= 0 && r0 + 1 < RowCount) _rawCellDensity[(r0 + 1) * ColumnCount + c0 + 1] += m * w11;
            }
        }
        else
        {
            float massPerAgent = invCellArea * granulation;
            for (int i = 0; i < activeAgentCount; i++)
            {
                int col = (int)(agentPosX[i] * InvCellSize);
                int row = (int)(agentPosY[i] * InvCellSize);

                if (col >= 0 && col < ColumnCount && row >= 0 && row < RowCount)
                {
                    _rawCellDensity[row * ColumnCount + col] += massPerAgent;
                }
            }
        }

        // Spatial smoothing (diffuse density across neighborhood to eliminate discrete grid holes)
        int smoothingPasses = granulation >= 16 ? 3 : 1;
        float[] source = _rawCellDensity;
        float[] target = _smoothedCellDensity;

        for (int pass = 0; pass < smoothingPasses; pass++)
        {
            for (int r = 0; r < RowCount; r++)
            {
                int rOffset = r * ColumnCount;
                int rPrev = Math.Max(0, r - 1) * ColumnCount;
                int rNext = Math.Min(RowCount - 1, r + 1) * ColumnCount;

                for (int c = 0; c < ColumnCount; c++)
                {
                    int cPrev = Math.Max(0, c - 1);
                    int cNext = Math.Min(ColumnCount - 1, c + 1);

                    float center = source[rOffset + c];
                    float orthogonal = source[rOffset + cPrev] + source[rOffset + cNext] +
                                       source[rPrev + c] + source[rNext + c];

                    target[rOffset + c] = 0.50f * center + 0.125f * orthogonal;
                }
            }

            if (pass < smoothingPasses - 1)
            {
                Array.Copy(target, source, TotalCells);
            }
        }

        // Sink protection: exit zones and their immediate vicinity must NEVER be penalized
        if (updatePenalty)
        {
            for (int r = 0; r < RowCount; r++)
            {
                int rOffset = r * ColumnCount;

                for (int c = 0; c < ColumnCount; c++)
                {
                    int idx = rOffset + c;
                    if (_obstacleMask[idx])
                    {
                        DynamicCrowdPenalty[idx] = 0f;
                        continue;
                    }

                    if (_protectedSinkMask[idx])
                    {
                        DynamicCrowdPenalty[idx] = 0f;
                        continue;
                    }

                    float rho = _smoothedCellDensity[idx];
                    float targetPenalty = 0f;
                    // Obvious blockage threshold:
                    // When density > 0.80 os/m², congestion impedes flow.
                    // In queues (rho >= 2.0 - 4.5 os/m²), speed drops by 5x to 50x, so travel time impedance is significant.
                    float maxPenalty = 120.0f;
                    if (rho > 0.80f)
                    {
                        float excess = rho - 0.80f;
                        targetPenalty = excess * 6.0f + excess * excess * 2.0f;
                        if (targetPenalty > maxPenalty) targetPenalty = maxPenalty;
                    }

                    // Temporal Exponential Moving Average
                    DynamicCrowdPenalty[idx] = 0.70f * DynamicCrowdPenalty[idx] + 0.30f * targetPenalty;
                }
            }
        }
    }

    public float SampleSmoothedDensity(double px, double py)
    {
        double u = px * InvCellSize - 0.5;
        double v = py * InvCellSize - 0.5;
        int c0 = (int)Math.Floor(u);
        int r0 = (int)Math.Floor(v);
        float s = (float)Math.Clamp(u - c0, 0.0, 1.0);
        float t = (float)Math.Clamp(v - r0, 0.0, 1.0);

        int c0Clamped = Math.Clamp(c0, 0, ColumnCount - 1);
        int r0Clamped = Math.Clamp(r0, 0, RowCount - 1);
        int c1Clamped = Math.Clamp(c0 + 1, 0, ColumnCount - 1);
        int r1Clamped = Math.Clamp(r0 + 1, 0, RowCount - 1);

        float d00 = _smoothedCellDensity[r0Clamped * ColumnCount + c0Clamped];
        float d10 = _smoothedCellDensity[r0Clamped * ColumnCount + c1Clamped];
        float d01 = _smoothedCellDensity[r1Clamped * ColumnCount + c0Clamped];
        float d11 = _smoothedCellDensity[r1Clamped * ColumnCount + c1Clamped];

        return (1.0f - s) * (1.0f - t) * d00 +
               s * (1.0f - t) * d10 +
               (1.0f - s) * t * d01 +
               s * t * d11;
    }

    public void InitializeStaticObstacles(Obstacle[] obstacles)
    {
        Array.Clear(_obstacleMask, 0, TotalCells);

        for (int row = 0; row < RowCount; row++)
        {
            double wy = (row + 0.5) * CellSize;
            double cellMinY = row * CellSize;
            double cellMaxY = cellMinY + CellSize;
            int rowOffset = row * ColumnCount;

            for (int col = 0; col < ColumnCount; col++)
            {
                double wx = (col + 0.5) * CellSize;
                double cellMinX = col * CellSize;
                double cellMaxX = cellMinX + CellSize;

                foreach (var obstacle in obstacles)
                {
                    bool isObstacle;
                    if (obstacle.Width < CellSize || obstacle.Height < CellSize)
                    {
                        // For thin obstacles (e.g. barricades or narrow walls), check AABB overlap so they are not missed
                        isObstacle = cellMinX < obstacle.X + obstacle.Width &&
                                     cellMaxX > obstacle.X &&
                                     cellMinY < obstacle.Y + obstacle.Height &&
                                     cellMaxY > obstacle.Y;
                    }
                    else
                    {
                        // Cell is an obstacle only if its center is strictly inside the obstacle interior.
                        // Points on the exact boundary (e.g. wx == obstacle.X) represent free passage along the outer wall.
                        isObstacle = wx > obstacle.X + 0.001 && wx < obstacle.X + obstacle.Width - 0.001 &&
                                     wy > obstacle.Y + 0.001 && wy < obstacle.Y + obstacle.Height - 0.001;
                    }

                    if (isObstacle)
                    {
                        _obstacleMask[rowOffset + col] = true;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Replaces the obstacle mask with a precomputed raster of identical dimensions.</summary>
    public void InitializeStaticMask(bool[] mask)
    {
        if (mask.Length != TotalCells) throw new ArgumentException("Obstacle mask has an invalid length.", nameof(mask));
        Array.Copy(mask, _obstacleMask, TotalCells);
    }

    public bool IsObstacleCell(int col, int row) => _obstacleMask[row * ColumnCount + col];

    private IReadOnlyList<EvacuationTarget>? CachedTargets;
    private double _cachedWeightDistance = 1.0;
    private double _cachedWeightOccupancy = 0.0;
    private double _cachedWorldMaxDistance = 1000.0;

    public readonly Dictionary<string, float[]> TargetDistanceMatrices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Multi-source field: every exit zone is a zero-potential sink.</summary>
    public void BuildStaticField(Obstacle[] exitZones)
    {
        CachedTargets = null;
        BuildStaticFieldFromSinks(exitZones, new float[exitZones.Length]);
    }

    public void BuildStaticField(Obstacle[] obstacles, Obstacle exitZone)
    {
        CachedTargets = null;
        BuildStaticFieldFromSinks(new[] { exitZone }, new[] { 0f });
        BuildIndividualTargetDistanceFields(new[]
        {
            new EvacuationTarget("exit-main", "Strefa Ewakuacji", exitZone.X, exitZone.Y, exitZone.Width, exitZone.Height, 1000, 0, true)
        });
    }

    public void BuildStaticField(
        Obstacle[] obstacles,
        IReadOnlyList<EvacuationTarget> targets,
        double weightDistance = 1.0,
        double weightOccupancy = 0.0,
        double maxDistance = 1000.0)
    {
        CachedTargets = targets;
        _cachedWeightDistance = weightDistance;
        _cachedWeightOccupancy = weightOccupancy;
        _cachedWorldMaxDistance = maxDistance;

        if (targets == null || targets.Count == 0)
        {
            if (_cachedExitZones.Length > 0) BuildStaticFieldFromSinks(_cachedExitZones, new float[_cachedExitZones.Length]);
            return;
        }

        var activeTargets = targets.Where(t => t.IsActive).ToList();
        if (activeTargets.Count == 0) return;

        double openZoneScale = OccupancyRouting.OpenZoneScale(activeTargets);
        bool hasCapacity = activeTargets.Any(t => !t.IsFull);
        double wDist = Math.Max(0.001, weightDistance);
        double wOcc = Math.Max(0.0, weightOccupancy);
        double dMax = Math.Max(1.0, maxDistance);

        var zones = new Obstacle[activeTargets.Count];
        var initialPotentials = new float[activeTargets.Count];

        for (int i = 0; i < activeTargets.Count; i++)
        {
            var t = activeTargets[i];
            zones[i] = new Obstacle(t.X, t.Y, t.Width, t.Height);
            double occRatio = OccupancyRouting.Cost(t, openZoneScale);
            double potential = (wOcc / wDist) * dMax * occRatio;

            if (t.IsFull && hasCapacity)
            {
                potential += 100000.0; // Hard capacity penalty
            }

            initialPotentials[i] = (float)potential;
        }

        BuildStaticFieldFromSinks(zones, initialPotentials);
        BuildIndividualTargetDistanceFields(activeTargets);
    }

    private void BuildStaticFieldFromSinks(IReadOnlyList<Obstacle> zones, IReadOnlyList<float> initialPotentials)
    {
        foreach (var _ in BuildStaticFieldSteps(zones, initialPotentials)) { }
    }

    public async Task BuildStaticFieldAsync(Obstacle[] exitZones, CancellationToken cancellationToken = default)
    {
        CachedTargets = null;
        var scheduler = new PreparationScheduler();
        foreach (var _ in BuildStaticFieldSteps(exitZones, new float[exitZones.Length]))
            await scheduler.YieldAsync(cancellationToken);
    }

    private IEnumerable<int> BuildStaticFieldSteps(IReadOnlyList<Obstacle> zones, IReadOnlyList<float> initialPotentials)
    {
        _cachedExitZones = zones.ToArray();
        Array.Fill(StaticPotentialFieldMatrix, float.MaxValue);
        BuildSinkProtectedMask();

        PriorityQueue<int, float> pq = new(1024);

        for (int z = 0; z < zones.Count; z++)
        {
            var zone = zones[z];
            float basePot = initialPotentials[z];

            int minCol = Math.Clamp((int)(zone.X * InvCellSize), 0, ColumnCount - 1);
            int maxCol = Math.Clamp((int)((zone.X + zone.Width) * InvCellSize), 0, ColumnCount - 1);
            int minRow = Math.Clamp((int)(zone.Y * InvCellSize), 0, RowCount - 1);
            int maxRow = Math.Clamp((int)((zone.Y + zone.Height) * InvCellSize), 0, RowCount - 1);

            for (int r = minRow; r <= maxRow; r++)
            {
                int rOffset = r * ColumnCount;
                for (int c = minCol; c <= maxCol; c++)
                {
                    int destIdx = rOffset + c;
                    if (!_obstacleMask[destIdx])
                    {
                        if (basePot < StaticPotentialFieldMatrix[destIdx])
                        {
                            StaticPotentialFieldMatrix[destIdx] = basePot;
                            pq.Enqueue(destIdx, basePot);
                        }
                    }
                }
            }
        }

        float step = (float)CellSize;
        float diagStep = (float)(CellSize * 1.41421356);
        float knightStep = (float)(CellSize * 2.23606798);

        // 16-kierunkowe sąsiedztwo (4 ortogonalne, 4 przekątne, 8 skoczków)
        int[] dCol = {
            1, -1, 0, 0,
            1, 1, -1, -1,
            1, 1, -1, -1, 2, 2, -2, -2
        };
        int[] dRow = {
            0, 0, 1, -1,
            1, -1, 1, -1,
            2, -2, 2, -2, 1, -1, 1, -1
        };
        float[] costs = {
            step, step, step, step,
            diagStep, diagStep, diagStep, diagStep,
            knightStep, knightStep, knightStep, knightStep, knightStep, knightStep, knightStep, knightStep
        };

        int processed = 0;
        while (pq.Count > 0)
        {
            if (++processed % 256 == 0) yield return processed;
            if (!pq.TryDequeue(out int currIdx, out float currPot))
                break;

            if (currPot > StaticPotentialFieldMatrix[currIdx])
                continue;

            int currCol = currIdx % ColumnCount;
            int currRow = currIdx / ColumnCount;

            for (int i = 0; i < 16; i++)
            {
                int nCol = currCol + dCol[i];
                int nRow = currRow + dRow[i];

                if (nCol >= 0 && nCol < ColumnCount && nRow >= 0 && nRow < RowCount)
                {
                    int nIdx = nRow * ColumnCount + nCol;
                    if (_obstacleMask[nIdx]) continue;

                    // Weryfikacja braku przecinania narożników przeszkód
                    if (i >= 4 && i < 8)
                    {
                        if (_obstacleMask[currRow * ColumnCount + nCol] &&
                            _obstacleMask[nRow * ColumnCount + currCol])
                            continue;
                    }
                    else if (i >= 8)
                    {
                        int midCol = currCol + Math.Sign(dCol[i]);
                        int midRow = currRow + Math.Sign(dRow[i]);
                        if (_obstacleMask[midRow * ColumnCount + midCol] ||
                            _obstacleMask[currRow * ColumnCount + midCol] ||
                            _obstacleMask[midRow * ColumnCount + currCol])
                            continue;
                    }

                    float candPot = currPot + costs[i];
                    if (candPot < StaticPotentialFieldMatrix[nIdx])
                    {
                        StaticPotentialFieldMatrix[nIdx] = candPot;
                        pq.Enqueue(nIdx, candPot);
                    }
                }
            }
        }

        Array.Copy(StaticPotentialFieldMatrix, DynamicPotentialFieldMatrix, TotalCells);
        Array.Clear(DynamicCrowdPenalty, 0, TotalCells);
    }

    private void BuildSinkProtectedMask()
    {
        Array.Clear(_protectedSinkMask);
        foreach (var exitZone in _cachedExitZones)
        {
            int exitMinCol = Math.Clamp((int)((exitZone.X - 2.5 * CellSize) * InvCellSize), 0, ColumnCount - 1);
            int exitMaxCol = Math.Clamp((int)((exitZone.X + exitZone.Width + 2.5 * CellSize) * InvCellSize), 0, ColumnCount - 1);
            int exitMinRow = Math.Clamp((int)((exitZone.Y - 2.5 * CellSize) * InvCellSize), 0, RowCount - 1);
            int exitMaxRow = Math.Clamp((int)((exitZone.Y + exitZone.Height + 2.5 * CellSize) * InvCellSize), 0, RowCount - 1);
            for (int r = exitMinRow; r <= exitMaxRow; r++)
                _protectedSinkMask.AsSpan(r * ColumnCount + exitMinCol, exitMaxCol - exitMinCol + 1).Fill(true);
        }
    }

    public void BuildIndividualTargetDistanceFields(IReadOnlyList<EvacuationTarget> targets)
    {
        TargetDistanceMatrices.Clear();
        if (targets == null || targets.Count == 0) return;

        float step = (float)CellSize;
        float diagStep = (float)(CellSize * 1.41421356);
        float knightStep = (float)(CellSize * 2.23606798);

        int[] dCol = {
            1, -1, 0, 0,
            1, 1, -1, -1,
            1, 1, -1, -1, 2, 2, -2, -2
        };
        int[] dRow = {
            0, 0, 1, -1,
            1, -1, 1, -1,
            2, -2, 2, -2, 1, -1, 1, -1
        };
        float[] costs = {
            step, step, step, step,
            diagStep, diagStep, diagStep, diagStep,
            knightStep, knightStep, knightStep, knightStep, knightStep, knightStep, knightStep, knightStep
        };

        foreach (var target in targets.Where(t => t.IsActive))
        {
            var distMatrix = new float[TotalCells];
            Array.Fill(distMatrix, float.MaxValue);
            var pq = new PriorityQueue<int, float>(512);

            int minCol = Math.Clamp((int)(target.X * InvCellSize), 0, ColumnCount - 1);
            int maxCol = Math.Clamp((int)((target.X + target.Width) * InvCellSize), 0, ColumnCount - 1);
            int minRow = Math.Clamp((int)(target.Y * InvCellSize), 0, RowCount - 1);
            int maxRow = Math.Clamp((int)((target.Y + target.Height) * InvCellSize), 0, RowCount - 1);

            for (int r = minRow; r <= maxRow; r++)
            {
                int rOffset = r * ColumnCount;
                for (int c = minCol; c <= maxCol; c++)
                {
                    int destIdx = rOffset + c;
                    if (!_obstacleMask[destIdx])
                    {
                        distMatrix[destIdx] = 0f;
                        pq.Enqueue(destIdx, 0f);
                    }
                }
            }

            while (pq.Count > 0)
            {
                if (!pq.TryDequeue(out int currIdx, out float currDist))
                    break;

                if (currDist > distMatrix[currIdx])
                    continue;

                int currCol = currIdx % ColumnCount;
                int currRow = currIdx / ColumnCount;

                for (int i = 0; i < 16; i++)
                {
                    int nCol = currCol + dCol[i];
                    int nRow = currRow + dRow[i];

                    if (nCol >= 0 && nCol < ColumnCount && nRow >= 0 && nRow < RowCount)
                    {
                        int nIdx = nRow * ColumnCount + nCol;
                        if (_obstacleMask[nIdx]) continue;

                        if (i >= 4 && i < 8)
                        {
                            if (_obstacleMask[currRow * ColumnCount + nCol] &&
                                _obstacleMask[nRow * ColumnCount + currCol])
                                continue;
                        }
                        else if (i >= 8)
                        {
                            int midCol = currCol + Math.Sign(dCol[i]);
                            int midRow = currRow + Math.Sign(dRow[i]);
                            if (_obstacleMask[midRow * ColumnCount + midCol] ||
                                _obstacleMask[currRow * ColumnCount + midCol] ||
                                _obstacleMask[midRow * ColumnCount + currCol])
                                continue;
                        }

                        float candDist = currDist + costs[i];
                        if (candDist < distMatrix[nIdx])
                        {
                            distMatrix[nIdx] = candDist;
                            pq.Enqueue(nIdx, candDist);
                        }
                    }
                }
            }

            TargetDistanceMatrices[target.Id] = distMatrix;
        }
    }

    public double GetWalkableDistance(string targetId, double px, double py)
    {
        if (TargetDistanceMatrices.TryGetValue(targetId, out var matrix))
        {
            int col = Math.Clamp((int)(px * InvCellSize), 0, ColumnCount - 1);
            int row = Math.Clamp((int)(py * InvCellSize), 0, RowCount - 1);
            float d = matrix[row * ColumnCount + col];
            if (d < float.MaxValue - 1000f)
            {
                return d;
            }
        }
        var tgt = CachedTargets?.FirstOrDefault(t => string.Equals(t.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (tgt != null)
        {
            return TargetSelector.CalculateDistance(px, py, tgt);
        }
        return 0.0;
    }

    public float[] GetSmoothedDensitiesCopy()
    {
        float[] copy = new float[TotalCells];
        Array.Copy(_smoothedCellDensity, copy, TotalCells);
        return copy;
    }

    public void SetSmoothedDensities(float[] densities)
    {
        if (densities != null && densities.Length == TotalCells)
        {
            Array.Copy(densities, _smoothedCellDensity, TotalCells);
        }
    }

    public void RefineDynamicField()
    {
        if (_cachedExitZones.Length == 0) return;

        bool hasCongestion = false;
        for (int i = 0; i < TotalCells; i++)
        {
            if (DynamicCrowdPenalty[i] > 0.5f)
            {
                hasCongestion = true;
                break;
            }
        }

        if (!hasCongestion)
        {
            Array.Copy(StaticPotentialFieldMatrix, DynamicPotentialFieldMatrix, TotalCells);
            return;
        }

        Array.Fill(DynamicPotentialFieldMatrix, float.MaxValue);
        _dynamicFieldFrontier.Clear();

        if (CachedTargets != null && CachedTargets.Count > 1)
        {
            var activeTargets = CachedTargets.Where(t => t.IsActive).ToList();
            double openZoneScale = OccupancyRouting.OpenZoneScale(activeTargets);
            bool hasCapacity = activeTargets.Any(t => !t.IsFull);
            double wDist = Math.Max(0.001, _cachedWeightDistance);
            double wOcc = Math.Max(0.0, _cachedWeightOccupancy);
            double dMax = Math.Max(1.0, _cachedWorldMaxDistance);

            for (int i = 0; i < activeTargets.Count; i++)
            {
                var t = activeTargets[i];
                double occRatio = OccupancyRouting.Cost(t, openZoneScale);
                float basePot = (float)((wOcc / wDist) * dMax * occRatio);
                if (t.IsFull && hasCapacity) basePot += 100000f;

                int minCol = Math.Clamp((int)(t.X * InvCellSize), 0, ColumnCount - 1);
                int maxCol = Math.Clamp((int)((t.X + t.Width) * InvCellSize), 0, ColumnCount - 1);
                int minRow = Math.Clamp((int)(t.Y * InvCellSize), 0, RowCount - 1);
                int maxRow = Math.Clamp((int)((t.Y + t.Height) * InvCellSize), 0, RowCount - 1);

                for (int r = minRow; r <= maxRow; r++)
                {
                    int rOffset = r * ColumnCount;
                    for (int c = minCol; c <= maxCol; c++)
                    {
                        int destIdx = rOffset + c;
                        if (!_obstacleMask[destIdx])
                        {
                            if (basePot < DynamicPotentialFieldMatrix[destIdx])
                            {
                                DynamicPotentialFieldMatrix[destIdx] = basePot;
                                _dynamicFieldFrontier.Enqueue(destIdx, basePot);
                            }
                        }
                    }
                }
            }
        }
        else
        {
            foreach (var exitZone in _cachedExitZones)
            {
                int minCol = Math.Clamp((int)(exitZone.X * InvCellSize), 0, ColumnCount - 1);
                int maxCol = Math.Clamp((int)((exitZone.X + exitZone.Width) * InvCellSize), 0, ColumnCount - 1);
                int minRow = Math.Clamp((int)(exitZone.Y * InvCellSize), 0, RowCount - 1);
                int maxRow = Math.Clamp((int)((exitZone.Y + exitZone.Height) * InvCellSize), 0, RowCount - 1);

                for (int r = minRow; r <= maxRow; r++)
                {
                    int rOffset = r * ColumnCount;
                    for (int c = minCol; c <= maxCol; c++)
                    {
                        int destIdx = rOffset + c;
                        if (!_obstacleMask[destIdx] && DynamicPotentialFieldMatrix[destIdx] != 0f)
                        {
                            DynamicPotentialFieldMatrix[destIdx] = 0f;
                            _dynamicFieldFrontier.Enqueue(destIdx, 0f);
                        }
                    }
                }
            }
        }

        float step = (float)CellSize;
        float diagStep = (float)(CellSize * 1.41421356);

        int[] dCol = { 1, -1, 0, 0, 1, 1, -1, -1 };
        int[] dRow = { 0, 0, 1, -1, 1, -1, 1, -1 };
        float[] costs = { step, step, step, step, diagStep, diagStep, diagStep, diagStep };

        while (_dynamicFieldFrontier.Count > 0)
        {
            if (!_dynamicFieldFrontier.TryDequeue(out int currIdx, out float currPot))
                break;

            if (currPot > DynamicPotentialFieldMatrix[currIdx])
                continue;

            int currCol = currIdx % ColumnCount;
            int currRow = currIdx / ColumnCount;

            for (int i = 0; i < 8; i++)
            {
                int nCol = currCol + dCol[i];
                int nRow = currRow + dRow[i];

                if (nCol >= 0 && nCol < ColumnCount && nRow >= 0 && nRow < RowCount)
                {
                    int nIdx = nRow * ColumnCount + nCol;
                    if (_obstacleMask[nIdx]) continue;

                    // Diagonals: verify no corner-cutting through obstacles
                    if (i >= 4)
                    {
                        if (_obstacleMask[currRow * ColumnCount + nCol] &&
                            _obstacleMask[nRow * ColumnCount + currCol])
                            continue;
                    }

                    float avgPenalty = 0.5f * (DynamicCrowdPenalty[currIdx] + DynamicCrowdPenalty[nIdx]);
                    float edgeCost = costs[i] * (1.0f + avgPenalty);
                    float candPot = currPot + edgeCost;
                    if (candPot < DynamicPotentialFieldMatrix[nIdx])
                    {
                        DynamicPotentialFieldMatrix[nIdx] = candPot;
                        _dynamicFieldFrontier.Enqueue(nIdx, candPot);
                    }
                }
            }
        }
    }

    public (double dx, double dy) GetFlowDirection(double px, double py, float[]? field = null, Obstacle[]? exitZones = null)
    {
        field ??= DynamicPotentialFieldMatrix;
        exitZones ??= _cachedExitZones;
        const float maxValidPot = float.MaxValue * 0.5f;

        // Ciągłe współrzędne względem środków komórek siatki
        double u = px * InvCellSize - 0.5;
        double v = py * InvCellSize - 0.5;

        int c0 = Math.Clamp((int)Math.Floor(u), 0, ColumnCount - 2);
        int r0 = Math.Clamp((int)Math.Floor(v), 0, RowCount - 2);

        double s = Math.Clamp(u - c0, 0.0, 1.0);
        double t = Math.Clamp(v - r0, 0.0, 1.0);

        int idx00 = r0 * ColumnCount + c0;
        int idx10 = idx00 + 1;
        int idx01 = idx00 + ColumnCount;
        int idx11 = idx01 + 1;

        float p00 = field[idx00];
        float p10 = field[idx10];
        float p01 = field[idx01];
        float p11 = field[idx11];

        bool v00 = p00 < maxValidPot;
        bool v10 = p10 < maxValidPot;
        bool v01 = p01 < maxValidPot;
        bool v11 = p11 < maxValidPot;

        int nearestCol = Math.Clamp((int)Math.Round(u), 0, ColumnCount - 1);
        int nearestRow = Math.Clamp((int)Math.Round(v), 0, RowCount - 1);
        int nearestIdx = nearestRow * ColumnCount + nearestCol;

        if (field[nearestIdx] >= maxValidPot || (!v00 && !v10 && !v01 && !v11))
        {
            float bestPot = maxValidPot;
            double bestDx = 1.0;
            double bestDy = 0.0;

            int[] cOffsets = { 0, 0, -1, 1, -1, 1, -1, 1 };
            int[] rOffsets = { -1, 1, 0, 0, -1, -1, 1, 1 };

            for (int i = 0; i < 8; i++)
            {
                int nc = nearestCol + cOffsets[i];
                int nr = nearestRow + rOffsets[i];
                if (nc >= 0 && nc < ColumnCount && nr >= 0 && nr < RowCount)
                {
                    int nIdx = nr * ColumnCount + nc;
                    float nPot = field[nIdx];
                    if (nPot < bestPot)
                    {
                        bestPot = nPot;
                        bestDx = cOffsets[i];
                        bestDy = rOffsets[i];
                    }
                }
            }

            double mag = Math.Sqrt(bestDx * bestDx + bestDy * bestDy);
            if (mag > 0.0001) return (bestDx / mag, bestDy / mag);
            return (1.0, 0.0);
        }

        // Gradient analityczny dwuliniowy (flowX = -dP/dx, flowY = -dP/dy)
        double flowX, flowY;
        if (v00 && v10 && v01 && v11)
        {
            flowX = (1.0 - t) * (p00 - p10) + t * (p01 - p11);
            flowY = (1.0 - s) * (p00 - p01) + s * (p10 - p11);
        }
        else
        {
            double gradX0 = double.NaN;
            if (v00 && v10) gradX0 = p00 - p10;
            else if (v00 && !v10) gradX0 = -1.0;
            else if (!v00 && v10) gradX0 = 1.0;

            double gradX1 = double.NaN;
            if (v01 && v11) gradX1 = p01 - p11;
            else if (v01 && !v11) gradX1 = -1.0;
            else if (!v01 && v11) gradX1 = 1.0;

            if (!double.IsNaN(gradX0) && !double.IsNaN(gradX1))
                flowX = (1.0 - t) * gradX0 + t * gradX1;
            else if (!double.IsNaN(gradX0))
                flowX = gradX0;
            else if (!double.IsNaN(gradX1))
                flowX = gradX1;
            else
                flowX = 0;

            double gradY0 = double.NaN;
            if (v00 && v01) gradY0 = p00 - p01;
            else if (v00 && !v01) gradY0 = -1.0;
            else if (!v00 && v01) gradY0 = 1.0;

            double gradY1 = double.NaN;
            if (v10 && v11) gradY1 = p10 - p11;
            else if (v10 && !v11) gradY1 = -1.0;
            else if (!v10 && v11) gradY1 = 1.0;

            if (!double.IsNaN(gradY0) && !double.IsNaN(gradY1))
                flowY = (1.0 - s) * gradY0 + s * gradY1;
            else if (!double.IsNaN(gradY0))
                flowY = gradY0;
            else if (!double.IsNaN(gradY1))
                flowY = gradY1;
            else
                flowY = 0;
        }

        double gradientMagnitude = Math.Sqrt(flowX * flowX + flowY * flowY);
        if (gradientMagnitude > 0.0001)
        {
            return (flowX / gradientMagnitude, flowY / gradientMagnitude);
        }

        // Inside a sink/target zone (flat potential = 0), guide agents toward the target center
        if (exitZones != null && exitZones.Length > 0)
        {
            double bestDistSq = double.MaxValue;
            double bestDirX = 0, bestDirY = 0;
            for (int i = 0; i < exitZones.Length; i++)
            {
                var z = exitZones[i];
                double cx = z.X + z.Width * 0.5;
                double cy = z.Y + z.Height * 0.5;
                double dx = cx - px;
                double dy = cy - py;
                double dSq = dx * dx + dy * dy;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestDirX = dx;
                    bestDirY = dy;
                }
            }
            double len = Math.Sqrt(bestDistSq);
            if (len > 0.001)
            {
                return (bestDirX / len, bestDirY / len);
            }
            return (1.0, 0.0);
        }

        return (1.0, 0.0);
    }

    public float SamplePotential(double px, double py, float[]? field = null)
    {
        int col = (int)(px * InvCellSize);
        int row = (int)(py * InvCellSize);
        if (col < 0 || col >= ColumnCount || row < 0 || row >= RowCount)
            return float.MaxValue;
        return (field ?? DynamicPotentialFieldMatrix)[row * ColumnCount + col];
    }
}
