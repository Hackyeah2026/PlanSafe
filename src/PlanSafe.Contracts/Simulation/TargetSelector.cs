using System;
using System.Collections.Generic;
using System.Linq;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;

namespace PlanSafe.Contracts.Simulation;

/// <summary>
/// Core evacuation target selection and load balancing algorithm.
/// Evaluates multi-criteria cost function with hard capacity guards,
/// roadblock line segment intersections, geofencing, and anti-flapping hysteresis.
/// </summary>
public static class TargetSelector
{
    public const double DefaultMaxDistance = 1000.0;
    public const double CapacityOverflowPenalty = 1e9;
    public const double RoadblockBlockagePenalty = 5e8;

    /// <summary>
    /// Geographic target selection using real WGS84 coordinates, Haversine distance,
    /// geofencing, roadblock detection, and hysteresis anti-flapping.
    /// </summary>
    public static TargetAssignmentResponse SelectGeoTarget(
        double citizenLat,
        double citizenLng,
        IReadOnlyList<EvacuationTarget> targets,
        double weightDistance = 1.0,
        double weightOccupancy = 0.0,
        double? maxDistanceMeters = null,
        IReadOnlyList<IReadOnlyList<GeoCoordinate>>? roadblocks = null,
        IReadOnlyList<IReadOnlyList<GeoCoordinate>>? evacuationZones = null,
        string? currentTargetId = null,
        bool ignoreHysteresis = false,
        bool strictGeofence = true)
    {
        // 1. Geofence checking: check whether citizen is inside designated evacuation zone (if any defined)
        bool isOutsideZone = false;
        if (evacuationZones != null && evacuationZones.Count > 0)
        {
            bool isInsideAnyZone = false;
            foreach (var zone in evacuationZones)
            {
                if (zone != null && zone.Count >= 3 && GeoMath.IsPointInPolygon(citizenLat, citizenLng, zone))
                {
                    isInsideAnyZone = true;
                    break;
                }
            }

            if (!isInsideAnyZone)
            {
                isOutsideZone = true;
                if (strictGeofence)
                {
                    return new TargetAssignmentResponse(
                        Target: null,
                        Distance: 0.0,
                        OccupancyRatio: 0.0,
                        Instructions: "Your current location is outside the designated evacuation zone.",
                        CalculatedCost: 0.0,
                        IsSimulationEngineBased: false,
                        TargetEvaluations: Array.Empty<TargetEvaluationDto>(),
                        RoutePath: null,
                        IsOutsideZone: true
                    );
                }
            }
        }

        if (targets == null || targets.Count == 0)
        {
            return new TargetAssignmentResponse(
                Target: null,
                Distance: 0.0,
                OccupancyRatio: 0.0,
                Instructions: "No evacuation points available.",
                CalculatedCost: 0.0,
                IsSimulationEngineBased: false,
                TargetEvaluations: Array.Empty<TargetEvaluationDto>());
        }

        var activeTargets = targets.Where(t => t.IsActive).ToList();
        if (activeTargets.Count == 0)
        {
            return new TargetAssignmentResponse(
                Target: null,
                Distance: 0.0,
                OccupancyRatio: 0.0,
                Instructions: "No active evacuation points.",
                CalculatedCost: 0.0,
                IsSimulationEngineBased: false,
                TargetEvaluations: Array.Empty<TargetEvaluationDto>());
        }

        double wDist = Math.Max(0.0, weightDistance);
        double wOcc = Math.Max(0.0, weightOccupancy);

        double dMax = maxDistanceMeters.GetValueOrDefault(0.0);
        if (dMax <= 0.0)
        {
            double calculatedMaxDist = activeTargets.Max(t =>
                GeoMath.CalculateDistanceMeters(citizenLat, citizenLng, t.Latitude ?? t.Y, t.Longitude ?? t.X));
            dMax = Math.Max(100.0, calculatedMaxDist > 0.0 ? calculatedMaxDist : DefaultMaxDistance);
        }

        // Determine if at least one target has available capacity
        bool hasAnyTargetWithCapacity = activeTargets.Any(t => t.CurrentOccupancy < t.Capacity);

        var evaluations = new List<TargetEvaluationDto>();
        var candidateCosts = new Dictionary<string, (EvacuationTarget Target, double Cost, double Dist, double OccRatio, bool IsFull)>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in activeTargets)
        {
            double targetLat = target.Latitude ?? target.Y;
            double targetLng = target.Longitude ?? target.X;

            double dist = GeoMath.CalculateDistanceMeters(citizenLat, citizenLng, targetLat, targetLng);
            double normalizedDist = Math.Clamp(dist / dMax, 0.0, 10.0);

            // Self-Occupancy discount: if the citizen is currently assigned here, discount by 1
            bool isCurrentTarget = !string.IsNullOrWhiteSpace(currentTargetId) &&
                                   string.Equals(target.Id, currentTargetId, StringComparison.OrdinalIgnoreCase);
            int evaluatedOccupancy = isCurrentTarget
                ? Math.Max(0, target.CurrentOccupancy - 1)
                : target.CurrentOccupancy;

            int capacity = Math.Max(1, target.Capacity);
            double occRatio = (double)evaluatedOccupancy / capacity;

            double distCost = wDist * normalizedDist;
            double occCost = wOcc * occRatio;
            double tieBreaker = 0.0001 * normalizedDist;
            double cost = distCost + occCost + tieBreaker;

            bool isFull = evaluatedOccupancy >= capacity;
            if (isFull && hasAnyTargetWithCapacity)
            {
                cost += CapacityOverflowPenalty;
            }

            // Roadblock intersection test
            if (roadblocks != null && roadblocks.Count > 0)
            {
                bool hitsRoadblock = false;
                foreach (var line in roadblocks)
                {
                    if (line == null || line.Count < 2) continue;
                    for (int i = 0; i + 1 < line.Count; i++)
                    {
                        if (GeoMath.SegmentsIntersect(citizenLat, citizenLng, targetLat, targetLng,
                            line[i].Lat, line[i].Lng, line[i + 1].Lat, line[i + 1].Lng))
                        {
                            hitsRoadblock = true;
                            break;
                        }
                    }
                    if (hitsRoadblock) break;
                }

                if (hitsRoadblock)
                {
                    cost += RoadblockBlockagePenalty;
                }
            }

            candidateCosts[target.Id] = (target, cost, dist, occRatio, isFull);

            evaluations.Add(new TargetEvaluationDto(
                TargetId: target.Id,
                TargetName: target.Name,
                WalkableDistance: Math.Round(dist, 1),
                NormalizedDistance: Math.Round(normalizedDist, 3),
                OccupancyRatio: Math.Round(occRatio, 3),
                DistanceCost: Math.Round(distCost, 4),
                OccupancyCost: Math.Round(occCost, 4),
                TotalCost: Math.Round(cost, 4),
                IsFull: isFull,
                IsSelected: false));
        }

