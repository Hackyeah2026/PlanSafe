using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PlanSafe.Contracts.Models.Session;

namespace PlanSafe.App.Services;

/// <summary>
/// Service managing map sessions, Git-like scenario branches, and active session state.
/// </summary>
public interface IMapSessionService
{
    Task<List<MapSession>> GetAllSessionsAsync();
    Task<MapSession?> GetSessionAsync(string id);
    Task<MapSession> GetActiveSessionAsync();
    Task<MapSession> SetActiveSessionAsync(string id);
    Task<MapSession> SaveSessionAsync(MapSession session);
    Task<MapSession> CreateBranchAsync(SessionBranchRequest request);
    Task<bool> DeleteSessionAsync(string id);
    Task<List<MapSessionTreeNode>> GetSessionTreeAsync();
    Task<string> ExportSessionsJsonAsync();
    Task<int> ImportSessionsJsonAsync(string json);

    event Action<MapSession>? ActiveSessionChanged;
    event Action? SessionsUpdated;
}
