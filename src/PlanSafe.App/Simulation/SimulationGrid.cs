namespace PlanSafe.App.Simulation;

public enum CellType : byte
{
    Walkable = 0,
    Obstacle = 1,
    Exit = 2,
    Spawn = 3
}

/// <summary>
/// 2D simulation grid supporting static Dijkstra distance field generation,
/// dynamic crowd density accumulation, and combined potential gradient evaluation.
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

    public const float ImpassablePotential = 1e6f;
    public const float JamDensityThreshold = 4.5f; // Density above which a crowd cell acts as impenetrable
    public const float DensityPenaltyWeight = 8.0f; // Scale factor for dynamic potential diversion

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

        Array.Fill(BasePotential, ImpassablePotential);
        Array.Fill(TotalPotential, ImpassablePotential);
    }

    public int GetIndex(int c, int r) => r * Cols + c;

    public bool IsInBounds(int c, int r) => c >= 0 && c < Cols && r >= 0 && r < Rows;

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
    }

    /// <summary>
    /// Computes static Dijkstra distance potential field from all exit cells.
    /// Runs 8-way wavefront propagation with diagonal cost sqrt(2) * CellSize.
    /// </summary>
    public void ComputeDijkstraField()
    {
        Array.Fill(BasePotential, ImpassablePotential);

        var pq = new PriorityQueue<int, float>();

        // Enqueue all exit cells as seeds with potential 0
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                int idx = GetIndex(c, r);
                if (Cells[idx] == (byte)CellType.Exit)
                {
                    BasePotential[idx] = 0f;
                    pq.Enqueue(idx, 0f);
                }
            }
        }

        float diagCost = (float)(Math.Sqrt(2.0) * CellSize);
        float orthoCost = CellSize;

        // 8-directional offsets
        int[] dCol = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] dRow = { -1, -1, -1, 0, 0, 1, 1, 1 };
        float[] costs = { diagCost, orthoCost, diagCost, orthoCost, orthoCost, diagCost, orthoCost, diagCost };

        while (pq.TryDequeue(out int currIdx, out float currDist))
        {
            if (currDist > BasePotential[currIdx])
            {
                continue;
            }

            int currCol = currIdx % Cols;
            int currRow = currIdx / Cols;

            for (int i = 0; i < 8; i++)
            {
                int nc = currCol + dCol[i];
                int nr = currRow + dRow[i];

                if (!IsInBounds(nc, nr)) continue;

                int nIdx = GetIndex(nc, nr);
                if (Cells[nIdx] == (byte)CellType.Obstacle) continue;

                // Check diagonal corner cutting against obstacles
                if (dCol[i] != 0 && dRow[i] != 0)
                {
                    if (Cells[GetIndex(currCol + dCol[i], currRow)] == (byte)CellType.Obstacle &&
                        Cells[GetIndex(currCol, currRow + dRow[i])] == (byte)CellType.Obstacle)
                    {
                        continue; // Cannot squeeze between two diagonal wall blocks
                    }
                }

                float newDist = currDist + costs[i];
                if (newDist < BasePotential[nIdx])
                {
                    BasePotential[nIdx] = newDist;
                    pq.Enqueue(nIdx, newDist);
                }
            }
        }

        // Initialize TotalPotential from BasePotential
        Array.Copy(BasePotential, TotalPotential, TotalCells);
        RecomputeGradients();
    }

    /// <summary>
    /// Applies dynamic crowd density penalty to base potential field and updates vector gradients.
    /// Dense crowd acts as a high-resistance region; density >= JamDensityThreshold acts as impenetrable.
    /// </summary>
    public void UpdateDynamicPotential()
    {
        for (int i = 0; i < TotalCells; i++)
        {
            if (Cells[i] == (byte)CellType.Obstacle || BasePotential[i] >= ImpassablePotential)
            {
                TotalPotential[i] = ImpassablePotential;
                continue;
            }

            if (Cells[i] == (byte)CellType.Exit)
            {
                TotalPotential[i] = 0f;
                continue;
            }

            float rho = DensityGrid[i];
            float penalty = 0f;

            if (rho >= JamDensityThreshold)
            {
                // Extremely congested: acts as an impenetrable barrier, forcing diversion
                penalty = ImpassablePotential * 0.5f;
            }
            else if (rho > 1.0f)
            {
                // Quadratic crowd density penalty adds resistance, diverting traffic to lower density paths
                float ratio = (rho - 1.0f) / (JamDensityThreshold - 1.0f);
                penalty = DensityPenaltyWeight * (ratio * ratio) * Width;
            }

            TotalPotential[i] = BasePotential[i] + penalty;
        }

        RecomputeGradients();
    }

    /// <summary>
    /// Recomputes normalized gradient vectors across the grid based on TotalPotential.
    /// </summary>
    private void RecomputeGradients()
    {
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                int idx = GetIndex(c, r);

                if (Cells[idx] == (byte)CellType.Obstacle || TotalPotential[idx] >= ImpassablePotential)
                {
                    GradientX[idx] = 0f;
                    GradientY[idx] = 0f;
                    continue;
                }

                if (Cells[idx] == (byte)CellType.Exit)
                {
                    GradientX[idx] = 0f;
                    GradientY[idx] = 0f;
                    continue;
                }

                // Central difference with obstacle fallback
                float potL = (c > 0 && IsPassable(c - 1, r)) ? TotalPotential[GetIndex(c - 1, r)] : TotalPotential[idx];
                float potR = (c < Cols - 1 && IsPassable(c + 1, r)) ? TotalPotential[GetIndex(c + 1, r)] : TotalPotential[idx];
                float potU = (r > 0 && IsPassable(c, r - 1)) ? TotalPotential[GetIndex(c, r - 1)] : TotalPotential[idx];
                float potD = (r < Rows - 1 && IsPassable(c, r + 1)) ? TotalPotential[GetIndex(c, r + 1)] : TotalPotential[idx];

                float dx = (potR - potL) / (2f * CellSize);
                float dy = (potD - potU) / (2f * CellSize);

                // Desired direction is along negative gradient (path of steepest descent)
                float dirX = -dx;
                float dirY = -dy;

                float len = (float)Math.Sqrt(dirX * dirX + dirY * dirY);
                if (len > 1e-5f)
                {
                    GradientX[idx] = dirX / len;
                    GradientY[idx] = dirY / len;
                }
                else
                {
                    // Fallback to steepest passable 8-neighbor if gradient is flat
                    FindSteepestNeighborDirection(c, r, out float fbX, out float fbY);
                    GradientX[idx] = fbX;
                    GradientY[idx] = fbY;
                }
            }
        }
    }

    private void FindSteepestNeighborDirection(int c, int r, out float dirX, out float dirY)
    {
        int currIdx = GetIndex(c, r);
        float minPot = TotalPotential[currIdx];
        int bestC = c;
        int bestR = r;

        for (int dr = -1; dr <= 1; dr++)
        {
            for (int dc = -1; dc <= 1; dc++)
            {
                if (dc == 0 && dr == 0) continue;
                int nc = c + dc;
                int nr = r + dr;
                if (!IsInBounds(nc, nr) || !IsPassable(nc, nr)) continue;

                int nIdx = GetIndex(nc, nr);
                if (TotalPotential[nIdx] < minPot)
                {
                    minPot = TotalPotential[nIdx];
                    bestC = nc;
                    bestR = nr;
                }
            }
        }

        dirX = (bestC - c) * CellSize;
        dirY = (bestR - r) * CellSize;
        float len = (float)Math.Sqrt(dirX * dirX + dirY * dirY);
        if (len > 1e-5f)
        {
            dirX /= len;
            dirY /= len;
        }
        else
        {
            dirX = 0f;
            dirY = 0f;
        }
    }

    /// <summary>
    /// Samples continuous desired direction vector at (x, y) using bilinear interpolation of gradient field.
    /// Runs in O(1) time regardless of agent count.
    /// </summary>
    public void SampleDesiredDirection(float x, float y, out float dirX, out float dirY)
    {
        float gx = x / CellSize - 0.5f;
        float gy = y / CellSize - 0.5f;

        int c0 = Math.Clamp((int)Math.Floor(gx), 0, Cols - 1);
        int r0 = Math.Clamp((int)Math.Floor(gy), 0, Rows - 1);
        int c1 = Math.Clamp(c0 + 1, 0, Cols - 1);
        int r1 = Math.Clamp(r0 + 1, 0, Rows - 1);

        float tx = Math.Clamp(gx - c0, 0f, 1f);
        float ty = Math.Clamp(gy - r0, 0f, 1f);

        int i00 = GetIndex(c0, r0);
        int i10 = GetIndex(c1, r0);
        int i01 = GetIndex(c0, r1);
        int i11 = GetIndex(c1, r1);

        float gx0 = GradientX[i00] * (1f - tx) + GradientX[i10] * tx;
        float gx1 = GradientX[i01] * (1f - tx) + GradientX[i11] * tx;
        dirX = gx0 * (1f - ty) + gx1 * ty;

        float gy0 = GradientY[i00] * (1f - tx) + GradientY[i10] * tx;
        float gy1 = GradientY[i01] * (1f - tx) + GradientY[i11] * tx;
        dirY = gy0 * (1f - ty) + gy1 * ty;

        float len = (float)Math.Sqrt(dirX * dirX + dirY * dirY);
        if (len > 1e-5f)
        {
            dirX /= len;
            dirY /= len;
        }
        else
        {
            int centerIdx = GetIndex(Math.Clamp((int)(x / CellSize), 0, Cols - 1), Math.Clamp((int)(y / CellSize), 0, Rows - 1));
            dirX = GradientX[centerIdx];
            dirY = GradientY[centerIdx];
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
