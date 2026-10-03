namespace PlanSafe.App.Simulation;

/// <summary>
/// High-performance crowd simulation engine operating on double-buffered Struct-of-Arrays (SoA).
/// Executes batched passes (density scatter, dynamic potential, spatial partitioning, vectorized agent update)
/// designed for zero garbage collection and direct WebGPU compute shader migration.
/// </summary>
public class CrowdSimulator
{
    public SimulationGrid Grid { get; }
    public AgentBuffer CurrentBuffer { get; private set; }
    public AgentBuffer NextBuffer { get; private set; }

    public int MaxAgents => CurrentBuffer.Capacity;
    public int ActiveAgents { get; private set; }
    public int EvacuatedCount { get; private set; }
    public float SimulationTime { get; private set; }
    public float MeanSpeed { get; private set; }
    public float PeakDensity { get; private set; }

    // Physical agent parameters
    public float AgentRadius { get; set; } = 0.20f; // 20cm radius (40cm shoulder width)
    public float LookaheadDistance { get; set; } = 1.20f; // Distance over which anticipatory braking occurs
    public float RelaxationTime { get; set; } = 0.25f; // Relaxation time to target velocity
    public float PushStiffness { get; set; } = 40.0f; // Contact force push stiffness
    public float RepulsionStiffness { get; set; } = 60.0f; // Overlap repulsion stiffness

    // Spatial hash for O(1) neighbor lookups
    private readonly int[] _spatialHead;
    private readonly int[] _spatialNext;
    private readonly float _spatialCellSize;
    private readonly int _spatialCols;
    private readonly int _spatialRows;

    public CrowdSimulator(SimulationGrid grid, int maxAgents = 5000)
    {
        Grid = grid;
        CurrentBuffer = new AgentBuffer(maxAgents);
        NextBuffer = new AgentBuffer(maxAgents);

        _spatialCellSize = Math.Max(0.5f, LookaheadDistance);
        _spatialCols = (int)Math.Ceiling(Grid.Width / _spatialCellSize);
        _spatialRows = (int)Math.Ceiling(Grid.Height / _spatialCellSize);
        int totalSpatialCells = _spatialCols * _spatialRows;

        _spatialHead = new int[totalSpatialCells];
        _spatialNext = new int[maxAgents];
    }

    public void Reset()
    {
        CurrentBuffer.Clear();
        NextBuffer.Clear();
        ActiveAgents = 0;
        EvacuatedCount = 0;
        SimulationTime = 0f;
        MeanSpeed = 0f;
        PeakDensity = 0f;
        Array.Clear(Grid.DensityGrid, 0, Grid.TotalCells);
    }

    public void SpawnAgent(float x, float y)
    {
        CurrentBuffer.AddAgent(x, y);
        NextBuffer.AddAgent(x, y);
    }

