namespace PlanSafe.Contracts.Models.Map;

/// <summary>
/// High-level semantic category for a map entity.
/// </summary>
public enum ZoneCategory
{
    EvacuationZone,
    SafeLocation,
    Blockade
}

/// <summary>
/// Geometric primitive used to represent the map zone.
/// </summary>
public enum ZoneShape
{
    Circle,
    Polygon,
    Line,
    Point
}
