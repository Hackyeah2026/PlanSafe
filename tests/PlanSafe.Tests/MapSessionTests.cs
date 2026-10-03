using System;
using System.Collections.Generic;
using System.Text.Json;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Session;
using Xunit;

namespace PlanSafe.Tests;

public class MapSessionTests
{
    [Fact]
    public void CreateDefaultRoot_InitializesWithValidDefaults()
    {
        var session = MapSession.CreateDefaultRoot();

        Assert.NotNull(session.Id);
        Assert.Null(session.ParentSessionId);
        Assert.True(session.IsRoot);
        Assert.Equal("main", session.BranchName);
        Assert.Equal(14, session.ZoomLevel);
        Assert.NotNull(session.MapCenter);
        Assert.Equal(2, session.MapCenter.Length);
        Assert.Empty(session.Items);
        Assert.NotNull(session.SimulationConfig);
        Assert.Equal(400, session.SimulationConfig.AgentCount);
    }

    [Fact]
    public void DeepClone_CreatesIndependentItemsWithoutSharedReferences()
    {
        var parent = MapSession.CreateDefaultRoot();
        parent.Items.Add(new EvacCircleZoneItem
        {
            Name = "Hazard Alpha",
            Center = [50.0614, 19.9366],
            Radius = 150
        });
        parent.Items.Add(new BlockadeZoneItem
        {
            Name = "Bridge Barrier",
            StartPoint = [50.0600, 19.9300],
            EndPoint = [50.0610, 19.9350]
        });
        parent.Items.Add(new SafePointZoneItem
        {
            Name = "Shelter 1",
            Position = [50.0620, 19.9400]
        });

        // Act - Deep clone
        var child = parent.DeepClone(
            newBranchName: "scenario/barrier-test",
            newDescription: "Testing secondary routes");

        // Assert - Structural inheritance
        Assert.NotEqual(parent.Id, child.Id);
        Assert.Equal(parent.Id, child.ParentSessionId);
        Assert.False(child.IsRoot);
        Assert.Equal("scenario/barrier-test", child.BranchName);
        Assert.Equal("scenario/barrier-test", child.Name);
        Assert.Equal(3, child.Items.Count);

        // Assert - Items must have distinct IDs and independent memory references
        for (int i = 0; i < parent.Items.Count; i++)
        {
            Assert.NotEqual(parent.Items[i].Id, child.Items[i].Id);
            Assert.Equal(parent.Items[i].Name, child.Items[i].Name);
            Assert.Equal(parent.Items[i].Type, child.Items[i].Type);
        }

        // Act - Mutate child item and add new item to child
        var childCircle = (EvacCircleZoneItem)child.Items[0];
        childCircle.Radius = 999;
        child.Items.Add(new SafeCircleZoneItem
        {
            Name = "New Safe Haven in Child",
            Center = [50.07, 19.95],
            Radius = 80
        });

        // Assert - Parent remains unaltered
        var parentCircle = (EvacCircleZoneItem)parent.Items[0];
        Assert.Equal(150, parentCircle.Radius);
        Assert.Equal(3, parent.Items.Count);
        Assert.Equal(4, child.Items.Count);
    }

    [Fact]
    public void Serialization_PolymorphicRoundtrip_PreservesAllZoneTypes()
    {
        var session = new MapSession
        {
            Name = "Comprehensive Scenario",
            BranchName = "feature/poly-test"
        };

        session.Items.Add(new EvacCircleZoneItem { Name = "Evac Circle", Center = [50.1, 19.1], Radius = 100 });
        session.Items.Add(new SafeCircleZoneItem { Name = "Safe Circle", Center = [50.2, 19.2], Radius = 200 });
        session.Items.Add(new EvacPolygonZoneItem { Name = "Evac Poly", Coordinates = new() { new[] { 50.1, 19.1 }, new[] { 50.2, 19.2 }, new[] { 50.3, 19.3 } } });
        session.Items.Add(new SafePolygonZoneItem { Name = "Safe Poly", Coordinates = new() { new[] { 50.4, 19.4 }, new[] { 50.5, 19.5 }, new[] { 50.6, 19.6 } } });
        session.Items.Add(new BlockadeZoneItem { Name = "Blockade", StartPoint = [50.1, 19.1], EndPoint = [50.2, 19.2] });
        session.Items.Add(new SafePointZoneItem { Name = "Safe Pin", Position = [50.7, 19.7] });

        var json = JsonSerializer.Serialize(session);
        var deserialized = JsonSerializer.Deserialize<MapSession>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(6, deserialized.Items.Count);
        Assert.IsType<EvacCircleZoneItem>(deserialized.Items[0]);
        Assert.IsType<SafeCircleZoneItem>(deserialized.Items[1]);
        Assert.IsType<EvacPolygonZoneItem>(deserialized.Items[2]);
        Assert.IsType<SafePolygonZoneItem>(deserialized.Items[3]);
        Assert.IsType<BlockadeZoneItem>(deserialized.Items[4]);
        Assert.IsType<SafePointZoneItem>(deserialized.Items[5]);
    }
}
