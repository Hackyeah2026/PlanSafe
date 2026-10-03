using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PlanSafe.Contracts.Models.Session;

/// <summary>
/// DTO for requesting the creation of a new scenario branch from an existing session.
/// </summary>
public class SessionBranchRequest
{
    [Required]
    [JsonPropertyName("parentSessionId")]
    public string ParentSessionId { get; set; } = string.Empty;

    [Required]
    [MinLength(1)]
    [JsonPropertyName("branchName")]
    public string BranchName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("copyItems")]
    public bool CopyItems { get; set; } = true;
}
