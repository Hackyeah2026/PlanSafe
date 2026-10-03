using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using PlanSafe.Contracts.Models.Map;

namespace PlanSafe.Contracts.Models.Session;

/// <summary>
/// Represents a version-controlled map session containing a snapshot of all user-defined
/// zones, safe locations, blockades, map viewport settings, and simulation parameters.
/// Supports Git-like branching for testing alternative evacuation scenarios.
/// </summary>
public class MapSession
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("parentSessionId")]
    public string? ParentSessionId { get; set; }

    [JsonPropertyName("branchName")]
    public string BranchName { get; set; } = "main";

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name
    {
        get => BranchName;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && 
                (string.IsNullOrWhiteSpace(BranchName) || BranchName == "main") &&
                !value.StartsWith("Baseline Scenario", StringComparison.OrdinalIgnoreCase))
            {
                BranchName = value;
            }
        }
    }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("mapCenter")]
    public double[] MapCenter { get; set; } = [50.0614, 19.9366]; // Kraków city center

    [JsonPropertyName("zoomLevel")]
    public int ZoomLevel { get; set; } = 14;

    [JsonPropertyName("items")]
    public List<MapZoneItem> Items { get; set; } = new();

    [JsonPropertyName("simulationConfig")]
    public SimulationConfig SimulationConfig { get; set; } = new();

    [JsonPropertyName("tags")]
    public Dictionary<string, string> Tags { get; set; } = new();

    [JsonIgnore]
    public bool IsRoot => string.IsNullOrWhiteSpace(ParentSessionId);

    [JsonIgnore]
    public int EvacZoneCount => Items.Count(i => i.Category == ZoneCategory.EvacuationZone);

    [JsonIgnore]
    public int SafeZoneCount => Items.Count(i => i.Category == ZoneCategory.SafeLocation);

    [JsonIgnore]
    public int BlockadeCount => Items.Count(i => i.Category == ZoneCategory.Blockade);

    /// <summary>
    /// Creates a deep clone of this session as a new child branch.
    /// Clones all items independently so edits to the child branch never affect this session.
    /// </summary>
    public MapSession DeepClone(string newBranchName, string? newDescription = null, bool copyItems = true)
    {
        var child = new MapSession
        {
            Id = Guid.NewGuid().ToString("N"),
            ParentSessionId = this.Id,
            BranchName = newBranchName.Trim(),
            Description = string.IsNullOrWhiteSpace(newDescription) ? null : newDescription.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MapCenter = (double[])MapCenter.Clone(),
            ZoomLevel = ZoomLevel,
            SimulationConfig = SimulationConfig.Clone(),
            Tags = new Dictionary<string, string>(Tags),
            Items = copyItems
                ? Items.Select(i => i.Clone(generateNewId: true)).ToList()
                : new List<MapZoneItem>()
        };

        return child;
    }

    /// <summary>
    /// Factory creating the standard initial root session.
    /// </summary>
    public static MapSession CreateDefaultRoot(string branchName = "main")
    {
        return new MapSession
        {
            Id = Guid.NewGuid().ToString("N"),
            ParentSessionId = null,
            BranchName = branchName,
            Description = "Default baseline evacuation scenario centered on Kraków Old Town.",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MapCenter = [50.0614, 19.9366],
            ZoomLevel = 14,
            Items = new List<MapZoneItem>(),
            SimulationConfig = new SimulationConfig()
        };
    }
}
