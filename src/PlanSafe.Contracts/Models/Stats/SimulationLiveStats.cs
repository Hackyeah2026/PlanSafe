namespace PlanSafe.Contracts.Models.Stats;

/// <summary>
/// Aggregated container holding all real-time evacuation telemetry and historical series for visualization.
/// </summary>
public class SimulationLiveStats
{
    public int TotalAgents { get; set; }
    public int ActiveAgents { get; set; }
    public int EvacuatedAgents { get; set; }
    public float SimulationTime { get; set; }
    public float CurrentMeanSpeed { get; set; }
    public float CurrentMeanDensity { get; set; }
    public float CurrentPeakDensity { get; set; }

    public List<CohortEvacuationStat> Cohorts { get; set; } = new();
    public List<SpeedTimeSeriesPoint> SpeedHistory { get; set; } = new();
    public List<DensityTimeSeriesPoint> DensityHistory { get; set; } = new();

    public int EvacuationPercent => TotalAgents > 0
        ? (int)Math.Round((float)EvacuatedAgents / TotalAgents * 100f)
        : 0;

    public bool IsComplete => TotalAgents > 0 && EvacuatedAgents >= TotalAgents;

    public float FormattedSimulationTimeMinutes => SimulationTime / 60.0f;

    /// <summary>
    /// Computes the dynamic maximum Y-axis value and tick marks for population cohort durations.
    /// Uses clean minute steps (0.25, 0.5, or 1.0) with headroom so bars never hit the ceiling.
    /// </summary>
    public (float maxMinutes, float[] ticks) GetCohortYScale()
    {
        float rawMax = 0.4f;
        foreach (var c in Cohorts)
        {
            if (c.DisplayMinutes > rawMax)
            {
                rawMax = c.DisplayMinutes;
            }
        }

        // Add 15% headroom above highest cohort
        float target = Math.Max(0.5f, rawMax * 1.15f);
        float step;
        float maxVal;

        if (target <= 1.0f)
        {
            step = 0.25f;
            maxVal = (float)Math.Ceiling(target / step) * step;
        }
        else if (target <= 2.5f)
        {
            step = 0.5f;
            maxVal = (float)Math.Ceiling(target / step) * step;
        }
        else if (target <= 5.0f)
        {
            step = 1.0f;
            maxVal = (float)Math.Ceiling(target / step) * step;
        }
        else
        {
            step = (float)Math.Ceiling(target / 5.0f);
            maxVal = (float)Math.Ceiling(target / step) * step;
        }

        var list = new List<float>();
        for (float v = 0f; v <= maxVal + 0.001f; v += step)
        {
            list.Add(v);
        }
        return (maxVal, list.ToArray());
    }

    /// <summary>
    /// Computes dynamic maximum speed and clean Y-axis ticks with generous headroom
    /// so that the standard deviation band (mu +- sigma) never clips against the top ceiling.
    /// </summary>
    public (float maxSpeed, float[] ticks) GetSpeedYScale(float minimum = 2.0f)
    {
        float rawMax = minimum;
        foreach (var p in SpeedHistory)
        {
            if (p.Plus1Sigma > rawMax)
            {
                rawMax = p.Plus1Sigma;
            }
        }

        // Add 15% headroom above highest band peak
        float target = rawMax * 1.15f;
        float maxVal;
        float step;

        if (target <= 2.0f)
        {
            maxVal = 2.0f;
            step = 0.4f; // 0.0, 0.4, 0.8, 1.2, 1.6, 2.0 (5 clean steps)
        }
        else if (target <= 2.5f)
        {
            maxVal = 2.5f;
            step = 0.5f; // 0.0, 0.5, 1.0, 1.5, 2.0, 2.5
        }
        else
        {
            step = (float)Math.Ceiling(target / 5.0f * 10f) / 10f;
            maxVal = step * 5f;
        }

        var list = new List<float>();
        for (float v = 0f; v <= maxVal + 0.001f; v += step)
        {
            list.Add(v);
        }
        return (maxVal, list.ToArray());
    }

    /// <summary>
    /// Computes dynamic maximum density (ped/m2) and integer Y-axis ticks
    /// ensuring the critical jam threshold (3.5 ped/m2) is clearly within view.
    /// </summary>
    public (float maxDensity, int[] ticks) GetDensityYScale(float minimum = 5.0f)
    {
        float rawMax = minimum;
        foreach (var p in DensityHistory)
        {
            if (p.Plus1Sigma > rawMax)
            {
                rawMax = p.Plus1Sigma;
            }
        }

        int maxVal = Math.Max(5, (int)Math.Ceiling(rawMax * 1.15f));
        int step = maxVal <= 6 ? 1 : 2;
        if (maxVal % step != 0) maxVal += (step - (maxVal % step));

        var list = new List<int>();
        for (int d = 0; d <= maxVal; d += step)
        {
            list.Add(d);
        }
        return (maxVal, list.ToArray());
    }

    /// <summary>
    /// Computes the dynamic maximum Y-axis value for cohort minutes, with clean step headroom.
    /// </summary>
    public float GetMaxCohortMinutes() => GetCohortYScale().maxMinutes;

    /// <summary>
    /// Computes the dynamic maximum X-axis time in seconds for time-series charts.
    /// </summary>
    public float GetMaxTimeSeconds(float minimum = 60f)
    {
        float maxT = minimum;
        if (SpeedHistory.Count > 0)
        {
            maxT = Math.Max(maxT, SpeedHistory[^1].TimeSeconds);
        }
        if (DensityHistory.Count > 0)
        {
            maxT = Math.Max(maxT, DensityHistory[^1].TimeSeconds);
        }
        // Headroom to nearest 30s or 60s
        return (float)Math.Ceiling(maxT / 30.0f) * 30.0f;
    }

    /// <summary>
    /// Computes the dynamic maximum Y-axis density (ped/m2) with headroom.
    /// </summary>
    public float GetMaxDensity(float minimum = 4.0f) => GetDensityYScale(minimum).maxDensity;
}
