namespace PlanSafe.Contracts.Models.Gus;

/// <summary>
/// Represents a 125m x 125m demographic census grid cell from the Polish Central Statistical Office (GUS NSP 2021).
/// </summary>
public class GusGridCell
{
    /// <summary>
    /// Unique European grid identifier, e.g., "125mN3042000E5030000".
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Total resident population in this 125m grid cell according to NSP 2021 census.
    /// </summary>
    public int Pop { get; set; }

    /// <summary>
    /// South-west corner latitude (WGS84).
    /// </summary>
    public double MinLat { get; set; }

    /// <summary>
    /// South-west corner longitude (WGS84).
    /// </summary>
    public double MinLng { get; set; }

    /// <summary>
    /// North-east corner latitude (WGS84).
    /// </summary>
    public double MaxLat { get; set; }

    /// <summary>
    /// North-east corner longitude (WGS84).
    /// </summary>
    public double MaxLng { get; set; }

    /// <summary>
    /// Center latitude (WGS84).
    /// </summary>
    public double Lat { get; set; }

    /// <summary>
    /// Center longitude (WGS84).
    /// </summary>
    public double Lng { get; set; }
}
