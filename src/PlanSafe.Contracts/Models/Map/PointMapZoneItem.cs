using System.Text.Json.Serialization;

namespace PlanSafe.Contracts.Models.Map;

/// <summary>
/// Abstract base class for point map items defined by a single position.
/// </summary>
public abstract class PointMapZoneItem : MapZoneItem
{
    [JsonPropertyName("position")]
    public double[]? Position { get; set; }

    [JsonIgnore]
    public override ZoneShape Shape => ZoneShape.Point;

    [JsonIgnore]
    public GeoCoordinate? PositionCoordinate => GeoCoordinate.FromArray(Position);
}

/// <summary>
/// Designated safe assembly point or shelter.
/// </summary>
public class SafePointZoneItem : PointMapZoneItem
{
    [JsonIgnore]
    public override string Type => "safe_point";

    [JsonIgnore]
    public override ZoneCategory Category => ZoneCategory.SafeLocation;

    [JsonIgnore]
    public override string DisplayLabel => "Safe Location";

    [JsonIgnore]
    public override string CardCssClass => "card-safe";

    public override MapZoneItem Clone(bool generateNewId = true) => new SafePointZoneItem
    {
        Id = generateNewId ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        Color = Color,
        MetricInfo = MetricInfo,
        CreatedAt = CreatedAt,
        Position = Position != null ? (double[])Position.Clone() : null
    };
}