        // Find candidate with lowest evaluated cost
        var lowestCostCandidate = candidateCosts.Values.OrderBy(c => c.Cost).First();
        EvacuationTarget selectedTarget = lowestCostCandidate.Target;
        double selectedCost = lowestCostCandidate.Cost;
        double selectedDist = lowestCostCandidate.Dist;
        double selectedOccRatio = lowestCostCandidate.OccRatio;

        // Anti-Flapping & Hysteresis barrier:
        // If citizen already has an allocated shelter and this isn't a forced manual relocation:
        if (!string.IsNullOrWhiteSpace(currentTargetId) && !ignoreHysteresis &&
            candidateCosts.TryGetValue(currentTargetId, out var currentAllocated))
        {
            if (selectedTarget.Id != currentAllocated.Target.Id)
            {
                // Must be at least 15% cheaper to overcome hysteresis barrier
                bool exceedsPercentageMargin = lowestCostCandidate.Cost < currentAllocated.Cost * 0.85;

                // Equal cost tie-breaker
                bool costsArePracticallyEqual = Math.Abs(currentAllocated.Cost - lowestCostCandidate.Cost) < 1e-4;
                bool isSignificantlyCloser = lowestCostCandidate.Dist < currentAllocated.Dist * 0.85 ||
                                             lowestCostCandidate.Dist <= currentAllocated.Dist - 25.0;

                bool shouldSwitch = exceedsPercentageMargin ||
                                    (costsArePracticallyEqual && isSignificantlyCloser);

                if (!shouldSwitch)
                {
                    // Resist flapping; retain current target
                    selectedTarget = currentAllocated.Target;
                    selectedCost = currentAllocated.Cost;
                    selectedDist = currentAllocated.Dist;
                    selectedOccRatio = currentAllocated.OccRatio;
                }
            }
        }

        // Mark selected in evaluations
        for (int i = 0; i < evaluations.Count; i++)
        {
            if (string.Equals(evaluations[i].TargetId, selectedTarget.Id, StringComparison.OrdinalIgnoreCase))
            {
                evaluations[i] = evaluations[i] with { IsSelected = true };
                break;
            }
        }

        double targetDestLat = selectedTarget.Latitude ?? selectedTarget.Y;
        double targetDestLng = selectedTarget.Longitude ?? selectedTarget.X;
        double bearingDeg = GeoMath.CalculateBearingDegrees(citizenLat, citizenLng, targetDestLat, targetDestLng);

