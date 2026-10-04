using System;
using System.Collections.Generic;
using PlanSafe.Contracts.Models.Simulation;

namespace PlanSafe.Api.Services;

/// <summary>
/// Operational state management service for evacuation planning, shelter occupancies,
/// roadblocks, citizen target assignments, and session lifecycles.
/// </summary>
public interface IEvacuationStateService : IDisposable
{
    string? CurrentSessionId { get; }
    DateTime LastActivityUtc { get; }
    DateTime? ExpiresAtUtc { get; }

    PublishPlanResponse PublishPlan(PublishPlanRequest request);
    TargetAssignmentResponse AssignTarget(TargetAssignmentRequest request);
    CheckInResponse CheckIn(CheckInRequest request);
    IReadOnlyList<EvacuationTarget> GetTargets();
    EvacuationPlanConfig? GetConfig();
    IReadOnlyList<ObstacleDto> GetObstacles();
    bool UpdateTargetOccupancy(string targetId, int occupancy);
    void Reset();
    bool CleanIfInactive();
}
