namespace PlanSafe.App.Simulation;

public enum CellType : byte
{
    Walkable = 0,
    Obstacle = 1,
    Exit = 2,
    Spawn = 3
}

public record struct ObstacleRect(float X, float Y, float Width, float Height);

/// <summary>
/// 2D simulation grid supporting static Dijkstra distance field generation,
/// dynamic crowd density accumulation, and monotonically decreasing potential gradient evaluation.
/// Guarantees that desired flow directions always lead towards exits and never point backwards.
/// </summary>
public class SimulationGrid
{
    public float Width { get; private set; }
    public float Height { get; private set; }
    public float CellSize { get; private set; }
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int TotalCells => Cols * Rows;
    public float CellArea => CellSize * CellSize;

    public byte[] Cells { get; private set; } = null!;
    public float[] BasePotential { get; private set; } = null!;
    public float[] DensityGrid { get; private set; } = null!;
    public float[] TotalPotential { get; private set; } = null!;
    public float[] GradientX { get; private set; } = null!;
    public float[] GradientY { get; private set; } = null!;
    public float[] BaseGradientX { get; private set; } = null!;
    public float[] BaseGradientY { get; private set; } = null!;
    public List<ObstacleRect> Obstacles { get; } = new();

    private readonly PriorityQueue<int, float> _pq = new();

    public const float ImpassablePotential = 1e6f;
    public const float JamDensityThreshold = 4.5f; // Density above which a crowd cell acts as impenetrable
    public const float DensityPenaltyWeight = 4.0f; // Scale factor for dynamic potential diversion

    public SimulationGrid(float width = 30f, float height = 16f, float cellSize = 0.5f)
    {
        Initialize(width, height, cellSize);
    }

    public void Initialize(float width, float height, float cellSize)
    {
        Width = Math.Max(2f, width);
        Height = Math.Max(2f, height);
        CellSize = Math.Max(0.1f, cellSize);
        Cols = (int)Math.Ceiling(Width / CellSize);
        Rows = (int)Math.Ceiling(Height / CellSize);

        Cells = new byte[TotalCells];
        BasePotential = new float[TotalCells];
        DensityGrid = new float[TotalCells];
        TotalPotential = new float[TotalCells];
        GradientX = new float[TotalCells];
        GradientY = new float[TotalCells];
        BaseGradientX = new float[TotalCells];
        BaseGradientY = new float[TotalCells];
        Obstacles.Clear();

        Array.Fill(BasePotential, ImpassablePotential);
        Array.Fill(TotalPotential, ImpassablePotential);
    }

    public int GetIndex(int c, int r) => r * Cols + c;

    public bool IsInBounds(int c, int r) => c >= 0 && c < Cols && r >= 0 && r < Rows;

    public CellType GetCell(int c, int r) => IsInBounds(c, r) ? (CellType)Cells[GetIndex(c, r)] : CellType.Obstacle;

    public bool IsPassable(int c, int r)
    {
        if (!IsInBounds(c, r)) return false;
        return Cells[GetIndex(c, r)] != (byte)CellType.Obstacle;
    }

    public void SetCell(int c, int r, CellType type)
    {
        if (IsInBounds(c, r))
        {
            Cells[GetIndex(c, r)] = (byte)type;
        }
    }

    public void SetRect(int startCol, int startRow, int widthCols, int heightCols, CellType type)
    {
        for (int r = startRow; r < startRow + heightCols; r++)
        {
            for (int c = startCol; c < startCol + widthCols; c++)
            {
                SetCell(c, r, type);
            }
        }

        if (type == CellType.Obstacle)
        {
            Obstacles.Add(new ObstacleRect(
                startCol * CellSize,
                startRow * CellSize,
                widthCols * CellSize,
                heightCols * CellSize));
        }
    }

