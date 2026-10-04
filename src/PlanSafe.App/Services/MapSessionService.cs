using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Session;

namespace PlanSafe.App.Services;

/// <inheritdoc />
public class MapSessionService : IMapSessionService
{
    private const string SessionsStorageKey = "plansafe_sessions_v1";
    private const string ActiveSessionStorageKey = "plansafe_active_session_id";
    private const string LegacyItemsStorageKey = "plansafe_map_items";

    private readonly IJSRuntime _jsRuntime;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private bool _isInitialized = false;
    private List<MapSession> _sessions = new();
    private string? _activeSessionId;

    /// <inheritdoc />
    public event Action<MapSession>? ActiveSessionChanged;
    /// <inheritdoc />
    public event Action? SessionsUpdated;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public MapSessionService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_isInitialized) return;

        await _lock.WaitAsync();
        try
        {
            if (_isInitialized) return;

            string? sessionsRaw = null;
            string? activeIdRaw = null;

            try
            {
                sessionsRaw = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", SessionsStorageKey);
                activeIdRaw = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", ActiveSessionStorageKey);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MapSessionService] Warning accessing localStorage: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(sessionsRaw))
            {
                try
                {
                    _sessions = JsonSerializer.Deserialize<List<MapSession>>(sessionsRaw, JsonOptions) ?? new();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MapSessionService] Error deserializing sessions, creating fallback: {ex.Message}");
                    _sessions = new List<MapSession>();
                }
            }

            // If empty, initialize root session with legacy migration
            if (_sessions.Count == 0)
            {
                var root = MapSession.CreateDefaultRoot();

                // Check for legacy items in localStorage to preserve user's past drawings
                try
                {
                    var legacyItemsRaw = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", LegacyItemsStorageKey);
                    if (!string.IsNullOrWhiteSpace(legacyItemsRaw))
                    {
                        var legacyItems = JsonSerializer.Deserialize<List<MapZoneItem>>(legacyItemsRaw, JsonOptions);
                        if (legacyItems != null && legacyItems.Count > 0)
                        {
                            root.Items = legacyItems;
                        }
                    }
                }
                catch
                {
                    // Ignore legacy migration failure
                }

                _sessions.Add(root);
                _activeSessionId = root.Id;
                await PersistToStorageAsync();
            }
            else
            {
                // Sanitize legacy "Baseline Scenario" prefix contamination from earlier sessions
                bool sanitized = false;
                foreach (var session in _sessions)
                {
                    if (session.IsRoot && (session.BranchName.StartsWith("Baseline", StringComparison.OrdinalIgnoreCase) || session.BranchName == "Baseline Scenario (Kraków)"))
                    {
                        session.BranchName = "main";
                        sanitized = true;
                    }
                    else if (session.BranchName.Contains("Baseline Scenario", StringComparison.OrdinalIgnoreCase))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(session.BranchName, @"Branch\s*(\d+)");
                        if (match.Success)
                        {
                            session.BranchName = $"scenario/branch-{match.Groups[1].Value}";
                        }
                        else
                        {
                            session.BranchName = session.BranchName.Replace("Baseline Scenario (Kraków)", "", StringComparison.OrdinalIgnoreCase).Trim(' ', '(', ')', '-');
                            if (string.IsNullOrWhiteSpace(session.BranchName))
                            {
                                session.BranchName = session.IsRoot ? "main" : $"scenario/branch-{session.Id.Substring(0, 4)}";
                            }
                        }
                        sanitized = true;
                    }
                }

                if (sanitized)
                {
                    await PersistToStorageAsync();
                }

                // Verify active session exists in loaded sessions
                if (!string.IsNullOrWhiteSpace(activeIdRaw) && _sessions.Any(s => s.Id == activeIdRaw))
                {
                    _activeSessionId = activeIdRaw;
                }
                else
                {
                    _activeSessionId = _sessions[0].Id;
                    await _jsRuntime.InvokeVoidAsync("localStorage.setItem", ActiveSessionStorageKey, _activeSessionId);
                }
            }

            _isInitialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<List<MapSession>> GetAllSessionsAsync()
    {
        await EnsureInitializedAsync();
        return _sessions.OrderBy(s => s.CreatedAt).ToList();
    }

    /// <inheritdoc />
    public async Task<MapSession?> GetSessionAsync(string id)
    {
        await EnsureInitializedAsync();
        return _sessions.FirstOrDefault(s => s.Id == id);
    }

    /// <inheritdoc />
    public async Task<MapSession> GetActiveSessionAsync()
    {
        await EnsureInitializedAsync();
        var active = _sessions.FirstOrDefault(s => s.Id == _activeSessionId) ?? _sessions[0];
        return active;
    }

    /// <inheritdoc />
    public async Task<MapSession> SetActiveSessionAsync(string id)
    {
        await EnsureInitializedAsync();

        var target = _sessions.FirstOrDefault(s => s.Id == id);
        if (target == null)
        {
            throw new ArgumentException($"Session with ID '{id}' not found.");
        }

        if (_activeSessionId != target.Id)
        {
            _activeSessionId = target.Id;
            try
            {
                await _jsRuntime.InvokeVoidAsync("localStorage.setItem", ActiveSessionStorageKey, _activeSessionId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MapSessionService] Failed writing active ID: {ex.Message}");
            }

            ActiveSessionChanged?.Invoke(target);
            SessionsUpdated?.Invoke();
        }

        return target;
    }

    /// <inheritdoc />
    public async Task<MapSession> SaveSessionAsync(MapSession session)
    {
        await EnsureInitializedAsync();

        await _lock.WaitAsync();
        try
        {
            session.UpdatedAt = DateTime.UtcNow;

            var existingIndex = _sessions.FindIndex(s => s.Id == session.Id);
            if (existingIndex >= 0)
            {
                _sessions[existingIndex] = session;
            }
            else
            {
                _sessions.Add(session);
            }

            await PersistToStorageAsync();
        }
        finally
        {
            _lock.Release();
        }

        if (session.Id == _activeSessionId)
        {
            ActiveSessionChanged?.Invoke(session);
        }
        SessionsUpdated?.Invoke();

        return session;
    }

    /// <inheritdoc />
    public async Task<MapSession> CreateBranchAsync(SessionBranchRequest request)
    {
        await EnsureInitializedAsync();

        if (string.IsNullOrWhiteSpace(request.ParentSessionId))
            throw new ArgumentException("Parent session ID cannot be empty.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.BranchName))
            throw new ArgumentException("Branch name cannot be empty.", nameof(request));

        MapSession child;

        await _lock.WaitAsync();
        try
        {
            var parent = _sessions.FirstOrDefault(s => s.Id == request.ParentSessionId);
            if (parent == null)
            {
                throw new InvalidOperationException($"Parent session '{request.ParentSessionId}' does not exist.");
            }

            child = parent.DeepClone(
                newBranchName: request.BranchName,
                newDescription: request.Description,
                copyItems: request.CopyItems);

            _sessions.Add(child);
            _activeSessionId = child.Id;

            await PersistToStorageAsync();
        }
        finally
        {
            _lock.Release();
        }

        ActiveSessionChanged?.Invoke(child);
        SessionsUpdated?.Invoke();

        return child;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSessionAsync(string id)
    {
        await EnsureInitializedAsync();

        await _lock.WaitAsync();
        try
        {
            if (_sessions.Count <= 1)
            {
                return false; // Don't delete the only existing session
            }

            var target = _sessions.FirstOrDefault(s => s.Id == id);
            if (target == null) return false;

            // Re-parent children if target has any children
            var children = _sessions.Where(s => s.ParentSessionId == id).ToList();
            foreach (var child in children)
            {
                child.ParentSessionId = target.ParentSessionId;
            }

            _sessions.Remove(target);

            // If we deleted the active session, switch to its parent or the first session
            if (_activeSessionId == id)
            {
                var newActive = _sessions.FirstOrDefault(s => s.Id == target.ParentSessionId) ?? _sessions[0];
                _activeSessionId = newActive.Id;
            }

            await PersistToStorageAsync();
        }
        finally
        {
            _lock.Release();
        }

        var active = await GetActiveSessionAsync();
        ActiveSessionChanged?.Invoke(active);
        SessionsUpdated?.Invoke();

        return true;
    }

    /// <inheritdoc />
    public async Task<List<MapSessionTreeNode>> GetSessionTreeAsync()
    {
        await EnsureInitializedAsync();
        return MapSessionTree.BuildTree(_sessions, _activeSessionId);
    }

    /// <inheritdoc />
    public async Task<string> ExportSessionsJsonAsync()
    {
        await EnsureInitializedAsync();
        return JsonSerializer.Serialize(_sessions, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <inheritdoc />
    public async Task<int> ImportSessionsJsonAsync(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;

        List<MapSession>? imported;
        try
        {
            imported = JsonSerializer.Deserialize<List<MapSession>>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            throw new FormatException($"Invalid sessions JSON format: {ex.Message}", ex);
        }

        if (imported == null || imported.Count == 0) return 0;

        await EnsureInitializedAsync();

        await _lock.WaitAsync();
        try
        {
            int addedCount = 0;
            foreach (var imp in imported)
            {
                var existingIdx = _sessions.FindIndex(s => s.Id == imp.Id);
                if (existingIdx >= 0)
                {
                    _sessions[existingIdx] = imp;
                }
                else
                {
                    _sessions.Add(imp);
                    addedCount++;
                }
            }

            await PersistToStorageAsync();
            return addedCount;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task PersistToStorageAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_sessions, JsonOptions);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", SessionsStorageKey, json);
            if (!string.IsNullOrEmpty(_activeSessionId))
            {
                await _jsRuntime.InvokeVoidAsync("localStorage.setItem", ActiveSessionStorageKey, _activeSessionId);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MapSessionService] Error persisting sessions to localStorage: {ex.Message}");
        }
    }
}
