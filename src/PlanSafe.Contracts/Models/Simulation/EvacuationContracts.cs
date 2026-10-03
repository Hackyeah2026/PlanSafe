using System;
using System.Collections.Generic;

namespace PlanSafe.Contracts.Models.Simulation;

/// <summary>
/// Serializable DTO representing a physical obstacle or road blockade.
/// </summary>
public sealed record ObstacleDto(
    double X,
    double Y,
    double Width,
    double Height,
    string? Id = null
);

/// <summary>
/// Represents an evacuation shelter, designated safe point, or exit gate.
/// </summary>
public sealed record EvacuationTarget(
    string Id,
    string Name,
    double X,
    double Y,
    double Width,
    double Height,
    int Capacity,
    int CurrentOccupancy,
    bool IsActive = true,
    double? Latitude = null,
    double? Longitude = null
)
{
    public double CenterX => X + (Width / 2.0);
    public double CenterY => Y + (Height / 2.0);
    public double OccupancyRatio => Capacity > 0 ? (double)CurrentOccupancy / Capacity : 1.0;
    public bool IsFull => CurrentOccupancy >= Capacity;
    public int AvailableCapacity => Math.Max(0, Capacity - CurrentOccupancy);
}

/// <summary>
/// Immutable snapshot representing the simulation engine state at a specific point in time.
/// </summary>
public sealed record SimulationSnapshot(
    string SessionId,
    double Timestamp,
    double WorldWidth,
    double WorldHeight,
    IReadOnlyList<ObstacleDto> Obstacles,
    IReadOnlyList<EvacuationTarget> Targets,
    double WeightDistance = 0.5,
    double WeightOccupancy = 0.5,
    int ActiveAgentCount = 0,
    double[]? AgentPositionsX = null,
    double[]? AgentPositionsY = null,
    float[]? SmoothedDensities = null
);

/// <summary>
/// Breakdown of the mathematical and simulation cost calculated for a candidate evacuation gathering point.
/// </summary>
public sealed record TargetEvaluationDto(
    string TargetId,
    string TargetName,
    double WalkableDistance,
    double NormalizedDistance,
    double OccupancyRatio,
    double DistanceCost,
    double OccupancyCost,
    double TotalCost,
    bool IsFull,
    bool IsSelected
);

/// <summary>
/// Response payload containing the optimal assigned evacuation target, predicted metrics, and citizen instructions.
/// </summary>
public sealed record TargetAssignmentResponse(
    EvacuationTarget? Target,
    double Distance,
    double OccupancyRatio,
    string Instructions,
    double CalculatedCost = 0.0,
    double? BearingDegrees = null,
    double? FlowDirectionX = null,
    double? FlowDirectionY = null,
    bool IsSimulationEngineBased = true,
    IReadOnlyList<TargetEvaluationDto>? TargetEvaluations = null
);

/// <summary>
/// Request parameters for evaluating and assigning an optimal evacuation target.
/// </summary>
public sealed record TargetAssignmentRequest(
    double X,
    double Y,
    double WeightDistance = 1.0,
    double WeightOccupancy = 0.0,
    double? MaxDistance = null,
    IReadOnlyList<EvacuationTarget>? CustomTargets = null,
    SimulationSnapshot? CustomSnapshot = null
);

/// <summary>
/// Request payload sent by the emergency planner to publish an approved evacuation plan.
/// </summary>
public sealed record PublishPlanRequest(
    IReadOnlyList<EvacuationTarget> Targets,
    IReadOnlyList<ObstacleDto>? Obstacles = null,
    string? BaseUrl = null,
    double WeightDistance = 0.5,
    double WeightOccupancy = 0.5,
    double WorldWidth = 200.0,
    double WorldHeight = 200.0,
    SimulationSnapshot? Snapshot = null
);

/// <summary>
/// Response payload containing the generated evacuation session link and SVG QR code.
/// </summary>
public sealed record PublishPlanResponse(
    string SessionId,
    string EvacuateUrl,
    string QrCodeSvg,
    DateTime CreatedAtUtc
);

/// <summary>
/// Request to check-in an evacuee into a designated evacuation target / shelter.
/// </summary>
public sealed record CheckInRequest(
    string TargetId,
    int Count = 1
);

/// <summary>
/// Response payload for shelter check-in.
/// </summary>
public sealed record CheckInResponse(
    bool Success,
    string Message,
    EvacuationTarget? Target = null
);

/// <summary>
/// Request payload to update the current occupancy (agent count) of a gathering point.
/// </summary>
public sealed record TargetOccupancyUpdateRequest(int Occupancy);

/// <summary>WGS84 position in degrees.</summary>
public sealed record MapGeoCoordinate(double Lat, double Lng);

/// <summary>Exit placed on the nearest walkable street; X/Y are world meters.</summary>
public sealed record ScenarioExit(string Name, double Lat, double Lng, double X, double Y, double Radius, double SnapDistanceMeters);
