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
    // A non-positive capacity represents an open safe zone with no occupancy limit.
    public bool HasCapacityLimit => Capacity > 0;
    public double OccupancyRatio => Capacity > 0 ? (double)CurrentOccupancy / Capacity : 0.0;
    public bool IsFull => HasCapacityLimit && CurrentOccupancy >= Capacity;
    public int? AvailableCapacity => HasCapacityLimit ? Math.Max(0, Capacity - CurrentOccupancy) : null;
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
    double OccupancyRatio = 0.0,
    string Instructions = "",
    double CalculatedCost = 0.0,
    double? BearingDegrees = null,
    double? FlowDirectionX = null,
    double? FlowDirectionY = null,
    bool IsSimulationEngineBased = true,
    IReadOnlyList<TargetEvaluationDto>? TargetEvaluations = null,
    IReadOnlyList<PlanSafe.Contracts.Models.Map.GeoCoordinate>? RoutePath = null,
    bool IsOutsideZone = false
);

/// <summary>
/// Request parameters for evaluating and assigning an optimal evacuation target.
/// </summary>
public sealed record TargetAssignmentRequest(
    double X,
    double Y,
    double? WeightDistance = null,
    double? WeightOccupancy = null,
    double? MaxDistance = null,
    IReadOnlyList<EvacuationTarget>? CustomTargets = null,
    SimulationSnapshot? CustomSnapshot = null,
    double? Latitude = null,
    double? Longitude = null,
    string? SessionId = null,
    string? CurrentTargetId = null,
    bool IgnoreHysteresis = false,
    bool StrictGeofence = false
);

/// <summary>
/// Simple generic operation outcome message.
/// </summary>
public sealed record GenericActionResult(bool Success, string? Message = null);

/// <summary>
/// Status result of background inactivity cleanup.
/// </summary>
public sealed record CleanupActionResult(bool Cleaned, string? SessionId = null);

/// <summary>
/// Configuration and state representation of an active published evacuation plan session.
/// </summary>
public sealed record EvacuationPlanConfig(
    string SessionId,
    IReadOnlyList<EvacuationTarget> Targets,
    IReadOnlyList<ObstacleDto> Obstacles,
    IReadOnlyList<List<PlanSafe.Contracts.Models.Map.GeoCoordinate>> Roadblocks,
    IReadOnlyList<List<PlanSafe.Contracts.Models.Map.GeoCoordinate>> EvacuationZones,
    double WeightDistance = 0.5,
    double WeightOccupancy = 0.5,
    double? MapCenterLat = null,
    double? MapCenterLng = null,
    int? ZoomLevel = null,
    DateTime CreatedAtUtc = default,
    DateTime? ExpiresAtUtc = null,
    IReadOnlyList<List<PlanSafe.Contracts.Models.Map.GeoCoordinate>>? SafeZones = null
);

/// <summary>
/// Request payload sent by the emergency planner to publish an approved evacuation plan.
/// </summary>
public sealed record PublishPlanRequest(
    IReadOnlyList<EvacuationTarget> Targets,
    IReadOnlyList<ObstacleDto>? Obstacles = null,
    IReadOnlyList<List<PlanSafe.Contracts.Models.Map.GeoCoordinate>>? Roadblocks = null,
    IReadOnlyList<List<PlanSafe.Contracts.Models.Map.GeoCoordinate>>? EvacuationZones = null,
    string? BaseUrl = null,
    double WeightDistance = 0.5,
    double WeightOccupancy = 0.5,
    double WorldWidth = 200.0,
    double WorldHeight = 200.0,
    double? MapCenterLat = null,
    double? MapCenterLng = null,
    int? ZoomLevel = null,
    SimulationSnapshot? Snapshot = null,
    IReadOnlyList<List<PlanSafe.Contracts.Models.Map.GeoCoordinate>>? SafeZones = null
);

/// <summary>
/// Response payload containing the generated evacuation session link and SVG QR code.
/// </summary>
public sealed record PublishPlanResponse(
    string SessionId,
    string EvacuateUrl,
    string QrCodeSvg,
    DateTime CreatedAtUtc,
    DateTime? ExpiresAtUtc = null
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
