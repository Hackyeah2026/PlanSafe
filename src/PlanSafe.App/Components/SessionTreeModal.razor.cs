using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PlanSafe.Contracts.Models.Session;
using PlanSafe.App.Services;

namespace PlanSafe.App.Components;

public partial class SessionTreeModal : ComponentBase
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IMapSessionService SessionService { get; set; } = default!;

    [Parameter] public bool IsOpen { get; set; }
    [Parameter] public EventCallback<MapSession> OnCheckoutSession { get; set; }
    [Parameter] public EventCallback<MapSession> OnRequestBranch { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private class GraphNodeLayout
    {
        public MapSessionTreeNode Node { get; set; } = null!;
        public int RowIndex { get; set; }
        public int Depth => Node.Depth;
        public double X { get; set; }
        public double Y { get; set; }
        public string Color { get; set; } = "#38bdf8";
        public bool IsActive => Node.IsActive;
        public MapSession Session => Node.Session;
        public string? ParentId => Session.ParentSessionId;
    }

    private class GraphEdge
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public string Color { get; set; } = "#38bdf8";
        public bool IsActiveLineage { get; set; }
    }

    private static readonly string[] BranchColors = new[]
    {
        "#38bdf8", // Sky blue (root / main)
        "#34d399", // Emerald
        "#c084fc", // Purple
        "#fbbf24", // Amber
        "#f472b6", // Pink
        "#22d3ee", // Cyan
        "#818cf8", // Indigo
        "#fb923c"  // Orange
    };

    private const double RowHeight = 44.0;
    private const double LaneWidth = 28.0;
    private const double OffsetX = 24.0;
    private const double OffsetY = 22.0;

    private List<MapSession> _allSessions = new();
    private List<MapSessionTreeNode> _treeRoots = new();
    private List<GraphNodeLayout> _layoutNodes = new();
    private List<GraphEdge> _edges = new();
    private double _svgWidth = 80;
    private double _svgHeight = 100;

    private string? _activeSessionId;
    private string _searchQuery = string.Empty;
    private bool _isLoading = false;

    protected override async Task OnParametersSetAsync()
    {
        if (IsOpen)
        {
            await RefreshDataAsync();
        }
    }

    public async Task RefreshDataAsync()
    {
        _isLoading = true;
        try
        {
            _allSessions = await SessionService.GetAllSessionsAsync();
            var active = await SessionService.GetActiveSessionAsync();
            _activeSessionId = active.Id;

            FilterTree();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void ClearSearch()
    {
        _searchQuery = string.Empty;
        FilterTree();
    }

    private void FilterTree()
    {
        var filteredSessions = string.IsNullOrWhiteSpace(_searchQuery)
            ? _allSessions
            : _allSessions.Where(s =>
                s.BranchName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                (s.Description != null && s.Description.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))).ToList();

        _treeRoots = MapSessionTree.BuildTree(filteredSessions, _activeSessionId);
        var flatNodes = MapSessionTree.Flatten(_treeRoots);

        BuildGraphLayout(flatNodes);
    }

    private void BuildGraphLayout(List<MapSessionTreeNode> flatNodes)
    {
        _layoutNodes.Clear();
        _edges.Clear();

        if (flatNodes.Count == 0)
        {
            _svgWidth = 80;
            _svgHeight = 100;
            return;
        }

        int maxDepth = flatNodes.Max(n => n.Depth);
        _svgWidth = Math.Max(70.0, OffsetX + (maxDepth + 1) * LaneWidth + 12.0);
        _svgHeight = Math.Max(80.0, flatNodes.Count * RowHeight);

        // Active lineage set for highlighting the active branch path
        var activeLineage = _activeSessionId != null
            ? MapSessionTree.GetLineage(_allSessions, _activeSessionId).Select(s => s.Id).ToHashSet()
            : new HashSet<string>();

        var layoutDict = new Dictionary<string, GraphNodeLayout>();

        for (int i = 0; i < flatNodes.Count; i++)
        {
            var node = flatNodes[i];
            string color = node.IsRoot
                ? BranchColors[0]
                : BranchColors[1 + (Math.Abs(node.BranchName.GetHashCode()) % (BranchColors.Length - 1))];

            var layout = new GraphNodeLayout
            {
                Node = node,
                RowIndex = i,
                X = OffsetX + node.Depth * LaneWidth,
                Y = i * RowHeight + OffsetY,
                Color = color
            };

            _layoutNodes.Add(layout);
            layoutDict[node.Id] = layout;
        }

        // Build edges connecting children to parents
        foreach (var layout in _layoutNodes)
        {
            if (!string.IsNullOrWhiteSpace(layout.ParentId) && layoutDict.TryGetValue(layout.ParentId, out var parentLayout))
            {
                _edges.Add(new GraphEdge
                {
                    X1 = parentLayout.X,
                    Y1 = parentLayout.Y,
                    X2 = layout.X,
                    Y2 = layout.Y,
                    Color = layout.Color,
                    IsActiveLineage = activeLineage.Contains(layout.Session.Id)
                });
            }
        }
    }

    private static string GetBranchPath(double x1, double y1, double x2, double y2)
    {
        if (Math.Abs(x1 - x2) < 1.0)
        {
            return $"M {x1:F1} {y1:F1} L {x2:F1} {y2:F1}";
        }

        double forkStartY = Math.Max(y1, y2 - 22.0);
        double midY = (forkStartY + y2) / 2.0;

        return $"M {x1:F1} {y1:F1} L {x1:F1} {forkStartY:F1} C {x1:F1} {midY:F1}, {x2:F1} {midY:F1}, {x2:F1} {y2:F1}";
    }

    private async Task HandleCheckout(MapSession session)
    {
        await SessionService.SetActiveSessionAsync(session.Id);
        _activeSessionId = session.Id;
        FilterTree();

        if (OnCheckoutSession.HasDelegate)
        {
            await OnCheckoutSession.InvokeAsync(session);
        }
    }

    private async Task HandleBranchFrom(MapSession session)
    {
        if (OnRequestBranch.HasDelegate)
        {
            await OnRequestBranch.InvokeAsync(session);
        }
    }

    private async Task HandleNewBranchFromActive()
    {
        var active = await SessionService.GetActiveSessionAsync();
        await HandleBranchFrom(active);
    }

    private async Task HandleDelete(MapSession session)
    {
        bool confirmed = await JS.InvokeAsync<bool>("confirm", $"Are you sure you want to delete branch '{session.BranchName}'?");
        if (confirmed)
        {
            await SessionService.DeleteSessionAsync(session.Id);
            await RefreshDataAsync();
        }
    }

    private async Task ExportJson()
    {
        var json = await SessionService.ExportSessionsJsonAsync();
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", json);
        await JS.InvokeVoidAsync("alert", "Session tree JSON copied to clipboard!");
    }

    private static string GetRelativeTime(DateTime dt)
    {
        var diff = DateTime.UtcNow - dt;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return dt.ToString("MMM d");
    }

    private Task Close() => OnClose.InvokeAsync();
}
