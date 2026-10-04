using Microsoft.JSInterop;

namespace PlanSafe.App.Components;

public partial class EvacuationMap
{
    private IJSObjectReference? _tourModule;
    private bool _tourPending;
    private bool _tourReady;
    private bool _tourPreview;
    private bool _panelBeforeTour;

    private async Task InitializeMapTour()
    {
        try
        {
            var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/mapTour.js");
            if (_disposed) { await module.DisposeAsync(); return; }
            _tourModule = module;
            if (await module.InvokeAsync<bool>("shouldStart") && !_disposed)
                PrepareMapTour();
        }
        catch (JSException ex)
        {
            Console.WriteLine($"[PlanSafe] Map tour unavailable: {ex.Message}");
        }
    }

    private void PrepareMapTour()
    {
        _panelBeforeTour = _isMobilePanelOpen;
        _isMobilePanelOpen = true;
        _tourPreview = true;
        _tourPending = true;
    }

    private async Task ReplayMapTour()
    {
        if (!_tourReady || _tourPreview || _isSimulating || _isStarting || _activeDrawMode != "none") return;
        if (_tourModule == null) await InitializeMapTour();
        if (_tourModule != null && !_tourPreview) PrepareMapTour();
    }

    [JSInvokable]
    public void OnMapTourClosed()
    {
        if (_disposed) return;
        _tourPreview = false;
        _isMobilePanelOpen = _panelBeforeTour;
        StateHasChanged();
    }

    private async Task DisposeMapTour()
    {
        if (_tourModule == null) return;
        try
        {
            await _tourModule.InvokeVoidAsync("dispose", MapContainerId);
            await _tourModule.DisposeAsync();
        }
        catch (JSException) { }
    }
}
