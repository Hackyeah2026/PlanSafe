using System;
using System.Text.Json.Serialization;

namespace PlanSafe.Contracts.Models.Map;

/// <summary>
/// Abstract base entity for all map zones, safe locations, and blockades.
/// Provides polymorphic JSON serialization compatible with Leaflet map interop.
/// </summary>
[JsonConverter(typeof(MapZoneItemJsonConverter))]
public abstract class MapZoneItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// String identifier matching the Leaflet JS type discriminator.
    /// </summary>
    [JsonIgnore]
    public abstract string Type { get; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("color")]
    public string Color { get; set; } = string.Empty;

    [JsonPropertyName("metricInfo")]
    public string? MetricInfo { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public abstract ZoneCategory Category { get; }

    [JsonIgnore]
    public abstract ZoneShape Shape { get; }

    [JsonIgnore]
    public abstract string DisplayLabel { get; }

    [JsonIgnore]
    public abstract string CardCssClass { get; }

    /// <summary>
    /// Creates a deep clone of this map zone item.
    /// </summary>
    public abstract MapZoneItem Clone(bool generateNewId = true);
}
