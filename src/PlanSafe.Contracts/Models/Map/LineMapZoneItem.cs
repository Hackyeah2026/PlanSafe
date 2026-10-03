using System.Text.Json.Serialization;

namespace PlanSafe.Contracts.Models.Map;

/// <summary>
/// Abstract base class for linear map items defined by start and end points.
/// </summary>
public abstract class LineMapZoneItem : MapZoneItem
{
    [JsonPropertyName("startPoint")]
    public double[]? StartPoint { get; set; }

    [JsonPropertyName("endPoint")]
    public double[]? EndPoint { get; set; }

    [JsonIgnore]
    public override ZoneShape Shape => ZoneShape.Line;

    [JsonIgnore]
    public GeoCoordinate? StartCoordinate => GeoCoordinate.FromArray(StartPoint);

    [JsonIgnore]
    public GeoCoordinate? EndCoordinate => GeoCoordinate.FromArray(EndPoint);
}

/// <summary>
/// Road blockade or barrier preventing transit.
/// </summary>
public class BlockadeZoneItem : LineMapZoneItem
{
    [JsonIgnore]
    public override string Type => "blockade";

    [JsonIgnore]
    public override ZoneCategory Category => ZoneCategory.Blockade;

    [JsonIgnore]
    public override string DisplayLabel => "Blockade";

    [JsonIgnore]
    public override string CardCssClass => "card-blockade";

    public override MapZoneItem Clone(bool generateNewId = true) => new BlockadeZoneItem
    {
        Id = generateNewId ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        Color = Color,
        MetricInfo = MetricInfo,
        CreatedAt = CreatedAt,
        StartPoint = StartPoint != null ? (double[])StartPoint.Clone() : null,
        EndPoint = EndPoint != null ? (double[])EndPoint.Clone() : null
    };
}
