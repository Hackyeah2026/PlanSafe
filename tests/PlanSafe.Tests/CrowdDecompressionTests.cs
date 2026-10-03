using PlanSafe.App.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class CrowdDecompressionTests
{
    [Fact]
    public void MultiAgentCluster_DecompressesOverTimeUntilComfortDensity()
    {
        // Setup 5 agents marching in a 1D column with initial dense spacing of 0.80m (Regime II, rho ~ 1.88 os/m²)
        int nAgents = 5;
        double[] positions = new double[nAgents];
        double[] speeds = new double[nAgents];
        double baseSpeed = 1.34;

        for (int i = 0; i < nAgents; i++)
        {
            positions[i] = i * 0.80; // Agent 4 is at 3.2m (front), Agent 0 is at 0.0m (back)
        }

        double dt = 0.05; // 50ms per step
        int totalSteps = 200; // 10 seconds simulation to allow 5-agent cascade to propagate

        double initialTotalSpan = positions[nAgents - 1] - positions[0]; // 3.2m

        for (int step = 0; step < totalSteps; step++)
        {
            // Calculate effective density and speed for each agent
            for (int i = 0; i < nAgents; i++)
            {
                double forwardDistance = (i < nAgents - 1) ? (positions[i + 1] - positions[i]) : 10.0;
                double effectiveDensity = PedestrianFundamentalDiagram.CalculateEffectiveDensity(0.0, forwardDistance);
                speeds[i] = PedestrianFundamentalDiagram.CalculateSpeed(effectiveDensity, baseSpeed);
            }

            // Update positions
            for (int i = 0; i < nAgents; i++)
            {
                positions[i] += speeds[i] * dt;
            }
        }

        double finalTotalSpan = positions[nAgents - 1] - positions[0];

        // The column must have expanded (decompressed)
        Assert.True(finalTotalSpan > initialTotalSpan * 1.5,
            $"Crowd column should decompress: initial span {initialTotalSpan:F2}m -> final span {finalTotalSpan:F2}m");

        // Every pair of agents must have decompressed into the comfort density zone (spacing >= 1.30m, rho <= 0.70)
        for (int i = 0; i < nAgents - 1; i++)
        {
            double finalGap = positions[i + 1] - positions[i];
            double finalDensity = PedestrianFundamentalDiagram.CalculateEffectiveDensity(0.0, finalGap);
            int regime = PedestrianFundamentalDiagram.GetRegime(finalDensity);

            Assert.True(finalGap >= 1.25, $"Gap between agent {i} and {i + 1} ({finalGap:F2}m) should be in comfort range (>= 1.25m)");
            Assert.Equal(1, regime); // Must be in Regime I (Comfort zone)
        }
    }

    [Fact]
    public void RearAgent_InDenseCrowd_SlowsDownDirectlyProportionalToEmpiricalGraph()
    {
        // Spacing = 0.80m -> density rho = 1.20 / 0.64 = 1.875 os/m²
        double gap = 0.80;
        double density = PedestrianFundamentalDiagram.CalculateEffectiveDensity(0.0, gap);

        Assert.Equal(2, PedestrianFundamentalDiagram.GetRegime(density)); // Regime II

        double rearSpeed = PedestrianFundamentalDiagram.CalculateSpeed(density);
        double frontSpeed = PedestrianFundamentalDiagram.CalculateSpeed(0.0); // Open space

        // Front speed should be 1.34 m/s
        Assert.Equal(1.34, frontSpeed, precision: 2);

        // Rear speed should be ~0.59 m/s (approx 44% of front speed)
        Assert.InRange(rearSpeed, 0.55, 0.65);
        Assert.True(rearSpeed < frontSpeed * 0.50, "Rear speed in 0.8m spacing must be less than 50% of free speed");
    }

    [Fact]
    public void CriticalRegimeThresholds_AreExactlyPreserved()
    {
        // Verify the 4 exact regime boundaries from the image
        Assert.Equal(0.70, PedestrianFundamentalDiagram.ComfortDensityThreshold);
        Assert.Equal(2.20, PedestrianFundamentalDiagram.ConstrainedDensityThreshold);
        Assert.Equal(4.70, PedestrianFundamentalDiagram.DenseCrowdThreshold);
        Assert.Equal(5.40, PedestrianFundamentalDiagram.JamDensity);

        // Check continuity across boundaries
        double eps = 1e-6;
        double vAtRegime1End = PedestrianFundamentalDiagram.CalculateSpeed(0.70 - eps);
        double vAtRegime2Start = PedestrianFundamentalDiagram.CalculateSpeed(0.70 + eps);
        Assert.True(Math.Abs(vAtRegime1End - vAtRegime2Start) < 0.01, "Curve must be continuous at Regime I/II boundary");

        double vAtRegime2End = PedestrianFundamentalDiagram.CalculateSpeed(2.20 - eps);
        double vAtRegime3Start = PedestrianFundamentalDiagram.CalculateSpeed(2.20 + eps);
        Assert.True(Math.Abs(vAtRegime2End - vAtRegime3Start) < 0.01, "Curve must be continuous at Regime II/III boundary");

        double vAtRegime3End = PedestrianFundamentalDiagram.CalculateSpeed(4.70 - eps);
        double vAtRegime4Start = PedestrianFundamentalDiagram.CalculateSpeed(4.70 + eps);
        Assert.True(Math.Abs(vAtRegime3End - vAtRegime4Start) < 0.01, "Curve must be continuous at Regime III/IV boundary");
    }
}
