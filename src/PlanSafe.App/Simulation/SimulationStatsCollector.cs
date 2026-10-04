using PlanSafe.Contracts.Models.Stats;

namespace PlanSafe.App.Simulation;

/// <summary>
/// High-efficiency telemetry recorder for crowd evacuation simulations.
/// Collects non-cumulative cohort evacuation times, mean velocity time-series,
/// and discrete crowd density distributions (mean mu and std dev sigma) with zero physics interference.
/// </summary>
public class SimulationStatsCollector
{
    public SimulationLiveStats Stats { get; } = new();

    private float _lastSampleTime = -1f;
    private float _sampleInterval = 0.5f; // Sample telemetry every 0.5s of simulation time
    private int _currentCohortIndex = 0;
    private float _lastCohortCompletedTime = 0f;

    public void Reset(int totalAgents)
    {
        Stats.TotalAgents = totalAgents;
        Stats.ActiveAgents = totalAgents;
        Stats.EvacuatedAgents = 0;
        Stats.SimulationTime = 0f;
        Stats.CurrentMeanSpeed = 0f;
        Stats.CurrentMeanDensity = 0f;
        Stats.CurrentPeakDensity = 0f;

        Stats.Cohorts.Clear();
        Stats.SpeedHistory.Clear();
        Stats.DensityHistory.Clear();

        _currentCohortIndex = 0;
        _lastCohortCompletedTime = 0f;
        _lastSampleTime = -1f;

        // Initialize 10 distinct population cohorts (0-10%, 10-20%, ..., 90-100%)
        for (int i = 0; i < 10; i++)
        {
            int pFrom = i * 10;
            int pTo = (i + 1) * 10;
            int targetThreshold = Math.Max(1, (totalAgents * pTo + 99) / 100);

            Stats.Cohorts.Add(new CohortEvacuationStat
            {
                CohortIndex = i,
                CohortLabel = $"{pFrom}–{pTo}%",
                PercentFrom = pFrom,
                PercentTo = pTo,
                AgentThreshold = targetThreshold,
                IsCompleted = false,
                CompletedTimeSeconds = 0f,
                DurationSeconds = 0f,
                CurrentElapsedSeconds = 0f
            });
        }
    }

