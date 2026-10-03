using System;
using System.Collections.Generic;
using PlanSafe.Contracts.Models.Session;
using Xunit;

namespace PlanSafe.Tests;

public class MapSessionTreeTests
{
    [Fact]
    public void BuildTree_CorrectlyConstructsMultiLevelHierarchy()
    {
        // Setup:
        // root (main)
        //  ├── branchA (scenario/barrier)
        //  │    └── subBranchA1 (scenario/barrier-variant)
        //  └── branchB (scenario/shelters)
        var root = MapSession.CreateDefaultRoot("main");
        var branchA = root.DeepClone("scenario/barrier");
        var subBranchA1 = branchA.DeepClone("scenario/barrier-variant");
        var branchB = root.DeepClone("scenario/shelters");

        var allSessions = new List<MapSession> { root, branchA, subBranchA1, branchB };

        // Act
        var tree = MapSessionTree.BuildTree(allSessions, activeSessionId: subBranchA1.Id);

        // Assert
        Assert.Single(tree); // Exactly 1 root
        var rootNode = tree[0];
        Assert.Equal("main", rootNode.BranchName);
        Assert.Equal(0, rootNode.Depth);
        Assert.False(rootNode.IsActive);
        Assert.Equal(2, rootNode.Children.Count);

        var nodeA = rootNode.Children.Find(c => c.BranchName == "scenario/barrier");
        Assert.NotNull(nodeA);
        Assert.Equal(1, nodeA.Depth);
        Assert.Single(nodeA.Children);

        var nodeA1 = nodeA.Children[0];
        Assert.Equal("scenario/barrier-variant", nodeA1.BranchName);
        Assert.Equal(2, nodeA1.Depth);
        Assert.True(nodeA1.IsActive); // Checked out active session!

        var nodeB = rootNode.Children.Find(c => c.BranchName == "scenario/shelters");
        Assert.NotNull(nodeB);
        Assert.Equal(1, nodeB.Depth);
        Assert.Empty(nodeB.Children);
    }

    [Fact]
    public void Flatten_ReturnsPreOrderTraversal()
    {
        var root = MapSession.CreateDefaultRoot("main");
        var branchA = root.DeepClone("branch-a");
        var subA = branchA.DeepClone("sub-a");
        var branchB = root.DeepClone("branch-b");

        var tree = MapSessionTree.BuildTree(new[] { root, branchA, subA, branchB });
        var flattened = MapSessionTree.Flatten(tree);

        Assert.Equal(4, flattened.Count);
        Assert.Equal("main", flattened[0].BranchName);
        Assert.Equal("branch-a", flattened[1].BranchName);
        Assert.Equal("sub-a", flattened[2].BranchName);
        Assert.Equal("branch-b", flattened[3].BranchName);
    }

    [Fact]
    public void GetLineage_ReturnsPathFromRootToTarget()
    {
        var root = MapSession.CreateDefaultRoot("main");
        var branchA = root.DeepClone("branch-a");
        var subA = branchA.DeepClone("sub-a");

        var lineage = MapSessionTree.GetLineage(new[] { root, branchA, subA }, subA.Id);

        Assert.Equal(3, lineage.Count);
        Assert.Equal(root.Id, lineage[0].Id);
        Assert.Equal(branchA.Id, lineage[1].Id);
        Assert.Equal(subA.Id, lineage[2].Id);
    }
}
