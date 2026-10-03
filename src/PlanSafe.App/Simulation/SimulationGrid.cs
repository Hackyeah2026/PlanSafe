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

        float diagCost = (float)(Math.Sqrt(2.0) * CellSize);
        float orthoCost = CellSize;

        // 8-directional offsets
        int[] dCol = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] dRow = { -1, -1, -1, 0, 0, 1, 1, 1 };
        float[] costs = { diagCost, orthoCost, diagCost, orthoCost, orthoCost, diagCost, orthoCost, diagCost };

        while (_pq.TryDequeue(out int currIdx, out float currDist))
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
                        continue;
                    }
                }

                float newDist = currDist + costs[i];
                if (newDist < BasePotential[nIdx])
                {
                    BasePotential[nIdx] = newDist;
                    _pq.Enqueue(nIdx, newDist);
                }
            }
        }

        // Compute static base gradient
        RecomputeGradients(BasePotential, BaseGradientX, BaseGradientY, enforceBase: false);

        // Initialize TotalPotential from BasePotential
        Array.Copy(BasePotential, TotalPotential, TotalCells);
        Array.Copy(BaseGradientX, GradientX, TotalCells);
        Array.Copy(BaseGradientY, GradientY, TotalCells);
    }

    /// <summary>
    /// Computes dynamic potential field by propagating from exits with crowd density costs.
    /// Guarantees that TotalPotential decreases monotonically towards exits without local maxima or backward gradients.
    /// Jammed areas (rho >= JamDensityThreshold) act as impassable obstacles, diverting flow around them.
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

        float diagUnit = (float)Math.Sqrt(2.0);
        int[] dCol = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] dRow = { -1, -1, -1, 0, 0, 1, 1, 1 };

        while (_pq.TryDequeue(out int currIdx, out float currDist))
        {
            if (currDist > TotalPotential[currIdx]) continue;

            int currCol = currIdx % Cols;
            int currRow = currIdx / Cols;

            for (int i = 0; i < 8; i++)
            {
                int nc = currCol + dCol[i];
                int nr = currRow + dRow[i];

                if (!IsInBounds(nc, nr)) continue;

                int nIdx = GetIndex(nc, nr);
                if (Cells[nIdx] == (byte)CellType.Obstacle) continue;

                // Diagonal corner cutting check
                if (dCol[i] != 0 && dRow[i] != 0)
                {
                    if (Cells[GetIndex(currCol + dCol[i], currRow)] == (byte)CellType.Obstacle &&
                        Cells[GetIndex(currCol, currRow + dRow[i])] == (byte)CellType.Obstacle)
                    {
                        continue;
                    }
                }

                // Dynamic resistance based on crowd density
                float rho = DensityGrid[nIdx];
                float distStep = (dCol[i] != 0 && dRow[i] != 0) ? (diagUnit * CellSize) : CellSize;
                float densityMultiplier = 1.0f;
                if (rho > 0.5f)
                {
                    float ratio = (rho - 0.5f) / Math.Max(0.1f, JamDensityThreshold - 0.5f);
                    densityMultiplier = 1.0f + DensityPenaltyWeight * Math.Min(25.0f, ratio * ratio);
                }

                float newDist = currDist + distStep * densityMultiplier;
                if (newDist < TotalPotential[nIdx])
                {
                    TotalPotential[nIdx] = newDist;
                    _pq.Enqueue(nIdx, newDist);
                }
            }
        }

        // For any cells unreachable through dynamic field (e.g. completely encircled by jam),
        // fallback to BasePotential so agents never get trapped without a path
        for (int i = 0; i < TotalCells; i++)
        {
            if (Cells[i] != (byte)CellType.Obstacle && TotalPotential[i] >= ImpassablePotential)
            {
                TotalPotential[i] = BasePotential[i];
            }
        }

        RecomputeGradients(TotalPotential, GradientX, GradientY, enforceBase: true);
    }

    /// <summary>
    /// Recomputes normalized gradient vectors across the grid based on the specified potential field.
    /// When enforceBase is true, verifies that all vectors maintain positive progress towards the exits.
    /// </summary>
    private void RecomputeGradients(float[] potentialField, float[] outGradX, float[] outGradY, bool enforceBase)
    {
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                int idx = GetIndex(c, r);

                if (Cells[idx] == (byte)CellType.Obstacle || potentialField[idx] >= ImpassablePotential)
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

                float currPot = potentialField[idx];

                // Central or one-sided differences
                float dx = 0f;
                bool hasL = c > 0 && IsPassable(c - 1, r) && potentialField[GetIndex(c - 1, r)] < ImpassablePotential;
                bool hasR = c < Cols - 1 && IsPassable(c + 1, r) && potentialField[GetIndex(c + 1, r)] < ImpassablePotential;

                if (hasL && hasR)
                {
                    dx = (potentialField[GetIndex(c + 1, r)] - potentialField[GetIndex(c - 1, r)]) / (2f * CellSize);
                }
                else if (hasR)
                {
                    dx = (potentialField[GetIndex(c + 1, r)] - currPot) / CellSize;
                }
                else if (hasL)
                {
                    dx = (currPot - potentialField[GetIndex(c - 1, r)]) / CellSize;
                }

                float dy = 0f;
                bool hasU = r > 0 && IsPassable(c, r - 1) && potentialField[GetIndex(c, r - 1)] < ImpassablePotential;
                bool hasD = r < Rows - 1 && IsPassable(c, r + 1) && potentialField[GetIndex(c, r + 1)] < ImpassablePotential;

                if (hasU && hasD)
                {
                    dy = (potentialField[GetIndex(c, r + 1)] - potentialField[GetIndex(c, r - 1)]) / (2f * CellSize);
                }
                else if (hasD)
                {
                    dy = (potentialField[GetIndex(c, r + 1)] - currPot) / CellSize;
                }
                else if (hasU)
                {
                    dy = (currPot - potentialField[GetIndex(c, r - 1)]) / CellSize;
                }

                float dirX = -dx;
                float dirY = -dy;
                float len = (float)Math.Sqrt(dirX * dirX + dirY * dirY);

                if (len > 1e-4f)
                {
                    dirX /= len;
                    dirY /= len;
                }
                else
                {
                    // Fallback to steepest 8-neighbor descent
                    FindSteepestNeighborDirection(c, r, potentialField, out dirX, out dirY);
                }

                if (enforceBase && BaseGradientX != null)
                {
                    float bgx = BaseGradientX[idx];
                    float bgy = BaseGradientY[idx];
                    float baseLenSq = bgx * bgx + bgy * bgy;

                    if (baseLenSq > 0.5f)
                    {
                        // Lateral unit vector perpendicular to base direction
                        float perpX = -bgy;
                        float perpY = bgx;

                        float fParallel = dirX * bgx + dirY * bgy;
                        float fPerp = dirX * perpX + dirY * perpY;

                        // Ensure forward progress is always dominant: maximum deflection angle is 45 degrees
                        const float minForward = 0.7071f; // cos(45 deg)
                        if (fParallel < minForward)
                        {
                            fParallel = minForward;
                            float maxPerp = 0.7071f;
                            fPerp = Math.Clamp(fPerp, -maxPerp, maxPerp);

                            dirX = fParallel * bgx + fPerp * perpX;
                            dirY = fParallel * bgy + fPerp * perpY;
                            float renorm = (float)Math.Sqrt(dirX * dirX + dirY * dirY);
                            if (renorm > 1e-4f)
                            {
                                dirX /= renorm;
                                dirY /= renorm;
                            }
                            else
                            {
                                dirX = bgx;
                                dirY = bgy;
                            }
                        }
                    }
                }

                outGradX[idx] = dirX;
                outGradY[idx] = dirY;
            }
        }
    }

    private void FindSteepestNeighborDirection(int c, int r, float[] potentialField, out float dirX, out float dirY)
    {
        int currIdx = GetIndex(c, r);
        float currPot = potentialField[currIdx];
        float minPot = currPot;
        int bestC = c;
        int bestR = r;

        int[] dCol = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] dRow = { -1, -1, -1, 0, 0, 1, 1, 1 };

        for (int i = 0; i < 8; i++)
        {
            int nc = c + dCol[i];
            int nr = r + dRow[i];
            if (!IsInBounds(nc, nr) || !IsPassable(nc, nr)) continue;

            if (dCol[i] != 0 && dRow[i] != 0)
            {
                if (Cells[GetIndex(c + dCol[i], r)] == (byte)CellType.Obstacle &&
                    Cells[GetIndex(c, r + dRow[i])] == (byte)CellType.Obstacle)
                {
                    continue;
                }
            }

            int nIdx = GetIndex(nc, nr);
            if (potentialField[nIdx] < minPot)
            {
                minPot = potentialField[nIdx];
                bestC = nc;
                bestR = nr;
            }
        }

        if (bestC == c && bestR == r)
        {
            dirX = 0f;
            dirY = 0f;
            return;
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
        if (len > 1e-4f)
        {
            dirX /= len;
            dirY /= len;
        }
        else
        {
            int centerIdx = GetIndex(Math.Clamp((int)(x / CellSize), 0, Cols - 1), Math.Clamp((int)(y / CellSize), 0, Rows - 1));
            dirX = GradientX[centerIdx];
            dirY = GradientY[centerIdx];
            if (dirX == 0f && dirY == 0f && BaseGradientX != null)
            {
                dirX = BaseGradientX[centerIdx];
                dirY = BaseGradientY[centerIdx];
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
