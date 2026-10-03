using System.Collections.Generic;
using System.Threading.Tasks;
using PlanSafe.Contracts.Models.Gus;
using PlanSafe.Contracts.Models.Map;

namespace PlanSafe.App.Services.Gus;

/// <summary>
/// Service interface for querying GUS NSP 2021 125m demographic grid data.
/// </summary>
public interface IGusCensusService
{
    /// <summary>
    /// Loads the census dataset if not already loaded into memory.
    /// </summary>
    Task EnsureLoadedAsync();

    /// <summary>
    /// Indicates whether the census dataset is loaded and ready for queries.
    /// </summary>
    bool IsLoaded { get; }

    /// <summary>
    /// Total count of loaded 125m grid cells.
    /// </summary>
    int CellCount { get; }

    /// <summary>
    /// Total population represented across all loaded cells.
    /// </summary>
    int TotalCensusPopulation { get; }

    /// <summary>
    /// Retrieves all loaded 125m grid cells.
    /// </summary>
    IReadOnlyList<GusGridCell> GetAllCells();

    /// <summary>
    /// Finds all 125m cells that overlap the given map zone.
    /// </summary>
    IReadOnlyList<(GusGridCell Cell, double OverlapRatio)> GetIntersectingCells(MapZoneItem zone);

    /// <summary>
    /// Calculates the area-weighted resident population inside a single evacuation zone.
    /// </summary>
    int CalculateZonePopulation(MapZoneItem zone);

    /// <summary>
    /// Calculates the total unique resident population across multiple evacuation zones
    /// (accounting for zone overlap so residents are not double-counted).
    /// </summary>
    (int TotalPopulation, int AffectedCellsCount) CalculateEvacuationPopulation(IEnumerable<MapZoneItem> zones);
}
