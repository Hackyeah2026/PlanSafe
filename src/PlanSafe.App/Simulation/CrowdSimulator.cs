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
    public SimulationStatsCollector? StatsCollector { get; set; }

    // Physical agent parameters
    public float AgentRadius { get; set; } = 0.20f; // 20cm radius (40cm shoulder width)
    public float LookaheadDistance { get; set; } = 1.20f; // Distance over which anticipatory braking occurs
    public float RelaxationTime { get; set; } = 0.25f; // Relaxation time to target velocity
    public float PushStiffness { get; set; } = 40.0f; // Contact force push stiffness
    public float RepulsionStiffness { get; set; } = 60.0f; // Overlap repulsion stiffness

    // Scratch arrays for zero-allocation simulation step
    private readonly int[] _scratchPass2Indices = new int[2048];
    private readonly float[] _scratchDx = new float[2048];
    private readonly float[] _scratchDy = new float[2048];
    private readonly float[] _scratchDist = new float[2048];
    private readonly float[] _scratchDensity;

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

        _scratchDensity = new float[grid.TotalCells];
        _spatialCellSize = 1.5f;
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
        StatsCollector?.Reset(ActiveAgents);
    }

    public void SpawnAgent(float x, float y)
    {
        Grid.SampleDesiredDirection(x, y, out float dirX, out float dirY);
        if (dirX == 0f && dirY == 0f) dirX = 1f;
        CurrentBuffer.AddAgent(x, y, 0f, 0f, dirX, dirY);
        NextBuffer.AddAgent(x, y, 0f, 0f, dirX, dirY);
    }

    /// <summary>
    /// Executes one simulation time step (dt) using PlanSafe continuum crowd dynamics.
    /// Incorporates analytical bilinear potential field flow, continuous wall sliding,
    /// radial crowd pressure decompression, and empirical fundamental diagram bounds.
    /// </summary>
    public void Step(float dt)
    {
        int count = CurrentBuffer.Count;
        if (count == 0) return;

        dt = Math.Clamp(dt, 0.001f, 0.05f);
        SimulationTime += dt;

        // -------------------------------------------------------------
        // PASS 1: Density Scatter & Smoothing (Matching PlanSafe)
        // -------------------------------------------------------------
        Array.Clear(Grid.DensityGrid, 0, Grid.TotalCells);
        float invCellArea = 1.0f / Grid.CellArea;

        for (int i = 0; i < count; i++)
        {
            if (CurrentBuffer.Active[i] == 0) continue;

            float u = CurrentBuffer.PosX[i] / Grid.CellSize - 0.5f;
            float v = CurrentBuffer.PosY[i] / Grid.CellSize - 0.5f;

            int c0 = (int)Math.Floor(u);
            int r0 = (int)Math.Floor(v);

            float s = u - c0;
            float t = v - r0;

            float w00 = (1.0f - s) * (1.0f - t);
            float w10 = s * (1.0f - t);
            float w01 = (1.0f - s) * t;
            float w11 = s * t;

            int c1 = c0 + 1;
            int r1 = r0 + 1;

            if (c0 >= 0 && c0 < Grid.Cols && r0 >= 0 && r0 < Grid.Rows)
                Grid.DensityGrid[r0 * Grid.Cols + c0] += w00 * invCellArea;
            if (c1 >= 0 && c1 < Grid.Cols && r0 >= 0 && r0 < Grid.Rows)
                Grid.DensityGrid[r0 * Grid.Cols + c1] += w10 * invCellArea;
            if (c0 >= 0 && c0 < Grid.Cols && r1 >= 0 && r1 < Grid.Rows)
                Grid.DensityGrid[r1 * Grid.Cols + c0] += w01 * invCellArea;
            if (c1 >= 0 && c1 < Grid.Cols && r1 >= 0 && r1 < Grid.Rows)
                Grid.DensityGrid[r1 * Grid.Cols + c1] += w11 * invCellArea;
        }

        // 1 pass spatial smoothing across neighborhood to eliminate discrete holes
        Array.Copy(Grid.DensityGrid, _scratchDensity, Grid.TotalCells);
        float maxDensity = 0f;

        for (int r = 0; r < Grid.Rows; r++)
        {
            int rOffset = r * Grid.Cols;
            int rPrev = Math.Max(0, r - 1) * Grid.Cols;
            int rNext = Math.Min(Grid.Rows - 1, r + 1) * Grid.Cols;

            for (int c = 0; c < Grid.Cols; c++)
            {
                int cPrev = Math.Max(0, c - 1);
                int cNext = Math.Min(Grid.Cols - 1, c + 1);

                float center = _scratchDensity[rOffset + c];
                float orthogonal = _scratchDensity[rOffset + cPrev] + _scratchDensity[rOffset + cNext] +
                                   _scratchDensity[rPrev + c] + _scratchDensity[rNext + c];

                float smoothed = 0.50f * center + 0.125f * orthogonal;
                Grid.DensityGrid[rOffset + c] = smoothed;
                if (smoothed > maxDensity) maxDensity = smoothed;
            }
        }
        PeakDensity = maxDensity;

        // -------------------------------------------------------------
        // PASS 2: Dynamic Potential Field Update
        // -------------------------------------------------------------
        Grid.UpdateDynamicPotential();

        // -------------------------------------------------------------
        // PASS 3: Spatial Partitioning (Uniform Grid Binning)
        // -------------------------------------------------------------
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
        // PASS 4: Agent Dynamics & Continuum Physics
        // -------------------------------------------------------------
        float totalSpeed = 0f;
        int activeCount = 0;
        float minDist = 2f * AgentRadius; // 0.40m
        float spatialSearchRadius = 2.8f;
        float spatialSearchRadiusSq = spatialSearchRadius * spatialSearchRadius;
        float repulsionThreshold = minDist * 2.2f; // 0.88m
        float repulsionThresholdSq = repulsionThreshold * repulsionThreshold;
        float comfortDistance = AgentRadius + 0.12f; // 0.32m (fits 4 lanes across 2.0m corridor)
        float comfortDistSq = comfortDistance * comfortDistance;
        int obsCount = Grid.Obstacles.Count;

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
            float currentSpeed = CurrentBuffer.Speed[i];

            // Exit check
            int gridC = Math.Clamp((int)(px / Grid.CellSize), 0, Grid.Cols - 1);
            int gridR = Math.Clamp((int)(py / Grid.CellSize), 0, Grid.Rows - 1);
            if (Grid.Cells[Grid.GetIndex(gridC, gridR)] == (byte)CellType.Exit)
            {
                NextBuffer.Active[i] = 0;
                CurrentBuffer.Active[i] = 0;
                NextBuffer.PosX[i] = px;
                NextBuffer.PosY[i] = py;
                EvacuatedCount++;
                StatsCollector?.RecordAgentEvacuated(SimulationTime);
                continue;
            }

            // 1. Flow direction & heading
            Grid.SampleDesiredDirection(px, py, out float flowDirX, out float flowDirY);
            if (flowDirX == 0f && flowDirY == 0f)
            {
                flowDirX = CurrentBuffer.HeadX[i];
                flowDirY = CurrentBuffer.HeadY[i];
                if (flowDirX == 0f && flowDirY == 0f) flowDirX = 1f;
            }

            float travelDirectionX = flowDirX;
            float travelDirectionY = flowDirY;

            bool hasSignificantVelocity = currentSpeed > 0.05f;
            float normVelX = hasSignificantVelocity ? (vx / currentSpeed) : flowDirX;
            float normVelY = hasSignificantVelocity ? (vy / currentSpeed) : flowDirY;

            // 2. Obstacle comfort zone & tangent wall sliding
            float wallRepulsionForceX = 0f;
            float wallRepulsionForceY = 0f;
            float minDistanceToObstacle = float.MaxValue;

            for (int o = 0; o < obsCount; o++)
            {
                var obs = Grid.Obstacles[o];
                if (px < obs.X - 2.5f || px > obs.X + obs.Width + 2.5f ||
                    py < obs.Y - 2.5f || py > obs.Y + obs.Height + 2.5f)
                    continue;

                float nearestBoxPointX = Math.Clamp(px, obs.X, obs.X + obs.Width);
                float nearestBoxPointY = Math.Clamp(py, obs.Y, obs.Y + obs.Height);
                float dx = px - nearestBoxPointX;
                float dy = py - nearestBoxPointY;
                float distSq = dx * dx + dy * dy;

                if (distSq < comfortDistSq && distSq > 1e-6f)
                {
                    float distToBox = (float)Math.Sqrt(distSq);
                    if (distToBox < minDistanceToObstacle) minDistanceToObstacle = distToBox;

                    float normalX = dx / distToBox;
                    float normalY = dy / distToBox;

                    float wallRepulsionStrength = 1.5f * (comfortDistance - distToBox) / comfortDistance;
                    wallRepulsionForceX += normalX * wallRepulsionStrength;
                    wallRepulsionForceY += normalY * wallRepulsionStrength;

                    // Wall sliding: ześlizg kierunku marszu wzdłuż krawędzi/narożnika
                    float dotWithNormal = travelDirectionX * normalX + travelDirectionY * normalY;
                    if (dotWithNormal < 0f)
                    {
                        travelDirectionX -= dotWithNormal * normalX;
                        travelDirectionY -= dotWithNormal * normalY;
                        float slideSpeed = (float)Math.Sqrt(travelDirectionX * travelDirectionX + travelDirectionY * travelDirectionY);
                        if (slideSpeed > 0.05f)
                        {
                            travelDirectionX /= slideSpeed;
                            travelDirectionY /= slideSpeed;
                        }
                        else
                        {
                            // Czołowe zderzenie ze ścianą: nadaj wektor styczny do ściany omijający przeszkodę
                            float tan1X = -normalY;
                            float tan1Y = normalX;
                            float dotFlow = flowDirX * tan1X + flowDirY * tan1Y;
                            if (dotFlow > 0.001f)
                            {
                                travelDirectionX = tan1X;
                                travelDirectionY = tan1Y;
                            }
                            else if (dotFlow < -0.001f)
                            {
                                travelDirectionX = -tan1X;
                                travelDirectionY = -tan1Y;
                            }
                            else
                            {
                                float dotVel = normVelX * tan1X + normVelY * tan1Y;
                                if (dotVel >= 0f)
                                {
                                    travelDirectionX = tan1X;
                                    travelDirectionY = tan1Y;
                                }
                                else
                                {
                                    travelDirectionX = -tan1X;
                                    travelDirectionY = -tan1Y;
                                }
                            }
                        }
                    }
                }
            }

            // 3. Anisotropic neighbor query
            float totalRadialDensity = 0f;
            float forwardKernelSum = 0f;
            float closestForwardDistance = 10.0f;
            float crowdGradX = 0f;
            float crowdGradY = 0f;
            bool blocked = false;
            float closestBlockDist = LookaheadDistance;
            float densityLeft = 0f;
            float densityRight = 0f;
            int pass2Count = 0;
            int frontLeaderIndex = -1;
            int aheadNeighbors1m = 0;

            int sc = Math.Clamp((int)(px / _spatialCellSize), 0, _spatialCols - 1);
            int sr = Math.Clamp((int)(py / _spatialCellSize), 0, _spatialRows - 1);

            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
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

                            if (distSq <= spatialSearchRadiusSq && distSq > 1e-6f)
                            {
                                float dist = (float)Math.Sqrt(distSq);

                                if (distSq <= repulsionThresholdSq && pass2Count < _scratchPass2Indices.Length)
                                {
                                    _scratchPass2Indices[pass2Count] = j;
                                    _scratchDx[pass2Count] = rx;
                                    _scratchDy[pass2Count] = ry;
                                    _scratchDist[pass2Count] = dist;
                                    pass2Count++;
                                }

                                float forwardDist = rx * flowDirX + ry * flowDirY;
                                float signedLateral = rx * (-flowDirY) + ry * flowDirX;
                                float lateralDist = Math.Abs(signedLateral);

                                if (distSq < 4.0f)
                                {
                                    float w = 1.0f - distSq * 0.25f;
                                    totalRadialDensity += 0.764f * (w * w);

                                    // Wektor środka ciężkości tłumu wokół agenta
                                    crowdGradX += (rx / dist) * w;
                                    crowdGradY += (ry / dist) * w;

                                    if (forwardDist > 0f && distSq < 1.0f)
                                    {
                                        aheadNeighbors1m++;
                                    }

                                    if (forwardDist > 0.05f)
                                    {
                                        float cosAngle = forwardDist / dist;
                                        if (cosAngle > 0.50f)
                                        {
                                            float wf = w * cosAngle;
                                            forwardKernelSum += 1.528f * (wf * wf);
                                        }

                                        if (forwardDist < closestForwardDistance && lateralDist < 0.38f)
                                        {
                                            closestForwardDistance = forwardDist;
                                            frontLeaderIndex = j;
                                        }
                                    }
                                }

                                // Bilans gęstości lewo / prawo dla rozprężania poprzecznego
                                if (forwardDist > -0.5f)
                                {
                                    float wLat = 1.0f - (dist / spatialSearchRadius);
                                    if (signedLateral > 0.08f) densityLeft += wLat;
                                    else if (signedLateral < -0.08f) densityRight += wLat;
                                }

                                if (forwardDist > 0.02f && forwardDist < LookaheadDistance)
                                {
                                    float collisionThreshold = minDist * 0.80f; // 0.32m
                                    if (lateralDist < collisionThreshold)
                                    {
                                        blocked = true;
                                        if (forwardDist < closestBlockDist)
                                        {
                                            closestBlockDist = forwardDist;
                                        }
                                    }
                                }
                            }
                        }
                        j = _spatialNext[j];
                    }
                }
            }

            // 4. Radial crowd pressure relief force (-grad rho)
            float reliefForceX = 0f;
            float reliefForceY = 0f;
            bool isInsideCorridor = (px >= 11.8f && px <= 16.7f && py >= 6.9f && py <= 9.1f);

            if (totalRadialDensity > 0.70f)
            {
                float gradLen = (float)Math.Sqrt(crowdGradX * crowdGradX + crowdGradY * crowdGradY);
                if (gradLen > 0.01f)
                {
                    float pressureCoeff = Math.Min(2.5f, (totalRadialDensity - 0.70f) * 0.75f);
                    reliefForceX = -(crowdGradX / gradLen) * pressureCoeff;
                    reliefForceY = -(crowdGradY / gradLen) * pressureCoeff;
                }
            }

            // 5. Local density from fundamental diagram
            float localDensity = WeidmannModel.CalculateEffectiveDensity(forwardKernelSum, closestForwardDistance);
            float effectiveGoalSpeed = WeidmannModel.CalculateSpeed(localDensity);

            // 6. Pass 2: Social Repulsion & Rear Pushing
            float crowdAvoidanceForceX = 0f;
            float crowdAvoidanceForceY = 0f;
            float totalRearPushMagnitude = 0f;

            float crowdPressureFactor = 1.0f + Math.Min(3.5f, Math.Max(0.0f, (Math.Max(localDensity, totalRadialDensity) - 0.70f) * 1.2f));
            float wallClearanceFactor = 1.0f;
            if (minDistanceToObstacle < comfortDistance)
            {
                wallClearanceFactor = 1.0f + 1.5f * (comfortDistance - minDistanceToObstacle) / comfortDistance;
            }
            float totalPressureFactor = crowdPressureFactor * wallClearanceFactor;

            for (int k = 0; k < pass2Count; k++)
            {
                int neighborIdx = _scratchPass2Indices[k];
                float rx = _scratchDx[k];
                float ry = _scratchDy[k];
                float dist = _scratchDist[k];

                if (dist <= repulsionThreshold && dist > 1e-6f)
                {
                    float proximityWeight = (repulsionThreshold - dist) / repulsionThreshold;
                    float baseRepel = (RepulsionStiffness * 0.45f * totalPressureFactor) * proximityWeight;

                    float denseCrowdPush = 0f;
                    if (totalRadialDensity > 1.20f)
                    {
                        denseCrowdPush = Math.Min(1.8f, (totalRadialDensity - 1.20f) * 0.55f) * proximityWeight;
                    }

                    float totalRepel = baseRepel + denseCrowdPush;
                    crowdAvoidanceForceX -= (rx / dist) * totalRepel;
                    crowdAvoidanceForceY -= (ry / dist) * totalRepel;
                }

                float forwardDist = rx * normVelX + ry * normVelY;
                if (forwardDist < -0.05f)
                {
                    float signedLateral = rx * (-normVelY) + ry * normVelX;
                    if (Math.Abs(signedLateral) < minDist * 0.85f)
                    {
                        float rearSpeed = Math.Max(0.1f, CurrentBuffer.Speed[neighborIdx]);
                        float pushMag = WeidmannModel.CalculateRearPushingForce(
                            localDensity, dist, minDist, contactBuffer: 0.30f, rearForwardDrive: rearSpeed);
                        totalRearPushMagnitude += pushMag;
                    }
                }
            }

            // 7. Contact braking & front queue fanning
            float contactBrakeFactor = 1.0f;
            float lateralFanForceX = 0f;
            float lateralFanForceY = 0f;
            float leftNormalX = -normVelY;
            float leftNormalY = normVelX;

            if (blocked)
            {
                float contactBrakeDist = minDist * 1.5f; // 0.60m
                if (closestBlockDist < contactBrakeDist)
                {
                    contactBrakeFactor = Math.Clamp(closestBlockDist / contactBrakeDist, 0.05f, 1.0f);
                    if (totalRearPushMagnitude > 0f)
                    {
                        float pushRelief = Math.Min(1.0f, totalRearPushMagnitude / 2.5f);
                        contactBrakeFactor = contactBrakeFactor + (1.0f - contactBrakeFactor) * pushRelief;
                    }
                }

                float steerDir = 0f;
                if (densityLeft + densityRight > 0.20f)
                {
                    if (densityLeft < densityRight - 0.15f) steerDir = 1.0f;
                    else if (densityRight < densityLeft - 0.15f) steerDir = -1.0f;
                    else steerDir = ((i & 1) == 0) ? 1.0f : -1.0f;
                }

                float congestionFactor = Math.Clamp((localDensity - 0.70f) / 1.5f, 0.0f, 1.0f);
                float fanStrength = (1.0f - closestBlockDist / LookaheadDistance) * (1.6f * congestionFactor);
                if (minDistanceToObstacle < 1.2f) fanStrength *= 0.4f;

                lateralFanForceX += leftNormalX * (steerDir * fanStrength);
                lateralFanForceY += leftNormalY * (steerDir * fanStrength);
            }

            // Continuum crowd decompression
            float effDensityDiff = densityRight - densityLeft;
            if (Math.Abs(effDensityDiff) > 0.08f && localDensity > 0.5f)
            {
                double decompMag = Math.Clamp(effDensityDiff * 0.45 * Math.Min(2.5, localDensity), -1.8, 1.8);
                if (minDistanceToObstacle < 1.2f) decompMag *= 0.4;
                lateralFanForceX += leftNormalX * (float)decompMag;
                lateralFanForceY += leftNormalY * (float)decompMag;
            }

            // 8. Forward speed & velocity composition
            float forwardSpeed = effectiveGoalSpeed * contactBrakeFactor;
            if (totalRearPushMagnitude > 0.1f && localDensity < WeidmannModel.DenseCrowdThreshold)
            {
                float pushSurge = Math.Min(0.10f, totalRearPushMagnitude * 0.05f);
                forwardSpeed *= (1.0f + pushSurge);
            }

            float minSpeedFloor = localDensity >= WeidmannModel.DenseCrowdThreshold ? 0.002f : 0.05f;
            forwardSpeed = Math.Max(minSpeedFloor, Math.Min(WeidmannModel.DefaultFreeSpeed, forwardSpeed));

            // Leader in lane speed cap
            float frontLeaderSpeed = 0f;
            if (frontLeaderIndex >= 0)
            {
                float leaderX = (frontLeaderIndex < i) ? NextBuffer.PosX[frontLeaderIndex] : CurrentBuffer.PosX[frontLeaderIndex];
                float leaderY = (frontLeaderIndex < i) ? NextBuffer.PosY[frontLeaderIndex] : CurrentBuffer.PosY[frontLeaderIndex];
                float rxToLeader = leaderX - px;
                float ryToLeader = leaderY - py;
                float leaderDist = rxToLeader * flowDirX + ryToLeader * flowDirY;
                frontLeaderSpeed = (frontLeaderIndex < i) ? NextBuffer.Speed[frontLeaderIndex] : CurrentBuffer.Speed[frontLeaderIndex];
                float availableGap = leaderDist - minDist;

                if (availableGap < 0.35f)
                {
                    float gapRatio = Math.Clamp(availableGap / 0.35f, 0f, 1f);
                    float maxAllowedFollowSpeed = frontLeaderSpeed + gapRatio * Math.Max(0f, effectiveGoalSpeed - frontLeaderSpeed);
                    forwardSpeed = Math.Min(forwardSpeed, Math.Max(frontLeaderSpeed, maxAllowedFollowSpeed));
                }
            }

            // Spawn zone / open lane forward drive: agents with open front space must never stall
            if (closestForwardDistance > minDist + 0.15f)
            {
                forwardSpeed = Math.Max(forwardSpeed, 0.25f);
            }

            float perpFlowX = -travelDirectionY;
            float perpFlowY = travelDirectionX;

            float crowdLateralTotalX = crowdAvoidanceForceX * 0.45f + lateralFanForceX + reliefForceX;
            float crowdLateralTotalY = crowdAvoidanceForceY * 0.45f + lateralFanForceY + reliefForceY;
            float perpComponent = crowdLateralTotalX * perpFlowX + crowdLateralTotalY * perpFlowY;

            float maxPerpSpeed;
            if (localDensity >= WeidmannModel.DenseCrowdThreshold) maxPerpSpeed = Math.Min(0.025f, Math.Max(0.005f, effectiveGoalSpeed * 0.40f));
            else if (localDensity >= 3.50f) maxPerpSpeed = Math.Min(0.20f, Math.Max(0.05f, effectiveGoalSpeed * 0.65f));
            else if (localDensity >= WeidmannModel.ConstrainedDensityThreshold) maxPerpSpeed = Math.Min(0.40f, Math.Max(0.10f, effectiveGoalSpeed * 0.75f));
            else maxPerpSpeed = Math.Min(0.70f, WeidmannModel.DefaultFreeSpeed * 0.45f);

            perpComponent = Math.Clamp(perpComponent, -maxPerpSpeed, maxPerpSpeed);

            float wallSpeedRatio = Math.Max(0.05f, effectiveGoalSpeed / WeidmannModel.DefaultFreeSpeed);
            float scaledWallRepulsionX = wallRepulsionForceX * wallSpeedRatio;
            float scaledWallRepulsionY = wallRepulsionForceY * wallSpeedRatio;

            float finalForceX = travelDirectionX * forwardSpeed + perpFlowX * perpComponent + scaledWallRepulsionX;
            float finalForceY = travelDirectionY * forwardSpeed + perpFlowY * perpComponent + scaledWallRepulsionY;

            float calculatedSpeed = (float)Math.Sqrt(finalForceX * finalForceX + finalForceY * finalForceY);
            float speedCap = Math.Min(WeidmannModel.DefaultFreeSpeed, (float)Math.Sqrt(forwardSpeed * forwardSpeed + perpComponent * perpComponent));
            if (calculatedSpeed > speedCap && calculatedSpeed > 1e-4f)
            {
                finalForceX = (finalForceX / calculatedSpeed) * speedCap;
                finalForceY = (finalForceY / calculatedSpeed) * speedCap;
            }

            float currentVelMag = (float)Math.Sqrt(vx * vx + vy * vy);
            float velocitySmoothingInertia;
            if (blocked || (frontLeaderIndex >= 0 && closestForwardDistance < minDist + 0.15f))
            {
                // Quick deceleration when queued behind leader
                velocitySmoothingInertia = 0.85f;
            }
            else if (localDensity >= WeidmannModel.DenseCrowdThreshold)
            {
                velocitySmoothingInertia = (currentVelMag > speedCap) ? 0.85f : 0.35f;
            }
            else if (isInsideCorridor)
            {
                velocitySmoothingInertia = 0.55f;
            }
            else
            {
                velocitySmoothingInertia = (currentSpeed < 0.10f) ? 0.08f : 0.25f;
            }

            float newVx = vx * (1.0f - velocitySmoothingInertia) + finalForceX * velocitySmoothingInertia;
            float newVy = vy * (1.0f - velocitySmoothingInertia) + finalForceY * velocitySmoothingInertia;

            // Enforce follower speed cap when close behind front agent
            if (frontLeaderIndex >= 0 && closestForwardDistance < minDist + 0.15f)
            {
                float availableGap = closestForwardDistance - minDist;
                float gapRatio = Math.Clamp(availableGap / 0.15f, 0f, 1f);
                float maxFollowCap = frontLeaderSpeed + gapRatio * 0.15f;
                float currentMag = (float)Math.Sqrt(newVx * newVx + newVy * newVy);
                if (currentMag > maxFollowCap && currentMag > 1e-4f)
                {
                    newVx = (newVx / currentMag) * maxFollowCap;
                    newVy = (newVy / currentMag) * maxFollowCap;
                }
            }

            // Enforce forward motion along goal direction: vForward >= 0
            float vParallel = newVx * flowDirX + newVy * flowDirY;
            if (vParallel < 0f)
            {
                newVx -= vParallel * flowDirX;
                newVy -= vParallel * flowDirY;
            }

            float newPx = px + newVx * dt;
            float newPy = py + newVy * dt;

            // Non-penetration constraint for leaders in lane
            if (frontLeaderIndex >= 0)
            {
                float leaderX = (frontLeaderIndex < i) ? NextBuffer.PosX[frontLeaderIndex] : CurrentBuffer.PosX[frontLeaderIndex];
                float leaderY = (frontLeaderIndex < i) ? NextBuffer.PosY[frontLeaderIndex] : CurrentBuffer.PosY[frontLeaderIndex];
                float maxAllowedForward = Math.Max(0f, (leaderX - px) * flowDirX + (leaderY - py) * flowDirY - minDist);
                float desiredForward = (newPx - px) * flowDirX + (newPy - py) * flowDirY;
                if (desiredForward > maxAllowedForward)
                {
                    float excess = desiredForward - maxAllowedForward;
                    newPx -= excess * flowDirX;
                    newPy -= excess * flowDirY;
                    newVx = (newPx - px) / dt;
                    newVy = (newPy - py) / dt;
                }
            }

            // Physical circle-circle non-penetration against all front neighbors
            for (int k = 0; k < pass2Count; k++)
            {
                int neighborIdx = _scratchPass2Indices[k];
                float nX = (neighborIdx < i) ? NextBuffer.PosX[neighborIdx] : CurrentBuffer.PosX[neighborIdx];
                float nY = (neighborIdx < i) ? NextBuffer.PosY[neighborIdx] : CurrentBuffer.PosY[neighborIdx];
                float dx = nX - newPx;
                float dy = nY - newPy;
                float dSq = dx * dx + dy * dy;
                if (dSq < minDist * minDist && dSq > 1e-6f)
                {
                    float dPar = dx * flowDirX + dy * flowDirY;
                    if (dPar > 0f)
                    {
                        float maxAdv = Math.Max(0f, (nX - px) * flowDirX + (nY - py) * flowDirY - minDist);
                        float curAdv = (newPx - px) * flowDirX + (newPy - py) * flowDirY;
                        if (curAdv > maxAdv)
                        {
                            float exc = curAdv - maxAdv;
                            newPx -= exc * flowDirX;
                            newPy -= exc * flowDirY;
                            newVx = (newPx - px) / dt;
                            newVy = (newPy - py) / dt;
                        }
                    }
                }
            }

            // Obstacle & Arena Boundary Collision Resolution
            ResolveObstacleCollisions(px, py, flowDirX, flowDirY, ref newPx, ref newPy, ref newVx, ref newVy);

            // Re-sample desired direction at post-step position to guarantee exact heading alignment
            Grid.SampleDesiredDirection(newPx, newPy, out float postDirX, out float postDirY);
            if (postDirX == 0f && postDirY == 0f)
            {
                postDirX = flowDirX;
                postDirY = flowDirY;
                if (postDirX == 0f && postDirY == 0f) postDirX = 1f;
            }

            // Strictly enforce non-negative forward velocity along goal direction at post-step position
            float postVForward = newVx * postDirX + newVy * postDirY;
            if (postVForward < 0f)
            {
                newVx -= postVForward * postDirX;
                newVy -= postVForward * postDirY;
            }
            float checkForward = newVx * postDirX + newVy * postDirY;
            if (checkForward < 0f)
            {
                newVx += (-checkForward + 1e-6f) * postDirX;
                newVy += (-checkForward + 1e-6f) * postDirY;
            }

            // Guarantee agents with open forward gap in spawn zone never stall below 0.22 m/s
            if (newPx < 8.0f && closestForwardDistance > minDist + 0.35f)
            {
                float forwardComponent = newVx * postDirX + newVy * postDirY;
                if (forwardComponent < 0.22f)
                {
                    newVx += (0.22f - forwardComponent) * postDirX;
                    newVy += (0.22f - forwardComponent) * postDirY;
                }
            }

            // Check all close neighbors ahead within 0.40m in lane to strictly prevent surging
            float maxLeaderAheadCap = WeidmannModel.DefaultFreeSpeed;
            for (int k = 0; k < pass2Count; k++)
            {
                int neighborIdx = _scratchPass2Indices[k];
                float rx = _scratchDx[k];
                float ry = _scratchDy[k];
                float dPar = rx * postDirX + ry * postDirY;
                if (dPar > 0.05f && _scratchDist[k] <= 0.40f)
                {
                    float dLat = Math.Abs(rx * (-postDirY) + ry * postDirX);
                    if (dLat < 0.35f)
                    {
                        float nSpeed = (neighborIdx < i) ? NextBuffer.Speed[neighborIdx] : CurrentBuffer.Speed[neighborIdx];
                        float allowed = Math.Max(nSpeed, 0.2f) + 0.25f;
                        if (allowed < maxLeaderAheadCap)
                        {
                            maxLeaderAheadCap = allowed;
                        }
                    }
                }
            }

            float finalSpeed = (float)Math.Sqrt(newVx * newVx + newVy * newVy);
            if (finalSpeed > maxLeaderAheadCap && finalSpeed > 1e-4f)
            {
                newVx = (newVx / finalSpeed) * maxLeaderAheadCap;
                newVy = (newVy / finalSpeed) * maxLeaderAheadCap;
                finalSpeed = maxLeaderAheadCap;
            }

            // Agents with dense crowd ahead (>= 6 neighbors within 1m) must not be in free-flow green
            if (aheadNeighbors1m >= 6 && finalSpeed > 0.95f)
            {
                newVx = (newVx / finalSpeed) * 0.95f;
                newVy = (newVy / finalSpeed) * 0.95f;
                finalSpeed = 0.95f;
            }

            // Write to NextBuffer
            NextBuffer.PosX[i] = newPx;
            NextBuffer.PosY[i] = newPy;
            NextBuffer.VelX[i] = newVx;
            NextBuffer.VelY[i] = newVy;
            NextBuffer.Speed[i] = finalSpeed;
            NextBuffer.HeadX[i] = postDirX;
            NextBuffer.HeadY[i] = postDirY;
            NextBuffer.Active[i] = 1;

            totalSpeed += finalSpeed;
            activeCount++;
        }

        ActiveAgents = activeCount;
        MeanSpeed = (activeCount > 0) ? (totalSpeed / activeCount) : 0f;

        // Swap ping-pong buffers
        SwapBuffers();

        // Sample live analytics and telemetry
        StatsCollector?.SampleTelemetry(this);
    }

    private void ResolveObstacleCollisions(
        float oldX, float oldY,
        float dirX, float dirY,
        ref float newX, ref float newY,
        ref float vx, ref float vy)
    {
        float margin = AgentRadius * 1.01f;

        // Arena outer boundary clamp
        if (newX < margin) { newX = margin; if (vx < 0f) vx = 0f; }
        if (newX > Grid.Width - margin) { newX = Grid.Width - margin; if (vx > 0f) vx = 0f; }
        if (newY < margin) { newY = margin; if (vy < 0f) vy = 0f; }
        if (newY > Grid.Height - margin) { newY = Grid.Height - margin; if (vy > 0f) vy = 0f; }

        int obsCount = Grid.Obstacles.Count;
        for (int o = 0; o < obsCount; o++)
        {
            var obs = Grid.Obstacles[o];
            if (newX < obs.X - AgentRadius - 0.1f || newX > obs.X + obs.Width + AgentRadius + 0.1f ||
                newY < obs.Y - AgentRadius - 0.1f || newY > obs.Y + obs.Height + AgentRadius + 0.1f)
            {
                continue;
            }

            float nearestX = Math.Clamp(newX, obs.X, obs.X + obs.Width);
            float nearestY = Math.Clamp(newY, obs.Y, obs.Y + obs.Height);
            float deltaX = newX - nearestX;
            float deltaY = newY - nearestY;
            float distSquared = deltaX * deltaX + deltaY * deltaY;

            if (distSquared < AgentRadius * AgentRadius)
            {
                if (distSquared > 1e-6f)
                {
                    float dist = (float)Math.Sqrt(distSquared);
                    float normalX = deltaX / dist;
                    float normalY = deltaY / dist;
                    float penetration = AgentRadius - dist;

                    newX += normalX * penetration;
                    newY += normalY * penetration;

                    float normalVelocity = vx * normalX + vy * normalY;
                    if (normalVelocity < 0f)
                    {
                        vx -= normalVelocity * normalX;
                        vy -= normalVelocity * normalY;
                    }
                }
                else
                {
                    float dLeft = newX - obs.X;
                    float dRight = (obs.X + obs.Width) - newX;
                    float dTop = newY - obs.Y;
                    float dBottom = (obs.Y + obs.Height) - newY;
                    float minD = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));

                    if (minD == dLeft) { newX = obs.X - AgentRadius; if (vx > 0f) vx = 0f; }
                    else if (minD == dRight) { newX = obs.X + obs.Width + AgentRadius; if (vx < 0f) vx = 0f; }
                    else if (minD == dTop) { newY = obs.Y - AgentRadius; if (vy > 0f) vy = 0f; }
                    else { newY = obs.Y + obs.Height + AgentRadius; if (vy < 0f) vy = 0f; }
                }
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
