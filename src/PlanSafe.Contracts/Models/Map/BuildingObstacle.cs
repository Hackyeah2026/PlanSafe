using System.Text.Json.Serialization;

namespace PlanSafe.Contracts.Models.Map;

/// <summary>
/// Represents a real building or physical obstacle bounding box in geographic coordinates.
/// Used by simulation engines to construct solid collision obstacles and pathfinding barriers.
/// </summary>
public class BuildingObstacle
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("minLat")]
    public double MinLat { get; set; }

    [JsonPropertyName("maxLat")]
    public double MaxLat { get; set; }

    [JsonPropertyName("minLng")]
    public double MinLng { get; set; }

    [JsonPropertyName("maxLng")]
    public double MaxLng { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("buildingType")]
    public string? BuildingType { get; set; }

    /// <summary>
    /// Polygon boundary vertices in geographic coordinates [latitude, longitude].
    /// If null or empty, the axis-aligned bounding box (MinLat, MaxLat, MinLng, MaxLng) is used.
    /// </summary>
    [JsonPropertyName("polygon")]
    public List<double[]>? Polygon { get; set; }

    /// <summary>
    /// Optional inner rings (e.g. courtyards, dziedzińce) in geographic coordinates [latitude, longitude].
    /// Any inner ring will remain open/walkable even when the outer building polygon is solid.
    /// </summary>
    [JsonPropertyName("innerPolygons")]
    public List<List<double[]>>? InnerPolygons { get; set; }
}
