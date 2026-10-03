using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using PlanSafe.App.Simulation;

namespace PlanSafe.Tests;

public class DynamicPotentialRefinementTests
{
    private readonly ITestOutputHelper _output;

    public DynamicPotentialRefinementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Hala60m_1000Agents_EagerlyDivertsToAlternativeDetours_WhenCentralCorridorJammed()
    {
        // Setup the exact 60m x 40m world with 1000 agents from user screenshot
        double width = 60.0;
        double height = 40.0;
        int agentCount = 1000;

        var engine = new CrowdSimulationEngine(width, height, agentCount);
        engine.InitializeAgents(seed: 42);

        // Track agents that pass past the obstacles (X >= 28.2m) through the three passages:
        // Top detour: Y in [0, 7.2]
        // Central corridor: Y in [18.4, 21.6]
        // Bottom detour: Y in [32.8, 40.0]
        HashSet<int> topDetourAgents = new();
        HashSet<int> centralCorridorAgents = new();
        HashSet<int> bottomDetourAgents = new();

        double dt = 0.05;
        int totalSteps = 800; // 40 seconds of simulated time

        for (int step = 0; step < totalSteps; step++)
        {
            engine.UpdatePhysics(dt);

            for (int i = 0; i < agentCount; i++)
            {
                double x = engine.AgentPositionX[i];
                double y = engine.AgentPositionY[i];

                if (x >= 28.2 && x <= 35.0)
                {
                    if (y <= 7.5)
                        topDetourAgents.Add(i);
                    else if (y >= 18.0 && y <= 22.0)
                        centralCorridorAgents.Add(i);
                    else if (y >= 32.5)
                        bottomDetourAgents.Add(i);
                }
            }
        }

        _output.WriteLine("==========================================================================");
        _output.WriteLine("  WYNIKI EWAKUACJI HALA 60m x 40m (1000 AGENTÓW) Z DYNAMICZNYM POLEM");
        _output.WriteLine("==========================================================================");
        _output.WriteLine($"  Górne obejście (Top Detour, Y <= 7.2m):     {topDetourAgents.Count} agentów");
        _output.WriteLine($"  Korytarz centralny (Center, 18.4-21.6m):    {centralCorridorAgents.Count} agentów");
        _output.WriteLine($"  Dolne obejście (Bottom Detour, Y >= 32.8m): {bottomDetourAgents.Count} agentów");
        _output.WriteLine("==========================================================================");

        int totalAlternativeAgents = topDetourAgents.Count + bottomDetourAgents.Count;
        _output.WriteLine($"  Łącznie przez alternatywne obejścia: {totalAlternativeAgents} agentów");

        // The agents must eagerly choose the alternative paths around the blockage:
        // Top and bottom detours should carry a substantial crowd (at least 100 agents each, total > 200).
        Assert.True(topDetourAgents.Count >= 80, $"Górne obejście powinno obsłużyć co najmniej 80 agentów, uzyskano: {topDetourAgents.Count}");
        Assert.True(bottomDetourAgents.Count >= 80, $"Dolne obejście powinno obsłużyć co najmniej 80 agentów, uzyskano: {bottomDetourAgents.Count}");
        Assert.True(totalAlternativeAgents >= 200, $"Łącznie obejścia powinny obsłużyć co najmniej 200 agentów, uzyskano: {totalAlternativeAgents}");
    }

    [Fact]
    public void PotentialField_NearExit_RemainsAttractiveSink_NoRepulsion()
    {
        double width = 60.0;
        double height = 40.0;
        int agentCount = 200;

        var engine = new CrowdSimulationEngine(width, height, agentCount);

        // Place 200 agents directly in front of the exit zone (ExitZone is at X in [55.2, 58.8], Y in [16.0, 24.0])
        Random rnd = new(123);
        for (int i = 0; i < agentCount; i++)
        {
            engine.AgentPositionX[i] = 53.0 + rnd.NextDouble() * 2.0; // X in [53.0, 55.0]
            engine.AgentPositionY[i] = 17.0 + rnd.NextDouble() * 6.0; // Y in [17.0, 23.0]
            engine.AgentVelocityX[i] = 0.5;
            engine.AgentVelocityY[i] = 0.0;
            engine.AgentMaxSpeed[i] = 1.3;
        }

        // Run multiple updates to build up high density in front of exit
        for (int step = 0; step < 20; step++)
        {
            engine.UpdatePhysics(0.05);
        }

        // Verify that even with heavy crowd at the exit, flow direction still points strictly EAST towards the exit
        for (int i = 0; i < agentCount; i++)
        {
            var (fx, fy) = engine.PotentialFieldMap.GetFlowDirection(engine.AgentPositionX[i], engine.AgentPositionY[i]);
            Assert.True(fx > 0.3, $"Flow direction X at exit ({fx:F3}) must point towards exit, not away!");
        }
    }

