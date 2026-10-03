using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PlanSafe.Contracts.Models.Map;
using Xunit;

namespace PlanSafe.Tests;

public class MapZoneHierarchyTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Deserialization_CircleZone_ProducesEvacCircleZoneItem()
    {
        var json = """
        {
            "id": "item-1",
            "type": "circle_zone",
            "name": "Hazard Area 1",
            "color": "#ea580c",
            "center": [50.0614, 19.9366],
            "radius": 250.0,
            "metricInfo": "Radius: 250 m"
        }
        """;

        var item = JsonSerializer.Deserialize<MapZoneItem>(json, _jsonOptions);

        Assert.NotNull(item);
        var circle = Assert.IsType<EvacCircleZoneItem>(item);
        Assert.Equal("item-1", circle.Id);
        Assert.Equal("circle_zone", circle.Type);
        Assert.Equal("Hazard Area 1", circle.Name);
        Assert.Equal("#ea580c", circle.Color);
        Assert.Equal(ZoneCategory.EvacuationZone, circle.Category);
        Assert.Equal(ZoneShape.Circle, circle.Shape);
        Assert.Equal("Evac Zone (Circle)", circle.DisplayLabel);
        Assert.Equal("card-zone", circle.CardCssClass);
        Assert.Equal(250.0, circle.Radius);
        Assert.NotNull(circle.Center);
        Assert.Equal(50.0614, circle.Center[0]);
        Assert.Equal(19.9366, circle.Center[1]);

        Assert.NotNull(circle.CenterCoordinate);
        Assert.Equal(50.0614, circle.CenterCoordinate.Value.Latitude);
        Assert.Equal(19.9366, circle.CenterCoordinate.Value.Longitude);
    }

    [Fact]
    public void Deserialization_SafeCircle_ProducesSafeCircleZoneItem()
    {
        var json = """
        {
            "id": "safe-1",
            "type": "safe_circle",
            "name": "Assembly Park",
            "color": "#22c55e",
            "center": [50.05, 19.95],
            "radius": 150.0,
            "metricInfo": "Radius: 150 m"
        }
        """;

        var item = JsonSerializer.Deserialize<MapZoneItem>(json, _jsonOptions);

        Assert.NotNull(item);
        var circle = Assert.IsType<SafeCircleZoneItem>(item);
        Assert.Equal("safe_circle", circle.Type);
        Assert.Equal(ZoneCategory.SafeLocation, circle.Category);
        Assert.Equal(ZoneShape.Circle, circle.Shape);
        Assert.Equal("Safe Zone (Circle)", circle.DisplayLabel);
        Assert.Equal("card-safe", circle.CardCssClass);
        Assert.Equal(150.0, circle.Radius);
    }

    [Fact]
    public void Deserialization_PolygonZone_ProducesEvacPolygonZoneItem()
    {
        var json = """
        {
            "id": "poly-1",
            "type": "polygon_zone",
            "name": "Flooded District",
            "color": "#ea580c",
            "coordinates": [
                [50.06, 19.93],
                [50.07, 19.94],
                [50.065, 19.95]
            ],
            "metricInfo": "3 vertices"
        }
        """;

        var item = JsonSerializer.Deserialize<MapZoneItem>(json, _jsonOptions);

        Assert.NotNull(item);
        var polygon = Assert.IsType<EvacPolygonZoneItem>(item);
        Assert.Equal("polygon_zone", polygon.Type);
        Assert.Equal(ZoneCategory.EvacuationZone, polygon.Category);
        Assert.Equal(ZoneShape.Polygon, polygon.Shape);
        Assert.Equal("Evac Zone (Polygon)", polygon.DisplayLabel);
        Assert.Equal("card-zone", polygon.CardCssClass);
        Assert.Equal(3, polygon.Coordinates?.Count);

        var vertices = polygon.Vertices.ToList();
        Assert.Equal(3, vertices.Count);
        Assert.Equal(50.06, vertices[0].Latitude);
        Assert.Equal(19.93, vertices[0].Longitude);
    }

    [Fact]
    public void Deserialization_SafePolygon_ProducesSafePolygonZoneItem()
    {
        var json = """
        {
            "id": "poly-safe",
            "type": "safe_polygon",
            "name": "Stadium Safe Field",
            "color": "#22c55e",
            "coordinates": [
                [50.06, 19.90],
                [50.065, 19.91],
                [50.06, 19.92]
            ]
        }
        """;

        var item = JsonSerializer.Deserialize<MapZoneItem>(json, _jsonOptions);

        Assert.NotNull(item);
        var polygon = Assert.IsType<SafePolygonZoneItem>(item);
        Assert.Equal("safe_polygon", polygon.Type);
        Assert.Equal(ZoneCategory.SafeLocation, polygon.Category);
        Assert.Equal(ZoneShape.Polygon, polygon.Shape);
        Assert.Equal("Safe Zone (Polygon)", polygon.DisplayLabel);
        Assert.Equal("card-safe", polygon.CardCssClass);
    }

    [Fact]
    public void Deserialization_Blockade_ProducesBlockadeZoneItem()
    {
        var json = """
        {
            "id": "blk-1",
            "type": "blockade",
            "name": "Main Bridge Blockade",
            "color": "#ef4444",
            "startPoint": [50.0610, 19.9350],
            "endPoint": [50.0620, 19.9360],
            "metricInfo": "Length: 120 m"
        }
        """;

        var item = JsonSerializer.Deserialize<MapZoneItem>(json, _jsonOptions);

        Assert.NotNull(item);
        var blockade = Assert.IsType<BlockadeZoneItem>(item);
        Assert.Equal("blockade", blockade.Type);
        Assert.Equal(ZoneCategory.Blockade, blockade.Category);
        Assert.Equal(ZoneShape.Line, blockade.Shape);
        Assert.Equal("Blockade", blockade.DisplayLabel);
        Assert.Equal("card-blockade", blockade.CardCssClass);

        Assert.NotNull(blockade.StartCoordinate);
        Assert.Equal(50.0610, blockade.StartCoordinate.Value.Latitude);
        Assert.Equal(19.9350, blockade.StartCoordinate.Value.Longitude);

        Assert.NotNull(blockade.EndCoordinate);
        Assert.Equal(50.0620, blockade.EndCoordinate.Value.Latitude);
        Assert.Equal(19.9360, blockade.EndCoordinate.Value.Longitude);
    }

    [Fact]
    public void Deserialization_SafePoint_ProducesSafePointZoneItem()
    {
        var json = """
        {
            "id": "sp-1",
            "type": "safe_point",
            "name": "Command Safe Center",
            "color": "#22c55e",
            "position": [50.0614, 19.9366]
        }
        """;

        var item = JsonSerializer.Deserialize<MapZoneItem>(json, _jsonOptions);

        Assert.NotNull(item);
        var point = Assert.IsType<SafePointZoneItem>(item);
        Assert.Equal("safe_point", point.Type);
        Assert.Equal(ZoneCategory.SafeLocation, point.Category);
        Assert.Equal(ZoneShape.Point, point.Shape);
        Assert.Equal("Safe Location", point.DisplayLabel);
        Assert.Equal("card-safe", point.CardCssClass);

        Assert.NotNull(point.PositionCoordinate);
        Assert.Equal(50.0614, point.PositionCoordinate.Value.Latitude);
        Assert.Equal(19.9366, point.PositionCoordinate.Value.Longitude);
    }

    [Fact]
    public void Serialization_EmitsExpectedTypeDiscriminatorForLeaflet()
    {
        MapZoneItem circle = new EvacCircleZoneItem
        {
            Id = "c1",
            Name = "Circle 1",
            Color = "#ea580c",
            Center = [50.0, 20.0],
            Radius = 100
        };

        MapZoneItem blockade = new BlockadeZoneItem
        {
            Id = "b1",
            Name = "Blockade 1",
            Color = "#ef4444",
            StartPoint = [50.1, 20.1],
            EndPoint = [50.2, 20.2]
        };

        var circleJson = JsonSerializer.Serialize(circle, _jsonOptions);
        var blockadeJson = JsonSerializer.Serialize(blockade, _jsonOptions);

        using var circleDoc = JsonDocument.Parse(circleJson);
        Assert.Equal("circle_zone", circleDoc.RootElement.GetProperty("type").GetString());

        using var blockadeDoc = JsonDocument.Parse(blockadeJson);
        Assert.Equal("blockade", blockadeDoc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void Roundtrip_PolymorphicList_PreservesAllDerivedTypesAndCategories()
    {
        var originalItems = new List<MapZoneItem>
        {
            new EvacCircleZoneItem { Name = "Evac 1", Center = [50.0, 19.9], Radius = 200 },
            new SafeCircleZoneItem { Name = "Safe Circle 1", Center = [50.1, 19.8], Radius = 300 },
            new EvacPolygonZoneItem { Name = "Evac Poly 1", Coordinates = [[50.0, 19.0], [50.1, 19.1], [50.0, 19.2]] },
            new SafePolygonZoneItem { Name = "Safe Poly 1", Coordinates = [[50.2, 19.2], [50.3, 19.3], [50.2, 19.4]] },
            new BlockadeZoneItem { Name = "Blockade 1", StartPoint = [50.05, 19.95], EndPoint = [50.06, 19.96] },
            new SafePointZoneItem { Name = "Safe Point 1", Position = [50.07, 19.97] }
        };

        var json = JsonSerializer.Serialize(originalItems, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<List<MapZoneItem>>(json, _jsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(6, deserialized.Count);

        Assert.IsType<EvacCircleZoneItem>(deserialized[0]);
        Assert.IsType<SafeCircleZoneItem>(deserialized[1]);
        Assert.IsType<EvacPolygonZoneItem>(deserialized[2]);
        Assert.IsType<SafePolygonZoneItem>(deserialized[3]);
        Assert.IsType<BlockadeZoneItem>(deserialized[4]);
        Assert.IsType<SafePointZoneItem>(deserialized[5]);

        // Category counting
        Assert.Equal(2, deserialized.Count(i => i.Category == ZoneCategory.EvacuationZone));
        Assert.Equal(3, deserialized.Count(i => i.Category == ZoneCategory.SafeLocation));
        Assert.Equal(1, deserialized.Count(i => i.Category == ZoneCategory.Blockade));
    }

    [Fact]
    public void GeoCoordinate_ConvertsToArrayAndString()
    {
        var coord = new GeoCoordinate(50.0614, 19.9366);
        var array = coord.ToArray();

        Assert.Equal(2, array.Length);
        Assert.Equal(50.0614, array[0]);
        Assert.Equal(19.9366, array[1]);

        var restored = GeoCoordinate.FromArray(array);
        Assert.NotNull(restored);
        Assert.Equal(coord, restored.Value);

        Assert.Null(GeoCoordinate.FromArray(null));
        Assert.Null(GeoCoordinate.FromArray([50.0]));
    }

    [Fact]
    public void Deserialization_UnknownType_ProducesUnknownMapZoneItem()
    {
        var json = """
        {
            "id": "unknown-1",
            "type": "custom_drone_patrol",
            "name": "Aerial Patrol 1",
            "color": "#6366f1"
        }
        """;

        var item = JsonSerializer.Deserialize<MapZoneItem>(json, _jsonOptions);

        Assert.NotNull(item);
        var unknown = Assert.IsType<UnknownMapZoneItem>(item);
        Assert.Equal("unknown-1", unknown.Id);
        Assert.Equal("unknown", unknown.Type);
        Assert.Equal("Aerial Patrol 1", unknown.Name);
        Assert.Equal("Custom Zone", unknown.DisplayLabel);
        Assert.Equal("card-zone", unknown.CardCssClass);
    }

    [Fact]
    public void PatternMatching_CorrectlyResolvesAllDerivedTypes()
    {
        var items = new List<MapZoneItem>
        {
            new EvacCircleZoneItem { Name = "Evac Circle" },
            new SafeCircleZoneItem { Name = "Safe Circle" },
            new EvacPolygonZoneItem { Name = "Evac Poly" },
            new SafePolygonZoneItem { Name = "Safe Poly" },
            new BlockadeZoneItem { Name = "Blockade" },
            new SafePointZoneItem { Name = "Safe Point" },
            new UnknownMapZoneItem { Name = "Custom" }
        };

        var labels = items.Select(item => item switch
        {
            SafeCircleZoneItem => "safe_circle_matched",
            SafePolygonZoneItem => "safe_polygon_matched",
            SafePointZoneItem => "safe_point_matched",
            BlockadeZoneItem => "blockade_matched",
            EvacCircleZoneItem => "evac_circle_matched",
            EvacPolygonZoneItem => "evac_poly_matched",
            _ => "other_matched"
        }).ToList();

        Assert.Equal("evac_circle_matched", labels[0]);
        Assert.Equal("safe_circle_matched", labels[1]);
        Assert.Equal("evac_poly_matched", labels[2]);
        Assert.Equal("safe_polygon_matched", labels[3]);
        Assert.Equal("blockade_matched", labels[4]);
        Assert.Equal("safe_point_matched", labels[5]);
        Assert.Equal("other_matched", labels[6]);
    }
}

