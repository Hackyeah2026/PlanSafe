using System;
using Xunit;
using Xunit.Abstractions;
using PlanSafe.App.Simulation;

namespace PlanSafe.Tests;

public class WallAgentDiagnostics
{
    private readonly ITestOutputHelper _output;

    public WallAgentDiagnostics(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DiagnoseAgentAtWestWall()
    {
        double width = 200.0;
        double height = 200.0;

        var obstacles = new[]
        {
            new Obstacle(width * 0.35, height * 0.18, width * 0.12, height * 0.28), // [70, 94] x [36, 92]
            new Obstacle(width * 0.35, height * 0.54, width * 0.12, height * 0.28)  // [70, 94] x [108, 164]
        };

        var exitZone = new Obstacle(width * 0.92, height * 0.40, width * 0.06, height * 0.20);

        double cellSize = Math.Max(4.0, Math.Min(width, height) / 125.0);
        var grid = new PotentialFieldGrid(cellSize, width, height);
        grid.InitializeStaticObstacles(obstacles);
        grid.BuildStaticField(obstacles, exitZone);

        // Test coordinates near the stuck agent on the west wall of the bottom obstacle:
        // Bottom obstacle is at X in [70, 94], Y in [108, 164]
        double[] testXs = { 60.0, 64.0, 66.0, 67.0, 67.5, 68.0, 69.5 };
        double[] testYs = { 108.0, 110.0, 111.0, 112.0, 114.0, 116.0 };

        foreach (var ty in testYs)
        {
            foreach (var tx in testXs)
            {
                var (fx, fy) = grid.GetFlowDirection(tx, ty);
                _output.WriteLine($"Pos: ({tx:F1}, {ty:F1}) -> Flow: ({fx:F3}, {fy:F3})");
            }
        }
    }

    [Fact]
    public void LeftoverAgentsAtWestWall_EvacuateWithoutSticking()
    {
        double width = 200.0;
        double height = 200.0;

        var obstacles = new[]
        {
            new Obstacle(width * 0.35, height * 0.18, width * 0.12, height * 0.28), // [70, 94] x [36, 92]
            new Obstacle(width * 0.35, height * 0.54, width * 0.12, height * 0.28)  // [70, 94] x [108, 164]
        };

        var exitZone = new Obstacle(width * 0.92, height * 0.40, width * 0.06, height * 0.20);

        double cellSize = Math.Max(4.0, Math.Min(width, height) / 125.0);
        var grid = new PotentialFieldGrid(cellSize, width, height);
        grid.InitializeStaticObstacles(obstacles);
        grid.BuildStaticField(obstacles, exitZone);

        // Place 2 agents right at the problem spot on the west face of the bottom obstacle
        int agentCount = 2;
        double[] posX = { 69.5, 69.5 };
        double[] posY = { 111.0, 111.6 };
        double[] velX = { 0.0, 0.0 };
        double[] velY = { 0.0, 0.0 };
        double[] radius = { 0.35, 0.35 };
        double[] maxSpeed = { 1.4, 1.4 };
        double whiskerLength = 2.5;

        double dt = 0.05; // 50ms per step
        // Run 200 steps (10 seconds)
        for (int step = 0; step < 200; step++)
        {
            for (int i = 0; i < agentCount; i++)
            {
                var (flowDirectionX, flowDirectionY) = grid.GetFlowDirection(posX[i], posY[i]);
                double currentAgentSpeed = Math.Sqrt(velX[i] * velX[i] + velY[i] * velY[i]);
                bool hasSignificantVelocity = currentAgentSpeed > 0.05;
                double normalizedVelocityX = hasSignificantVelocity ? velX[i] / currentAgentSpeed : (flowDirectionX != 0 ? flowDirectionX : 1.0);
                double normalizedVelocityY = hasSignificantVelocity ? velY[i] / currentAgentSpeed : (flowDirectionY != 0 ? flowDirectionY : 0.0);

                double goalForceX = flowDirectionX * maxSpeed[i];
                double goalForceY = flowDirectionY * maxSpeed[i];

                double wallRepulsionForceX = 0;
                double wallRepulsionForceY = 0;
                double minDistanceToObstacle = double.MaxValue;

                foreach (var obstacle in obstacles)
                {
                    double nearestBoxPointX = Math.Max(obstacle.X, Math.Min(posX[i], obstacle.X + obstacle.Width));
                    double nearestBoxPointY = Math.Max(obstacle.Y, Math.Min(posY[i], obstacle.Y + obstacle.Height));
                    double distanceToBoxVectorX = posX[i] - nearestBoxPointX;
                    double distanceToBoxVectorY = posY[i] - nearestBoxPointY;
                    double distanceToBoxSquared = distanceToBoxVectorX * distanceToBoxVectorX + distanceToBoxVectorY * distanceToBoxVectorY;

                    double comfortDistance = radius[i] + 0.6;
                    double comfortDistanceSquared = comfortDistance * comfortDistance;

                    if (distanceToBoxSquared < comfortDistanceSquared && distanceToBoxSquared > 0.000001)
                    {
                        double distanceToBox = Math.Sqrt(distanceToBoxSquared);
                        if (distanceToBox < minDistanceToObstacle) minDistanceToObstacle = distanceToBox;

                        double normalX = distanceToBoxVectorX / distanceToBox;
                        double normalY = distanceToBoxVectorY / distanceToBox;

                        double wallRepulsionStrength = 3.5 * (comfortDistance - distanceToBox) / comfortDistance;
                        double wfx = normalX * wallRepulsionStrength;
                        double wfy = normalY * wallRepulsionStrength;

                        double dotFlow = wfx * flowDirectionX + wfy * flowDirectionY;
                        if (dotFlow < 0)
                        {
                            wfx -= dotFlow * flowDirectionX;
                            wfy -= dotFlow * flowDirectionY;
                        }

                        wallRepulsionForceX += wfx;
                        wallRepulsionForceY += wfy;

                        double dotWithNormal = goalForceX * normalX + goalForceY * normalY;
                        if (dotWithNormal < 0)
                        {
                            goalForceX -= dotWithNormal * normalX;
                            goalForceY -= dotWithNormal * normalY;

                            double slideSpeed = Math.Sqrt(goalForceX * goalForceX + goalForceY * goalForceY);
                            if (slideSpeed > 0.001)
                            {
                                goalForceX = (goalForceX / slideSpeed) * maxSpeed[i];
                                goalForceY = (goalForceY / slideSpeed) * maxSpeed[i];
                            }
                        }
                    }
                }

                double crowdAvoidanceForceX = 0;
                double crowdAvoidanceForceY = 0;
                double radialDensity = 0.0;
                double closestForwardDistance = 10.0;

                int other = 1 - i;
                double dx = posX[other] - posX[i];
                double dy = posY[other] - posY[i];
                double distSq = dx * dx + dy * dy;
                if (distSq < 4.0 && distSq > 0.000001)
                {
                    double w = 1.0 - distSq * 0.25;
                    radialDensity += 0.764 * (w * w);

                    double fDist = dx * normalizedVelocityX + dy * normalizedVelocityY;
                    if (fDist > 0.05 && fDist < closestForwardDistance)
                    {
                        double latDist = Math.Abs(dx * (-normalizedVelocityY) + dy * normalizedVelocityX);
                        double minDist = radius[i] + radius[other];
                        if (latDist < minDist * 0.9)
                        {
                            closestForwardDistance = fDist;
                        }
                    }
                }

                double localDensity = PedestrianFundamentalDiagram.CalculateEffectiveDensity(radialDensity, closestForwardDistance);
                double crowdPressureFactor = 1.0 + Math.Min(3.5, Math.Max(0.0, (localDensity - 0.7) * 1.2));
                double wallComfortDistance = radius[i] + 0.6;
                double wallClearanceFactor = 1.0;
                if (minDistanceToObstacle < wallComfortDistance)
                {
                    wallClearanceFactor = 1.0 + 1.5 * (wallComfortDistance - minDistanceToObstacle) / wallComfortDistance;
                }
                double totalPressureFactor = crowdPressureFactor * wallClearanceFactor;

                bool blocked = false;
                double closestBlockDist = whiskerLength;
                double minimumDistance = radius[i] + radius[other];
                double repulsionThreshold = minimumDistance * 2.2;
                if (distSq <= repulsionThreshold * repulsionThreshold && distSq > 0.000001)
                {
                    double dist = Math.Sqrt(distSq);
                    double repelStrength = (4.5 * 0.45 * totalPressureFactor) * (repulsionThreshold - dist) / repulsionThreshold;
                    crowdAvoidanceForceX -= (dx / dist) * repelStrength;
                    crowdAvoidanceForceY -= (dy / dist) * repelStrength;
                }

                double forwardDist = dx * normalizedVelocityX + dy * normalizedVelocityY;
                if (forwardDist > 0.05 && forwardDist < whiskerLength)
                {
                    double lateralDist = Math.Abs(dx * (-normalizedVelocityY) + dy * normalizedVelocityX);
                    double collisionThreshold = minimumDistance * 0.8;
                    if (lateralDist < collisionThreshold)
                    {
                        blocked = true;
                        closestBlockDist = forwardDist;
                    }
                }

                if (blocked)
                {
                    double brakeFactor = Math.Clamp(closestBlockDist / whiskerLength, 0.25, 1.0);
                    goalForceX *= brakeFactor;
                    goalForceY *= brakeFactor;
                }

                double effectiveGoalSpeed = PedestrianFundamentalDiagram.CalculateSpeed(localDensity, maxSpeed[i]);
                double currentGoalMag = Math.Sqrt(goalForceX * goalForceX + goalForceY * goalForceY);
                if (currentGoalMag > 0.0001)
                {
                    goalForceX = (goalForceX / currentGoalMag) * Math.Min(currentGoalMag, effectiveGoalSpeed);
                    goalForceY = (goalForceY / currentGoalMag) * Math.Min(currentGoalMag, effectiveGoalSpeed);
                }

                double finalForceX = goalForceX + wallRepulsionForceX + crowdAvoidanceForceX * 0.45;
                double finalForceY = goalForceY + wallRepulsionForceY + crowdAvoidanceForceY * 0.45;

                double calcSpeed = Math.Sqrt(finalForceX * finalForceX + finalForceY * finalForceY);
                if (calcSpeed > maxSpeed[i])
                {
                    finalForceX = (finalForceX / calcSpeed) * maxSpeed[i];
                    finalForceY = (finalForceY / calcSpeed) * maxSpeed[i];
                }

                double velocitySmoothingInertia = 0.25;
                velX[i] = velX[i] * (1.0 - velocitySmoothingInertia) + finalForceX * velocitySmoothingInertia;
                velY[i] = velY[i] * (1.0 - velocitySmoothingInertia) + finalForceY * velocitySmoothingInertia;

                posX[i] += velX[i] * dt;
                posY[i] += velY[i] * dt;

                foreach (var obstacle in obstacles)
                {
                    double nearestPointX = Math.Clamp(posX[i], obstacle.X, obstacle.X + obstacle.Width);
                    double nearestPointY = Math.Clamp(posY[i], obstacle.Y, obstacle.Y + obstacle.Height);
                    double dX = posX[i] - nearestPointX;
                    double dY = posY[i] - nearestPointY;
                    double dSq = dX * dX + dY * dY;
                    if (dSq < radius[i] * radius[i] && dSq > 0.000001)
                    {
                        double d = Math.Sqrt(dSq);
                        double nx = dX / d;
                        double ny = dY / d;
                        double pen = radius[i] - d;
                        posX[i] += nx * pen;
                        posY[i] += ny * pen;
                        double nVel = velX[i] * nx + velY[i] * ny;
                        if (nVel < 0)
                        {
                            velX[i] -= nVel * nx;
                            velY[i] -= nVel * ny;
                        }
                    }
                }
            }
        }

        _output.WriteLine($"Final Pos Agent 0: ({posX[0]:F2}, {posY[0]:F2})");
        _output.WriteLine($"Final Pos Agent 1: ({posX[1]:F2}, {posY[1]:F2})");

        Assert.True(posY[0] < 108.0, $"Agent 0 should have rounded corner past Y=108, but was at Y={posY[0]:F2}");
        Assert.True(posY[1] < 108.0, $"Agent 1 should have rounded corner past Y=108, but was at Y={posY[1]:F2}");
    }

    [Fact]
    public void ClusterAtObstacleCorner_EvacuatesCompletely()
    {
        double width = 200.0;
        double height = 200.0;
        int agentCount = 30;

        var engine = new CrowdSimulationEngine(width, height, agentCount);

        // Place 30 agents packed near the top obstacle corner (X in [66, 69.5], Y in [87, 91.8])
        int idx = 0;
        for (int r = 0; r < 6; r++)
        {
            for (int c = 0; c < 5; c++)
            {
                if (idx < agentCount)
                {
                    engine.AgentPositionX[idx] = 66.5 + c * 0.65;
                    engine.AgentPositionY[idx] = 88.0 + r * 0.65;
                    engine.AgentVelocityX[idx] = 0.0;
                    engine.AgentVelocityY[idx] = 0.0;
                    engine.AgentRadius[idx] = 0.35;
                    engine.AgentMaxSpeed[idx] = 1.34;
                    idx++;
                }
            }
        }

        double dt = 0.05;
        // Run 1000 steps (50 seconds of simulation)
        for (int step = 0; step < 1000; step++)
        {
            engine.UpdatePhysics(dt);
        }

        int stuckCount = 0;
        for (int i = 0; i < agentCount; i++)
        {
            // If still before X=70 and near obstacle (Y in [36, 92]), they are stuck
            if (engine.AgentPositionX[i] < 71.0 && engine.AgentPositionY[i] < 92.5)
            {
                stuckCount++;
                _output.WriteLine($"Stuck Agent {i}: Pos=({engine.AgentPositionX[i]:F2}, {engine.AgentPositionY[i]:F2}), Vel=({engine.AgentVelocityX[i]:F3}, {engine.AgentVelocityY[i]:F3}), Rho={engine.AgentLocalDensity[i]:F2}");
            }
        }

        _output.WriteLine($"Total stuck agents out of {agentCount}: {stuckCount}");
        Assert.Equal(0, stuckCount);
    }

    [Fact]
    public void FullSimulation_1000Agents_CheckForStuckCornerClump_AtStep1600()
    {
        double width = 200.0;
        double height = 200.0;
        int agentCount = 1000;

        var engine = new CrowdSimulationEngine(width, height, agentCount);
        engine.InitializeAgents(seed: 12345);

        double dt = 0.05;
        // Run 1600 steps (80 seconds, exactly like in the user's screenshot)
        for (int step = 0; step < 1600; step++)
        {
            engine.UpdatePhysics(dt);
        }

        int stuckTop = 0;
        int stuckBottom = 0;
        for (int i = 0; i < agentCount; i++)
        {
            double x = engine.AgentPositionX[i];
            double y = engine.AgentPositionY[i];
            double vx = engine.AgentVelocityX[i];
            double vy = engine.AgentVelocityY[i];
            double speed = Math.Sqrt(vx * vx + vy * vy);

            // Top obstacle west wall/corner: X in [60, 71], Y in [75, 93]
            if (x >= 60.0 && x <= 71.0 && y >= 75.0 && y <= 93.0 && speed < 0.15)
            {
                stuckTop++;
                _output.WriteLine($"Stuck Top [{i}]: Pos=({x:F2}, {y:F2}), Vel=({vx:F3}, {vy:F3}), Speed={speed:F3}, Rho={engine.AgentLocalDensity[i]:F2}");
            }
            // Bottom obstacle west wall/corner: X in [60, 71], Y in [107, 125]
            if (x >= 60.0 && x <= 71.0 && y >= 107.0 && y <= 125.0 && speed < 0.15)
            {
                stuckBottom++;
                _output.WriteLine($"Stuck Bottom [{i}]: Pos=({x:F2}, {y:F2}), Vel=({vx:F3}, {vy:F3}), Speed={speed:F3}, Rho={engine.AgentLocalDensity[i]:F2}");
            }
        }

        // Print details for agent 224
        var (fX, fY) = engine.PotentialFieldMap.GetFlowDirection(engine.AgentPositionX[224], engine.AgentPositionY[224]);
        _output.WriteLine($"Agent 224 Flow Direction: ({fX:F3}, {fY:F3})");

        _output.WriteLine($"Stuck Top Count: {stuckTop}, Stuck Bottom Count: {stuckBottom}");
        Assert.Equal(0, stuckTop);
        Assert.Equal(0, stuckBottom);
    }
}
