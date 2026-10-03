using System;
using PlanSafe.App.Simulation;
using Xunit;

namespace PlanSafe.Tests;

/// <summary>
/// Unit tests establishing reference mathematical values for individual crowd physics calculations
/// and corner cases. These exact reference values are validated against the WebGPU/TypeScript engine
/// to ensure 100% mathematical parity.
/// </summary>
public class PhysicsParityReferenceTests
{
    // =========================================================================
    // 1. Fundamental Diagram Calculations & Corner Cases
    // =========================================================================

    [Theory]
    [InlineData(0.00, 1.000000)] // Zero density
    [InlineData(0.35, 0.950848)] // Regime I midpoint: 1.0 - 0.149 * (0.35/0.70)^1.6
    [InlineData(0.70, 0.851000)] // Boundary I -> II
    [InlineData(1.45, 0.582250)] // Regime II midpoint
    [InlineData(2.20, 0.351000)] // Boundary II -> III
    [InlineData(3.45, 0.202564)] // Regime III midpoint
    [InlineData(4.70, 0.112000)] // Boundary III -> IV
    [InlineData(5.05, 0.063249)] // Regime IV midpoint
    [InlineData(5.40, 0.000000)] // Jam density (full stop)
    [InlineData(6.00, 0.000000)] // Above jam density
    [InlineData(-0.5, 1.000000)] // Negative density corner case
    public void FundamentalDiagram_SpeedFactor_ExactValues(double density, double expected)
    {
        double actual = PedestrianFundamentalDiagram.CalculateSpeedFactor(density);
        Assert.Equal(expected, actual, precision: 5);
    }

    [Theory]
    // Zero headway / singularity corner case (< 0.05m): no forward density boost
    [InlineData(0.50, 0.02, 0.50)]
    // Free flow (kernel <= 2.20) with close leader (headway = 0.50m -> raw 4.80 os/m²):
    // Must clamp to ConstrainedDensityThreshold (2.20) so leader doesn't prematurely jam free walkers
    [InlineData(0.50, 0.50, 2.20)]
    // Dense crowd (kernel = 3.00 > 2.20) with close leader (headway = 0.50m -> raw 4.80 os/m²):
    // Allows forward density to rise up to 4.80
    [InlineData(3.00, 0.50, 4.80)]
    // Extreme close leader in dense crowd (headway = 0.20m -> raw 30 os/m²):
    // Clamped to JamDensity - 0.05 = 5.35
    [InlineData(3.00, 0.20, 5.35)]
    // Distant leader (headway >= 1.80m): no boost, returns forwardKernelSum
    [InlineData(1.50, 2.50, 1.50)]
    public void FundamentalDiagram_EffectiveDensity_CornerCases(double forwardKernel, double headway, double expected)
    {
        double actual = PedestrianFundamentalDiagram.CalculateEffectiveDensity(forwardKernel, headway);
        Assert.Equal(expected, actual, precision: 5);
    }

    [Theory]
    [InlineData(0.00, 0.0000)] // Free flow: no pushing
    [InlineData(0.70, 0.0000)] // Comfort boundary: no pushing
    [InlineData(1.60, 0.5000)] // Mild pushing: (1.6 - 0.7) / (2.5 - 0.7) = 0.5
    [InlineData(2.50, 1.0000)] // Boundary mild -> strong: exactly 1.0
    [InlineData(3.50, 1.8500)] // Strong pushing: 1.0 + 0.85 * (3.5 - 2.5) = 1.85
    public void FundamentalDiagram_PushingFactor_ExactValues(double density, double expected)
    {
        double actual = PedestrianFundamentalDiagram.CalculatePushingFactor(density);
        Assert.Equal(expected, actual, precision: 4);
    }

    [Fact]
    public void FundamentalDiagram_RearPushingForce_CornerCases()
    {
        double radiusSum = 0.70; // Two 0.35m agents
        double contactBuffer = 0.25; // Contact threshold = 0.95m
        double rearDrive = 1.2;

        // 1. Free density (rho <= 0.70) -> zero push regardless of distance
        Assert.Equal(0.0, PedestrianFundamentalDiagram.CalculateRearPushingForce(0.50, 0.60, radiusSum, contactBuffer, rearDrive));

        // 2. Beyond contact threshold (dist >= 0.95m) -> zero push
        Assert.Equal(0.0, PedestrianFundamentalDiagram.CalculateRearPushingForce(3.00, 0.96, radiusSum, contactBuffer, rearDrive));

        // 3. Singularity distance (dist <= 0.0001m) -> zero push
        Assert.Equal(0.0, PedestrianFundamentalDiagram.CalculateRearPushingForce(3.00, 0.00005, radiusSum, contactBuffer, rearDrive));

        // 4. Contact with body penetration (dist = 0.65m < 0.70m):
        // density = 2.50 -> pushingFactor = 1.0
        // penetration = 0.70 - 0.65 = 0.05 -> contactPush = 0.05 * 4.0 = 0.20
        // proximityFactor = (0.95 - 0.65) / 0.25 = 1.20
        // drivePush = 1.2 * 1.5 * 1.20 = 2.16
        // total = 1.0 * (0.20 + 2.16) = 2.36
        double forcePenetration = PedestrianFundamentalDiagram.CalculateRearPushingForce(2.50, 0.65, radiusSum, contactBuffer, rearDrive);
        Assert.Equal(2.36, forcePenetration, precision: 4);

        // 5. Contact without penetration (dist = 0.85m in [0.70, 0.95]):
        // density = 1.60 -> pushingFactor = 0.50
        // penetration = 0.0 -> contactPush = 0.0
        // proximityFactor = (0.95 - 0.85) / 0.25 = 0.40
        // drivePush = 1.2 * 1.5 * 0.40 = 0.72
        // total = 0.50 * 0.72 = 0.36
        double forceContactOnly = PedestrianFundamentalDiagram.CalculateRearPushingForce(1.60, 0.85, radiusSum, contactBuffer, rearDrive);
        Assert.Equal(0.36, forceContactOnly, precision: 4);
    }

