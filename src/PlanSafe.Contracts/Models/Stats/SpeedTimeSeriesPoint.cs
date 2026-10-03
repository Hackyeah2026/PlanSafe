namespace PlanSafe.Contracts.Models.Stats;

/// <summary>
/// Telemetry data point capturing mean agent velocity and standard deviation bands (mu +- sigma, mu +- 2*sigma)
/// across active agents at a specific simulation timestamp.
/// </summary>
public record SpeedTimeSeriesPoint(
    float TimeSeconds,
    float MeanSpeed,
    float StdDevSpeed = 0f
)
{
    public float Plus1Sigma => MeanSpeed + StdDevSpeed;
    public float Minus1Sigma => Math.Max(0f, MeanSpeed - StdDevSpeed);
    public float Plus2Sigma => MeanSpeed + 2.0f * StdDevSpeed;
    public float Minus2Sigma => Math.Max(0f, MeanSpeed - 2.0f * StdDevSpeed);
}
