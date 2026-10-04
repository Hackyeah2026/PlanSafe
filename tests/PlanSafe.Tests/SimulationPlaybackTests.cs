using PlanSafe.App.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class SimulationPlaybackTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(50)]
    public void PlaybackSpeedPreservesPhysicsTicks(int speed)
    {
        var batched = new CrowdSimulationEngine(200, 200, 100);
        var single = new CrowdSimulationEngine(200, 200, 100);
        batched.InitializeAgents(seed: 42);
        single.InitializeAgents(seed: 42);
        batched.Step(0.016, speed);
        for (int i = 0; i < speed; i++) single.Step(0.016, 1);

        Assert.Equal(single.SimulationTime, batched.SimulationTime, precision: 10);
        Assert.Equal(single.AgentPositionX, batched.AgentPositionX);
        Assert.Equal(single.AgentPositionY, batched.AgentPositionY);
        Assert.Equal(single.AgentVelocityX, batched.AgentVelocityX);
        Assert.Equal(single.AgentVelocityY, batched.AgentVelocityY);
    }

    [Fact]
    public void GpuTelemetryDrivesStatisticsCohortsAndCompletion()
    {
        var collector = new SimulationStatsCollector();
        collector.Reset(197);
        collector.SampleTelemetry(new GpuSimulationTelemetry
        {
            SimulationTime = 1,
            ActiveDots = 59,
            ActiveAgents = 176,
            EvacuatedAgents = 21,
            MeanSpeed = 1.2f,
            SpeedStdDev = 0.2f,
            MeanDensity = 2,
            DensityStdDev = 0.3f,
            PeakDensity = 3
        });
        Assert.Equal(176, collector.Stats.ActiveAgents);
        Assert.Equal(21, collector.Stats.EvacuatedAgents);
        Assert.True(collector.Stats.Cohorts[0].IsCompleted);
        Assert.False(collector.Stats.Cohorts[1].IsCompleted);
        Assert.False(collector.Stats.IsComplete);
        Assert.Equal(1.2f, collector.Stats.CurrentMeanSpeed);
        Assert.Single(collector.Stats.SpeedHistory);

        collector.SampleTelemetry(new GpuSimulationTelemetry { SimulationTime = 2, EvacuatedAgents = 197 });
        Assert.True(collector.Stats.IsComplete);
        Assert.All(collector.Stats.Cohorts, c => Assert.True(c.IsCompleted));
        Assert.Equal(0, collector.Stats.SpeedHistory[^1].MeanSpeed);
        collector.SampleTelemetry(new GpuSimulationTelemetry { SimulationTime = 3, EvacuatedAgents = 197 });
        Assert.Equal(2, collector.Stats.SpeedHistory.Count);
        Assert.Equal(2, collector.Stats.SimulationTime);
    }
}