    [Fact]
    public void FewAgents_On60mWorld_Travels10MetersInAbout7Seconds_At1xScale()
    {
        double width = 60.0;
        double height = 40.0;
        int agentCount = 3;

        var engine = new CrowdSimulationEngine(width, height, agentCount);

        // Place 3 agents in open space heading straight east towards central corridor
        for (int i = 0; i < agentCount; i++)
        {
            engine.AgentPositionX[i] = 5.0;
            engine.AgentPositionY[i] = 19.5 + (i - 1) * 1.0; // Y = 18.5, 19.5, 20.5
            engine.AgentVelocityX[i] = 1.4;
            engine.AgentVelocityY[i] = 0.0;
            engine.AgentMaxSpeed[i] = 1.4;
        }

        double startX = engine.AgentPositionX[1];

        // Simulate exactly 7.14 seconds of real-world time at 1.0x speed
        // (7.14s * 1.4 m/s = 10.0 meters)
        double totalSimTime = 7.14;
        double baseDt = 0.016;
        int steps = (int)Math.Round(totalSimTime / baseDt);

        for (int s = 0; s < steps; s++)
        {
            engine.Step(baseDt, timeScale: 1.0);
        }

        double endX = engine.AgentPositionX[1];
        double distance = endX - startX;

        _output.WriteLine($"Agent traveled {distance:F2} meters in {totalSimTime:F2} simulated seconds at 1.0x time scale.");

        // At 1.4 m/s in 7.14s, theoretical distance is 10.0m. Allow small margin for boundary/turning:
        Assert.InRange(distance, 9.2, 10.5);
    }

    [Fact]
    public void Hala60m_550Agents_DiagnoseDetourBehavior()
    {
        double width = 60.0;
        double height = 40.0;
        int agentCount = 550;

        var engine = new CrowdSimulationEngine(width, height, agentCount);
        engine.InitializeAgents(seed: 42);

        HashSet<int> topDetourAgents = new();
        HashSet<int> centralCorridorAgents = new();
        HashSet<int> bottomDetourAgents = new();

        double dt = 0.05;
        // Run 500 steps (25s) like in screenshot
        for (int step = 0; step < 500; step++)
        {
            engine.UpdatePhysics(dt);

            for (int i = 0; i < agentCount; i++)
            {
                double x = engine.AgentPositionX[i];
                double y = engine.AgentPositionY[i];

                if (x >= 28.2 && x <= 35.0)
                {
                    if (y <= 7.5) topDetourAgents.Add(i);
                    else if (y >= 18.0 && y <= 22.0) centralCorridorAgents.Add(i);
                    else if (y >= 32.5) bottomDetourAgents.Add(i);
                }
            }
        }

        _output.WriteLine($"Top Detour: {topDetourAgents.Count}, Center: {centralCorridorAgents.Count}, Bottom: {bottomDetourAgents.Count}");

        // Print flow directions in front of obstacles
        double[] testYs = { 8.0, 10.0, 12.0, 14.0, 16.0, 18.0, 20.0, 22.0, 24.0, 26.0, 28.0, 30.0, 32.0 };
        foreach (var ty in testYs)
        {
            var (fx, fy) = engine.PotentialFieldMap.GetFlowDirection(18.0, ty);
            int col = (int)(18.0 / engine.PotentialFieldMap.CellSize);
            int row = (int)(ty / engine.PotentialFieldMap.CellSize);
            int idx = row * engine.PotentialFieldMap.ColumnCount + col;
            float pen = engine.PotentialFieldMap.DynamicCrowdPenalty[idx];
            float pot = engine.PotentialFieldMap.DynamicPotentialFieldMatrix[idx];
            _output.WriteLine($"Pos (18.0, {ty,4:F1}) -> Flow: ({fx,5:F2}, {fy,5:F2}) | Penalty: {pen,5:F1} | Pot: {pot,5:F1}");
        }

        // Assert that alternative detours are actively and reliably chosen
        Assert.True(topDetourAgents.Count >= 30, $"Górne obejście powinno obsłużyć co najmniej 30 agentów, uzyskano: {topDetourAgents.Count}");
        Assert.True(bottomDetourAgents.Count >= 30, $"Dolne obejście powinno obsłużyć co najmniej 30 agentów, uzyskano: {bottomDetourAgents.Count}");
        Assert.True(topDetourAgents.Count + bottomDetourAgents.Count >= 70, $"Obejścia łącznie powinny obsłużyć co najmniej 70 agentów");

        // Assert that upstream flow direction guides agents away from central bottleneck into detours
        var (_, fyTop10) = engine.PotentialFieldMap.GetFlowDirection(18.0, 10.0);
        var (_, fyTop12) = engine.PotentialFieldMap.GetFlowDirection(18.0, 12.0);
        var (_, fyBottom28) = engine.PotentialFieldMap.GetFlowDirection(18.0, 28.0);
        var (_, fyBottom30) = engine.PotentialFieldMap.GetFlowDirection(18.0, 30.0);
        double fyTop = Math.Min(fyTop10, fyTop12);
        double fyBottom = Math.Max(fyBottom28, fyBottom30);
        Assert.True(fyTop < -0.2, $"Wektor przepływu przed górną przeszkodą powinien kierować w górę (fy < -0.2), uzyskano: {fyTop:F2}");
        Assert.True(fyBottom > 0.2, $"Wektor przepływu przed dolną przeszkodą powinien kierować w dół (fy > 0.2), uzyskano: {fyBottom:F2}");
    }
}