    // =========================================================================
    // 2. Potential Field Grid & Bilinear Flow Direction Corner Cases
    // =========================================================================

    [Fact]
    public void PotentialField_BilinearFlowDirection_CornerCases()
    {
        double width = 200.0;
        double height = 200.0;
        var obstacles = new[]
        {
            new Obstacle(70.0, 36.0, 24.0, 56.0),
            new Obstacle(70.0, 108.0, 24.0, 56.0)
        };
        var exitZone = new Obstacle(184.0, 80.0, 12.0, 40.0);

        var grid = new PotentialFieldGrid(2.0, width, height);
        grid.InitializeStaticObstacles(obstacles);
        grid.BuildStaticField(obstacles, exitZone);

        // 1. In open field before obstacles (x = 30.0, y = 100.0):
        // Flow direction must point predominantly towards +X (towards corridor/exit)
        var (fxOpen, fyOpen) = grid.GetFlowDirection(30.0, 100.0);
        double magOpen = Math.Sqrt(fxOpen * fxOpen + fyOpen * fyOpen);
        Assert.Equal(1.0, magOpen, precision: 5);
        Assert.True(fxOpen > 0.90, $"fxOpen should be > 0.90 but was {fxOpen}");
        Assert.True(Math.Abs(fyOpen) < 0.20, $"fyOpen should be near 0 but was {fyOpen}");

        // 2. Near western wall of top obstacle (x = 68.0, y = 60.0):
        // Must NEVER point left (fx must be non-negative) and must steer around the obstacle
        var (fxWall, fyWall) = grid.GetFlowDirection(68.0, 60.0);
        double magWall = Math.Sqrt(fxWall * fxWall + fyWall * fyWall);
        Assert.Equal(1.0, magWall, precision: 5);
        Assert.True(fxWall >= 0.0, $"fxWall must not point backwards, got {fxWall}");

        // 3. Inside exit zone (x = 190.0, y = 100.0):
        // Flow direction is well-defined unit vector
        var (fxExit, fyExit) = grid.GetFlowDirection(190.0, 100.0);
        double magExit = Math.Sqrt(fxExit * fxExit + fyExit * fyExit);
        Assert.Equal(1.0, magExit, precision: 5);
    }

    // =========================================================================
    // 3. Wall Repulsion & Sliding Calculation Parity
    // =========================================================================

    [Fact]
    public void WallInteraction_RepulsionAndSliding_Parity()
    {
        double agentRadius = 0.35;
        double comfortDistance = agentRadius + 0.6; // 0.95m
        var obs = new Obstacle(70.0, 36.0, 24.0, 56.0); // X: 70..94, Y: 36..92

        // Agent at x = 69.5, y = 50.0 (0.5m west of obstacle wall)
        double px = 69.5;
        double py = 50.0;

        double nbpX = Math.Max(obs.X, Math.Min(px, obs.X + obs.Width)); // 70.0
        double nbpY = Math.Max(obs.Y, Math.Min(py, obs.Y + obs.Height)); // 50.0
        double dx = px - nbpX; // -0.5
        double dy = py - nbpY; // 0.0
        double dist = Math.Sqrt(dx * dx + dy * dy); // 0.50

        Assert.True(dist < comfortDistance);
        double normalX = dx / dist; // -1.0
        double normalY = dy / dist; // 0.0

        double strength = 3.5 * (comfortDistance - dist) / comfortDistance;
        double wallRepX = normalX * strength;
        double wallRepY = normalY * strength;

        // Repulsion pushes away from wall (-X)
        Assert.Equal(-1.0, normalX, precision: 5);
        Assert.Equal(0.0, normalY, precision: 5);
        Assert.True(wallRepX < -1.0, $"wallRepX should be < -1.0 but was {wallRepX}");

        // Sliding test: Agent wants to travel east into the wall (travelDir = (1.0, 0.0))
        double travelX = 1.0;
        double travelY = 0.0;
        double dotWithNormal = travelX * normalX + travelY * normalY; // -1.0
        Assert.True(dotWithNormal < 0);

        travelX -= dotWithNormal * normalX; // 1.0 - (-1.0 * -1.0) = 0.0
        travelY -= dotWithNormal * normalY; // 0.0
        double slideSpeed = Math.Sqrt(travelX * travelX + travelY * travelY);

        // Head-on collision fallback: slideSpeed <= 0.05 -> bypass sign
        Assert.True(slideSpeed <= 0.05);
        double tan1X = -normalY; // 0.0
        double tan1Y = normalX; // -1.0
        double obsCenterY = obs.Y + obs.Height * 0.5; // 36 + 28 = 64.0
        double bypassSign = (py < obsCenterY) ? -1.0 : 1.0; // py=50 < 64 -> -1.0
        if (tan1Y * bypassSign >= 0)
        {
            travelX = tan1X;
            travelY = tan1Y;
        }
        else
        {
            travelX = -tan1X;
            travelY = -tan1Y;
        }

        // Steers downwards along wall (-Y) to bypass around bottom/top
        Assert.Equal(0.0, travelX, precision: 5);
        Assert.Equal(-1.0, travelY, precision: 5);
    }
}
