using PlanSafe.App.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class FundamentalDiagramTests
{
    [Fact]
    public void Regime1_ComfortDensity_MaintainsHighSpeed()
    {
        // Regime I: rho in [0.0, 0.70] (Comfort density zone)
        // Free flow speed should be maintained with only gentle decline (1.34 -> 1.14 m/s)
        double vAtZero = PedestrianFundamentalDiagram.CalculateSpeed(0.0);
        double vAtQuarter = PedestrianFundamentalDiagram.CalculateSpeed(0.25);
        double vAtHalf = PedestrianFundamentalDiagram.CalculateSpeed(0.50);
        double vAtComfortLimit = PedestrianFundamentalDiagram.CalculateSpeed(0.70);

        Assert.Equal(1.34, vAtZero, precision: 2);
        Assert.InRange(vAtQuarter, 1.25, 1.32);
        Assert.InRange(vAtHalf, 1.21, 1.26);
        Assert.InRange(vAtComfortLimit, 1.12, 1.16);

        // At comfort limit (0.70 os/m²), speed must still be >= 85% of desired free speed
        Assert.True(vAtComfortLimit >= 1.34 * 0.85);

        Assert.Equal(1, PedestrianFundamentalDiagram.GetRegime(0.0));
        Assert.Equal(1, PedestrianFundamentalDiagram.GetRegime(0.35));
        Assert.Equal(1, PedestrianFundamentalDiagram.GetRegime(0.70));
    }

    [Fact]
    public void Regime2_ConstrainedDensity_SteepSlowdown()
    {
        // Regime II: rho in (0.70, 2.20] (Constrained flow)
        // Rapid drop in speed as crowd compresses beyond comfort zone
        double vAt1_0 = PedestrianFundamentalDiagram.CalculateSpeed(1.00);
        double vAt1_5 = PedestrianFundamentalDiagram.CalculateSpeed(1.50);
        double vAt2_0 = PedestrianFundamentalDiagram.CalculateSpeed(2.00);
        double vAt2_2 = PedestrianFundamentalDiagram.CalculateSpeed(2.20);

        // Graph points:
        // rho = 1.00 -> v ~ 1.02 m/s
        // rho = 1.50 -> v ~ 0.77 m/s
        // rho = 2.00 -> v ~ 0.55 m/s
        // rho = 2.20 -> v ~ 0.47 m/s
        Assert.InRange(vAt1_0, 0.95, 1.05);
        Assert.InRange(vAt1_5, 0.73, 0.81);
        Assert.InRange(vAt2_0, 0.51, 0.59);
        Assert.InRange(vAt2_2, 0.44, 0.50);

        Assert.Equal(2, PedestrianFundamentalDiagram.GetRegime(0.71));
        Assert.Equal(2, PedestrianFundamentalDiagram.GetRegime(1.50));
        Assert.Equal(2, PedestrianFundamentalDiagram.GetRegime(2.20));
    }

    [Fact]
    public void Regime3_DenseCrowd_QueueShuffling()
    {
        // Regime III: rho in (2.20, 4.70] (Dense crowd queue)
        // Slower decline from 0.47 down to 0.15 m/s
        double vAt2_5 = PedestrianFundamentalDiagram.CalculateSpeed(2.50);
        double vAt3_0 = PedestrianFundamentalDiagram.CalculateSpeed(3.00);
        double vAt3_5 = PedestrianFundamentalDiagram.CalculateSpeed(3.50);
        double vAt4_0 = PedestrianFundamentalDiagram.CalculateSpeed(4.00);
        double vAt4_7 = PedestrianFundamentalDiagram.CalculateSpeed(4.70);

        // Graph points:
        // rho = 2.50 -> v ~ 0.41 m/s
        // rho = 3.00 -> v ~ 0.32 m/s
        // rho = 3.50 -> v ~ 0.25 m/s
        // rho = 4.00 -> v ~ 0.21 m/s
        // rho = 4.70 -> v ~ 0.15 m/s
        Assert.InRange(vAt2_5, 0.38, 0.44);
        Assert.InRange(vAt3_0, 0.29, 0.35);
        Assert.InRange(vAt3_5, 0.23, 0.28);
        Assert.InRange(vAt4_0, 0.18, 0.23);
        Assert.InRange(vAt4_7, 0.13, 0.17);

        Assert.Equal(3, PedestrianFundamentalDiagram.GetRegime(2.21));
        Assert.Equal(3, PedestrianFundamentalDiagram.GetRegime(3.50));
        Assert.Equal(3, PedestrianFundamentalDiagram.GetRegime(4.70));
    }

    [Fact]
    public void Regime4_JamDensity_DropsToStandstill()
    {
        // Regime IV: rho in (4.70, 5.40] (Jamming breakdown)
        // Sharp drop to complete stop at rho = 5.40 m^-2
        double vAt5_0 = PedestrianFundamentalDiagram.CalculateSpeed(5.00);
        double vAt5_25 = PedestrianFundamentalDiagram.CalculateSpeed(5.25);
        double vAtJam = PedestrianFundamentalDiagram.CalculateSpeed(5.40);
        double vOverJam = PedestrianFundamentalDiagram.CalculateSpeed(6.00);

        Assert.InRange(vAt5_0, 0.07, 0.13);
        Assert.InRange(vAt5_25, 0.02, 0.07);
        Assert.Equal(0.00, vAtJam, precision: 3);
        Assert.Equal(0.00, vOverJam, precision: 3);

        Assert.Equal(4, PedestrianFundamentalDiagram.GetRegime(4.71));
        Assert.Equal(4, PedestrianFundamentalDiagram.GetRegime(5.40));
        Assert.Equal(4, PedestrianFundamentalDiagram.GetRegime(6.00));
    }

    [Fact]
    public void Monotonicity_SpeedStrictlyDecreasesAsDensityIncreases()
    {
        double prevSpeed = double.MaxValue;
        for (double density = 0.0; density <= 6.0; density += 0.05)
        {
            double speed = PedestrianFundamentalDiagram.CalculateSpeed(density);
            Assert.True(speed <= prevSpeed + 1e-9, $"Speed at rho={density:F2} ({speed:F4}) exceeded speed at previous density ({prevSpeed:F4})");
            Assert.True(speed >= 0.0, $"Speed at rho={density:F2} is negative ({speed:F4})");
            prevSpeed = speed;
        }
    }

    [Fact]
    public void FrontWave_MovesFasterThanDenseBackCrowd_CausingDecompression()
    {
        // Front wave: loose crowd in comfort density (rho = 0.30 os/m²)
        double frontDensity = 0.30;
        double frontSpeed = PedestrianFundamentalDiagram.CalculateSpeed(frontDensity);

        // Rear crowd: dense pack compressed to rho = 1.80 os/m²
        double rearDensity = 1.80;
        double rearSpeed = PedestrianFundamentalDiagram.CalculateSpeed(rearDensity);

        // Front wave moves at near full speed (~1.27 m/s)
        Assert.True(frontSpeed > 1.25, $"Front speed {frontSpeed} should be > 1.25 m/s");

        // Rear crowd significantly slows down (~0.62 m/s)
        Assert.True(rearSpeed < 0.70, $"Rear speed {rearSpeed} should be < 0.70 m/s");

        // Speed ratio must be at least 1.8x, proving the front wave outpaces the rear
        double speedRatio = frontSpeed / rearSpeed;
        Assert.True(speedRatio >= 1.8, $"Front/rear speed ratio {speedRatio:F2} must be >= 1.8x to decompress the crowd");
    }

    [Fact]
    public void CrowdDecompression_ExpandsSpacingUntilComfortDensityAchieved()
    {
        // Follower starts packed closely behind leader at d = 0.85m (Regime II, rho ~ 1.66 os/m²)
        double followerPosition = 0.0;
        double leaderPosition = 0.85;
        double dt = 0.1; // 100ms per step

        // Leader is at the front in open space (rho = 0.0, moves at 1.34 m/s)
        double leaderSpeed = PedestrianFundamentalDiagram.CalculateSpeed(0.0);

        int stepsToComfortDensity = 0;
        bool comfortAchieved = false;

        for (int step = 0; step < 100; step++)
        {
            double distance = leaderPosition - followerPosition;
            double effectiveDensity = PedestrianFundamentalDiagram.CalculateEffectiveDensity(0.0, distance);
            double followerSpeed = PedestrianFundamentalDiagram.CalculateSpeed(effectiveDensity);

            leaderPosition += leaderSpeed * dt;
            followerPosition += followerSpeed * dt;

            int regime = PedestrianFundamentalDiagram.GetRegime(effectiveDensity);
            if (regime == 1 && !comfortAchieved)
            {
                comfortAchieved = true;
                stepsToComfortDensity = step;
            }
        }

        // Must achieve comfort density as crowd decompresses
        Assert.True(comfortAchieved, "Follower should achieve comfort density as leader pulls away");
        Assert.True(stepsToComfortDensity < 50, $"Comfort density achieved in {stepsToComfortDensity} steps");

        // Final spacing should be >= 1.30m (comfort distance)
        double finalDistance = leaderPosition - followerPosition;
        Assert.True(finalDistance >= 1.30, $"Final distance {finalDistance:F2}m should be >= 1.30m");
    }

    [Fact]
    public void DensityCalculator_DistinguishesFrontWaveFromDenseRear()
    {
        // Front wave: leader has no close neighbors ahead
        double frontRadial = 0.20;
        double frontHeadway = 5.0; // Open space ahead
        double frontDensity = PedestrianFundamentalDiagram.CalculateEffectiveDensity(frontRadial, frontHeadway);

        // Rear crowd: follower has neighbors at 0.9m ahead
        double rearRadial = 1.10;
        double rearHeadway = 0.90; // Person directly in front
        double rearDensity = PedestrianFundamentalDiagram.CalculateEffectiveDensity(rearRadial, rearHeadway);

        // Front is in Regime 1 (Comfort zone)
        Assert.True(frontDensity <= PedestrianFundamentalDiagram.ComfortDensityThreshold);
        Assert.Equal(1, PedestrianFundamentalDiagram.GetRegime(frontDensity));

        // Rear is in Regime 2 (Constrained flow, must slow down)
        Assert.True(rearDensity > PedestrianFundamentalDiagram.ComfortDensityThreshold);
        Assert.True(rearDensity <= PedestrianFundamentalDiagram.ConstrainedDensityThreshold);
        Assert.Equal(2, PedestrianFundamentalDiagram.GetRegime(rearDensity));

        // Speed comparison
        double vFront = PedestrianFundamentalDiagram.CalculateSpeed(frontDensity);
        double vRear = PedestrianFundamentalDiagram.CalculateSpeed(rearDensity);
        Assert.True(vFront >= 1.25, $"vFront={vFront} should be >= 1.25");
        Assert.True(vRear <= 0.80, $"vRear={vRear} should be <= 0.80");
    }

    [Fact]
    public void LeaderAtFrontOfCluster_WithDenseFollowersBehind_MovesAtFullFreeSpeed_DoesNotFreeze()
    {
        double width = 200.0;
        double height = 200.0;
        int agentCount = 9; // 1 leader at front + 8 followers directly behind

        var engine = new CrowdSimulationEngine(width, height, agentCount);

        // Leader at front: open space ahead (+X)
        engine.AgentPositionX[0] = 50.0;
        engine.AgentPositionY[0] = 100.0;
        engine.AgentVelocityX[0] = 1.34;
        engine.AgentVelocityY[0] = 0.0;

        // 8 followers densely packed directly behind leader (within 0.5m - 1.8m)
        double[] followerOffsetsX = { -0.5, -0.6, -0.9, -1.0, -1.2, -1.3, -1.6, -1.7 };
        double[] followerOffsetsY = { -0.2,  0.2, -0.3,  0.3, -0.1,  0.1, -0.2,  0.2 };

        for (int i = 0; i < 8; i++)
        {
            engine.AgentPositionX[1 + i] = 50.0 + followerOffsetsX[i];
            engine.AgentPositionY[1 + i] = 100.0 + followerOffsetsY[i];
            engine.AgentVelocityX[1 + i] = 0.8;
            engine.AgentVelocityY[1 + i] = 0.0;
        }

        // Run 1 simulation step
        engine.UpdatePhysics(0.05);

        double leaderVx = engine.AgentVelocityX[0];
        double leaderVy = engine.AgentVelocityY[0];
        double leaderSpeed = Math.Sqrt(leaderVx * leaderVx + leaderVy * leaderVy);
        double leaderDensity = engine.AgentLocalDensity[0];

        // The leader has NO ONE ahead in their direction of movement.
        // Therefore, their forward density must remain low (Regime 1), and speed must NOT freeze!
        Assert.True(leaderDensity <= PedestrianFundamentalDiagram.ComfortDensityThreshold,
            $"Leader density {leaderDensity:F2} os/m² should be in comfort zone (Regime 1) despite followers behind!");
        Assert.True(leaderSpeed >= 1.25,
            $"Leader speed {leaderSpeed:F2} m/s should remain near free speed (>= 1.25 m/s), but froze!");
    }

    [Fact]
    public void AgentAtObstacleCorner_GlidesSmoothlyAround_WithoutGettingStuck()
    {
        double width = 200.0;
        double height = 200.0;
        int agentCount = 1;

        var engine = new CrowdSimulationEngine(width, height, agentCount);

        // Place agent right near the entrance corner of the bottom obstacle:
        // Bottom obstacle: X in [70, 94], Y in [108, 164]
        // Corner is at (70, 108). Place agent at (69.6, 109.0)
        engine.AgentPositionX[0] = 69.6;
        engine.AgentPositionY[0] = 109.0;
        engine.AgentVelocityX[0] = 0.5;
        engine.AgentVelocityY[0] = -0.5;

        // Run 50 steps (2.5 seconds)
        for (int step = 0; step < 50; step++)
        {
            engine.UpdatePhysics(0.05);
        }

        double finalX = engine.AgentPositionX[0];
        double finalY = engine.AgentPositionY[0];

        // The agent should successfully round the corner into the corridor (Y < 108) and advance along +X
        Assert.True(finalY < 108.0, $"Agent should have rounded corner past Y=108, but was at Y={finalY:F2}");
        Assert.True(finalX > 69.6, $"Agent should have advanced past X=69.6, but was at X={finalX:F2}");
    }
}