    /// <summary>
    /// Executes one simulation time step (dt) using batched compute passes.
    /// </summary>
    public void Step(float dt)
    {
        int count = CurrentBuffer.Count;
        if (count == 0) return;

        dt = Math.Clamp(dt, 0.001f, 0.05f);
        SimulationTime += dt;

        // -------------------------------------------------------------
        // PASS 1: Density Scatter (Accumulate agents onto grid)
        // -------------------------------------------------------------
        Array.Clear(Grid.DensityGrid, 0, Grid.TotalCells);
        float maxDensity = 0f;

        for (int i = 0; i < count; i++)
        {
            if (CurrentBuffer.Active[i] == 0) continue;

            float x = CurrentBuffer.PosX[i];
            float y = CurrentBuffer.PosY[i];

            int c = Math.Clamp((int)(x / Grid.CellSize), 0, Grid.Cols - 1);
            int r = Math.Clamp((int)(y / Grid.CellSize), 0, Grid.Rows - 1);

            // Bilinear / kernel splatting onto 3x3 cells to prevent single-cell aliasing
            // Weights sum to 1.0 across the 3x3 footprint
            float centerW = 0.25f / Grid.CellArea;
            float orthoW = 0.125f / Grid.CellArea;
            float diagW = 0.0625f / Grid.CellArea;

            for (int dr = -1; dr <= 1; dr++)
            {
                for (int dc = -1; dc <= 1; dc++)
                {
                    int nc = c + dc;
                    int nr = r + dr;
                    if (Grid.IsInBounds(nc, nr))
                    {
                        int nIdx = Grid.GetIndex(nc, nr);
                        float w = (dc == 0 && dr == 0) ? centerW : ((dc == 0 || dr == 0) ? orthoW : diagW);
                        Grid.DensityGrid[nIdx] += w;
                        if (Grid.DensityGrid[nIdx] > maxDensity)
                        {
                            maxDensity = Grid.DensityGrid[nIdx];
                        }
                    }
                }
            }
        }
        PeakDensity = maxDensity;

        // -------------------------------------------------------------
        // PASS 2: Dynamic Potential & Gradient Field Update
        // -------------------------------------------------------------
        Grid.UpdateDynamicPotential();

        // -------------------------------------------------------------
        // PASS 3: Spatial Partitioning (Uniform Grid Binning)
        // -------------------------------------------------------------
        int totalSpatial = _spatialCols * _spatialRows;
        Array.Fill(_spatialHead, -1);

        for (int i = 0; i < count; i++)
        {
            if (CurrentBuffer.Active[i] == 0) continue;

            int sc = Math.Clamp((int)(CurrentBuffer.PosX[i] / _spatialCellSize), 0, _spatialCols - 1);
            int sr = Math.Clamp((int)(CurrentBuffer.PosY[i] / _spatialCellSize), 0, _spatialRows - 1);
            int sIdx = sr * _spatialCols + sc;

            _spatialNext[i] = _spatialHead[sIdx];
            _spatialHead[sIdx] = i;
        }

        // -------------------------------------------------------------
        // PASS 4: Agent Dynamics & Integration
        // -------------------------------------------------------------
        float contactDist = 2f * AgentRadius;
        float totalSpeed = 0f;
        int activeCount = 0;

        for (int i = 0; i < count; i++)
        {
            if (CurrentBuffer.Active[i] == 0)
            {
                NextBuffer.Active[i] = 0;
                continue;
            }

            float px = CurrentBuffer.PosX[i];
            float py = CurrentBuffer.PosY[i];
            float vx = CurrentBuffer.VelX[i];
            float vy = CurrentBuffer.VelY[i];

            // Check if agent reached an exit cell
            int gridC = Math.Clamp((int)(px / Grid.CellSize), 0, Grid.Cols - 1);
            int gridR = Math.Clamp((int)(py / Grid.CellSize), 0, Grid.Rows - 1);
            if (Grid.Cells[Grid.GetIndex(gridC, gridR)] == (byte)CellType.Exit)
            {
                NextBuffer.Active[i] = 0;
                CurrentBuffer.Active[i] = 0;
                NextBuffer.PosX[i] = px;
                NextBuffer.PosY[i] = py;
                EvacuatedCount++;
                continue;
            }

            // 1. Sample desired flow direction from Dijkstra potential field (O(1))
            Grid.SampleDesiredDirection(px, py, out float dirX, out float dirY);

            // Agent heading
            float currSpeed = (float)Math.Sqrt(vx * vx + vy * vy);
            float headX = (currSpeed > 0.05f) ? (vx / currSpeed) : dirX;
            float headY = (currSpeed > 0.05f) ? (vy / currSpeed) : dirY;

            // 2. Count neighbors for local crowd density (excluding self)
            int nearbyNeighbors = 0;
            float pushForceX = 0f;
            float pushForceY = 0f;
            float repulseForceX = 0f;
            float repulseForceY = 0f;
            float aheadSpeedCap = float.MaxValue;

            int sc = Math.Clamp((int)(px / _spatialCellSize), 0, _spatialCols - 1);
            int sr = Math.Clamp((int)(py / _spatialCellSize), 0, _spatialRows - 1);

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nsc = sc + dx;
                    int nsr = sr + dy;
                    if (nsc < 0 || nsc >= _spatialCols || nsr < 0 || nsr >= _spatialRows) continue;

                    int sIdx = nsr * _spatialCols + nsc;
                    int j = _spatialHead[sIdx];

                    while (j != -1)
                    {
                        if (j != i && CurrentBuffer.Active[j] == 1)
                        {
                            float rx = CurrentBuffer.PosX[j] - px;
                            float ry = CurrentBuffer.PosY[j] - py;
                            float distSq = rx * rx + ry * ry;

                            if (distSq < LookaheadDistance * LookaheadDistance && distSq > 1e-6f)
                            {
                                float dist = (float)Math.Sqrt(distSq);
                                float nx = rx / dist;
                                float ny = ry / dist;

                                // Relative angle with current heading
                                float cosTheta = headX * nx + headY * ny;

                                // Count neighbors within 1m radius for Weidmann local density
                                if (dist < 1.0f)
                                {
                                    nearbyNeighbors++;
                                }

                                if (cosTheta < 0f)
                                {
                                    // -------------------------------------------------------------
                                    // AGENT BEHIND:
                                    // No influence UNLESS in immediate physical contact distance (< 2r).
                                    // Pushes agent ahead in immediate distance!
                                    // -------------------------------------------------------------
                                    if (dist < contactDist)
                                    {
                                        float overlap = contactDist - dist;
                                        // Pushes agent i forward (in direction from j to i)
                                        pushForceX += -nx * overlap * PushStiffness;
                                        pushForceY += -ny * overlap * PushStiffness;
                                    }
                                }
                                else
                                {
                                    // -------------------------------------------------------------
                                    // AGENT IN FRONT:
                                    // Dynamically and gradually slow down before agent in front!
                                    // -------------------------------------------------------------
                                    if (cosTheta > 0.3f && dist < LookaheadDistance) // Ahead inside visual cone
                                    {
                                        float brakeRange = LookaheadDistance - contactDist;
                                        float brakeRatio = Math.Clamp((dist - contactDist) / Math.Max(0.01f, brakeRange), 0f, 1f);

                                        // Speed of front agent
                                        float frontSpeed = CurrentBuffer.Speed[j];
                                        float allowedSpeed = frontSpeed + brakeRatio * (WeidmannModel.DefaultFreeSpeed - frontSpeed);

                                        if (allowedSpeed < aheadSpeedCap)
                                        {
                                            aheadSpeedCap = Math.Max(0f, allowedSpeed);
                                        }
                                    }

                                    // Direct physical overlap in front -> soft repulsive separation
                                    if (dist < contactDist)
                                    {
                                        float overlap = contactDist - dist;
                                        repulseForceX -= nx * overlap * RepulsionStiffness;
                                        repulseForceY -= ny * overlap * RepulsionStiffness;
                                    }
                                }
                            }
                        }
                        j = _spatialNext[j];
                    }
                }
            }

