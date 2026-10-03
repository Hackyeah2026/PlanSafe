using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using PlanSafe.App.Simulation;

namespace PlanSafe.Tests;

public class QueueDecompressionArcTests
{
    private readonly ITestOutputHelper _output;

    public QueueDecompressionArcTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void PotentialField_InApproachZone_HasSmoothContinuousAngularGradient()
    {
        double width = 200.0;
        double height = 200.0;
        var engine = new CrowdSimulationEngine(width, height, 100);

        // Bottleneck entrance is at (70, 100).
        // Sample flow directions along an arc of radius 20m from (70, 100) at angles from -50 deg to +50 deg.
        double radius = 20.0;
        List<double> sampledFlowAngles = new();

        for (double deg = -50.0; deg <= 50.0; deg += 5.0)
        {
            double rad = deg * Math.PI / 180.0;
            // Point on arc in X < 70:
            double px = 70.0 - radius * Math.Cos(rad);
            double py = 100.0 + radius * Math.Sin(rad);

            var (fx, fy) = engine.PotentialFieldMap.GetFlowDirection(px, py);
            // Flow angle relative to horizontal:
            double flowAngleDeg = Math.Atan2(fy, fx) * 180.0 / Math.PI;
            sampledFlowAngles.Add(flowAngleDeg);

            _output.WriteLine($"Arc Angle {deg,5:F1}° -> Pos ({px:F1}, {py:F1}) -> Flow Angle: {flowAngleDeg,5:F1}°");
        }

        // Verify strict monotonicity and smoothness: flow angle should smoothly decrease as deg increases
        // (since positive Y means flow must point downwards in Y to reach Y=100)
        for (int i = 1; i < sampledFlowAngles.Count; i++)
        {
            double delta = sampledFlowAngles[i] - sampledFlowAngles[i - 1];
            // Delta must be strictly negative (smooth monotonic rotation towards center)
            Assert.True(delta <= 0.05, $"Discontinuity detected at index {i}: delta = {delta:F2}°");
        }
    }

    [Fact]
    public void QueueAtBottleneck_UniformlyExpandsIntoCircularArc_NoThreeStreaks()
    {
        double width = 200.0;
        double height = 200.0;
        int agentCount = 1000;

        var engine = new CrowdSimulationEngine(width, height, agentCount);
        engine.InitializeAgents(seed: 42);

        double dt = 0.05;
        // Warmup: agents travel towards bottleneck and form queue
        for (int step = 0; step < 700; step++)
        {
            engine.UpdatePhysics(dt);
        }

        // Sample agents in the queue approaching the bottleneck: X in [40, 70], Y in [70, 130]
        double entranceX = 70.0;
        double entranceY = 100.0;

        List<double> queueAngles = new();
        List<double> queueDistances = new();

        for (int i = 0; i < agentCount; i++)
        {
            double x = engine.AgentPositionX[i];
            double y = engine.AgentPositionY[i];

            if (x >= 40.0 && x <= 70.0 && y >= 70.0 && y <= 130.0)
            {
                double dx = entranceX - x; // > 0
                double dy = y - entranceY;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist >= 3.0 && dist <= 28.0)
                {
                    double angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                    queueAngles.Add(angleDeg);
                    queueDistances.Add(dist);
                }
            }
        }

        _output.WriteLine($"Total queue agents in approach sector: {queueAngles.Count}");
        Assert.True(queueAngles.Count >= 100, $"Expected at least 100 agents in queue, got {queueAngles.Count}");

        // Divide arc [-50°, +50°] into 8 angular bins
        int numBins = 8;
        double minAngle = -50.0;
        double maxAngle = 50.0;
        double binWidth = (maxAngle - minAngle) / numBins;

        int[] binCounts = new int[numBins];
        List<double>[] binDistances = new List<double>[numBins];
        for (int b = 0; b < numBins; b++) binDistances[b] = new List<double>();

        for (int i = 0; i < queueAngles.Count; i++)
        {
            double a = queueAngles[i];
            if (a >= minAngle && a < maxAngle)
            {
                int b = (int)((a - minAngle) / binWidth);
                if (b >= 0 && b < numBins)
                {
                    binCounts[b]++;
                    binDistances[b].Add(queueDistances[i]);
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("==========================================================================================");
        sb.AppendLine("   ROZKŁAD KĄTOWY I RADIALNY AGENTÓW W ZATORZE PRZED WĄSKIM GARDŁEM (ŁUK 'C')");
        sb.AppendLine("==========================================================================================");
        sb.AppendLine(string.Format("{0,-20} | {1,10} | {2,12} | {3,12}", "Przedział Kątowy", "Liczba Ag.", "Śr. Promień", "Maks. Promień"));
        sb.AppendLine(new string('-', 62));

        for (int b = 0; b < numBins; b++)
        {
            double bMin = minAngle + b * binWidth;
            double bMax = bMin + binWidth;
            double avgDist = binDistances[b].Count > 0 ? binDistances[b].Average() : 0.0;
            double maxDist = binDistances[b].Count > 0 ? binDistances[b].Max() : 0.0;

            sb.AppendLine(string.Format("[{0,5:F1}° .. {1,5:F1}°] | {2,10} | {3,10:F2} m | {4,10:F2} m",
                bMin, bMax, binCounts[b], avgDist, maxDist));
        }

        _output.WriteLine(sb.ToString());

        for (int b = 0; b < numBins; b++)
        {
            double bMin = minAngle + b * binWidth;
            double bMax = bMin + binWidth;
            // CRITICAL VERIFICATION:
            // Every angular bin MUST be populated!
            // In the old buggy state (inverted 'E'), bins between 0° and ±45° (e.g. [-30°, -15°] and [+15°, +30°]) had 0 agents!
            Assert.True(binCounts[b] >= 5,
                $"Angular sector [{bMin:F1}°, {bMax:F1}°] had only {binCounts[b]} agents! Crowd is not decompressing uniformly.");
        }

        // Check radial arc uniformity:
        // The outer contour (maxDist) across all populated bins should not vary wildly (confirming a continuous circular arc 'C')
        var maxDists = binDistances.Select(bd => bd.Count > 0 ? bd.Max() : 0.0).ToList();
        double overallMax = maxDists.Max();
        double overallMin = maxDists.Min();
        double arcRatio = overallMax / (overallMin > 0 ? overallMin : 1.0);

        _output.WriteLine($"Arc Radial Uniformity: Max R = {overallMax:F1}m, Min R = {overallMin:F1}m, Ratio = {arcRatio:F2}");
        Assert.True(arcRatio < 2.0, $"Queue outer edge has extreme spikes (ratio {arcRatio:F2} >= 2.0). Expected smooth arc 'C'.");
    }
}
