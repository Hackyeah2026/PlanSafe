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

    [JsonPropertyName("granulation")]
    public int Granulation { get; set; } = 1;

    [JsonPropertyName("socialRepulsionWeight")]
    public double SocialRepulsionWeight { get; set; } = 4.5;

    [JsonPropertyName("whiskerLength")]
    public double WhiskerLength { get; set; } = 2.5;

    [JsonPropertyName("showWhiskers")]
    public bool ShowWhiskers { get; set; } = false;

    [JsonPropertyName("renderMode")]
    public string RenderMode { get; set; } = "agents";

    [JsonPropertyName("renderFps")]
    public string RenderFps { get; set; } = "30";

    [JsonPropertyName("weightDistance")]
    public double WeightDistance { get; set; } = 0.5;

    [JsonPropertyName("weightOccupancy")]
    public double WeightOccupancy { get; set; } = 0.5;

    [JsonPropertyName("unlimited")]
    public bool Unlimited { get; set; } = false;

    [JsonPropertyName("useGusCensus")]
    public bool UseGusCensus { get; set; } = true;

    public SimulationConfig Clone()
    {
        return new SimulationConfig
        {
            AgentCount = AgentCount,
            CellSize = CellSize,
            AgentRadius = AgentRadius,
            EvacuationSpeed = EvacuationSpeed,
            DynamicDensityPenalty = DynamicDensityPenalty,
            TimeScale = TimeScale,
            Granulation = Granulation,
            SocialRepulsionWeight = SocialRepulsionWeight,
            WhiskerLength = WhiskerLength,
            ShowWhiskers = ShowWhiskers,
            RenderMode = RenderMode,
            RenderFps = RenderFps,
            WeightDistance = WeightDistance,
            WeightOccupancy = WeightOccupancy,
            Unlimited = Unlimited,
            UseGusCensus = UseGusCensus
        };
    }
}
