using System.Text.Json.Serialization;

namespace PlanSafe.Contracts.Models.Map;

/// <summary>
/// Fallback map item for custom or unrecognized types.
/// </summary>
public class UnknownMapZoneItem : MapZoneItem
{
    [JsonIgnore]
    public override string Type => "unknown";

    [JsonIgnore]
    public override ZoneCategory Category => ZoneCategory.EvacuationZone;

    [JsonIgnore]
    public override ZoneShape Shape => ZoneShape.Polygon;

    [JsonIgnore]
    public override string DisplayLabel => "Custom Zone";

    [JsonIgnore]
    public override string CardCssClass => "card-zone";

    public override MapZoneItem Clone(bool generateNewId = true) => new UnknownMapZoneItem
    {
        Id = generateNewId ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        Color = Color,
        MetricInfo = MetricInfo,
        CreatedAt = CreatedAt
    };
}