    /// <summary>
    /// Invoked whenever an agent crosses an exit cell.
    /// Tracks milestones to lock in differential / incremental cohort durations: Delta T = T_k - T_{k-1}.
    /// </summary>
    public void RecordAgentEvacuated(float simulationTime)
    {
        Stats.EvacuatedAgents++;

        while (_currentCohortIndex < Stats.Cohorts.Count)
        {
            var cohort = Stats.Cohorts[_currentCohortIndex];
            if (Stats.EvacuatedAgents >= cohort.AgentThreshold)
            {
                cohort.IsCompleted = true;
                cohort.CompletedTimeSeconds = simulationTime;
                cohort.DurationSeconds = Math.Max(0.01f, simulationTime - _lastCohortCompletedTime);
                cohort.CurrentElapsedSeconds = cohort.DurationSeconds;

                _lastCohortCompletedTime = simulationTime;
                _currentCohortIndex++;
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// Periodically samples simulation telemetry (speed and density distribution)
    /// without imposing overhead on the high-frequency physics tick.
    /// </summary>
    public void SampleTelemetry(CrowdSimulator sim)
    {
        Stats.SimulationTime = sim.SimulationTime;
        Stats.ActiveAgents = sim.ActiveAgents;
        Stats.CurrentPeakDensity = sim.PeakDensity;
        Stats.CurrentMeanSpeed = sim.MeanSpeed;

        // Update live elapsed time of currently in-progress cohort
        if (_currentCohortIndex < Stats.Cohorts.Count)
        {
            Stats.Cohorts[_currentCohortIndex].CurrentElapsedSeconds =
                Math.Max(0f, sim.SimulationTime - _lastCohortCompletedTime);
        }

        // Throttle telemetry sampling interval
        if (_lastSampleTime >= 0f && (sim.SimulationTime - _lastSampleTime) < _sampleInterval)
        {
            return;
        }

        _lastSampleTime = sim.SimulationTime;

        // Sample speed and density distribution (mu and sigma) across active agents
        int count = sim.CurrentBuffer.Count;
        float sumDensity = 0f;
        float sumSqDensity = 0f;
        float sumSpeed = 0f;
        float sumSqSpeed = 0f;
        int n = 0;

        for (int i = 0; i < count; i++)
        {
            if (sim.CurrentBuffer.Active[i] == 0) continue;

            float spd = sim.CurrentBuffer.Speed[i];
            sumSpeed += spd;
            sumSqSpeed += spd * spd;

            float x = sim.CurrentBuffer.PosX[i];
            float y = sim.CurrentBuffer.PosY[i];

            int c = Math.Clamp((int)(x / sim.Grid.CellSize), 0, sim.Grid.Cols - 1);
            int r = Math.Clamp((int)(y / sim.Grid.CellSize), 0, sim.Grid.Rows - 1);

            float d = sim.Grid.DensityGrid[sim.Grid.GetIndex(c, r)];
            sumDensity += d;
            sumSqDensity += d * d;
            n++;
        }

        float meanSpeed = n > 0 ? (sumSpeed / n) : 0f;
        float varSpeed = n > 0 ? Math.Max(0f, (sumSqSpeed / n) - (meanSpeed * meanSpeed)) : 0f;
        float stdDevSpeed = (float)Math.Sqrt(varSpeed);

        float meanDensity = n > 0 ? (sumDensity / n) : 0f;
        float variance = n > 0 ? Math.Max(0f, (sumSqDensity / n) - (meanDensity * meanDensity)) : 0f;
        float stdDev = (float)Math.Sqrt(variance);

        Stats.CurrentMeanSpeed = meanSpeed;
        Stats.CurrentMeanDensity = meanDensity;

        Stats.SpeedHistory.Add(new SpeedTimeSeriesPoint(sim.SimulationTime, meanSpeed, stdDevSpeed));
        Stats.DensityHistory.Add(new DensityTimeSeriesPoint(sim.SimulationTime, meanDensity, stdDev));
    }

    /// <summary>
    /// Periodically samples simulation telemetry from CrowdSimulationEngine.
    /// </summary>
    public void SampleTelemetry(CrowdSimulationEngine engine)
    {
        // If simulation was already completed and terminal point added, do not record further
        if (Stats.IsComplete && Stats.ActiveAgents == 0 && Stats.SpeedHistory.Count > 0 && Stats.SpeedHistory[^1].MeanSpeed <= 0.001f)
        {
            return;
        }

        float simTime = (float)engine.SimulationTime;
        int activeDots = engine.ActiveAgentCount;
        int active = (activeDots == 0) ? 0 : Math.Max(0, engine.AgentCount - engine.EvacuatedCount);
        Stats.SimulationTime = simTime;
        Stats.ActiveAgents = active;
        Stats.EvacuatedAgents = (activeDots == 0) ? Stats.TotalAgents : engine.EvacuatedCount;

        if (activeDots == 0)
        {
            Stats.CurrentPeakDensity = 0f;
            Stats.CurrentMeanSpeed = 0f;
            Stats.CurrentMeanDensity = 0f;

            // When evacuation finishes, lock in any remaining cohorts
            while (_currentCohortIndex < Stats.Cohorts.Count)
            {
                var cohort = Stats.Cohorts[_currentCohortIndex];
                cohort.IsCompleted = true;
                cohort.CompletedTimeSeconds = simTime;
                cohort.DurationSeconds = Math.Max(0.01f, simTime - _lastCohortCompletedTime);
                cohort.CurrentElapsedSeconds = cohort.DurationSeconds;
                _lastCohortCompletedTime = simTime;
                _currentCohortIndex++;
            }

            // Only add a single terminal point at 0 if the previous point was not zero
            if (Stats.SpeedHistory.Count > 0 && Stats.SpeedHistory[^1].MeanSpeed > 0.001f)
            {
                Stats.SpeedHistory.Add(new SpeedTimeSeriesPoint(simTime, 0f, 0f));
                Stats.DensityHistory.Add(new DensityTimeSeriesPoint(simTime, 0f, 0f));
            }
            return;
        }

        // Check cohort completions
        while (_currentCohortIndex < Stats.Cohorts.Count)
        {
            var cohort = Stats.Cohorts[_currentCohortIndex];
            if (Stats.EvacuatedAgents >= cohort.AgentThreshold)
            {
                cohort.IsCompleted = true;
                cohort.CompletedTimeSeconds = simTime;
                cohort.DurationSeconds = Math.Max(0.01f, simTime - _lastCohortCompletedTime);
                cohort.CurrentElapsedSeconds = cohort.DurationSeconds;

                _lastCohortCompletedTime = simTime;
                _currentCohortIndex++;
            }
            else
            {
                break;
            }
        }

        if (_currentCohortIndex < Stats.Cohorts.Count)
        {
            Stats.Cohorts[_currentCohortIndex].CurrentElapsedSeconds =
                Math.Max(0f, simTime - _lastCohortCompletedTime);
        }

        // Throttle telemetry sampling interval
        if (_lastSampleTime >= 0f && (simTime - _lastSampleTime) < _sampleInterval)
        {
            return;
        }

        _lastSampleTime = simTime;

        float sumDensity = 0f;
        float sumSqDensity = 0f;
        float sumSpeed = 0f;
        float sumSqSpeed = 0f;
        float peakDensity = 0f;
        int n = 0;

        for (int i = 0; i < activeDots; i++)
        {
            double vx = engine.AgentVelocityX[i];
            double vy = engine.AgentVelocityY[i];
            float spd = (float)Math.Sqrt(vx * vx + vy * vy);
            float d = (float)engine.AgentLocalDensity[i];

            sumSpeed += spd;
            sumSqSpeed += spd * spd;
            sumDensity += d;
            sumSqDensity += d * d;
            if (d > peakDensity) peakDensity = d;
            n++;
        }

        float meanSpeed = n > 0 ? (sumSpeed / n) : 0f;
        float varSpeed = n > 0 ? Math.Max(0f, (sumSqSpeed / n) - (meanSpeed * meanSpeed)) : 0f;
        float stdDevSpeed = (float)Math.Sqrt(varSpeed);

        float meanDensity = n > 0 ? (sumDensity / n) : 0f;
        float variance = n > 0 ? Math.Max(0f, (sumSqDensity / n) - (meanDensity * meanDensity)) : 0f;
        float stdDev = (float)Math.Sqrt(variance);

        Stats.CurrentPeakDensity = peakDensity;
        Stats.CurrentMeanSpeed = meanSpeed;
        Stats.CurrentMeanDensity = meanDensity;

        Stats.SpeedHistory.Add(new SpeedTimeSeriesPoint(simTime, meanSpeed, stdDevSpeed));
        Stats.DensityHistory.Add(new DensityTimeSeriesPoint(simTime, meanDensity, stdDev));
    }

    /// <summary>Samples the active GPU simulation without running a second CPU simulation.</summary>
    public void SampleTelemetry(GpuSimulationTelemetry sample)
    {
        if (Stats.IsComplete && Stats.ActiveAgents == 0 && Stats.SpeedHistory.Count > 0 && Stats.SpeedHistory[^1].MeanSpeed <= 0.001f)
        {
            return;
        }

        float simTime = sample.SimulationTime;
        Stats.SimulationTime = simTime;
        Stats.ActiveAgents = sample.ActiveAgents;
        Stats.EvacuatedAgents = sample.EvacuatedAgents;
        Stats.CurrentMeanSpeed = sample.MeanSpeed;
        Stats.CurrentMeanDensity = sample.MeanDensity;
        Stats.CurrentPeakDensity = sample.PeakDensity;

        while (_currentCohortIndex < Stats.Cohorts.Count)
        {
            var cohort = Stats.Cohorts[_currentCohortIndex];
            if (Stats.EvacuatedAgents < cohort.AgentThreshold) break;
            cohort.IsCompleted = true;
            cohort.CompletedTimeSeconds = simTime;
            cohort.DurationSeconds = Math.Max(0.01f, simTime - _lastCohortCompletedTime);
            cohort.CurrentElapsedSeconds = cohort.DurationSeconds;
            _lastCohortCompletedTime = simTime;
            _currentCohortIndex++;
        }
        if (_currentCohortIndex < Stats.Cohorts.Count)
        {
            Stats.Cohorts[_currentCohortIndex].CurrentElapsedSeconds = Math.Max(0f, simTime - _lastCohortCompletedTime);
        }

        if (sample.ActiveDots == 0)
        {
            if (Stats.SpeedHistory.Count > 0 && Stats.SpeedHistory[^1].MeanSpeed > 0.001f)
            {
                Stats.SpeedHistory.Add(new SpeedTimeSeriesPoint(simTime, 0f, 0f));
                Stats.DensityHistory.Add(new DensityTimeSeriesPoint(simTime, 0f, 0f));
            }
            return;
        }
        if (_lastSampleTime >= 0f && simTime - _lastSampleTime < _sampleInterval) return;
        _lastSampleTime = simTime;
        Stats.SpeedHistory.Add(new SpeedTimeSeriesPoint(simTime, sample.MeanSpeed, sample.SpeedStdDev));
        Stats.DensityHistory.Add(new DensityTimeSeriesPoint(simTime, sample.MeanDensity, sample.DensityStdDev));
    }

    /// <summary>
    /// Creates realistic sample data representing the charts in the reference screenshots.
    /// Used for the Map view prior to full simulation integration.
    /// </summary>
    public static SimulationLiveStats CreateSampleData()
    {
        var stats = new SimulationLiveStats
        {
            TotalAgents = 1000,
            ActiveAgents = 0,
            EvacuatedAgents = 1000,
            SimulationTime = 600f,
            CurrentMeanSpeed = 1.34f,
            CurrentMeanDensity = 0.15f,
            CurrentPeakDensity = 3.65f
        };

        // Screenshot 1 Cohort times:
        // 0-10%: 45s
        // 10-20%: 30s
        // 20-30%: 35s
        // 30-40%: 40s
        // 40-50%: 45s
        // 50-60%: 55s
        // 60-70%: 1m 05s (65s)
        // 70-80%: 1m 15s (75s)
        // 80-90%: 1m 30s (90s)
        // 90-100%: 2m 00s (120s)
        float[] cohortDurations = { 45f, 30f, 35f, 40f, 45f, 55f, 65f, 75f, 90f, 120f };
        float cumulative = 0f;

        for (int i = 0; i < 10; i++)
        {
            int pFrom = i * 10;
            int pTo = (i + 1) * 10;
            float dur = cohortDurations[i];
            cumulative += dur;

            stats.Cohorts.Add(new CohortEvacuationStat
            {
                CohortIndex = i,
                CohortLabel = $"{pFrom}–{pTo}%",
                PercentFrom = pFrom,
                PercentTo = pTo,
                AgentThreshold = (i + 1) * 100,
                IsCompleted = true,
                CompletedTimeSeconds = cumulative,
                DurationSeconds = dur,
                CurrentElapsedSeconds = dur
            });
        }

        // Screenshot 2 Density profile & corresponding Speed profile over 600s:
        // Key phases:
        // Faza 1 (0-120s): Wypelnianie korytarzy (naplyw z pomieszczen) -> density rises 0.4 -> 2.6
        // Faza 2 (120-240s): Szczyt zatorow w gardlach -> peak density ~2.65, sigma ~0.55
        // Faza 3 (240-600s): Drenaz i rozrzedzenie -> density drops smoothly to < 0.5
        for (int t = 0; t <= 600; t += 10)
        {
            float time = t;
            float meanD;
            float sigma;
            float speed;

            if (time <= 60f)
            {
                float prog = time / 60f;
                meanD = 0.4f + prog * 1.0f; // 0.4 -> 1.4
                sigma = 0.15f + prog * 0.25f;
                speed = 1.30f - prog * 0.45f;
            }
            else if (time <= 180f)
            {
                float prog = (time - 60f) / 120f;
                meanD = 1.4f + prog * 1.25f; // 1.4 -> 2.65
                sigma = 0.40f + prog * 0.20f; // 0.4 -> 0.60
                speed = 0.85f - prog * 0.50f; // 0.85 -> 0.35 m/s
            }
            else if (time <= 240f)
            {
                float prog = (time - 180f) / 60f;
                meanD = 2.65f - prog * 0.45f; // 2.65 -> 2.2
                sigma = 0.60f - prog * 0.05f;
                speed = 0.35f + prog * 0.25f; // 0.35 -> 0.60
            }
            else if (time <= 360f)
            {
                float prog = (time - 240f) / 120f;
                meanD = 2.2f - prog * 1.4f; // 2.2 -> 0.8
                sigma = 0.55f - prog * 0.25f; // 0.55 -> 0.30
                speed = 0.60f + prog * 0.45f; // 0.60 -> 1.05
            }
            else
            {
                float prog = (time - 360f) / 240f;
                meanD = 0.8f - prog * 0.65f; // 0.8 -> 0.15
                sigma = 0.30f - prog * 0.22f; // 0.30 -> 0.08
                speed = 1.05f + prog * 0.29f; // 1.05 -> 1.34
            }

            float speedSigma = Math.Clamp(0.12f + (1.35f - speed) * 0.12f, 0.06f, 0.28f);
            stats.DensityHistory.Add(new DensityTimeSeriesPoint(time, meanD, sigma));
            stats.SpeedHistory.Add(new SpeedTimeSeriesPoint(time, speed, speedSigma));
        }

        return stats;
    }
}
