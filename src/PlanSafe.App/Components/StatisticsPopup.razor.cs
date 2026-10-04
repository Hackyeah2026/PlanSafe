using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using PlanSafe.Contracts.Models.Stats;
using System.Globalization;
using PlanSafe.App.Components.Statistics;

namespace PlanSafe.App.Components;

public partial class StatisticsPopup : ComponentBase
{
    [Parameter] public bool IsOpen { get; set; } = false;
    [Parameter] public bool IsLive { get; set; } = true;
    [Parameter] public SimulationLiveStats Stats { get; set; } = new();
    [Parameter] public EventCallback OnClose { get; set; }

    private string _activeTab = "cohorts";
    private string _previousTab = "cohorts";
    private bool _isMinimized = false;
    private bool _isMaximized = false;

    // Draggable position
    private double _posX = 24;
    private double _posY = 70;
    private double _dragX = 0;
    private double _dragY = 0;
    private bool _isDragging = false;
    private double _dragStartX;
    private double _dragStartY;
    private double _dragOriginX;
    private double _dragOriginY;

    private string GetOverlayStyle()
    {
        if (_isMaximized)
        {
            return "position: fixed; top: 8px; left: 8px; right: 8px; bottom: 8px; z-index: 1200; width: calc(100vw - 16px); height: calc(100vh - 16px); transform: none !important;";
        }
        string transform = (_dragX != 0 || _dragY != 0)
            ? $" transform: translate({_dragX.ToString("0.#", CultureInfo.InvariantCulture)}px, {_dragY.ToString("0.#", CultureInfo.InvariantCulture)}px);"
            : "";
        return $"position: fixed; top: {_posY}px; right: {_posX}px; z-index: 1200; max-height: calc(100vh - 90px);{transform}";
    }

    private void StartDrag(PointerEventArgs e)
    {
        if (_isMaximized || e.Button != 0) return;
        _isDragging = true;
        _dragStartX = e.ClientX;
        _dragStartY = e.ClientY;
        _dragOriginX = _dragX;
        _dragOriginY = _dragY;
    }

    private void OnDragMove(PointerEventArgs e)
    {
        if (!_isDragging || _isMaximized) return;
        if (e.Buttons == 0)
        {
            EndDrag(e);
            return;
        }

        double newDragX = _dragOriginX + (e.ClientX - _dragStartX);
        double newDragY = _dragOriginY + (e.ClientY - _dragStartY);

        // Clamping bounds to ensure the header always stays reachable on screen
        _dragX = Math.Clamp(newDragX, -2500, 750);
        _dragY = Math.Clamp(newDragY, -60, 900);
    }

    private void EndDrag(PointerEventArgs e)
    {
        _isDragging = false;
    }

    private void ResetPosition()
    {
        _dragX = 0;
        _dragY = 0;
    }

    private void ToggleMinimize()
    {
        _isMinimized = !_isMinimized;
    }

    private void ToggleMaximize()
    {
        _isMaximized = !_isMaximized;
        if (_isMaximized)
        {
            _isMinimized = false;
            if (_activeTab != "all")
            {
                _previousTab = _activeTab;
            }
            _activeTab = "all";
        }
        else
        {
            _activeTab = _previousTab == "all" ? "cohorts" : _previousTab;
        }
    }

    private async Task Close()
    {
        if (OnClose.HasDelegate)
        {
            await OnClose.InvokeAsync();
        }
    }
}
