using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlanSafe.Contracts.Models.Map;

/// <summary>
/// Custom polymorphic JSON converter for MapZoneItem hierarchy.
/// Allows the "type" discriminator to appear anywhere in the JSON payload (order-independent)
/// and ensures flawless interoperability with Leaflet JS and localStorage.
/// </summary>
public class MapZoneItemJsonConverter : JsonConverter<MapZoneItem>
{
    public override MapZoneItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected StartObject token, got {reader.TokenType}.");
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        string? typeStr = null;
        if (root.TryGetProperty("type", out var typeProp) || root.TryGetProperty("Type", out typeProp))
        {
            typeStr = typeProp.GetString();
        }

        MapZoneItem item = typeStr switch
        {
            "circle_zone" => new EvacCircleZoneItem(),
            "safe_circle" => new SafeCircleZoneItem(),
            "polygon_zone" => new EvacPolygonZoneItem(),
            "safe_polygon" => new SafePolygonZoneItem(),
            "safe_point" => new SafePointZoneItem(),
            "blockade" => new BlockadeZoneItem(),
            _ => new UnknownMapZoneItem()
        };

        // Populate base metadata
        if (root.TryGetProperty("id", out var idProp) || root.TryGetProperty("Id", out idProp))
        {
            item.Id = idProp.GetString() ?? item.Id;
        }

        if (root.TryGetProperty("name", out var nameProp) || root.TryGetProperty("Name", out nameProp))
        {
            item.Name = nameProp.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("color", out var colorProp) || root.TryGetProperty("Color", out colorProp))
        {
            item.Color = colorProp.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("metricInfo", out var metricProp) || root.TryGetProperty("MetricInfo", out metricProp))
        {
            item.MetricInfo = metricProp.GetString();
        }

        if ((root.TryGetProperty("createdAt", out var createdProp) || root.TryGetProperty("CreatedAt", out createdProp))
            && createdProp.TryGetDateTime(out var dt))
        {
            item.CreatedAt = dt;
        }

        // Populate shape-specific geometry
        switch (item)
        {
            case CircleMapZoneItem circle:
                if (root.TryGetProperty("center", out var centerProp) || root.TryGetProperty("Center", out centerProp))
                {
                    if (centerProp.ValueKind == JsonValueKind.Array)
                    {
                        circle.Center = centerProp.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    }
                }
                if (root.TryGetProperty("radius", out var radiusProp) || root.TryGetProperty("Radius", out radiusProp))
                {
                    if (radiusProp.TryGetDouble(out var r))
                    {
                        circle.Radius = r;
                    }
                }
                break;

            case PolygonMapZoneItem poly:
                if (root.TryGetProperty("coordinates", out var coordsProp) || root.TryGetProperty("Coordinates", out coordsProp))
                {
                    if (coordsProp.ValueKind == JsonValueKind.Array)
                    {
                        var coords = new List<double[]>();
                        foreach (var pt in coordsProp.EnumerateArray())
                        {
                            if (pt.ValueKind == JsonValueKind.Array)
                            {
                                coords.Add(pt.EnumerateArray().Select(e => e.GetDouble()).ToArray());
                            }
                        }
                        poly.Coordinates = coords;
                    }
                }
                break;

            case LineMapZoneItem line:
                if (root.TryGetProperty("startPoint", out var startProp) || root.TryGetProperty("StartPoint", out startProp))
                {
                    if (startProp.ValueKind == JsonValueKind.Array)
                    {
                        line.StartPoint = startProp.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    }
                }
                if (root.TryGetProperty("endPoint", out var endProp) || root.TryGetProperty("EndPoint", out endProp))
                {
                    if (endProp.ValueKind == JsonValueKind.Array)
                    {
                        line.EndPoint = endProp.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    }
                }
                break;

            case PointMapZoneItem point:
                if (root.TryGetProperty("position", out var posProp) || root.TryGetProperty("Position", out posProp))
                {
                    if (posProp.ValueKind == JsonValueKind.Array)
                    {
                        point.Position = posProp.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    }
                }
                break;
        }

        return item;
    }

    public override void Write(Utf8JsonWriter writer, MapZoneItem value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("type", value.Type);
        writer.WriteString("name", value.Name);
        writer.WriteString("color", value.Color);
        if (value.MetricInfo != null)
        {
            writer.WriteString("metricInfo", value.MetricInfo);
        }
        writer.WriteString("createdAt", value.CreatedAt.ToString("O"));

        switch (value)
        {
            case CircleMapZoneItem circle:
                if (circle.Center != null)
                {
                    writer.WriteStartArray("center");
                    foreach (var c in circle.Center)
                    {
                        writer.WriteNumberValue(c);
                    }
                    writer.WriteEndArray();
                }
                if (circle.Radius.HasValue)
                {
                    writer.WriteNumber("radius", circle.Radius.Value);
                }
                break;

            case PolygonMapZoneItem poly:
                if (poly.Coordinates != null)
                {
                    writer.WriteStartArray("coordinates");
                    foreach (var pt in poly.Coordinates)
                    {
                        writer.WriteStartArray();
                        foreach (var c in pt)
                        {
                            writer.WriteNumberValue(c);
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
                break;

            case LineMapZoneItem line:
                if (line.StartPoint != null)
                {
                    writer.WriteStartArray("startPoint");
                    foreach (var c in line.StartPoint)
                    {
                        writer.WriteNumberValue(c);
                    }
                    writer.WriteEndArray();
                }
                if (line.EndPoint != null)
                {
                    writer.WriteStartArray("endPoint");
                    foreach (var c in line.EndPoint)
                    {
                        writer.WriteNumberValue(c);
                    }
                    writer.WriteEndArray();
                }
                break;

            case PointMapZoneItem point:
                if (point.Position != null)
                {
                    writer.WriteStartArray("position");
                    foreach (var c in point.Position)
                    {
                        writer.WriteNumberValue(c);
                    }
                    writer.WriteEndArray();
                }
                break;
        }

        writer.WriteEndObject();
    }
}