            // 3. Evaluate Weidmann desired speed based on local crowd density
            float localRho = nearbyNeighbors / (MathF.PI * 1.0f * 1.0f);
            float desiredSpeed = WeidmannModel.CalculateSpeed(localRho);

            // 4. Compute target velocity and acceleration
            float targetSpeed = Math.Min(desiredSpeed, aheadSpeedCap);
            float targetVx = dirX * targetSpeed;
            float targetVy = dirY * targetSpeed;

            // Relaxation towards target velocity + external contact/repulsive forces
            float ax = (targetVx - vx) / RelaxationTime + pushForceX + repulseForceX;
            float ay = (targetVy - vy) / RelaxationTime + pushForceY + repulseForceY;

            // 5. Integrate velocity & position
            float newVx = vx + ax * dt;
            float newVy = vy + ay * dt;

            // Velocity clamp
            float newSpeed = (float)Math.Sqrt(newVx * newVx + newVy * newVy);
            float maxAllowedSpeed = WeidmannModel.DefaultFreeSpeed * 1.2f;
            if (newSpeed > maxAllowedSpeed)
            {
                newVx = (newVx / newSpeed) * maxAllowedSpeed;
                newVy = (newVy / newSpeed) * maxAllowedSpeed;
                newSpeed = maxAllowedSpeed;
            }

            float newPx = px + newVx * dt;
            float newPy = py + newVy * dt;

            // 6. Obstacle & Arena Boundary Collision Resolution
            ResolveObstacleCollisions(px, py, ref newPx, ref newPy, ref newVx, ref newVy);

            // Write to NextBuffer
            NextBuffer.PosX[i] = newPx;
            NextBuffer.PosY[i] = newPy;
            NextBuffer.VelX[i] = newVx;
            NextBuffer.VelY[i] = newVy;
            NextBuffer.Speed[i] = newSpeed;
            NextBuffer.Active[i] = 1;

            totalSpeed += newSpeed;
            activeCount++;
        }

        ActiveAgents = activeCount;
        MeanSpeed = (activeCount > 0) ? (totalSpeed / activeCount) : 0f;

        // Swap ping-pong buffers
        SwapBuffers();
    }

    private void ResolveObstacleCollisions(
        float oldX, float oldY,
        ref float newX, ref float newY,
        ref float vx, ref float vy)
    {
        float margin = AgentRadius * 1.05f;

        // Arena outer boundary clamp
        if (newX < margin) { newX = margin; vx = 0f; }
        if (newX > Grid.Width - margin) { newX = Grid.Width - margin; vx = 0f; }
        if (newY < margin) { newY = margin; vy = 0f; }
        if (newY > Grid.Height - margin) { newY = Grid.Height - margin; vy = 0f; }

        int c = Math.Clamp((int)(newX / Grid.CellSize), 0, Grid.Cols - 1);
        int r = Math.Clamp((int)(newY / Grid.CellSize), 0, Grid.Rows - 1);

        if (Grid.Cells[Grid.GetIndex(c, r)] == (byte)CellType.Obstacle)
        {
            // Revert movement and slide along available axis
            int oldC = Math.Clamp((int)(oldX / Grid.CellSize), 0, Grid.Cols - 1);
            int oldR = Math.Clamp((int)(oldY / Grid.CellSize), 0, Grid.Rows - 1);

            // Try horizontal slide
            if (Grid.Cells[Grid.GetIndex(c, oldR)] != (byte)CellType.Obstacle)
            {
                newY = oldY;
                vy = 0f;
            }
            // Try vertical slide
            else if (Grid.Cells[Grid.GetIndex(oldC, r)] != (byte)CellType.Obstacle)
            {
                newX = oldX;
                vx = 0f;
            }
            else
            {
                // Full stop at obstacle boundary
                newX = oldX;
                newY = oldY;
                vx = 0f;
                vy = 0f;
            }
        }
    }

    private void SwapBuffers()
    {
        var temp = CurrentBuffer;
        CurrentBuffer = NextBuffer;
        NextBuffer = temp;
    }
}
