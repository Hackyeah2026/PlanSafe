using System.Text.Json.Serialization;

namespace PlanSafe.Contracts.Models.Session;

/// <summary>
/// Simulation and evacuation execution settings tied to a specific map session.
/// Passed directly to the crowd simulation engine when testing scenarios.
/// </summary>
public class SimulationConfig
{
    [JsonPropertyName("agentCount")]
    public int AgentCount { get; set; } = 400;

    [JsonPropertyName("cellSize")]
    public float CellSize { get; set; } = 0.5f;

    [JsonPropertyName("agentRadius")]
    public float AgentRadius { get; set; } = 0.20f;

    [JsonPropertyName("evacuationSpeed")]
    public float EvacuationSpeed { get; set; } = 1.34f;

    [JsonPropertyName("dynamicDensityPenalty")]
    public bool DynamicDensityPenalty { get; set; } = true;

    [JsonPropertyName("timeScale")]
    public float TimeScale { get; set; } = 1.0f;

    public SimulationConfig Clone()
    {
        return new SimulationConfig
        {
            AgentCount = AgentCount,
            CellSize = CellSize,
            AgentRadius = AgentRadius,
            EvacuationSpeed = EvacuationSpeed,
            DynamicDensityPenalty = DynamicDensityPenalty,
            TimeScale = TimeScale
        };
    }
}
