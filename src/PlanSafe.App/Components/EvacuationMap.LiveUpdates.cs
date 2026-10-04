using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Models.Map;
using Microsoft.JSInterop;

namespace PlanSafe.App.Components;

public partial class EvacuationMap
{
    private MapSimulationBounds? _simulationBounds;
    private long _mapItemsRevision;
    private long _preparedMapRevision;
    private bool _isUpdatingMap;
    private Task? _mapUpdateTask;
    private bool IsSimulationBusy => _isStarting || _isUpdatingMap || _simulationStopsPending > 0;

    private Task RefreshSimulationMapAsync()
    {
        if (_mapUpdateTask is not null) return _mapUpdateTask;
        if (!_isSimulating || _simulationEngine is null || _simulationBounds is null ||
            _preparedMapRevision == _mapItemsRevision) return Task.CompletedTask;
        return _mapUpdateTask = UpdateSimulationMapAsync();
    }

    private async Task UpdateSimulationMapAsync()
    {
        bool resume = _isRunning;
        bool updated = false;
        _isUpdatingMap = true;
        _simulationCancellation?.Cancel();
        _preparationCancellation = new CancellationTokenSource();
        var cancellationToken = _preparationCancellation.Token;
        StateHasChanged();
        try
        {
            await Task.Yield();
            await StopSimulationLoopAsync();
            StateHasChanged();
            while (!_disposed && _preparedMapRevision != _mapItemsRevision)
            {
                long revision = _mapItemsRevision;
                var items = _mapItems.ToList();
                var evacuationZones = items.Where(item => item.Category == ZoneCategory.EvacuationZone).ToList();
                var safeLocations = items.Where(item => item.Category == ZoneCategory.SafeLocation).ToList();
                if (safeLocations.Count == 0)
                {
                    _simulationWarningMessage = () => L.Simulation.DestinationRequired;
                    return;
                }
                var previousBounds = _simulationBounds!;
                var bounds = new MapSimulationBounds(items, _activeSession, previousBounds);
                var prepared = await MapSimulationScenarioPreparer.PrepareAsync(bounds, items,
                    evacuationZones, safeLocations, OsmService, cancellationToken,
                    L.Shelter.Label, L.Demo.Exit, L.Demo.MainExit);
                if (revision != _mapItemsRevision) continue;
                for (int index = 0; index < prepared.Scenario.Exits.Length; index++)
                    prepared.Scenario.Exits[index] = prepared.Scenario.Exits[index] with { Capacity = 0 };
                double offsetX = (previousBounds.MinimumLongitude - bounds.MinimumLongitude) * bounds.MetersPerDegreeLongitude;
                double offsetY = (bounds.MaximumLatitude - previousBounds.MaximumLatitude) * bounds.MetersPerDegreeLatitude;
                await _simulationEngine!.UpdateMapAsync(prepared.Scenario, offsetX, offsetY, cancellationToken);
                await ConfigureSimulationRendererAsync(prepared);
                if (_activeEngine == "webgpu" && _simulationRenderer is not null)
                    await _simulationRenderer.InvokeVoidAsync("updateMapGpu", MapGpuSnapshot.Capture(_simulationEngine), offsetX, offsetY);
                _simulationBounds = bounds;
                _preparedMapRevision = revision;
                _simulationWarningMessage = null;
                await RequestRender();
            }
            updated = true;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception)
        {
            _simulationWarningMessage = () => L.Simulation.UpdateFailed(exception.Message);
        }
        finally
        {
            _isUpdatingMap = false;
            _preparationCancellation?.Dispose();
            _preparationCancellation = null;
            _mapUpdateTask = null;
            if (!_disposed)
            {
                if (updated && resume) StartSimulationLoop();
                StateHasChanged();
            }
        }
    }

    private async Task ConfigureSimulationRendererAsync(PreparedMapSimulation prepared)
    {
        if (_simulationRenderer is null) return;
        var bounds = prepared.Bounds;
        await _simulationRenderer.InvokeVoidAsync("setWorldConfig", new
        {
            worldWidth = bounds.WorldWidth,
            worldHeight = bounds.WorldHeight,
            originLat = bounds.MaximumLatitude,
            originLng = bounds.MinimumLongitude,
            minLat = bounds.MinimumLatitude,
            maxLng = bounds.MaximumLongitude,
            obstacles = Array.Empty<object>(),
            targets = prepared.Targets.Select(target => new
            {
                x = target.X,
                y = target.Y,
                width = target.Width,
                height = target.Height,
                name = target.Name,
                id = target.Id,
                capacity = 0
            })
        });
        var scenario = prepared.Scenario;
        await _simulationRenderer.InvokeVoidAsync("setMapTerrain",
            Array.ConvertAll(scenario.Blocked, blocked => blocked ? (byte)1 : (byte)0),
            scenario.Columns, scenario.Rows, scenario.CellSize);
    }
}