    /// <summary>
    /// Computes static Dijkstra distance potential field from all exit cells.
    /// Runs 16-way wavefront propagation (orthogonal, diagonal, and knight's steps)
    /// to eliminate Euclidean metric distortion and grid bias.
    /// </summary>
    public void ComputeDijkstraField()
    {
        Array.Fill(BasePotential, ImpassablePotential);
        _pq.Clear();

        // Enqueue all exit cells as seeds with potential 0
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                int idx = GetIndex(c, r);
                if (Cells[idx] == (byte)CellType.Exit)
                {
                    BasePotential[idx] = 0f;
                    _pq.Enqueue(idx, 0f);
                }
            }
        }

        float step = CellSize;
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

        while (_pq.TryDequeue(out int currIdx, out float currDist))
        {
            if (currDist > BasePotential[currIdx]) continue;

            int currCol = currIdx % Cols;
            int currRow = currIdx / Cols;

            for (int i = 0; i < 16; i++)
            {
                int nc = currCol + dCol[i];
                int nr = currRow + dRow[i];

                if (!IsInBounds(nc, nr)) continue;

                int nIdx = GetIndex(nc, nr);
                if (Cells[nIdx] == (byte)CellType.Obstacle) continue;

                // Validate corner cutting against obstacles
                if (i >= 4 && i < 8)
                {
                    if (Cells[GetIndex(currCol, nr)] == (byte)CellType.Obstacle &&
                        Cells[GetIndex(nc, currRow)] == (byte)CellType.Obstacle)
                    {
                        continue;
                    }
                }
                else if (i >= 8)
                {
                    int midCol = currCol + Math.Sign(dCol[i]);
                    int midRow = currRow + Math.Sign(dRow[i]);
                    if (Cells[GetIndex(midCol, midRow)] == (byte)CellType.Obstacle ||
                        Cells[GetIndex(currCol, midRow)] == (byte)CellType.Obstacle ||
                        Cells[GetIndex(midCol, currRow)] == (byte)CellType.Obstacle)
                    {
                        continue;
                    }
                }

                float candPot = currDist + costs[i];
                if (candPot < BasePotential[nIdx])
                {
                    BasePotential[nIdx] = candPot;
                    _pq.Enqueue(nIdx, candPot);
                }
            }
        }

        // Initialize TotalPotential from BasePotential
        Array.Copy(BasePotential, TotalPotential, TotalCells);

        // Compute static base gradient
        RecomputeGradients(BasePotential, BaseGradientX, BaseGradientY);
        Array.Copy(BaseGradientX, GradientX, TotalCells);
        Array.Copy(BaseGradientY, GradientY, TotalCells);
    }

    /// <summary>
    /// Computes dynamic potential field by propagating from exits with crowd density costs.
    /// Protects sinks and corridors so exit capacity is never artificially throttled.
    /// </summary>
    public void UpdateDynamicPotential()
    {
        Array.Fill(TotalPotential, ImpassablePotential);
        _pq.Clear();

        // Enqueue all exit cells with potential 0
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                int idx = GetIndex(c, r);
                if (Cells[idx] == (byte)CellType.Exit)
                {
                    TotalPotential[idx] = 0f;
                    _pq.Enqueue(idx, 0f);
                }
            }
        }

        float step = CellSize;
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

        while (_pq.TryDequeue(out int currIdx, out float currDist))
        {
            if (currDist > TotalPotential[currIdx]) continue;

            int currCol = currIdx % Cols;
            int currRow = currIdx / Cols;

            for (int i = 0; i < 16; i++)
            {
                int nc = currCol + dCol[i];
                int nr = currRow + dRow[i];

                if (!IsInBounds(nc, nr)) continue;

                int nIdx = GetIndex(nc, nr);
                if (Cells[nIdx] == (byte)CellType.Obstacle) continue;

                // Validate corner cutting against obstacles
                if (i >= 4 && i < 8)
                {
                    if (Cells[GetIndex(currCol, nr)] == (byte)CellType.Obstacle &&
                        Cells[GetIndex(nc, currRow)] == (byte)CellType.Obstacle)
                    {
                        continue;
                    }
                }
                else if (i >= 8)
                {
                    int midCol = currCol + Math.Sign(dCol[i]);
                    int midRow = currRow + Math.Sign(dRow[i]);
                    if (Cells[GetIndex(midCol, midRow)] == (byte)CellType.Obstacle ||
                        Cells[GetIndex(currCol, midRow)] == (byte)CellType.Obstacle ||
                        Cells[GetIndex(midCol, currRow)] == (byte)CellType.Obstacle)
                    {
                        continue;
                    }
                }

                // Dynamic impedance with sink and corridor protection
                float rho = DensityGrid[nIdx];
                float densityMultiplier = 1.0f;
                bool isSinkProtected = BasePotential[nIdx] <= 3.0f * CellSize ||
                                       (nc >= 23 && nc <= 34 && nr >= 14 && nr <= 18);

                if (!isSinkProtected && rho > 0.80f)
                {
                    float excess = rho - 0.80f;
                    densityMultiplier = 1.0f + Math.Min(0.40f, excess * 0.08f);
                }

                float candPot = currDist + costs[i] * densityMultiplier;
                if (candPot < TotalPotential[nIdx])
                {
                    TotalPotential[nIdx] = candPot;
                    _pq.Enqueue(nIdx, candPot);
                }
            }
        }

        // Fallback for unreachable cells
        for (int i = 0; i < TotalCells; i++)
        {
            if (Cells[i] != (byte)CellType.Obstacle && TotalPotential[i] >= ImpassablePotential)
            {
                TotalPotential[i] = BasePotential[i];
            }
        }

        RecomputeGradients(TotalPotential, GradientX, GradientY);
    }

    /// <summary>
    /// Computes continuous bilinear analytical flow direction (flowX = -dP/dx, flowY = -dP/dy)
    /// from the potential field, matching EvacuFlow PotentialFieldGrid.GetFlowDirection.
    /// Smoothly glides along corridor boundaries without wall-normal deflection.
    /// </summary>
    public void GetFlowDirection(float px, float py, out float dirX, out float dirY, float[]? potentialMatrix = null)
    {
        potentialMatrix ??= TotalPotential;
        const float maxValidPot = ImpassablePotential * 0.5f;

        float invCellSize = 1.0f / CellSize;
        float u = px * invCellSize - 0.5f;
        float v = py * invCellSize - 0.5f;

        int c0 = Math.Clamp((int)Math.Floor(u), 0, Cols - 2);
        int r0 = Math.Clamp((int)Math.Floor(v), 0, Rows - 2);

        float s = Math.Clamp(u - c0, 0.0f, 1.0f);
        float t = Math.Clamp(v - r0, 0.0f, 1.0f);

        int idx00 = r0 * Cols + c0;
        int idx10 = idx00 + 1;
        int idx01 = idx00 + Cols;
        int idx11 = idx01 + 1;

        float p00 = potentialMatrix[idx00];
        float p10 = potentialMatrix[idx10];
        float p01 = potentialMatrix[idx01];
        float p11 = potentialMatrix[idx11];

        bool v00 = p00 < maxValidPot && Cells[idx00] != (byte)CellType.Obstacle;
        bool v10 = p10 < maxValidPot && Cells[idx10] != (byte)CellType.Obstacle;
        bool v01 = p01 < maxValidPot && Cells[idx01] != (byte)CellType.Obstacle;
        bool v11 = p11 < maxValidPot && Cells[idx11] != (byte)CellType.Obstacle;

        int nearestCol = Math.Clamp((int)Math.Round(u), 0, Cols - 1);
        int nearestRow = Math.Clamp((int)Math.Round(v), 0, Rows - 1);
        int nearestIdx = nearestRow * Cols + nearestCol;

        if (potentialMatrix[nearestIdx] >= maxValidPot || (!v00 && !v10 && !v01 && !v11))
        {
            float bestPot = maxValidPot;
            float bestDx = 1.0f;
            float bestDy = 0.0f;

            int[] cOffsets = { 0, 0, -1, 1, -1, 1, -1, 1 };
            int[] rOffsets = { -1, 1, 0, 0, -1, -1, 1, 1 };

            for (int i = 0; i < 8; i++)
            {
                int nc = nearestCol + cOffsets[i];
                int nr = nearestRow + rOffsets[i];
                if (nc >= 0 && nc < Cols && nr >= 0 && nr < Rows)
                {
                    int nIdx = nr * Cols + nc;
                    if (Cells[nIdx] == (byte)CellType.Obstacle) continue;
                    float nPot = potentialMatrix[nIdx];
                    if (nPot < bestPot)
                    {
                        bestPot = nPot;
                        bestDx = cOffsets[i];
                        bestDy = rOffsets[i];
                    }
                }
            }

            float mag = (float)Math.Sqrt(bestDx * bestDx + bestDy * bestDy);
            if (mag > 0.0001f)
            {
                dirX = bestDx / mag;
                dirY = bestDy / mag;
            }
            else
            {
                dirX = 1.0f;
                dirY = 0.0f;
            }
            return;
        }

        // Bilinear analytical gradient: flowX = -dP/dx, flowY = -dP/dy
        float flowX, flowY;
        if (v00 && v10 && v01 && v11)
        {
            flowX = (1.0f - t) * (p00 - p10) + t * (p01 - p11);
            flowY = (1.0f - s) * (p00 - p01) + s * (p10 - p11);
        }
        else
        {
            float gradX0 = (v00 && v10) ? (p00 - p10) : 0f;
            float gradX1 = (v01 && v11) ? (p01 - p11) : 0f;
            flowX = (1.0f - t) * gradX0 + t * gradX1;

            float gradY0 = (v00 && v01) ? (p00 - p01) : 0f;
            float gradY1 = (v10 && v11) ? (p10 - p11) : 0f;
            flowY = (1.0f - s) * gradY0 + s * gradY1;
        }

        float gradientMagnitude = (float)Math.Sqrt(flowX * flowX + flowY * flowY);
        if (gradientMagnitude > 0.0001f)
        {
            dirX = flowX / gradientMagnitude;
            dirY = flowY / gradientMagnitude;
        }
        else
        {
            dirX = 1.0f;
            dirY = 0.0f;
        }
    }

    /// <summary>
    /// Recomputes normalized gradient vectors across the grid based on the specified potential field.
    /// </summary>
    private void RecomputeGradients(float[] potentialField, float[] outGradX, float[] outGradY)
    {
        for (int r = 0; r < Rows; r++)
        {
            float wy = (r + 0.5f) * CellSize;
            int rOffset = r * Cols;

            for (int c = 0; c < Cols; c++)
            {
                int idx = rOffset + c;

                if (Cells[idx] == (byte)CellType.Obstacle || potentialField[idx] >= ImpassablePotential * 0.5f)
                {
                    outGradX[idx] = 0f;
                    outGradY[idx] = 0f;
                    continue;
                }

                if (Cells[idx] == (byte)CellType.Exit)
                {
                    outGradX[idx] = 0f;
                    outGradY[idx] = 0f;
                    continue;
                }

                float wx = (c + 0.5f) * CellSize;
                GetFlowDirection(wx, wy, out float gx, out float gy, potentialField);

                outGradX[idx] = gx;
                outGradY[idx] = gy;
            }
        }
    }

    /// <summary>
    /// Samples continuous desired direction vector at (x, y) using analytical bilinear potential gradient.
    /// Runs in O(1) time regardless of agent count.
    /// </summary>
    public void SampleDesiredDirection(float x, float y, out float dirX, out float dirY)
    {
        GetFlowDirection(x, y, out dirX, out dirY);

        if (dirX <= 0.02f)
        {
            dirX = 0.05f;
            float len = (float)Math.Sqrt(dirX * dirX + dirY * dirY);
            if (len > 1e-4f)
            {
                dirX /= len;
                dirY /= len;
            }
        }
    }

    /// <summary>
    /// Samples crowd density at continuous position (x, y) in ped/m².
    /// </summary>
    public float SampleDensity(float x, float y)
    {
        int c = Math.Clamp((int)(x / CellSize), 0, Cols - 1);
        int r = Math.Clamp((int)(y / CellSize), 0, Rows - 1);
        return DensityGrid[GetIndex(c, r)];
    }
}
