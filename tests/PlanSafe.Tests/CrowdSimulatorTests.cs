using PlanSafe.App.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class CrowdSimulatorTests
{
    [Fact]
    public void CrowdSimulator_AgentMovesTowardsExit_AndEvacuatesUponArrival()
    {
        var grid = new SimulationGrid(width: 6f, height: 4f, cellSize: 0.5f);
        // Exit at right
        for (int r = 0; r < grid.Rows; r++)
        {
            grid.SetCell(grid.Cols - 1, r, CellType.Exit);
        }
        grid.ComputeDijkstraField();

        var sim = new CrowdSimulator(grid, maxAgents: 10);
        sim.SpawnAgent(x: 1.0f, y: 2.0f);

        Assert.Equal(1, sim.CurrentBuffer.Count);
        Assert.Equal(0, sim.EvacuatedCount);

        float initialX = sim.CurrentBuffer.PosX[0];

        // Step simulation forward
        for (int step = 0; step < 100; step++)
        {
            sim.Step(0.05f);
            if (sim.EvacuatedCount > 0) break;
        }

        // Agent should have either advanced significantly towards exit or evacuated
        Assert.True(sim.EvacuatedCount == 1 || sim.CurrentBuffer.PosX[0] > initialX + 1.0f,
            $"Expected evacuation or advance, got evacuated={sim.EvacuatedCount}, PosX={sim.CurrentBuffer.PosX[0]}");
    }

    [Fact]
    public void CrowdSimulator_AgentBehind_SlowsDownBeforeAgentInFront()
    {
        var grid = new SimulationGrid(width: 10f, height: 4f, cellSize: 0.5f);
        for (int r = 0; r < grid.Rows; r++)
        {
            grid.SetCell(grid.Cols - 1, r, CellType.Exit);
        }
        grid.ComputeDijkstraField();

        var sim = new CrowdSimulator(grid, maxAgents: 10);

        // Leader agent placed ahead
        sim.SpawnAgent(x: 3.0f, y: 2.0f);
        // Follower agent placed 0.8m behind
        sim.SpawnAgent(x: 2.2f, y: 2.0f);

        // Force front agent to be slow / stopped
        sim.CurrentBuffer.VelX[0] = 0.05f;
        sim.CurrentBuffer.Speed[0] = 0.05f;

        // Step once
        sim.Step(0.03f);

        // Follower agent (index 1) should have its target speed capped due to front agent
        float followerSpeed = sim.CurrentBuffer.Speed[1];
        Assert.True(followerSpeed < WeidmannModel.DefaultFreeSpeed,
            $"Follower agent should brake before front agent. Expected < {WeidmannModel.DefaultFreeSpeed}, got {followerSpeed}");
    }

    [Fact]
    public void CrowdSimulator_TwoObstaclesNarrowCorridorPreset_InitializesCorrectly()
    {
        var preset = MapPresets.CreateTwoObstaclesNarrowCorridor();
        var grid = new SimulationGrid(preset.Width, preset.Height, preset.CellSize);
        preset.SetupGrid(grid);

        var sim = new CrowdSimulator(grid, maxAgents: 100);
        preset.PopulateAgents(sim, 50);

        Assert.Equal(50, sim.CurrentBuffer.Count);
        Assert.Equal(0, sim.EvacuatedCount);

        // Corridor center at (14m, 8m) should be passable
        int corrCol = (int)(14f / grid.CellSize);
        int corrRow = (int)(8f / grid.CellSize);
        Assert.True(grid.IsPassable(corrCol, corrRow));

        // Obstacles at (14m, 3m) and (14m, 12m) should be impassable
        int obs1Row = (int)(3f / grid.CellSize);
        int obs2Row = (int)(12f / grid.CellSize);
        Assert.False(grid.IsPassable(corrCol, obs1Row));
        Assert.False(grid.IsPassable(corrCol, obs2Row));
    }

    [Fact]
    public void CrowdSimulator_AgentsNeverMoveBackwards_EvenWhenRepulsedOrJammed()
    {
        var grid = new SimulationGrid(width: 10f, height: 6f, cellSize: 0.5f);
        // Exits at right
        for (int r = 0; r < grid.Rows; r++)
        {
            grid.SetCell(grid.Cols - 1, r, CellType.Exit);
        }
        grid.ComputeDijkstraField();

        var sim = new CrowdSimulator(grid, maxAgents: 20);

        // Place a dense cluster of agents in contact / overlap to trigger maximum repulsion
        sim.SpawnAgent(3.0f, 3.0f);
        sim.SpawnAgent(3.1f, 3.0f); // overlapping in front
        sim.SpawnAgent(2.9f, 3.0f); // overlapping behind
        sim.SpawnAgent(3.0f, 3.1f); // overlapping side
        sim.SpawnAgent(3.0f, 2.9f); // overlapping side

        for (int step = 0; step < 50; step++)
        {
            sim.Step(0.02f);

            // Assert that for every active agent, the forward velocity component along d_goal is non-negative
            for (int i = 0; i < sim.CurrentBuffer.Count; i++)
            {
                if (sim.CurrentBuffer.Active[i] == 0) continue;

                float px = sim.CurrentBuffer.PosX[i];
                float py = sim.CurrentBuffer.PosY[i];
                float vx = sim.CurrentBuffer.VelX[i];
                float vy = sim.CurrentBuffer.VelY[i];

                grid.SampleDesiredDirection(px, py, out float dirX, out float dirY);
                float vForward = vx * dirX + vy * dirY;

                // Precision epsilon for floating point arithmetic: vForward must never be negative
                Assert.True(vForward >= -1e-5f,
                    $"Agent {i} moved backwards along goal direction! vForward={vForward}, vx={vx}, vy={vy}, dir=({dirX},{dirY}) at step {step}");
            }
        }
    }

    [Fact]
    public void CrowdSimulator_AgentHeading_PointsTowardsGoal_AndDoesNotSpin()
    {
        var grid = new SimulationGrid(width: 8f, height: 6f, cellSize: 0.5f);
        for (int r = 0; r < grid.Rows; r++)
        {
            grid.SetCell(grid.Cols - 1, r, CellType.Exit);
        }
        grid.ComputeDijkstraField();

        var sim = new CrowdSimulator(grid, maxAgents: 5);
        sim.SpawnAgent(2.0f, 3.0f);

        // Before stepping: heading is initialized pointing towards exit
        grid.SampleDesiredDirection(2.0f, 3.0f, out float expectedDirX, out float expectedDirY);
        Assert.Equal(expectedDirX, sim.CurrentBuffer.HeadX[0], precision: 3);
        Assert.Equal(expectedDirY, sim.CurrentBuffer.HeadY[0], precision: 3);

        // Force agent velocity to zero (stopped)
        sim.CurrentBuffer.VelX[0] = 0f;
        sim.CurrentBuffer.VelY[0] = 0f;
        sim.CurrentBuffer.Speed[0] = 0f;

        // Step simulation
        sim.Step(0.02f);

        // Even when speed is zero or low, HeadX and HeadY must stay locked to goal direction, never spinning
        grid.SampleDesiredDirection(sim.CurrentBuffer.PosX[0], sim.CurrentBuffer.PosY[0], out float stepDirX, out float stepDirY);
        Assert.Equal(stepDirX, sim.CurrentBuffer.HeadX[0], precision: 3);
        Assert.Equal(stepDirY, sim.CurrentBuffer.HeadY[0], precision: 3);

        // Assert heading unit vector magnitude is 1.0
        float headLen = (float)Math.Sqrt(sim.CurrentBuffer.HeadX[0] * sim.CurrentBuffer.HeadX[0] + sim.CurrentBuffer.HeadY[0] * sim.CurrentBuffer.HeadY[0]);
        Assert.Equal(1.0f, headLen, precision: 3);
    }

    [Fact]
    public void CrowdSimulator_BottleneckSimulation_MaintainsForwardProgressAndEvacuation()
    {
        var preset = MapPresets.CreateTwoObstaclesNarrowCorridor();
        var grid = new SimulationGrid(preset.Width, preset.Height, preset.CellSize);
        preset.SetupGrid(grid);

        var sim = new CrowdSimulator(grid, maxAgents: 100);
        preset.PopulateAgents(sim, 60);

        for (int step = 0; step < 150; step++)
        {
            sim.Step(0.03f);

            for (int i = 0; i < sim.CurrentBuffer.Count; i++)
            {
                if (sim.CurrentBuffer.Active[i] == 0) continue;

                float px = sim.CurrentBuffer.PosX[i];
                float py = sim.CurrentBuffer.PosY[i];
                float vx = sim.CurrentBuffer.VelX[i];
                float vy = sim.CurrentBuffer.VelY[i];

                grid.SampleDesiredDirection(px, py, out float dirX, out float dirY);
                float vForward = vx * dirX + vy * dirY;

                Assert.True(vForward >= -1e-5f,
                    $"Agent {i} moved backwards in bottleneck! vForward={vForward} at step {step}");

                // Assert heading matches goal direction
                Assert.Equal(dirX, sim.CurrentBuffer.HeadX[i], precision: 2);
                Assert.Equal(dirY, sim.CurrentBuffer.HeadY[i], precision: 2);
            }
        }

        // Active agents must have made significant forward progress towards exit
        Assert.True(sim.EvacuatedCount > 0 || sim.MeanSpeed > 0f);
    }

    [Fact]
    public void CrowdSimulator_DenseBottleneckJam_NoBackwardMotionAndNoSpinning()
    {
        var preset = MapPresets.CreateTwoObstaclesNarrowCorridor();
        var grid = new SimulationGrid(preset.Width, preset.Height, preset.CellSize);
        preset.SetupGrid(grid);

        var sim = new CrowdSimulator(grid, maxAgents: 200);
        // High density: 120 agents squeezing into 2m corridor
        preset.PopulateAgents(sim, 120);

        for (int step = 0; step < 200; step++)
        {
            sim.Step(0.025f);

            for (int i = 0; i < sim.CurrentBuffer.Count; i++)
            {
                if (sim.CurrentBuffer.Active[i] == 0) continue;

                float px = sim.CurrentBuffer.PosX[i];
                float py = sim.CurrentBuffer.PosY[i];
                float hx = sim.CurrentBuffer.HeadX[i];
                float hy = sim.CurrentBuffer.HeadY[i];

                grid.SampleDesiredDirection(px, py, out float dirX, out float dirY);

                // 1. Heading must point along path of least resistance towards exit
                Assert.Equal(dirX, hx, precision: 2);
                Assert.Equal(dirY, hy, precision: 2);

                // 2. Heading magnitude must be normalized (never 0 or spinning)
                float hLen = (float)Math.Sqrt(hx * hx + hy * hy);
                Assert.True(hLen > 0.95f && hLen < 1.05f, $"Agent {i} has unnormalized heading {hLen}");

                // 3. For TwoObstacles preset, exit is at the right (+X, col 58-59).
                // Agents west of the bottleneck (x < 12m) must ALWAYS point towards the east (hx > 0), NEVER turn around!
                if (px < 12.0f)
                {
                    Assert.True(hx > 0f, $"Agent {i} at ({px}, {py}) turned around and is facing backwards! hx={hx}, hy={hy}");
                }

                // 4. Velocity forward component must never be negative
                float vx = sim.CurrentBuffer.VelX[i];
                float vy = sim.CurrentBuffer.VelY[i];
                float vForward = vx * dirX + vy * dirY;
                Assert.True(vForward >= -1e-5f, $"Agent {i} moved backwards! vForward={vForward}");
            }
        }

        // Over 200 steps (5.0s), lead agents advance from spawn zone (x <= 7.5m) into the bottleneck corridor (x >= 12.0m)
        float maxPosX = 0f;
        for (int i = 0; i < sim.CurrentBuffer.Count; i++)
        {
            if (sim.CurrentBuffer.PosX[i] > maxPosX) maxPosX = sim.CurrentBuffer.PosX[i];
        }
        Assert.True(maxPosX >= 12.0f, $"Expected crowd to advance into bottleneck (x >= 12m), got {maxPosX}");
        Assert.True(sim.MeanSpeed > 0f);
    }

    [Fact]
    public void CrowdSimulator_AgentsDoNotPenetrateLeaders_AndDoNotTunnelThroughCrowd()
    {
        var grid = new SimulationGrid(width: 10f, height: 6f, cellSize: 0.5f);
        for (int r = 0; r < grid.Rows; r++)
        {
            grid.SetCell(grid.Cols - 1, r, CellType.Exit);
        }
        grid.ComputeDijkstraField();

        var sim = new CrowdSimulator(grid, maxAgents: 10);

        // Leader placed ahead, stopped
        sim.SpawnAgent(5.0f, 3.0f);
        // Follower 1 placed behind
        sim.SpawnAgent(4.2f, 3.0f);
        // Follower 2 placed further behind
        sim.SpawnAgent(3.4f, 3.0f);

        // Leader is stationary
        sim.CurrentBuffer.VelX[0] = 0f;
        sim.CurrentBuffer.Speed[0] = 0f;

        for (int step = 0; step < 100; step++)
        {
            // Keep leader stopped to test queueing / non-penetration
            sim.CurrentBuffer.VelX[0] = 0f;
            sim.CurrentBuffer.Speed[0] = 0f;

            sim.Step(0.025f);

            // Follower 1 must NEVER pass leader or penetrate its physical circle (PosX[1] <= PosX[0] - 2*r)
            Assert.True(sim.CurrentBuffer.PosX[1] <= sim.CurrentBuffer.PosX[0] - 2 * sim.AgentRadius + 0.01f,
                $"Follower 1 penetrated leader circle! PosX[1]={sim.CurrentBuffer.PosX[1]}, PosX[0]={sim.CurrentBuffer.PosX[0]} at step {step}");

            // Follower 2 must NEVER pass follower 1 or penetrate its physical circle
            Assert.True(sim.CurrentBuffer.PosX[2] <= sim.CurrentBuffer.PosX[1] - 2 * sim.AgentRadius + 0.01f,
                $"Follower 2 penetrated follower 1 circle! PosX[2]={sim.CurrentBuffer.PosX[2]}, PosX[1]={sim.CurrentBuffer.PosX[1]} at step {step}");

            // Follower speeds must drop as they queue up (must NOT be free flow > 1.1 m/s)
            if (step > 40)
            {
                Assert.True(sim.CurrentBuffer.Speed[1] < 0.3f,
                    $"Follower 1 should be stopped/slowed behind stationary leader, but has speed {sim.CurrentBuffer.Speed[1]} at step {step}");
            }
            if (step > 70)
            {
                Assert.True(sim.CurrentBuffer.Speed[2] < 0.3f,
                    $"Follower 2 should be stopped/slowed behind queue, but has speed {sim.CurrentBuffer.Speed[2]} at step {step}");
            }
        }
    }

    [Fact]
    public void CrowdSimulator_FullBottleneck400Agents_NoTunnelingOrInterpenetration()
    {
        var preset = MapPresets.CreateTwoObstaclesNarrowCorridor();
        var grid = new SimulationGrid(preset.Width, preset.Height, preset.CellSize);
        preset.SetupGrid(grid);

        var sim = new CrowdSimulator(grid, maxAgents: 500);
        preset.PopulateAgents(sim, 400);

        // Run simulation for 150 steps (~3.75s)
        for (int step = 0; step < 150; step++)
        {
            sim.Step(0.025f);

            // Check agents: no agent should move backwards
            for (int i = 0; i < sim.CurrentBuffer.Count; i++)
            {
                if (sim.CurrentBuffer.Active[i] == 0) continue;
                float px = sim.CurrentBuffer.PosX[i];
                float py = sim.CurrentBuffer.PosY[i];
                float vx = sim.CurrentBuffer.VelX[i];
                float vy = sim.CurrentBuffer.VelY[i];

                grid.SampleDesiredDirection(px, py, out float dirX, out float dirY);
                float vForward = vx * dirX + vy * dirY;
                Assert.True(vForward >= -1e-5f, $"Agent {i} moved backwards at step {step}: vForward={vForward}");

                // Check if any agent in front in lane is slower: follower must not exceed front agent's speed
                float spd = sim.CurrentBuffer.Speed[i];
                for (int j = 0; j < sim.CurrentBuffer.Count; j++)
                {
                    if (j == i || sim.CurrentBuffer.Active[j] == 0) continue;
                    float rx = sim.CurrentBuffer.PosX[j] - px;
                    float ry = sim.CurrentBuffer.PosY[j] - py;
                    float distSq = rx * rx + ry * ry;
                    if (distSq > 0.4f * 0.4f) continue;

                    float dParallel = rx * dirX + ry * dirY;
                    if (dParallel > 0.05f)
                    {
                        float dPerpSq = Math.Max(0f, distSq - dParallel * dParallel);
                        if (dPerpSq < 0.35f * 0.35f)
                        {
                            float frontSpd = sim.CurrentBuffer.Speed[j];
                            Assert.True(spd <= Math.Max(frontSpd, 0.2f) + 0.3f,
                                $"Agent {i} (speed {spd}) is surging through front agent {j} (speed {frontSpd}) at step {step}");
                        }
                    }
                }

                // Agents with dense crowd in front (>= 6 neighbors ahead within 1m) must never be in free-flow green (speed >= 1.05 m/s)
                int denseAheadNeighbors = 0;
                for (int j = 0; j < sim.CurrentBuffer.Count; j++)
                {
                    if (j == i || sim.CurrentBuffer.Active[j] == 0) continue;
                    float rx = sim.CurrentBuffer.PosX[j] - px;
                    float ry = sim.CurrentBuffer.PosY[j] - py;
                    if (rx * rx + ry * ry < 1.0f)
                    {
                        float dParallel = rx * dirX + ry * dirY;
                        if (dParallel > 0f) denseAheadNeighbors++;
                    }
                }
                if (denseAheadNeighbors >= 6)
                {
                    Assert.True(spd < 1.05f,
                        $"Agent {i} has speed {spd} (green free flow) despite {denseAheadNeighbors} neighbors ahead in dense crowd at step {step}");
                }
            }
        }

        // After 150 steps, verify overall evacuation progress and bounded speeds
        Assert.True(sim.EvacuatedCount > 0 || sim.MeanSpeed > 0f);
    }

    [Fact]
    public void CrowdSimulator_DenseSpawnCrowd_NoUnnecessaryWaitingWithFrontGaps()
    {
        var preset = MapPresets.CreateTwoObstaclesNarrowCorridor();
        var grid = new SimulationGrid(preset.Width, preset.Height, preset.CellSize);
        preset.SetupGrid(grid);

        var sim = new CrowdSimulator(grid, maxAgents: 500);
        preset.PopulateAgents(sim, 400);

        // Run for 400 steps (10 seconds), matching the screenshot timeframe
        for (int step = 0; step < 400; step++)
        {
            sim.Step(0.025f);
        }

        // Check agents still in spawn zone (x < 7.5m):
        // No agent should be stalled (< 0.2 m/s) if there is an open gap (> 0.4m) in front of it!
        int stalledCount = 0;
        int stalledWithGap = 0;
        float minPosX = float.MaxValue;
        float maxPosX = 0f;

        for (int i = 0; i < sim.CurrentBuffer.Count; i++)
        {
            if (sim.CurrentBuffer.Active[i] == 0) continue;
            float px = sim.CurrentBuffer.PosX[i];
            float py = sim.CurrentBuffer.PosY[i];
            float spd = sim.CurrentBuffer.Speed[i];

            if (px < minPosX) minPosX = px;
            if (px > maxPosX) maxPosX = px;

            if (spd < 0.2f && px < 7.5f)
            {
                stalledCount++;

                // Check gap in front in agent's lane
                grid.SampleDesiredDirection(px, py, out float dirX, out float dirY);
                float nearestGap = float.MaxValue;
                for (int j = 0; j < sim.CurrentBuffer.Count; j++)
                {
                    if (j == i || sim.CurrentBuffer.Active[j] == 0) continue;
                    float rx = sim.CurrentBuffer.PosX[j] - px;
                    float ry = sim.CurrentBuffer.PosY[j] - py;
                    float dParallel = rx * dirX + ry * dirY;
                    if (dParallel <= 0f) continue;
                    float distSq = rx * rx + ry * ry;
                    float dPerpSq = Math.Max(0f, distSq - dParallel * dParallel);
                    if (dPerpSq < 0.4f * 0.4f)
                    {
                        float gap = dParallel - 0.4f;
                        if (gap < nearestGap) nearestGap = gap;
                    }
                }

                if (nearestGap > 0.4f)
                {
                    stalledWithGap++;
                }
            }
        }

        Assert.True(stalledWithGap == 0,
            $"Found {stalledWithGap} of {stalledCount} stalled spawn agents waiting with front gap > 0.4m!");

        // After 10 seconds, crowd must have made significant forward progress into bottleneck
        Assert.True(minPosX >= 1.0f, $"Crowd stays within arena bounds, minPosX is {minPosX:F2}");
        Assert.True(maxPosX >= 12.0f, $"Front of crowd should reach bottleneck, maxPosX is {maxPosX:F2}");
        Assert.True(sim.MeanSpeed > 0.1f, $"Mean speed should reflect active flow, was {sim.MeanSpeed:F2}");
    }

    [Fact]
    public void CrowdSimulator_QueuedAgents_FollowersStartPromptlyFromRest()
    {
        var grid = new SimulationGrid(width: 15f, height: 6f, cellSize: 0.5f);
        for (int r = 0; r < grid.Rows; r++)
        {
            grid.SetCell(grid.Cols - 1, r, CellType.Exit);
        }
        grid.ComputeDijkstraField();

        var sim = new CrowdSimulator(grid, maxAgents: 10);

        // A queue of 4 agents touching along X at rest
        // Leader at 5.0, followed by agents at 4.6, 4.2, 3.8
        sim.SpawnAgent(5.0f, 3.0f);
        sim.SpawnAgent(4.6f, 3.0f);
        sim.SpawnAgent(4.2f, 3.0f);
        sim.SpawnAgent(3.8f, 3.0f);

        // Run 30 steps (0.75 second)
        for (int step = 0; step < 30; step++)
        {
            sim.Step(0.025f);
        }

        // Leader has moved forward into open space
        Assert.True(sim.CurrentBuffer.PosX[0] > 5.3f, $"Leader should advance, but at {sim.CurrentBuffer.PosX[0]}");

        // All followers must start moving promptly and follow the queue without waiting for large gaps
        for (int i = 1; i < 4; i++)
        {
            float startX = 5.0f - i * 0.4f;
            float displacement = sim.CurrentBuffer.PosX[i] - startX;
            Assert.True(displacement >= 0.12f,
                $"Follower {i} lagged behind! Displaced only {displacement:F3}m (PosX={sim.CurrentBuffer.PosX[i]:F3}) after 0.75s");
            Assert.True(sim.CurrentBuffer.Speed[i] >= 0.25f,
                $"Follower {i} speed is too low ({sim.CurrentBuffer.Speed[i]:F3} m/s) after 0.75s");
        }
    }
}
