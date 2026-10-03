using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Models.Stats;
using Xunit;

namespace PlanSafe.Tests;

public class SimulationStatsTests
{
    [Fact]
    public void TestCohortCalculations_IncrementalNonCumulative()
    {
        var collector = new SimulationStatsCollector();
        collector.Reset(100); // 100 agents, each cohort milestone is 10 agents

        Assert.Equal(10, collector.Stats.Cohorts.Count);
        Assert.Equal(10, collector.Stats.Cohorts[0].AgentThreshold);
        Assert.Equal(20, collector.Stats.Cohorts[1].AgentThreshold);

        // Evacuate 10 agents at t = 45s
        for (int i = 0; i < 10; i++)
        {
            collector.RecordAgentEvacuated(45f);
        }

        var cohort0 = collector.Stats.Cohorts[0];
        Assert.True(cohort0.IsCompleted);
        Assert.Equal(45f, cohort0.CompletedTimeSeconds);
        Assert.Equal(45f, cohort0.DurationSeconds);
        Assert.Equal("45s", cohort0.FormattedDuration);

        // Evacuate next 10 agents at t = 75s (delta should be 75 - 45 = 30s)
        for (int i = 0; i < 10; i++)
        {
            collector.RecordAgentEvacuated(75f);
        }

        var cohort1 = collector.Stats.Cohorts[1];
        Assert.True(cohort1.IsCompleted);
        Assert.Equal(75f, cohort1.CompletedTimeSeconds);
        Assert.Equal(30f, cohort1.DurationSeconds); // Non-cumulative differential delta!
        Assert.Equal("30s", cohort1.FormattedDuration);

        // Evacuate next 10 agents at t = 110s (delta should be 110 - 75 = 35s)
        for (int i = 0; i < 10; i++)
        {
            collector.RecordAgentEvacuated(110f);
        }

        var cohort2 = collector.Stats.Cohorts[2];
        Assert.True(cohort2.IsCompleted);
        Assert.Equal(110f, cohort2.CompletedTimeSeconds);
        Assert.Equal(35f, cohort2.DurationSeconds);
        Assert.Equal("35s", cohort2.FormattedDuration);
    }

    [Fact]
    public void TestCohortFormatting_MinutesAndSeconds()
    {
        var c1 = new CohortEvacuationStat { IsCompleted = true, DurationSeconds = 45f };
        Assert.Equal("45s", c1.FormattedDuration);
        Assert.Equal(0.75f, c1.DisplayMinutes);

        var c2 = new CohortEvacuationStat { IsCompleted = true, DurationSeconds = 75f };
        Assert.Equal("1m 15s", c2.FormattedDuration);
        Assert.Equal(1.25f, c2.DisplayMinutes);

        var c3 = new CohortEvacuationStat { IsCompleted = true, DurationSeconds = 120f };
        Assert.Equal("2m 00s", c3.FormattedDuration);
        Assert.Equal(2.0f, c3.DisplayMinutes);
    }

    [Fact]
    public void TestSampleDataGenerator_Integrity()
    {
        var sample = SimulationStatsCollector.CreateSampleData();

        Assert.NotNull(sample);
        Assert.Equal(10, sample.Cohorts.Count);
        Assert.All(sample.Cohorts, c => Assert.True(c.IsCompleted));

        // Verify values matching Screenshot 1:
        // 0-10%: 45s, 10-20%: 30s, 20-30%: 35s, 30-40%: 40s, 40-50%: 45s,
        // 50-60%: 55s, 60-70%: 1m 05s, 70-80%: 1m 15s, 80-90%: 1m 30s, 90-100%: 2m 00s
        float[] expectedDurations = { 45f, 30f, 35f, 40f, 45f, 55f, 65f, 75f, 90f, 120f };
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(expectedDurations[i], sample.Cohorts[i].DurationSeconds);
        }

