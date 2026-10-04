using System.Collections.Generic;
using PlanSafe.Contracts.Models.Gus;
using PlanSafe.Contracts.Models.Map;

namespace PlanSafe.App.Services.Gus;

/// <summary>
/// Generates realistic 1:1 occupant agents based on GUS NSP 2021 census data,
/// placing agents strictly within the boundaries of evacuation zones.
/// </summary>
public interface IGusOccupantGenerator
{
    /// <summary>
    /// Generates individual occupant agents reflecting exact GUS 125m census counts
    /// inside the provided evacuation zones.
    /// </summary>
    IReadOnlyList<OccupantAgent> GenerateOccupants(IEnumerable<MapZoneItem> evacZones, int? randomSeed = null);

    Task<IReadOnlyList<OccupantAgent>> GenerateOccupantsAsync(IEnumerable<MapZoneItem> evacZones,
        int? randomSeed = null, CancellationToken cancellationToken = default);
}
