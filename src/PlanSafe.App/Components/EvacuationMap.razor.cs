using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Text.Json;
using System.Diagnostics;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;
using PlanSafe.App.Services;
using PlanSafe.App.Services.Gus;
using PlanSafe.Contracts.Models.Session;
using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Models.Stats;

namespace PlanSafe.App.Components;

public partial class EvacuationMap : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IMapInterop MapInterop { get; set; } = default!;
    [Inject] private IMapSessionService SessionService { get; set; } = default!;
    [Inject] private IGusCensusService CensusService { get; set; } = default!;
    [Inject] private IGusOccupantGenerator OccupantGenerator { get; set; } = default!;
    [Inject] private PlanSafe.App.Services.Osm.IOsmObstacleService OsmService { get; set; } = default!;

    private string MapContainerId { get; set; } = $"map-{Guid.NewGuid():N}";
    private bool _isMobilePanelOpen = false;
    private bool _isMapInitialized = false;
    private DotNetObjectReference<EvacuationMap>? _dotNetRef;

    private List<MapZoneItem> _mapItems = new();
    private MapSession? _activeSession;
    private MapSession? _branchParentSession;
    private bool _isCreateBranchOpen = false;
    private bool _isTreeMapOpen = false;
    private bool _isStatsOpen = false;

    // On-Map Crowd Simulation Engine State
    private ElementReference _simulationCanvas;
    private string SimCanvasId { get; set; } = $"sim-canvas-{Guid.NewGuid():N}";
    private bool _isConfigOpen = false;
    private bool _isSimulating = false;
    private bool _isRunning = false;
    private bool _isStarting;
    private CancellationTokenSource? _preparationCancellation;
    private string _activeEngine = "wasm";
    private Task? _simulationLoopTask;
    private int _totalSimulationAgents = 0;
    private SimulationConfig _simulationConfig = new();
    private CrowdSimulationEngine? _simulationEngine;
    private CancellationTokenSource? _simulationCancellation;
    private readonly SimulationStatsCollector _statsCollector = new();
    private IJSObjectReference? _simulationModule;
    private IJSObjectReference? _simulationRenderer;
    private Func<string?>? _simulationWarningMessage;

    private string _activeCategory = "navigate"; // "navigate", "evac", "safe", "blockade"
    private string _selectedShape = "polygon"; // "polygon", "circle"
    private string _activeDrawMode = "none";
    private string? _activeDrawHint;
    private int _currentPointCount = 0;
    private int _censusPopulation = 0;
    private bool _isCensusLoading = true;

    private bool _disposed = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_isMapInitialized && !_disposed)
        {
            _isMapInitialized = true;
            _activeSession = await SessionService.GetActiveSessionAsync();
            if (_disposed) return;

            _branchParentSession = _activeSession;
            _simulationConfig = _activeSession.SimulationConfig?.Clone() ?? new SimulationConfig();

            double lat = _activeSession.MapCenter is { Length: >= 2 } ? _activeSession.MapCenter[0] : 50.0614;
            double lng = _activeSession.MapCenter is { Length: >= 2 } ? _activeSession.MapCenter[1] : 19.9366;
            int zoom = _activeSession.ZoomLevel > 0 ? _activeSession.ZoomLevel : 14;

            _dotNetRef = DotNetObjectReference.Create(this);
            if (_disposed)
            {
                _dotNetRef.Dispose();
                _dotNetRef = null;
                return;
            }

            try
            {
                await MapInterop.InitializeMapAsync(MapContainerId, lat, lng, zoom, _dotNetRef);
            }
            catch (Exception) when (_disposed)
            {
                return;
            }

            if (_disposed) return;

            try
            {
                await MapInterop.LoadSessionItemsAsync(MapContainerId, _activeSession.Items, lat, lng, zoom);
            }
            catch (Exception) when (_disposed)
            {
                return;
            }

            if (_disposed) return;

            _mapItems = _activeSession.Items;
            StateHasChanged();

            // Create the map before loading population data. Terrain loads when simulation starts.
            await CensusService.EnsureLoadedAsync();
            if (_disposed) return;
            _isCensusLoading = false;

            RecalculateCensusData();
            if (_simulationConfig.UseGusCensus && _censusPopulation > 0)
            {
                _simulationConfig.AgentCount = _censusPopulation;
            }
            StateHasChanged();
        }
    }

    [JSInvokable]
    public async Task OnItemsUpdated(string itemsJson)
    {
        if (_disposed) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(itemsJson))
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                _mapItems = JsonSerializer.Deserialize<List<MapZoneItem>>(itemsJson, options) ?? new();
            }
            else
            {
                _mapItems.Clear();
            }

            if (_activeSession != null)
            {
                _activeSession.Items = _mapItems;
                await SessionService.SaveSessionAsync(_activeSession);
                if (_disposed) return;
            }

            if (_disposed) return;
            RecalculateCensusData();
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlanSafe] Error deserializing map items: {ex.Message}");
        }
    }

    private async Task HandleCheckoutSession(MapSession session)
    {
        _activeSession = session;
        _branchParentSession = session;
        _mapItems = session.Items;
        _simulationConfig = session.SimulationConfig?.Clone() ?? new SimulationConfig();
        _isTreeMapOpen = false;

        double lat = session.MapCenter is { Length: >= 2 } ? session.MapCenter[0] : 50.0614;
        double lng = session.MapCenter is { Length: >= 2 } ? session.MapCenter[1] : 19.9366;
        int zoom = session.ZoomLevel > 0 ? session.ZoomLevel : 14;

        await MapInterop.LoadSessionItemsAsync(MapContainerId, session.Items, lat, lng, zoom);
        RecalculateCensusData();
        StateHasChanged();
    }

    private void HandleRequestBranchFromTree(MapSession parent)
    {
        _branchParentSession = parent;
        _isTreeMapOpen = false;
        _isCreateBranchOpen = true;
    }

    private async Task HandleBranchCreated(SessionBranchRequest request)
    {
        var newBranch = await SessionService.CreateBranchAsync(request);
        await HandleCheckoutSession(newBranch);
    }

    private async Task HandleStartEvacuation()
    {
        if (_isStarting || _isPublishPlanOpen) return;
        await StopSimulation();
        _isPublishPlanOpen = true;
    }

    private async Task HandleStartSimulation()
    {
        if (_isStarting) return;
        if (_isSimulating)
        {
            await StopSimulation();
            return;
        }

        _simulationWarningMessage = null;

        if (_isCensusLoading)
        {
            _simulationWarningMessage = () => L.Census.Wait;
            StateHasChanged();
            return;
        }

        var evacuationZones = _mapItems.Where(i => i.Category == ZoneCategory.EvacuationZone).ToList();
        var safeLocations = _mapItems.Where(i => i.Category == ZoneCategory.SafeLocation).ToList();

        if (evacuationZones.Count == 0)
        {
            _simulationWarningMessage = () => L.Simulation.ZoneRequired;
            StateHasChanged();
            return;
        }

        if (safeLocations.Count == 0)
        {
            _simulationWarningMessage = () => L.Simulation.DestinationRequired;
            StateHasChanged();
            return;
        }

        try
        {
            _isStarting = true;
            _preparationCancellation = new CancellationTokenSource();
            var preparationToken = _preparationCancellation.Token;
            StateHasChanged();
            await Task.Delay(1, preparationToken);
            await StopSimulationLoopAsync();

            var mapBounds = new MapSimulationBounds(_mapItems, _activeSession);
            var preparedMap = await MapSimulationScenarioPreparer.PrepareAsync(
                mapBounds, _mapItems, evacuationZones, safeLocations, OsmService, preparationToken,
                L.Shelter.Label, L.Demo.Exit, L.Demo.MainExit);
            var mapScenario = preparedMap.Scenario;
            var targets = preparedMap.Targets;
            var toWorld = mapBounds.ToWorld;

            // 4. Generate Occupant Agents inside Evacuation Zones
            var initialPositions = new List<(double X, double Y)>();
            if (_simulationConfig.UseGusCensus)
            {
                var occupants = await OccupantGenerator.GenerateOccupantsAsync(evacuationZones, cancellationToken: preparationToken);
                foreach (var occupant in occupants)
                {
                    initialPositions.Add(toWorld(occupant.Latitude, occupant.Longitude));
                }
            }

            if (initialPositions.Count == 0 || !_simulationConfig.UseGusCensus)
            {
                int targetCount = !_simulationConfig.UseGusCensus && _simulationConfig.AgentCount > 0
                    ? _simulationConfig.AgentCount
                    : Math.Max(100, _censusPopulation > 0 ? _censusPopulation : 500);
                initialPositions = await EvacuationZoneSampler.GenerateAsync(evacuationZones, targetCount, toWorld, preparationToken);
            }

            _totalSimulationAgents = initialPositions.Count;
            int capacity = 0;
            for (int i = 0; i < targets.Count; i++)
                targets[i] = targets[i] with { Capacity = capacity };
            for (int i = 0; i < mapScenario.Exits.Length; i++)
                mapScenario.Exits[i] = mapScenario.Exits[i] with { Capacity = capacity };

            // 5. Initialize Unified Simulation Engine with MapScenario
            _simulationEngine = await CrowdSimulationEngine.CreateMapAsync(mapScenario, initialPositions,
                _simulationConfig.Granulation, _simulationConfig.SocialRepulsionWeight, cancellationToken: preparationToken);
            if (_disposed) return;
            _simulationEngine.WhiskerLength = _simulationConfig.WhiskerLength;
            _statsCollector.Reset(_simulationEngine.AgentCount);

            // 6. Initialize JS Canvas Overlay
            _simulationModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/crowdSimulatorInterop.js");
            _simulationRenderer ??= await _simulationModule.InvokeAsync<IJSObjectReference>("initMapSimulator", _simulationCanvas, MapContainerId);

            await _simulationRenderer.InvokeVoidAsync("setWorldConfig", new
            {
                worldWidth = mapBounds.WorldWidth,
                worldHeight = mapBounds.WorldHeight,
                originLat = mapBounds.MaximumLatitude,
                originLng = mapBounds.MinimumLongitude,
                minLat = mapBounds.MinimumLatitude,
                maxLng = mapBounds.MaximumLongitude,
                obstacles = Array.Empty<object>(),
                targets = targets.Select(t => new { x = t.X, y = t.Y, width = t.Width, height = t.Height, name = t.Name, id = t.Id, capacity = t.Capacity })
            });

            await _simulationRenderer.InvokeVoidAsync("setMapTerrain",
                Array.ConvertAll(mapScenario.Blocked, blocked => blocked ? (byte)1 : (byte)0),
                mapScenario.Columns, mapScenario.Rows, mapScenario.CellSize);

            _activeEngine = "wasm";
            try
            {
                string? preferredEngine = null;
                try { preferredEngine = await JS.InvokeAsync<string?>("localStorage.getItem", "plansafe_simulation_engine"); }
                catch (JSException) { }
                if (preferredEngine != "wasm" && await _simulationModule.InvokeAsync<bool>("checkWebGpuSupport"))
                {
                    await _simulationRenderer.InvokeVoidAsync("initMapGpu", MapGpuSnapshot.Capture(_simulationEngine));
                    _activeEngine = "webgpu";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlanSafe] Map WebGPU initialization failed; using WASM: {ex.Message}");
            }

            if (_disposed) return;
            // 7. Prepare the first frame before revealing the overlay.
            await RequestRender();
            _isSimulating = true;

            // 8. Start simulation loop
            StartSimulationLoop();
            StateHasChanged();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            _simulationWarningMessage = () => L.Simulation.StartFailed(ex.Message);
            StateHasChanged();
        }
        finally
        {
            _isStarting = false;
            _preparationCancellation?.Dispose();
            _preparationCancellation = null;
            if (!_disposed) StateHasChanged();
        }
    }

    private async Task RequestRender()
    {
        if (_simulationRenderer == null || _simulationEngine == null) return;
        try
        {
            if (_activeEngine == "webgpu")
            {
                await _simulationRenderer.InvokeVoidAsync("renderGpu", _simulationConfig.RenderMode, _simulationConfig.ShowWhiskers,
                    _simulationConfig.WhiskerLength, _simulationConfig.Granulation);
                return;
            }
            int count = _simulationEngine.SimulatedAgentCount;
            await _simulationRenderer.InvokeVoidAsync("render",
                _simulationEngine.AgentPositionX[..count],
                _simulationEngine.AgentPositionY[..count],
                _simulationEngine.AgentVelocityX[..count],
                _simulationEngine.AgentVelocityY[..count],
                _simulationEngine.AgentRadius[..count],
                count,
                _simulationConfig.RenderMode,
                _simulationConfig.ShowWhiskers,
                _simulationConfig.WhiskerLength,
                _simulationConfig.Granulation);
        }
        catch { }
    }

    private async Task RunSimulationLoopAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        long lastRenderTime = sw.ElapsedMilliseconds;
        long lastTelemetryTime = 0;
        long lastUiTime = 0;
        double lastStepTime = 0;
        double tickBudget = 0;

        while (!cancellationToken.IsCancellationRequested && _isRunning && _simulationEngine != null)
        {
            double stepTime = sw.Elapsed.TotalSeconds;
            if (_simulationConfig.Unlimited) tickBudget = 0;
            else tickBudget += (stepTime - lastStepTime) * _simulationConfig.TimeScale / 0.016;
            lastStepTime = stepTime;
            int batch = _simulationConfig.Unlimited ? 32 : Math.Clamp((int)Math.Floor(tickBudget), 0, 32);
            if (_activeEngine == "webgpu" && _simulationRenderer != null)
            {
                if (batch > 0) await _simulationRenderer.InvokeVoidAsync("stepGpu", batch);
                if (sw.ElapsedMilliseconds - lastTelemetryTime >= 100)
                {
                    await SampleGpuTelemetry();
                    lastTelemetryTime = sw.ElapsedMilliseconds;
                }
            }
            else if (batch > 0)
            {
                long chunkStart = sw.ElapsedMilliseconds;
                int completed = 0;
                for (int tick = 0; tick < batch; tick++)
                {
                    _simulationEngine.Step(0.016, 1.0);
                    completed++;
                    if (sw.ElapsedMilliseconds - chunkStart >= 12) break;
                }
                batch = completed;
                _statsCollector.SampleTelemetry(_simulationEngine);
            }
            if (!_simulationConfig.Unlimited) tickBudget -= batch;

            if (_statsCollector.Stats.IsComplete)
            {
                _isRunning = false;
                await RequestRender();
                await InvokeAsync(StateHasChanged);
                break;
            }

            long now = sw.ElapsedMilliseconds;
            int frameInterval = _simulationConfig.RenderFps switch
            {
                "15" => 66,
                "30" => 33,
                "60" => 16,
                _ => 16
            };

            if (now - lastRenderTime >= frameInterval)
            {
                lastRenderTime = now;
                await RequestRender();
            }

            if (now - lastUiTime >= 100)
            {
                lastUiTime = now;
                await InvokeAsync(StateHasChanged);
            }

            await Task.Delay(1);
        }
    }

    private void StartSimulationLoop()
    {
        _simulationCancellation?.Dispose();
        _simulationCancellation = new CancellationTokenSource();
        _isRunning = true;
        _simulationLoopTask = RunSimulationLoopAsync(_simulationCancellation.Token);
    }

    private async Task StopSimulationLoopAsync()
    {
        _simulationCancellation?.Cancel();
        try
        {
            if (_simulationLoopTask != null) await _simulationLoopTask;
            if (_isRunning && _activeEngine == "webgpu") await SampleGpuTelemetry();
        }
        finally
        {
            _isRunning = false;
        }
    }

    private async Task SampleGpuTelemetry()
    {
        if (_simulationRenderer == null) return;
        var sample = await _simulationRenderer.InvokeAsync<GpuSimulationTelemetry?>("getGpuTelemetry");
        if (sample != null) _statsCollector.SampleTelemetry(sample);
    }

    private async Task ToggleSimulation()
    {
        if (_isStarting) return;
        if (!_isRunning)
        {
            if (_statsCollector.Stats.IsComplete) await ResetSimulation();
            StartSimulationLoop();
        }
        else
        {
            await StopSimulationLoopAsync();
        }
        await InvokeAsync(StateHasChanged);
    }

    private async Task StepSimulation()
    {
        if (_isStarting || _isRunning || _simulationEngine == null) return;
        if (_activeEngine == "webgpu" && _simulationRenderer != null)
        {
            await _simulationRenderer.InvokeVoidAsync("stepGpu", 1);
            await SampleGpuTelemetry();
        }
        else
        {
            _simulationEngine.Step(0.016, 1.0);
            _statsCollector.SampleTelemetry(_simulationEngine);
        }
        await RequestRender();
        await InvokeAsync(StateHasChanged);
    }

    private async Task ResetSimulation()
    {
        if (_isStarting) return;
        await StopSimulationLoopAsync();
        if (_simulationEngine != null)
        {
            if (_activeEngine == "webgpu" && _simulationRenderer != null)
                await _simulationRenderer.InvokeVoidAsync("resetMapGpu");
            else _simulationEngine.Reset();
            _statsCollector.Reset(_simulationEngine.AgentCount);
        }
        await RequestRender();
        await InvokeAsync(StateHasChanged);
    }

    private async Task StopSimulation()
    {
        if (_isStarting) return;
        await StopSimulationLoopAsync();
        _isSimulating = false;
        if (_simulationRenderer != null)
        {
            try { await _simulationRenderer.InvokeVoidAsync("setWorldConfig", new { }); } catch { }
        }
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleRenderModeChanged(string mode)
    {
        _simulationConfig.RenderMode = mode;
        await RequestRender();
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleToggleWhiskers()
    {
        _simulationConfig.ShowWhiskers = !_simulationConfig.ShowWhiskers;
        await RequestRender();
        await InvokeAsync(StateHasChanged);
    }

    private Task HandleCycleSpeed()
    {
        if (_simulationConfig.Unlimited)
        {
            _simulationConfig.Unlimited = false;
            _simulationConfig.TimeScale = 1.0f;
        }
        else if (_simulationConfig.TimeScale < 2.0f)
        {
            _simulationConfig.TimeScale = 2.5f;
        }
        else if (_simulationConfig.TimeScale < 5.0f)
        {
            _simulationConfig.TimeScale = 5.0f;
        }
        else if (_simulationConfig.TimeScale < 10.0f)
        {
            _simulationConfig.TimeScale = 15.0f;
        }
        else
        {
            _simulationConfig.Unlimited = true;
        }
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task HandleSaveConfig(SimulationConfig config)
    {
        bool resume = _isRunning;
        await StopSimulationLoopAsync();
        bool granulationChanged = _simulationConfig.Granulation != config.Granulation;
        _simulationConfig = config.Clone();
        if (_activeSession != null)
        {
            _activeSession.SimulationConfig = _simulationConfig.Clone();
            await SessionService.SaveSessionAsync(_activeSession);
        }
        if (_simulationEngine != null)
        {
            _simulationEngine.SocialRepulsionWeight = _simulationConfig.SocialRepulsionWeight;
            _simulationEngine.WhiskerLength = _simulationConfig.WhiskerLength;
            _simulationEngine.WeightDistance = _simulationConfig.WeightDistance;
            _simulationEngine.WeightOccupancy = _simulationConfig.WeightOccupancy;
            _simulationEngine.PotentialFieldMap.RefineDynamicField();
            if (granulationChanged)
            {
                _simulationEngine.SetGranulation(_simulationConfig.Granulation);
                _statsCollector.Reset(_simulationEngine.AgentCount);
            }
            if (_activeEngine == "webgpu" && _simulationRenderer != null)
            {
                if (granulationChanged)
                    await _simulationRenderer.InvokeVoidAsync("initMapGpu", MapGpuSnapshot.Capture(_simulationEngine));
                else await _simulationRenderer.InvokeVoidAsync("setGpuWeight", _simulationConfig.SocialRepulsionWeight);
            }
        }
        await RequestRender();
        if (resume) StartSimulationLoop();
        StateHasChanged();
    }

    private int PlannedAgentCount => _isSimulating ? _totalSimulationAgents
        : !_simulationConfig.UseGusCensus && _simulationConfig.AgentCount > 0 ? _simulationConfig.AgentCount
        : _censusPopulation > 0 ? _censusPopulation : 500;
    private void RecalculateCensusData()
    {
        if (CensusService.IsLoaded)
        {
            var (pop, _) = CensusService.CalculateEvacuationPopulation(_mapItems);
            _censusPopulation = pop;
        }
    }

    [JSInvokable]
    public void OnDrawModeChanged(string mode, string? hint, int pointCount = 0)
    {
        if (_disposed) return;
        _activeDrawMode = mode ?? "none";
        _activeDrawHint = hint;
        _currentPointCount = pointCount;

        if (_activeDrawMode == "none")
        {
            _activeCategory = "navigate";
        }
        StateHasChanged();
    }

    private async Task SelectCategory(string category)
    {
        _activeCategory = category;
        _currentPointCount = 0;

        string targetMode = ResolveTargetMode();
        await MapInterop.SetDrawModeAsync(MapContainerId, targetMode);
    }

    private async Task SelectShape(string shape)
    {
        _selectedShape = shape;
        _currentPointCount = 0;

        string targetMode = ResolveTargetMode();
        await MapInterop.SetDrawModeAsync(MapContainerId, targetMode);
    }

    private string ResolveTargetMode()
    {
        return _activeCategory switch
        {
            "navigate" => "none",
            "evac" => _selectedShape == "circle" ? "evac_circle" : "evac_polygon",
            "safe" => _selectedShape == "circle" ? "safe_circle" : "safe_polygon",
            "blockade" => "blockade",
            _ => "none"
        };
    }

    private async Task FinishPolygon()
    {
        await MapInterop.FinishPolygonAsync(MapContainerId);
    }

    private async Task CancelDrawing()
    {
        _activeCategory = "navigate";
        _activeDrawMode = "none";
        _currentPointCount = 0;
        await MapInterop.CancelCurrentDrawingAsync(MapContainerId);
    }

    private async Task HandleDeleteItem(string itemId)
    {
        await MapInterop.DeleteMapItemAsync(MapContainerId, itemId);
    }

    private async Task HandleClearAll()
    {
        await MapInterop.ClearAllMapItemsAsync(MapContainerId);
    }

    private async Task HandlePanToItem(string itemId)
    {
        await MapInterop.PanToMapItemAsync(MapContainerId, itemId);
    }

    private async Task HandleRecenter()
    {
        await MapInterop.CenterOnKrakowAsync(MapContainerId);
    }

    private async Task ToggleMobilePanel()
    {
        _isMobilePanelOpen = !_isMobilePanelOpen;
        StateHasChanged();
        await Task.Delay(250);
        await MapInterop.InvalidateSizeAsync(MapContainerId);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _preparationCancellation?.Cancel();
        await StopSimulationLoopAsync();
        _simulationCancellation?.Dispose();

        if (_simulationRenderer != null)
        {
            try
            {
                await _simulationRenderer.InvokeVoidAsync("dispose");
                await _simulationRenderer.DisposeAsync();
            }
            catch
            {
                // Ignore disposal errors on teardown
            }
            _simulationRenderer = null;
        }
        if (_isMapInitialized)
        {
            try
            {
                await MapInterop.DisposeMapAsync(MapContainerId);
            }
            catch
            {
                // Ignore JS disconnected or unmount errors
            }
        }
        try
        {
            _dotNetRef?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _dotNetRef = null;
        }
    }
}
