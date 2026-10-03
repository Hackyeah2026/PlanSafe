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
        Grid.SampleDesiredDirection(x, y, out float dirX, out float dirY);
        if (dirX == 0f && dirY == 0f) dirX = 1f;
        CurrentBuffer.AddAgent(x, y, 0f, 0f, dirX, dirY);
        NextBuffer.AddAgent(x, y, 0f, 0f, dirX, dirY);
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
            if (dirX == 0f && dirY == 0f)
            {
                dirX = CurrentBuffer.HeadX[i];
                dirY = CurrentBuffer.HeadY[i];
                if (dirX == 0f && dirY == 0f) dirX = 1f;
            }

            // Heading is strictly the path of least resistance to the goal! (Never spins)
            float headX = dirX;
            float headY = dirY;

            // 2. Count neighbors for local crowd density & front/behind interactions
            int nearbyNeighbors = 0;
            float aheadSpeedCap = float.MaxValue;
            float maxForwardStep = float.MaxValue;
            float pushBoost = 0f;
            float lateralShiftX = 0f;
            float lateralShiftY = 0f;

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

                                // Longitudinal distance along the goal direction (positive = in front, negative = behind)
                                float dParallel = rx * dirX + ry * dirY;
                                float dPerpSq = Math.Max(0f, distSq - dParallel * dParallel);
                                float dPerp = (float)Math.Sqrt(dPerpSq);

                                // Count neighbors in forward travel hemisphere (dParallel > -AgentRadius within 1m radius)
                                // Trailing neighbors behind do not impede forward desired speed
                                if (dist < 1.0f && dParallel > -AgentRadius)
                                {
                                    nearbyNeighbors++;
                                }

                                if (dParallel < 0f)
                                {
                                    // -------------------------------------------------------------
                                    // AGENT BEHIND:
                                    // No influence UNLESS in immediate physical contact distance (< 2r).
                                    // Pushes agent ahead in immediate distance strictly forward along goal direction!
                                    // -------------------------------------------------------------
                                    if (dist < contactDist)
                                    {
                                        float overlap = contactDist - dist;
                                        pushBoost += (-dParallel / dist) * overlap * PushStiffness;
                                    }
                                }
                                else
                                {
                                    // -------------------------------------------------------------
                                    // AGENT IN FRONT:
                                    // Blocks forward motion if within lane width (dPerp < contactDist)
                                    // -------------------------------------------------------------
                                    if (dPerp < contactDist)
                                    {
                                        float contactParallel = (float)Math.Sqrt(Math.Max(0f, contactDist * contactDist - dPerpSq));
                                        float availableGap = dParallel - contactParallel;
                                        float frontSpeed = Math.Max(0f, CurrentBuffer.Speed[j]);

                                        if (availableGap <= 0f)
                                        {
                                            // Direct physical contact/overlap:
                                            // CANNOT move faster than front agent, and forward advance is bounded by front agent!
                                            aheadSpeedCap = Math.Min(aheadSpeedCap, frontSpeed);
                                            maxForwardStep = Math.Min(maxForwardStep, Math.Max(0f, frontSpeed * dt));

                                            // Lateral separation to slide into open lanes
                                            float overlap = -availableGap;
                                            float nPerpX = rx - dParallel * dirX;
                                            float nPerpY = ry - dParallel * dirY;
                                            float perpLen = (float)Math.Sqrt(nPerpX * nPerpX + nPerpY * nPerpY);

                                            if (perpLen > 1e-4f)
                                            {
                                                lateralShiftX -= (nPerpX / perpLen) * overlap * 0.5f;
                                                lateralShiftY -= (nPerpY / perpLen) * overlap * 0.5f;
                                            }
                                            else
                                            {
                                                float sign = (i % 2 == 0) ? 1.0f : -1.0f;
                                                lateralShiftX += sign * (-dirY) * overlap * 0.5f;
                                                lateralShiftY += sign * (dirX) * overlap * 0.5f;
                                            }
                                        }
                                        else if (dParallel < LookaheadDistance)
                                        {
                                            // Approaching from behind inside forward lane:
                                            // 1. Kinematic non-penetration limit in time step dt
                                            float kinematicLimit = (availableGap / dt) * 0.85f + frontSpeed;

                                            // 2. Smooth anticipatory braking when closing in within ComfortGap (25cm)
                                            const float comfortGap = 0.25f; // 25cm comfortable queue gap
                                            float brakeRatio = Math.Clamp(availableGap / comfortGap, 0f, 1f);
                                            float smoothSpeed = frontSpeed + brakeRatio * Math.Max(0f, WeidmannModel.DefaultFreeSpeed - frontSpeed);

                                            float allowedSpeed = Math.Min(smoothSpeed, kinematicLimit);
                                            aheadSpeedCap = Math.Min(aheadSpeedCap, Math.Max(0f, allowedSpeed));

                                            float allowedStep = availableGap * 0.85f + frontSpeed * dt;
                                            maxForwardStep = Math.Min(maxForwardStep, Math.Max(0f, allowedStep));
                                        }
                                    }
                                }
                            }
                        }
                        j = _spatialNext[j];
                    }
                }
            }

            // 3. Evaluate Weidmann desired speed based on forward crowd density
            // Effective area of 1m forward semicircle + shoulder margin is ~2.0 m²
            float localRho = nearbyNeighbors / 2.0f;

            float desiredSpeed = WeidmannModel.CalculateSpeed(localRho);

            // 4. Compute target forward speed (strictly bounded by front agents and density)
            float speedLimit = Math.Min(desiredSpeed, aheadSpeedCap);
            float currentSpeed = CurrentBuffer.Speed[i];

            // Forward acceleration towards target speed
            float accel = (speedLimit - currentSpeed) / RelaxationTime;
            if (pushBoost > 0f && currentSpeed < speedLimit)
            {
                // Behind push helps accelerate up to speedLimit, never past it
                accel += Math.Min(pushBoost, (speedLimit - currentSpeed) / dt);
            }

            // 5. Integrate forward speed: HARD CLAMP to speedLimit to prevent penetrating leaders or violating Weidmann density!
            float newSpeed = currentSpeed + accel * dt;
            newSpeed = Math.Clamp(newSpeed, 0f, speedLimit);

            // 6. Forward displacement strictly bounded by physical gap to front agents
            float forwardStep = Math.Min(newSpeed * dt, maxForwardStep);
            forwardStep = Math.Max(0f, forwardStep);
            newSpeed = (dt > 1e-6f) ? (forwardStep / dt) : 0f;

            // Strictly enforce lateralShift is perpendicular to (dirX, dirY) and clamped
            float dotLat = lateralShiftX * dirX + lateralShiftY * dirY;
            lateralShiftX -= dotLat * dirX;
            lateralShiftY -= dotLat * dirY;
            float latLen = (float)Math.Sqrt(lateralShiftX * lateralShiftX + lateralShiftY * lateralShiftY);
            float maxLat = AgentRadius * 0.25f; // at most 5cm per step
            if (latLen > maxLat)
            {
                lateralShiftX = (lateralShiftX / latLen) * maxLat;
                lateralShiftY = (lateralShiftY / latLen) * maxLat;
            }

            // Forward step along path of least resistance + lateral overlap adjustment
            float newPx = px + dirX * forwardStep + lateralShiftX;
            float newPy = py + dirY * forwardStep + lateralShiftY;

            // 6. Obstacle & Arena Boundary Collision Resolution
            float dummyVx = dirX * newSpeed;
            float dummyVy = dirY * newSpeed;
            ResolveObstacleCollisions(px, py, dirX, dirY, ref newPx, ref newPy, ref dummyVx, ref dummyVy);
            if (dummyVx == 0f && dummyVy == 0f)
            {
                newSpeed = 0f;
            }

            // 7. Sample desired direction along path of least resistance at new position
            Grid.SampleDesiredDirection(newPx, newPy, out float newDirX, out float newDirY);
            if (newDirX == 0f && newDirY == 0f)
            {
                newDirX = dirX;
                newDirY = dirY;
            }

            // Write to NextBuffer (velocity and heading strictly track path of least resistance)
            NextBuffer.PosX[i] = newPx;
            NextBuffer.PosY[i] = newPy;
            NextBuffer.VelX[i] = newDirX * newSpeed;
            NextBuffer.VelY[i] = newDirY * newSpeed;
            NextBuffer.Speed[i] = newSpeed;
            NextBuffer.HeadX[i] = newDirX;
            NextBuffer.HeadY[i] = newDirY;
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
        float dirX, float dirY,
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

            // Verify the sliding step does not move backwards along goal direction
            float slideProgress = (newX - oldX) * dirX + (newY - oldY) * dirY;
            if (slideProgress < 0f)
            {
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
