namespace PlanSafe.App.Simulation;

/// <summary>Aggregates reduced on the GPU; no agent arrays cross JS interop.</summary>
public sealed class GpuSimulationTelemetry
{
    public float SimulationTime { get; set; }
    public int ActiveDots { get; set; }
    public int ActiveAgents { get; set; }
    public int EvacuatedAgents { get; set; }
    public float MeanSpeed { get; set; }
    public float SpeedStdDev { get; set; }
    public float MeanDensity { get; set; }
    public float DensityStdDev { get; set; }
    public float PeakDensity { get; set; }
}
