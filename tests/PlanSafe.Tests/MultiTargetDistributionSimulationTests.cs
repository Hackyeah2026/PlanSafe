using System.Linq;
using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Models.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public sealed class MultiTargetDistributionSimulationTests
{
    [Fact]
    public void MultiTarget_ScenarioA_PureDistanceBias_AllUpperAgentsFlowToNorthExit()
    {
        var engine = new CrowdSimulationEngine(200, 200, 200);
        engine.SetMultiTargetEnabled(true);
        engine.WeightDistance = 1.0;
        engine.WeightOccupancy = 0.0;
        engine.InitializeAgents(42);

        // North shelter is at Y: [24, 64], South shelter is at Y: [136, 176]
        Assert.Equal(2, engine.Targets.Count);
        Assert.Equal("exit-north", engine.Targets[0].Id);
        Assert.Equal("exit-south", engine.Targets[1].Id);

        // Place test agents in upper half (Y = 30..50)
        for (int i = 0; i < 50; i++)
        {
            engine.AgentPositionX[i] = 20.0;
            engine.AgentPositionY[i] = 30.0 + (i * 0.3);
        }

        // Potential field at upper agents must point towards North exit
        for (int i = 0; i < 50; i++)
        {
            var (fx, fy) = engine.PotentialFieldMap.GetFlowDirection(engine.AgentPositionX[i], engine.AgentPositionY[i]);
            Assert.True(fx > 0.0, "Flow direction X must point forward towards shelters.");
        }
    }

    [Fact]
    public void MultiTarget_ScenarioB_OccupancyBalancing_AdjustsPotentialField()
    {
        var engine = new CrowdSimulationEngine(200, 200, 200);
        engine.SetMultiTargetEnabled(true);
        engine.WeightDistance = 0.5;
        engine.WeightOccupancy = 0.5;
        engine.InitializeAgents(42);

        // Artificially saturate exit-north (occupancy = capacity)
        var northTarget = engine.Targets[0] with { CurrentOccupancy = 100, Capacity = 100 };
        var southTarget = engine.Targets[1] with { CurrentOccupancy = 0, Capacity = 100 };
        engine.SetTargets(new[] { northTarget, southTarget });

        // Agent past the obstacle corridor (X=120, Y=100) equidistant to both shelters:
        // Because North is full (capacity exceeded), potential field must direct them towards South exit (fy > 0)
        var (fx, fy) = engine.PotentialFieldMap.GetFlowDirection(120.0, 100.0);
        Assert.True(fx > 0.0, "Flow direction X must point East.");
        Assert.True(fy > 0.0, "Flow direction Y must divert South towards the non-full shelter.");
    }

    [Fact]
    public void MultiTarget_CustomTargets_AreConfigurable()
    {
        var engine = new CrowdSimulationEngine(200, 200, 100);
        var customTargets = new[]
        {
            new EvacuationTarget("custom-1", "Brama Główna", 180, 20, 15, 30, 100, 0),
            new EvacuationTarget("custom-2", "Brama Zapasowa", 180, 150, 15, 30, 100, 0)
        };

        engine.SetTargets(customTargets);

        Assert.True(engine.MultiTargetEnabled);
        Assert.Equal(2, engine.Targets.Count);
        Assert.Equal("custom-1", engine.Targets[0].Id);
        Assert.Equal("custom-2", engine.Targets[1].Id);
    }
}
