namespace PlanSafe.Contracts.Models.Stats;

/// <summary>
/// Telemetry data point capturing mean crowd density and standard deviation bands (mu +- sigma, mu +- 2*sigma)
/// along evacuation routes at a specific simulation timestamp.
/// </summary>
public record DensityTimeSeriesPoint(
    float TimeSeconds,
    float MeanDensity,
    float StdDevDensity
)
{
    public float Plus1Sigma => MeanDensity + StdDevDensity;
    public float Minus1Sigma => Math.Max(0f, MeanDensity - StdDevDensity);
    public float Plus2Sigma => MeanDensity + 2.0f * StdDevDensity;
    public float Minus2Sigma => Math.Max(0f, MeanDensity - 2.0f * StdDevDensity);
}
