using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PlanSafe.Contracts.Models.Session;

/// <summary>
/// Tree node representation of a MapSession for hierarchical visualization (tree map / git graph).
/// </summary>
public class MapSessionTreeNode
{
    [JsonPropertyName("session")]
    public MapSession Session { get; set; } = null!;

    [JsonPropertyName("children")]
    public List<MapSessionTreeNode> Children { get; set; } = new();

    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonIgnore]
    public string Id => Session.Id;

    [JsonIgnore]
    public string BranchName => Session.BranchName;

    [JsonIgnore]
    public string Name => Session.Name ?? Session.BranchName;

    [JsonIgnore]
    public bool IsRoot => Session.IsRoot;

    [JsonIgnore]
    public int ChildCount => Children.Count;
}
