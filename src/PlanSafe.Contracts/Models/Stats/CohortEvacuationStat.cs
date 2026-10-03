namespace PlanSafe.Contracts.Models.Stats;

/// <summary>
/// Represents telemetry and duration metrics for a 10% population cohort evacuation segment.
/// Duration is differential / incremental (non-cumulative: Delta T = T_k - T_{k-1}).
/// </summary>
public class CohortEvacuationStat
{
    public int CohortIndex { get; set; }
    public string CohortLabel { get; set; } = string.Empty;
    public int PercentFrom { get; set; }
    public int PercentTo { get; set; }
    public int AgentThreshold { get; set; }

    /// <summary>
    /// True when this cohort's target threshold has been evacuated.
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// Cumulative simulation time (seconds) at the exact moment this cohort completed evacuation.
    /// </summary>
    public float CompletedTimeSeconds { get; set; }

    /// <summary>
    /// Incremental evacuation time (seconds) for this cohort specifically: Delta T = T_k - T_{k-1}.
    /// </summary>
    public float DurationSeconds { get; set; }

    /// <summary>
    /// Live elapsed seconds for the currently in-progress cohort (CurrentTime - T_{k-1}).
    /// </summary>
    public float CurrentElapsedSeconds { get; set; }

    /// <summary>
    /// Display duration in minutes for chart Y-axis.
    /// </summary>
    public float DisplayMinutes => (IsCompleted ? DurationSeconds : CurrentElapsedSeconds) / 60.0f;

    /// <summary>
    /// Human-friendly formatted duration, matching reference e.g. "45s", "1m 15s", "2m 00s".
    /// </summary>
    public string FormattedDuration
    {
        get
        {
            float totalSec = IsCompleted ? DurationSeconds : CurrentElapsedSeconds;
            if (totalSec <= 0f && !IsCompleted) return "-";

            int totalRoundSec = (int)Math.Round(totalSec);
            int minutes = totalRoundSec / 60;
            int seconds = totalRoundSec % 60;

            if (minutes == 0)
            {
                return $"{seconds}s";
            }
            return $"{minutes}m {seconds:D2}s";
        }
    }
}
