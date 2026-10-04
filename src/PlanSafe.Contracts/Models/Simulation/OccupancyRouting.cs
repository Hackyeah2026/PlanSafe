using System;
using System.Collections.Generic;
using System.Linq;

namespace PlanSafe.Contracts.Models.Simulation;

/// <summary>Relative load for routing, independent of safe-zone capacity limits.</summary>
public static class OccupancyRouting
{
    public static double OpenZoneScale(IEnumerable<EvacuationTarget> targets) =>
        Math.Max(1.0, targets.Where(t => t.IsActive && !t.HasCapacityLimit)
            .Sum(t => (double)Math.Max(0, t.CurrentOccupancy)));

    public static double Cost(EvacuationTarget target, double openZoneScale, int? occupancy = null) =>
        Math.Max(0, occupancy ?? target.CurrentOccupancy) /
        (target.HasCapacityLimit ? target.Capacity : Math.Max(1.0, openZoneScale));
}
