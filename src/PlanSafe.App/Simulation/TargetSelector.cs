using System;
using System.Collections.Generic;
using System.Linq;
using PlanSafe.Contracts.Models.Simulation;

namespace PlanSafe.App.Simulation;

/// <summary>
/// Mathematical target selection algorithm based on distance and occupancy weighting.
/// Implements Cost(T_i) = w_dist * (dist / D_max) + w_occ * (Occupancy / Capacity)
/// with hard capacity limits, roadblock penalties, and overflow fallback.
/// </summary>
public static class TargetSelector
{
    public const double DefaultMaxDistance = 1000.0;
    public const double CapacityOverflowPenalty = 1e9;
    public const double RoadblockBlockagePenalty = 5e8;

    public static TargetAssignmentResponse SelectTarget(
        double px,
        double py,
        IReadOnlyList<EvacuationTarget> targets,
        double weightDistance = 1.0,
        double weightOccupancy = 0.0,
        double? maxDistance = null)
    {
        if (targets == null || targets.Count == 0)
        {
            return new TargetAssignmentResponse(
                Target: null,
                Distance: 0.0,
                OccupancyRatio: 0.0,
                Instructions: "Brak dostępnych punktów ewakuacji.",
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
                Instructions: "Brak aktywnych punktów ewakuacyjnych.",
                CalculatedCost: 0.0,
                IsSimulationEngineBased: false,
                TargetEvaluations: Array.Empty<TargetEvaluationDto>());
        }

        // Clamp negative weights to zero
        double wDist = Math.Max(0.0, weightDistance);
        double wOcc = Math.Max(0.0, weightOccupancy);

        // Normalize distance scale D_max
        double dMax = maxDistance.GetValueOrDefault(0.0);
        if (dMax <= 0.0)
        {
            double calculatedMaxDist = activeTargets.Max(t => CalculateDistance(px, py, t));
            dMax = Math.Max(1.0, calculatedMaxDist > 0.0 ? calculatedMaxDist : DefaultMaxDistance);
        }

        // Determine if at least one target has available capacity
        double openZoneScale = OccupancyRouting.OpenZoneScale(activeTargets);
        bool hasAnyTargetWithCapacity = activeTargets.Any(t => !t.IsFull);

        EvacuationTarget? bestTarget = null;
        double minCost = double.PositiveInfinity;
        double bestDistance = 0.0;
        double bestOccupancyRatio = 0.0;
        var evaluations = new List<TargetEvaluationDto>();

        foreach (var target in activeTargets)
        {
            double dist = CalculateDistance(px, py, target);
            double normalizedDist = Math.Clamp(dist / dMax, 0.0, 10.0);
            double occRatio = target.OccupancyRatio;

            double distCost = wDist * normalizedDist;
            double occCost = wOcc * OccupancyRouting.Cost(target, openZoneScale, target.CurrentOccupancy);
            double cost = distCost + occCost;

            // Hard limit rule: if target is full and other non-full targets exist, apply extreme penalty
            bool isFull = target.IsFull;
            if (isFull && hasAnyTargetWithCapacity)
            {
                cost += CapacityOverflowPenalty;
            }

            if (cost < minCost)
            {
                minCost = cost;
                bestTarget = target;
                bestDistance = dist;
                bestOccupancyRatio = occRatio;
            }

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

        if (bestTarget == null)
        {
            bestTarget = activeTargets[0];
            bestDistance = CalculateDistance(px, py, bestTarget);
            bestOccupancyRatio = bestTarget.OccupancyRatio;
        }

        // Mark the selected candidate
        for (int i = 0; i < evaluations.Count; i++)
        {
            if (evaluations[i].TargetId == bestTarget.Id)
            {
                evaluations[i] = evaluations[i] with { IsSelected = true };
                break;
            }
        }

        double bearingDeg = GeoMath.CalculateEuclideanBearingDegrees(px, py, bestTarget.CenterX, bestTarget.CenterY);
        string instructions = FormatInstructions(bestTarget, bestDistance, bestOccupancyRatio);

        return new TargetAssignmentResponse(
            Target: bestTarget,
            Distance: Math.Round(bestDistance, 1),
            OccupancyRatio: Math.Round(bestOccupancyRatio, 3),
            Instructions: instructions,
            CalculatedCost: Math.Round(minCost, 4),
            BearingDegrees: bearingDeg,
            FlowDirectionX: Math.Cos(bearingDeg * Math.PI / 180.0),
            FlowDirectionY: Math.Sin(bearingDeg * Math.PI / 180.0),
            IsSimulationEngineBased: false,
            TargetEvaluations: evaluations);
    }

    /// <summary>
    /// Geographic target selection using real GPS coordinates and Haversine distance.
    /// </summary>
    public static TargetAssignmentResponse SelectGeoTarget(
        double citizenLat,
        double citizenLng,
        IReadOnlyList<EvacuationTarget> targets,
        double weightDistance = 1.0,
        double weightOccupancy = 0.0,
        double? maxDistanceMeters = null,
        IReadOnlyList<IReadOnlyList<(double Lat, double Lng)>>? roadblocks = null)
    {
        var convertedRoadblocks = roadblocks?.Select(rb =>
            (IReadOnlyList<PlanSafe.Contracts.Models.Map.GeoCoordinate>)rb.Select(pt => new PlanSafe.Contracts.Models.Map.GeoCoordinate(pt.Lat, pt.Lng)).ToList()
        ).ToList();

        return PlanSafe.Contracts.Simulation.TargetSelector.SelectGeoTarget(
            citizenLat,
            citizenLng,
            targets,
            weightDistance,
            weightOccupancy,
            maxDistanceMeters,
            convertedRoadblocks);
    }

    public static double CalculateDistance(double px, double py, EvacuationTarget target)
    {
        double dx = px - target.CenterX;
        double dy = py - target.CenterY;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public static double CalculateBearingDegrees(double x1, double y1, double x2, double y2)
    {
        return GeoMath.CalculateEuclideanBearingDegrees(x1, y1, x2, y2);
    }

    public static string FormatInstructions(EvacuationTarget target, double distance, double occupancyRatio)
    {
        string distFormatted = distance < 1000.0
            ? $"~{Math.Round(distance):F0} m"
            : $"~{(distance / 1000.0):F1} km";

        string safetyStatus;
        if (!target.HasCapacityLimit)
            safetyStatus = $"People here: {target.CurrentOccupancy}";
        else if (occupancyRatio < 0.50)
            safetyStatus = $"Bezpiecznie (obłożenie {(occupancyRatio * 100):F0}%)";
        else if (occupancyRatio < 0.80)
            safetyStatus = $"Umiarkowane obłożenie ({(occupancyRatio * 100):F0}%)";
        else if (occupancyRatio < 1.00)
            safetyStatus = $"Wysokie obłożenie ({(occupancyRatio * 100):F0}%)";
        else
            safetyStatus = $"Przepełniony ({(occupancyRatio * 100):F0}%)";

        return $"Kieruj się do: {target.Name} (Odległość: {distFormatted} • {safetyStatus})";
    }

    /// <summary>
    /// Evaluates target assignment using the simulation engine initialized from a simulation snapshot.
    /// </summary>
    public static TargetAssignmentResponse SelectTarget(
        double px,
        double py,
        SimulationSnapshot snapshot,
        double? customWeightDistance = null,
        double? customWeightOccupancy = null,
        IReadOnlyList<EvacuationTarget>? customTargets = null,
        double? maxDistance = null)
    {
        var engine = CrowdSimulationEngine.FromSnapshot(snapshot);
        return engine.EvaluateTargetAssignment(px, py, customWeightDistance, customWeightOccupancy, customTargets, maxDistance);
    }

    /// <summary>
    /// Evaluates target assignment directly using a running CrowdSimulationEngine instance.
    /// </summary>
    public static TargetAssignmentResponse SelectTarget(
        double px,
        double py,
        CrowdSimulationEngine engine,
        double? customWeightDistance = null,
        double? customWeightOccupancy = null,
        IReadOnlyList<EvacuationTarget>? customTargets = null,
        double? maxDistance = null)
    {
        return engine.EvaluateTargetAssignment(px, py, customWeightDistance, customWeightOccupancy, customTargets, maxDistance);
    }
}
