using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace PlanSafe.Contracts.Models.Map;

/// <summary>
/// Abstract base class for polygonal map items defined by boundary coordinates.
/// </summary>
public abstract class PolygonMapZoneItem : MapZoneItem
{
    [JsonPropertyName("coordinates")]
    public List<double[]>? Coordinates { get; set; }

    [JsonIgnore]
    public override ZoneShape Shape => ZoneShape.Polygon;

    [JsonIgnore]
    public IEnumerable<GeoCoordinate> Vertices =>
        Coordinates?
            .Select(c => GeoCoordinate.FromArray(c))
            .Where(c => c.HasValue)
            .Select(c => c!.Value)
        ?? Enumerable.Empty<GeoCoordinate>();
}

/// <summary>
/// Polygonal evacuation hazard zone.
/// </summary>
public class EvacPolygonZoneItem : PolygonMapZoneItem
{
    [JsonIgnore]
    public override string Type => "polygon_zone";

    [JsonIgnore]
    public override ZoneCategory Category => ZoneCategory.EvacuationZone;

    [JsonIgnore]
    public override string DisplayLabel => "Evac Zone (Polygon)";

    [JsonIgnore]
    public override string CardCssClass => "card-zone";

    public override MapZoneItem Clone(bool generateNewId = true) => new EvacPolygonZoneItem
    {
        Id = generateNewId ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        Color = Color,
        MetricInfo = MetricInfo,
        CreatedAt = CreatedAt,
        Coordinates = Coordinates?.Select(c => (double[])c.Clone()).ToList()
    };
}

/// <summary>
/// Polygonal safe evacuation zone / shelter.
/// </summary>
public class SafePolygonZoneItem : PolygonMapZoneItem
{
    [JsonIgnore]
    public override string Type => "safe_polygon";

    [JsonIgnore]
    public override ZoneCategory Category => ZoneCategory.SafeLocation;

    [JsonIgnore]
    public override string DisplayLabel => "Safe Zone (Polygon)";

    [JsonIgnore]
    public override string CardCssClass => "card-safe";

    public override MapZoneItem Clone(bool generateNewId = true) => new SafePolygonZoneItem
    {
        Id = generateNewId ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        Color = Color,
        MetricInfo = MetricInfo,
        CreatedAt = CreatedAt,
        Coordinates = Coordinates?.Select(c => (double[])c.Clone()).ToList()
    };
}
