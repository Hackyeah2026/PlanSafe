using System.Collections.Generic;
using System.Text.Json;
namespace PlanSafe.Contracts.Models.Simulation;

public sealed record RenderFrame(double[] PosX, double[] PosY, double[] Vx, double[] Vy, double[] Radius);

public static class WorkerProtocol
{
    // Commands use unseeded initialization and a separate five-lane render layout.
    public const int Version = 2;
    public const int RenderLaneCount = 5;
    public const int MaximumCatchUpSteps = 5;
    public const int MaximumRequestBytes = 16_384;
    public const int MaximumSessionLength = 64;
    public const long MaximumSafeInteger = 9_007_199_254_740_991;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        IncludeFields = true
    };
}

public sealed record WorkerRequest(
    int Version,
    string? Session,
    long Sequence,
    string? Kind,
    int Count,
    uint? Seed, // Must be null; seeded commands are rejected.
    int Steps = 0,
    double SocialRepulsionWeight = 4.5,
    int Granulation = 1,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] double? WorldWidth = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] double? WorldHeight = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] double? TimeScale = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Preset = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ObstacleDto>? Obstacles = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<EvacuationTarget>? Targets = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] double? WeightDistance = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] double? WeightOccupancy = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] SimulationSnapshot? Snapshot = null);

public sealed record WorkerResponse(
    int Version,
    string Session,
    long Sequence,
    long RunGeneration,
    long Tick,
    RenderFrame? Frame = null,
    SimulationSnapshot? Snapshot = null);

/// <summary>Metadata for a worker-local typed render buffer; the values are not JSON payload.</summary>
public sealed record WorkerRenderResponse(
    int Version,
    string Session,
    long Sequence,
    long RunGeneration,
    long Tick,
    int Count,
    long Generation);

/// <summary>Identity returned after a compute-only fixed-tick batch.</summary>
public sealed record SimulationProgress(
    int Version,
    string Session,
    long Sequence,
    long RunGeneration,
    long Tick);

/// <summary>Identity and shape of an explicitly captured worker-local preview.</summary>
public sealed record PreviewMetadata(
    int Version,
    string Session,
    long Sequence,
    long RunGeneration,
    long Tick,
    int Count,
    long StorageGeneration);