        Assert.Equal("45s", sample.Cohorts[0].FormattedDuration);
        Assert.Equal("30s", sample.Cohorts[1].FormattedDuration);
        Assert.Equal("1m 05s", sample.Cohorts[6].FormattedDuration);
        Assert.Equal("1m 15s", sample.Cohorts[7].FormattedDuration);
        Assert.Equal("1m 30s", sample.Cohorts[8].FormattedDuration);
        Assert.Equal("2m 00s", sample.Cohorts[9].FormattedDuration);

        // Verify Density & Speed curves over 600s
        Assert.NotEmpty(sample.DensityHistory);
        Assert.NotEmpty(sample.SpeedHistory);
        Assert.Equal(0f, sample.DensityHistory[0].TimeSeconds);
        Assert.Equal(600f, sample.DensityHistory[^1].TimeSeconds);

        // Verify bands mu +- sigma and mu +- 2*sigma
        foreach (var p in sample.DensityHistory)
        {
            Assert.True(p.Plus2Sigma >= p.Plus1Sigma);
            Assert.True(p.Plus1Sigma >= p.MeanDensity);
            Assert.True(p.MeanDensity >= p.Minus1Sigma);
            Assert.True(p.Minus1Sigma >= p.Minus2Sigma);
            Assert.True(p.Minus2Sigma >= 0f);
        }
    }

    [Fact]
    public void TestCrowdSimulatorIntegration_RecordsTelemetry()
    {
        var grid = new SimulationGrid(10f, 10f, 0.5f);
        grid.SetCell(9, 9, CellType.Exit);
        grid.ComputeDijkstraField();

        var sim = new CrowdSimulator(grid, maxAgents: 100);
        var collector = new SimulationStatsCollector();
        collector.Reset(20);
        sim.StatsCollector = collector;

        // Spawn 20 agents
        for (int i = 0; i < 20; i++)
        {
            sim.SpawnAgent(5f, 5f);
        }

        // Advance simulation for 2 seconds (100 steps at dt = 0.02)
        for (int step = 0; step < 100; step++)
        {
            sim.Step(0.02f);
        }

        Assert.True(sim.SimulationTime > 1.9f);
        Assert.True(collector.Stats.SpeedHistory.Count > 0);
        Assert.True(collector.Stats.DensityHistory.Count > 0);
        Assert.True(collector.Stats.Cohorts.Count == 10);
    }

    [Fact]
    public void TestCrowdSimulationEngineIntegration_RecordsTelemetry_AndEvacuates()
    {
        var engine = new CrowdSimulationEngine(60.0, 40.0, 50);
        var collector = new SimulationStatsCollector();
        collector.Reset(50);

        // Advance simulation for 2 seconds (120 steps at dt = 0.016)
        for (int step = 0; step < 120; step++)
        {
            engine.Step(0.016, 1.0);
            collector.SampleTelemetry(engine);
        }

        Assert.True(engine.SimulationTime >= 1.8);
        Assert.True(collector.Stats.SimulationTime >= 1.8f);
        Assert.True(collector.Stats.SpeedHistory.Count >= 3);
        Assert.True(collector.Stats.DensityHistory.Count >= 3);
        Assert.True(collector.Stats.Cohorts.Count == 10);
        Assert.True(collector.Stats.Cohorts[0].AgentThreshold == 5);

        // Place 10 agents directly inside ExitZone to trigger evacuation
        for (int i = 0; i < 10; i++)
        {
            engine.AgentPositionX[i] = engine.ExitZone.X + 1.0;
            engine.AgentPositionY[i] = engine.ExitZone.Y + 1.0;
        }

        // Update physics and sample telemetry
        engine.UpdatePhysics(0.05);
        collector.SampleTelemetry(engine);

        Assert.True(engine.EvacuatedCount >= 10, $"Expected >= 10 evacuated, got {engine.EvacuatedCount}");
        Assert.True(collector.Stats.EvacuatedAgents >= 10);
        Assert.True(collector.Stats.Cohorts[0].IsCompleted, "Cohort 0 (0-10%) should be completed");
        Assert.True(collector.Stats.Cohorts[0].DurationSeconds > 0f);
        Assert.True(collector.Stats.Cohorts[1].IsCompleted, "Cohort 1 (10-20%) should also be completed");
    }
}