        // Compute simulation potential field wavefront route (detouring around roadblocks)
        var routePath = GeoMath.CalculateWavefrontRoute(
            citizenLat, citizenLng,
            targetDestLat, targetDestLng,
            roadblocks
        );

        if (routePath.Count > 2)
        {
            double routeDist = 0.0;
            for (int p = 0; p + 1 < routePath.Count; p++)
            {
                routeDist += GeoMath.CalculateDistanceMeters(
                    routePath[p].Latitude, routePath[p].Longitude,
                    routePath[p + 1].Latitude, routePath[p + 1].Longitude
                );
            }
            if (routeDist > selectedDist)
            {
                selectedDist = routeDist;
            }
        }

        string instructions = FormatInstructions(selectedTarget, selectedDist, selectedOccRatio);
        if (isOutsideZone)
        {
            instructions = $"Warning: You are outside the designated evacuation zone. {instructions}";
        }

        return new TargetAssignmentResponse(
            Target: selectedTarget,
            Distance: Math.Round(selectedDist, 1),
            OccupancyRatio: Math.Round(selectedOccRatio, 3),
            Instructions: instructions,
            CalculatedCost: Math.Round(selectedCost, 4),
            BearingDegrees: bearingDeg,
            FlowDirectionX: Math.Cos(bearingDeg * Math.PI / 180.0),
            FlowDirectionY: Math.Sin(bearingDeg * Math.PI / 180.0),
            IsSimulationEngineBased: true,
            TargetEvaluations: evaluations,
            RoutePath: routePath,
            IsOutsideZone: isOutsideZone
        );
    }

    /// <summary>
    /// Cartesian 2D target selection (metric space).
    /// </summary>
    public static TargetAssignmentResponse SelectTarget(
        double px,
        double py,
        IReadOnlyList<EvacuationTarget> targets,
        double weightDistance = 1.0,
        double weightOccupancy = 0.0,
        double? maxDistance = null,
        IReadOnlyList<ObstacleDto>? obstacles = null,
        string? currentTargetId = null,
        bool ignoreHysteresis = false)
    {
        if (targets == null || targets.Count == 0)
        {
            return new TargetAssignmentResponse(
                Target: null,
                Distance: 0.0,
                OccupancyRatio: 0.0,
                Instructions: "No evacuation points available.",
                CalculatedCost: 0.0,
                IsSimulationEngineBased: false,
                TargetEvaluations: Array.Empty<TargetEvaluationDto>());
        }

        var activeTargets = targets.Where(t => t.IsActive).ToList();
        if (activeTargets.Count == 0)
        {
            return new TargetAssignmentResponse(
                Target: null,
                Distance: 0.0,
                OccupancyRatio: 0.0,
                Instructions: "No active evacuation points.",
                CalculatedCost: 0.0,
                IsSimulationEngineBased: false,
                TargetEvaluations: Array.Empty<TargetEvaluationDto>());
        }

        double wDist = Math.Max(0.0, weightDistance);
        double wOcc = Math.Max(0.0, weightOccupancy);

        double dMax = maxDistance.GetValueOrDefault(0.0);
        if (dMax <= 0.0)
        {
            double calculatedMaxDist = activeTargets.Max(t => CalculateEuclideanDistance(px, py, t));
            dMax = Math.Max(1.0, calculatedMaxDist > 0.0 ? calculatedMaxDist : DefaultMaxDistance);
        }

        bool hasAnyTargetWithCapacity = activeTargets.Any(t => t.CurrentOccupancy < t.Capacity);
        var evaluations = new List<TargetEvaluationDto>();
        var candidateCosts = new Dictionary<string, (EvacuationTarget Target, double Cost, double Dist, double OccRatio, bool IsFull)>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in activeTargets)
        {
            double dist = CalculateEuclideanDistance(px, py, target);
            double normalizedDist = Math.Clamp(dist / dMax, 0.0, 10.0);

            bool isCurrentTarget = !string.IsNullOrWhiteSpace(currentTargetId) &&
                                   string.Equals(target.Id, currentTargetId, StringComparison.OrdinalIgnoreCase);
            int evaluatedOccupancy = isCurrentTarget
                ? Math.Max(0, target.CurrentOccupancy - 1)
                : target.CurrentOccupancy;

            int capacity = Math.Max(1, target.Capacity);
            double occRatio = (double)evaluatedOccupancy / capacity;

            double distCost = wDist * normalizedDist;
            double occCost = wOcc * occRatio;
            double tieBreaker = 0.0001 * normalizedDist;
            double cost = distCost + occCost + tieBreaker;

            bool isFull = evaluatedOccupancy >= capacity;
            if (isFull && hasAnyTargetWithCapacity)
            {
                cost += CapacityOverflowPenalty;
            }

            if (obstacles != null && obstacles.Count > 0)
            {
                bool intersects = obstacles.Any(o =>
                    GeoMath.LineIntersectsRect(px, py, target.CenterX, target.CenterY, o.X, o.Y, o.Width, o.Height));
                if (intersects)
                {
                    cost += RoadblockBlockagePenalty;
                }
            }

            candidateCosts[target.Id] = (target, cost, dist, occRatio, isFull);

            evaluations.Add(new TargetEvaluationDto(
                TargetId: target.Id,
                TargetName: target.Name,
                WalkableDistance: Math.Round(dist, 1),
                NormalizedDistance: Math.Round(normalizedDist, 3),
                OccupancyRatio: Math.Round(occRatio, 3),
                DistanceCost: Math.Round(distCost, 4),
                OccupancyCost: Math.Round(occCost, 4),
                TotalCost: Math.Round(cost, 4),
                IsFull: isFull,
                IsSelected: false));
        }

        var lowestCandidate = candidateCosts.Values.OrderBy(c => c.Cost).First();
        EvacuationTarget selectedTarget = lowestCandidate.Target;
        double selectedCost = lowestCandidate.Cost;
        double selectedDist = lowestCandidate.Dist;
        double selectedOccRatio = lowestCandidate.OccRatio;

        if (!string.IsNullOrWhiteSpace(currentTargetId) && !ignoreHysteresis &&
            candidateCosts.TryGetValue(currentTargetId, out var currentAllocated))
        {
            if (selectedTarget.Id != currentAllocated.Target.Id)
            {
                bool exceedsPercentageMargin = lowestCandidate.Cost < currentAllocated.Cost * 0.85;

                bool costsArePracticallyEqual = Math.Abs(currentAllocated.Cost - lowestCandidate.Cost) < 1e-4;
                bool isSignificantlyCloser = lowestCandidate.Dist < currentAllocated.Dist * 0.85 ||
                                             lowestCandidate.Dist <= currentAllocated.Dist - 25.0;

                bool shouldSwitch = exceedsPercentageMargin ||
                                    (costsArePracticallyEqual && isSignificantlyCloser);

                if (!shouldSwitch)
                {
                    selectedTarget = currentAllocated.Target;
                    selectedCost = currentAllocated.Cost;
                    selectedDist = currentAllocated.Dist;
                    selectedOccRatio = currentAllocated.OccRatio;
                }
            }
        }

        for (int i = 0; i < evaluations.Count; i++)
        {
            if (string.Equals(evaluations[i].TargetId, selectedTarget.Id, StringComparison.OrdinalIgnoreCase))
            {
                evaluations[i] = evaluations[i] with { IsSelected = true };
                break;
            }
        }

        double bearingDeg = GeoMath.CalculateEuclideanBearingDegrees(px, py, selectedTarget.CenterX, selectedTarget.CenterY);
        string instructions = FormatInstructions(selectedTarget, selectedDist, selectedOccRatio);

        return new TargetAssignmentResponse(
            Target: selectedTarget,
            Distance: Math.Round(selectedDist, 1),
            OccupancyRatio: Math.Round(selectedOccRatio, 3),
            Instructions: instructions,
            CalculatedCost: Math.Round(selectedCost, 4),
            BearingDegrees: bearingDeg,
            FlowDirectionX: Math.Cos(bearingDeg * Math.PI / 180.0),
            FlowDirectionY: Math.Sin(bearingDeg * Math.PI / 180.0),
            IsSimulationEngineBased: false,
            TargetEvaluations: evaluations);
    }

    public static double CalculateEuclideanDistance(double px, double py, EvacuationTarget target)
    {
        double dx = px - target.CenterX;
        double dy = py - target.CenterY;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public static string FormatInstructions(EvacuationTarget target, double distance, double occupancyRatio)
    {
        string distFormatted = distance < 1000.0
            ? $"~{Math.Round(distance):F0} m"
            : $"~{(distance / 1000.0):F1} km";

        string safetyStatus;
        if (occupancyRatio < 0.50)
            safetyStatus = $"Safe (occupancy {(occupancyRatio * 100):F0}%)";
        else if (occupancyRatio < 0.80)
            safetyStatus = $"Moderate occupancy ({(occupancyRatio * 100):F0}%)";
        else if (occupancyRatio < 1.00)
            safetyStatus = $"High occupancy ({(occupancyRatio * 100):F0}%)";
        else
            safetyStatus = $"Over capacity ({(occupancyRatio * 100):F0}%)";

        return $"Head to: {target.Name} (Distance: {distFormatted} • {safetyStatus})";
    }
}
