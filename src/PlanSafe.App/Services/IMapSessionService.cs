using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PlanSafe.Contracts.Models.Session;

namespace PlanSafe.App.Services;

/// <summary>
/// Service managing map sessions, Git-like scenario branches, and active session state.
/// </summary>
/// <remarks>
/// Returned sessions are mutable service-owned objects. Save changes explicitly.
/// Mutation storage failures are logged and do not roll back in-memory changes.
/// </remarks>
public interface IMapSessionService
{
    /// <summary>Returns sessions ordered by creation time.</summary>
    /// <returns>A new list containing the service-owned session objects.</returns>
    Task<List<MapSession>> GetAllSessionsAsync();

    /// <summary>Finds a session by its exact ID.</summary>
    /// <param name="id">Session ID to find.</param>
    /// <returns>The stored session, or null when no ID matches.</returns>
    Task<MapSession?> GetSessionAsync(string id);

    /// <summary>Returns the active session, initializing a root session when storage is empty.</summary>
    /// <returns>The service-owned active session.</returns>
    Task<MapSession> GetActiveSessionAsync();

    /// <summary>Selects a stored session and notifies subscribers when the selection changes.</summary>
    /// <param name="id">Exact ID of the session to select.</param>
    /// <returns>The selected session.</returns>
    /// <exception cref="ArgumentException">No stored session has the requested ID.</exception>
    Task<MapSession> SetActiveSessionAsync(string id);

    /// <summary>Adds or replaces a session, updates its timestamp, and attempts to persist all sessions.</summary>
    /// <param name="session">Session object to store without cloning.</param>
    /// <returns>The same session object.</returns>
    Task<MapSession> SaveSessionAsync(MapSession session);

    /// <summary>Clones a parent into a new active branch and notifies subscribers.</summary>
    /// <param name="request">Parent ID, branch name, description, and item-copy preference.</param>
    /// <returns>The newly stored branch.</returns>
    /// <exception cref="ArgumentException">The parent ID or branch name is empty.</exception>
    /// <exception cref="InvalidOperationException">The requested parent does not exist.</exception>
    Task<MapSession> CreateBranchAsync(SessionBranchRequest request);

    /// <summary>Deletes a session, reparents its children, and replaces the active selection if needed.</summary>
    /// <param name="id">Exact ID of the session to delete.</param>
    /// <returns>False when the session is missing or is the only remaining session; otherwise true.</returns>
    Task<bool> DeleteSessionAsync(string id);

    /// <summary>Builds the scenario hierarchy with the current active selection marked.</summary>
    /// <returns>The root nodes of the session tree.</returns>
    Task<List<MapSessionTreeNode>> GetSessionTreeAsync();

    /// <summary>Serializes all sessions as indented JSON for export.</summary>
    /// <returns>JSON containing the stored session objects.</returns>
    Task<string> ExportSessionsJsonAsync();

    /// <summary>Merges JSON sessions by ID, replacing existing entries and adding new ones.</summary>
    /// <param name="json">A JSON session array; empty input adds nothing.</param>
    /// <returns>The number of new IDs added; replacements are not counted.</returns>
    /// <exception cref="FormatException">The supplied JSON cannot be deserialized as sessions.</exception>
    /// <remarks>The current implementation does not raise session change events after an import.</remarks>
    Task<int> ImportSessionsJsonAsync(string json);

    /// <summary>Signals active selection changes or saves to the active session.</summary>
    event Action<MapSession>? ActiveSessionChanged;

    /// <summary>Signals session saves, branch creation, deletion, or active selection changes.</summary>
    event Action? SessionsUpdated;
}
