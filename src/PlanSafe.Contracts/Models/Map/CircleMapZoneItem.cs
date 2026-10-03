using System.Text.Json.Serialization;

namespace PlanSafe.Contracts.Models.Map;

/// <summary>
/// Abstract base class for circular map items defined by center and radius.
/// </summary>
public abstract class CircleMapZoneItem : MapZoneItem
{
    [JsonPropertyName("center")]
    public double[]? Center { get; set; }

    [JsonPropertyName("radius")]
    public double? Radius { get; set; }

    [JsonIgnore]
    public override ZoneShape Shape => ZoneShape.Circle;

    [JsonIgnore]
    public GeoCoordinate? CenterCoordinate => GeoCoordinate.FromArray(Center);
}

/// <summary>
/// Circular evacuation hazard zone.
/// </summary>
public class EvacCircleZoneItem : CircleMapZoneItem
{
    [JsonIgnore]
    public override string Type => "circle_zone";

    [JsonIgnore]
    public override ZoneCategory Category => ZoneCategory.EvacuationZone;

    [JsonIgnore]
    public override string DisplayLabel => "Evac Zone (Circle)";

    [JsonIgnore]
    public override string CardCssClass => "card-zone";

    public override MapZoneItem Clone(bool generateNewId = true) => new EvacCircleZoneItem
    {
        Id = generateNewId ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        Color = Color,
        MetricInfo = MetricInfo,
        CreatedAt = CreatedAt,
        Center = Center != null ? (double[])Center.Clone() : null,
        Radius = Radius
    };
}

/// <summary>
/// Circular safe evacuation zone / shelter.
/// </summary>
public class SafeCircleZoneItem : CircleMapZoneItem
{
    [JsonIgnore]
    public override string Type => "safe_circle";

    [JsonIgnore]
    public override ZoneCategory Category => ZoneCategory.SafeLocation;

    [JsonIgnore]
    public override string DisplayLabel => "Safe Zone (Circle)";

    [JsonIgnore]
    public override string CardCssClass => "card-safe";

    public override MapZoneItem Clone(bool generateNewId = true) => new SafeCircleZoneItem
    {
        Id = generateNewId ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        Color = Color,
        MetricInfo = MetricInfo,
        CreatedAt = CreatedAt,
        Center = Center != null ? (double[])Center.Clone() : null,
        Radius = Radius
    };
}
