namespace PlanSafe.Contracts.Models.Gus;

/// <summary>
/// Represents an individual occupant (citizen/agent) located within an evacuation zone based on GUS census data.
/// </summary>
public class OccupantAgent
{
    /// <summary>
    /// Unique occupant identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// WGS84 Latitude.
    /// </summary>
    public double Latitude { get; set; }

    /// <summary>
    /// WGS84 Longitude.
    /// </summary>
    public double Longitude { get; set; }

    /// <summary>
    /// Identifier of the 125m GUS grid cell from which this occupant originates.
    /// </summary>
    public string CellId { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the evacuation zone containing this occupant.
    /// </summary>
    public string EvacZoneId { get; set; } = string.Empty;
}
