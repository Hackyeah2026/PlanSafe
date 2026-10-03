using System;
using System.Collections.Generic;
using System.Linq;

namespace PlanSafe.Contracts.Models.Session;

/// <summary>
/// Utility algorithms for constructing, traversing, and querying Git-style MapSession trees.
/// </summary>
public static class MapSessionTree
{
    /// <summary>
    /// Constructs a hierarchical forest of session tree nodes from a flat collection of MapSessions.
    /// Handles cycles safely and treats orphaned nodes as secondary roots.
    /// </summary>
    public static List<MapSessionTreeNode> BuildTree(IEnumerable<MapSession> sessions, string? activeSessionId = null)
    {
        var sessionList = sessions.ToList();
        if (sessionList.Count == 0)
        {
            return new List<MapSessionTreeNode>();
        }

        var sessionMap = sessionList.ToDictionary(s => s.Id, s => s);
        var childrenLookup = sessionList
            .Where(s => !string.IsNullOrEmpty(s.ParentSessionId) && sessionMap.ContainsKey(s.ParentSessionId))
            .GroupBy(s => s.ParentSessionId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.CreatedAt).ToList());

        // Roots are nodes with no ParentSessionId, or whose parent does not exist in the collection
        var rootSessions = sessionList
            .Where(s => string.IsNullOrEmpty(s.ParentSessionId) || !sessionMap.ContainsKey(s.ParentSessionId))
            .OrderBy(s => s.CreatedAt)
            .ToList();

        var visited = new HashSet<string>();
        var result = new List<MapSessionTreeNode>();

        foreach (var root in rootSessions)
        {
            if (visited.Contains(root.Id)) continue;
            var rootNode = BuildSubtree(root, 0, activeSessionId, childrenLookup, visited);
            result.Add(rootNode);
        }

        // Safeguard for any disconnected loops
        foreach (var session in sessionList)
        {
            if (!visited.Contains(session.Id))
            {
                var orphanNode = BuildSubtree(session, 0, activeSessionId, childrenLookup, visited);
                result.Add(orphanNode);
            }
        }

        return result;
    }

    private static MapSessionTreeNode BuildSubtree(
        MapSession session,
        int depth,
        string? activeSessionId,
        Dictionary<string, List<MapSession>> childrenLookup,
        HashSet<string> visited)
    {
        visited.Add(session.Id);

        var node = new MapSessionTreeNode
        {
            Session = session,
            Depth = depth,
            IsActive = string.Equals(session.Id, activeSessionId, StringComparison.OrdinalIgnoreCase)
        };

        if (childrenLookup.TryGetValue(session.Id, out var children))
        {
            foreach (var childSession in children)
            {
                if (!visited.Contains(childSession.Id))
                {
                    var childNode = BuildSubtree(childSession, depth + 1, activeSessionId, childrenLookup, visited);
                    node.Children.Add(childNode);
                }
            }
        }

        return node;
    }

    /// <summary>
    /// Flattens hierarchical tree nodes into pre-order list (useful for indentation-based UI rendering).
    /// </summary>
    public static List<MapSessionTreeNode> Flatten(IEnumerable<MapSessionTreeNode> roots)
    {
        var list = new List<MapSessionTreeNode>();
        foreach (var root in roots)
        {
            FlattenRecursive(root, list);
        }
        return list;
    }

    private static void FlattenRecursive(MapSessionTreeNode current, List<MapSessionTreeNode> accumulator)
    {
        accumulator.Add(current);
        foreach (var child in current.Children)
        {
            FlattenRecursive(child, accumulator);
        }
    }

    /// <summary>
    /// Searches for a node with the specified session ID anywhere in the tree.
    /// </summary>
    public static MapSessionTreeNode? FindNode(IEnumerable<MapSessionTreeNode> roots, string sessionId)
    {
        foreach (var root in roots)
        {
            if (string.Equals(root.Id, sessionId, StringComparison.OrdinalIgnoreCase))
                return root;

            var found = FindNode(root.Children, sessionId);
            if (found != null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Resolves the ancestor path from root down to the specified session ID (Git branch history).
    /// </summary>
    public static List<MapSession> GetLineage(IEnumerable<MapSession> allSessions, string targetSessionId)
    {
        var sessionMap = allSessions.ToDictionary(s => s.Id, s => s);
        var lineage = new List<MapSession>();
        var currentId = targetSessionId;
        var visited = new HashSet<string>();

        while (!string.IsNullOrEmpty(currentId) && sessionMap.TryGetValue(currentId, out var current))
        {
            if (!visited.Add(currentId)) break; // cycle protection
            lineage.Insert(0, current);
            currentId = current.ParentSessionId;
        }

        return lineage;
    }
}
