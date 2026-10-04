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

public partial class EvacuationMap : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IMapInterop MapInterop { get; set; } = default!;
    [Inject] private IMapSessionService SessionService { get; set; } = default!;
    [Inject] private IGusCensusService CensusService { get; set; } = default!;
    [Inject] private IGusOccupantGenerator OccupantGenerator { get; set; } = default!;
    [Inject] private PlanSafe.App.Services.Osm.IOsmObstacleService OsmService { get; set; } = default!;

    private string MapContainerId { get; set; } = $"map-{Guid.NewGuid():N}";
    private bool isMobilePanelOpen = false;
    private bool isMapInitialized = false;
    private DotNetObjectReference<EvacuationMap>? _dotNetRef;

    private List<MapZoneItem> _mapItems = new();
    private MapSession? _activeSession;
    private MapSession? _branchParentSession;
    private bool _isCreateBranchOpen = false;
    private bool _isTreeMapOpen = false;
    private bool _isStatsOpen = false;
    private readonly SimulationLiveStats _sampleStats = SimulationStatsCollector.CreateSampleData();

    // On-Map Crowd Simulation Engine State
    private ElementReference _simCanvasRef;
    private string SimCanvasId { get; set; } = $"sim-canvas-{Guid.NewGuid():N}";
    private bool _isConfigOpen = false;
    private bool _isSimulating = false;
    private bool _isRunning = false;
    private bool _isStarting;
    private CancellationTokenSource? _preparationCts;
    private string _activeEngine = "wasm";
    private Task? _simulationLoopTask;
    private int _simTotalAgents = 0;
    private SimulationConfig _simConfig = new();
    private CrowdSimulationEngine? _engine;
    private CancellationTokenSource? _simCts;
    private readonly SimulationStatsCollector _statsCollector = new();
    private IJSObjectReference? _simModule;
    private IJSObjectReference? _simulator;
    private string? _simWarningMessage;

    private string _activeCategory = "navigate"; // "navigate", "evac", "safe", "blockade"
    private string _selectedShape = "polygon"; // "polygon", "circle"
    private string _activeDrawMode = "none";
    private string? _activeDrawHint;
    private int _currentPointCount = 0;
    private int _censusPopulation = 0;
    private int _censusCells = 0;
    private bool _isCensusLoading = true;

    private bool _disposed = false;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !isMapInitialized && !_disposed)
        {
            isMapInitialized = true;
            _activeSession = await SessionService.GetActiveSessionAsync();
            if (_disposed) return;

            _branchParentSession = _activeSession;
            _simConfig = _activeSession.SimulationConfig?.Clone() ?? new SimulationConfig();

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
            if (_simConfig.UseGusCensus && _censusPopulation > 0)
            {
                _simConfig.AgentCount = _censusPopulation;
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

        _simWarningMessage = null;

        if (_isCensusLoading)
        {
            _simWarningMessage = "Population data is still loading. Try again shortly.";
            StateHasChanged();
            return;
        }

        var evacZones = _mapItems.Where(i => i.Category == ZoneCategory.EvacuationZone).ToList();
        var safeLocations = _mapItems.Where(i => i.Category == ZoneCategory.SafeLocation).ToList();

        if (evacZones.Count == 0)
        {
            _simWarningMessage = "Draw at least one evacuation zone before starting the simulation.";
            StateHasChanged();
            return;
        }

        if (safeLocations.Count == 0)
        {
            _simWarningMessage = "Narysuj przynajmniej jedno Bezpieczne Miejsce (Safe Location) jako punkt docelowy ewakuacji.";
            StateHasChanged();
            return;
        }

        try
        {
            _isStarting = true;
            _preparationCts = new CancellationTokenSource();
            var preparationToken = _preparationCts.Token;
            StateHasChanged();
            await Task.Delay(1, preparationToken);
            await StopSimulationLoopAsync();

            // 1. Calculate Geographic Bounding Box
            var (minLat, maxLat, minLng, maxLng) = ComputeGeographicBoundingBox(_mapItems, _activeSession);
            double midLat = (minLat + maxLat) / 2.0;
            double metersPerDegreeLat = 111320.0;
            double metersPerDegreeLng = metersPerDegreeLat * Math.Cos(midLat * Math.PI / 180.0);
            double worldWidth = Math.Max(50.0, (maxLng - minLng) * metersPerDegreeLng);
            double worldHeight = Math.Max(50.0, (maxLat - minLat) * metersPerDegreeLat);

            (double X, double Y) ToWorld(double lat, double lng) =>
                ((lng - minLng) * metersPerDegreeLng,
                 (maxLat - lat) * metersPerDegreeLat);

            // 2. Prepare Safe Locations (Targets)
            var targets = new List<EvacuationTarget>();
            var targetShapes = new Dictionary<string, (double[] Xs, double[] Ys)>();
            foreach (var item in safeLocations)
            {
                switch (item)
                {
                    case SafeCircleZoneItem circle when circle.Center is { Length: >= 2 }:
                        var (cx, cy) = ToWorld(circle.Center[0], circle.Center[1]);
                        double r = circle.Radius ?? 30.0;
                        targets.Add(new EvacuationTarget(item.Id, string.IsNullOrWhiteSpace(item.Name) ? "Shelter" : item.Name, cx - r, cy - r, r * 2, r * 2, 1000, 0, true));
                        var circXs = new double[32];
                        var circYs = new double[32];
                        for (int k = 0; k < 32; k++)
                        {
                            double ang = k * 2.0 * Math.PI / 32.0;
                            circXs[k] = cx + r * Math.Cos(ang);
                            circYs[k] = cy + r * Math.Sin(ang);
                        }
                        targetShapes[item.Id] = (circXs, circYs);
                        break;
                    case SafePolygonZoneItem poly when poly.Coordinates is { Count: >= 3 }:
                        var coords = poly.Coordinates.Where(c => c is { Length: >= 2 }).ToList();
                        double pMinX = coords.Min(c => ToWorld(c[0], c[1]).X);
                        double pMaxX = coords.Max(c => ToWorld(c[0], c[1]).X);
                        double pMinY = coords.Min(c => ToWorld(c[0], c[1]).Y);
                        double pMaxY = coords.Max(c => ToWorld(c[0], c[1]).Y);
                        targets.Add(new EvacuationTarget(item.Id, string.IsNullOrWhiteSpace(item.Name) ? "Shelter" : item.Name, pMinX, pMinY, Math.Max(10.0, pMaxX - pMinX), Math.Max(10.0, pMaxY - pMinY), 1000, 0, true));
                        targetShapes[item.Id] = (coords.Select(c => ToWorld(c[0], c[1]).X).ToArray(), coords.Select(c => ToWorld(c[0], c[1]).Y).ToArray());
                        break;
                    case SafePointZoneItem pt when pt.Position is { Length: >= 2 }:
                        var (px, py) = ToWorld(pt.Position[0], pt.Position[1]);
                        targets.Add(new EvacuationTarget(item.Id, string.IsNullOrWhiteSpace(item.Name) ? "Exit" : item.Name, px - 4.0, py - 4.0, 8.0, 8.0, 1000, 0, true));
                        break;
                }
            }
            if (targets.Count == 0)
            {
                targets.Add(new EvacuationTarget("shelter-east", "Main Exit", worldWidth * 0.90, worldHeight * 0.45, worldWidth * 0.08, worldHeight * 0.10, 1000, 0, true));
            }

            // 3. Prepare MapScenario raster and obstacles using full OSM terrain from krakow_osm.bin
            double rasterCellSize = Math.Max(2.0, Math.Ceiling(Math.Max(worldWidth, worldHeight) / 600.0 * 2) / 2);
            var builder = new MapScenarioBuilder(worldWidth, worldHeight, cellSize: rasterCellSize);
            await OsmService.RasterizeTerrainAsync(builder, minLat, maxLat, minLng, maxLng, (lat, lng) => ToWorld(lat, lng), preparationToken);

            // 3b. Blockades defined by user
            foreach (var blockade in _mapItems.OfType<BlockadeZoneItem>())
            {
                if (blockade.StartPoint is { Length: >= 2 } sp && blockade.EndPoint is { Length: >= 2 } ep)
                {
                    var p1 = ToWorld(sp[0], sp[1]);
                    var p2 = ToWorld(ep[0], ep[1]);
                    builder.SetCorridor([p1.X, p2.X], [p1.Y, p2.Y], halfWidth: Math.Max(1.5, rasterCellSize), isWalkable: false);
                }
            }

            // 3c. Safe locations (exits): snap the centre onto nearby walkable ground and
            // open only a small disk there, so the exit never cuts a hole through surrounding buildings.
            var mapExits = new List<MapExit>();
            foreach (var t in targets)
            {
                // Polygon / circle zones: cover the real shape with small square exit tiles. Only border
                // tiles are needed (people enter from outside), which keeps the exit count small. The
                // engine's single-square exit model therefore follows the drawn outline closely instead
                // of catching agents inside a large bounding square far outside the zone.
                if (targetShapes.TryGetValue(t.Id, out var shape))
                {
                    double tile = Math.Max(2.0 * rasterCellSize, 4.0);
                    double sMinX = Math.Max(0, shape.Xs.Min()), sMaxX = Math.Min(worldWidth, shape.Xs.Max());
                    double sMinY = Math.Max(0, shape.Ys.Min()), sMaxY = Math.Min(worldHeight, shape.Ys.Max());
                    while (((sMaxX - sMinX) / tile) * ((sMaxY - sMinY) / tile) > 40000) tile *= 1.5;
                    int nx = Math.Max(1, (int)Math.Ceiling((sMaxX - sMinX) / tile));
                    int ny = Math.Max(1, (int)Math.Ceiling((sMaxY - sMinY) / tile));
                    var inside = new bool[nx, ny];
                    for (int i = 0; i < nx; i++)
                        for (int j = 0; j < ny; j++)
                            inside[i, j] = MapScenario.IsPointInPolygon(sMinX + (i + 0.5) * tile, sMinY + (j + 0.5) * tile, shape.Xs, shape.Ys);
                    int added = 0;
                    for (int i = 0; i < nx; i++)
                    {
                        for (int j = 0; j < ny; j++)
                        {
                            if (!inside[i, j]) continue;
                            bool border = i == 0 || j == 0 || i == nx - 1 || j == ny - 1 ||
                                          !inside[i - 1, j] || !inside[i + 1, j] || !inside[i, j - 1] || !inside[i, j + 1];
                            if (!border) continue;
                            double tx = sMinX + (i + 0.5) * tile, ty = sMinY + (j + 0.5) * tile;
                            if (tx <= 0 || ty <= 0 || tx >= worldWidth || ty >= worldHeight) continue;
                            mapExits.Add(new MapExit(tx, ty, tile * 0.5));
                            added++;
                        }
                    }
                    if (added > 0) continue;
                }

                // Point zones (or degenerate shapes): one small exit snapped onto walkable ground,
                // opening only a small disk so it never cuts a hole through surrounding buildings.
                double cx = t.X + t.Width * 0.5;
                double cy = t.Y + t.Height * 0.5;
                double r = Math.Max(4.0, Math.Min(t.Width, t.Height) * 0.5);
                int col = Math.Clamp((int)(cx / rasterCellSize), 0, builder.Columns - 1);
                int row = Math.Clamp((int)(cy / rasterCellSize), 0, builder.Rows - 1);
                if (!builder.IsWalkable(col, row) && builder.FindNearestWalkable(cx, cy, Math.Max(30.0, r)) is { } snapped)
                {
                    cx = snapped.X;
                    cy = snapped.Y;
                }
                mapExits.Add(new MapExit(cx, cy, r));
                builder.SetDisk(cx, cy, Math.Max(2.0, rasterCellSize), isWalkable: true);
            }

            // 3d. Map spawn zones
            var mapSpawnZones = new List<MapSpawnZone>();
            foreach (var zone in evacZones)
            {
                int people = 100;
                switch (zone)
                {
                    case PolygonMapZoneItem poly when poly.Coordinates is { Count: >= 3 }:
                        var pts = poly.Coordinates.Where(c => c is { Length: >= 2 }).ToList();
                        var xs = pts.Select(c => ToWorld(c[0], c[1]).X).ToArray();
                        var ys = pts.Select(c => ToWorld(c[0], c[1]).Y).ToArray();
                        mapSpawnZones.Add(new MapSpawnZone(xs, ys, people));
                        break;
                    case CircleMapZoneItem circle when circle.Center is { Length: >= 2 }:
                        var (cx, cy) = ToWorld(circle.Center[0], circle.Center[1]);
                        double r = circle.Radius ?? 30.0;
                        var cXs = new double[16];
                        var cYs = new double[16];
                        for (int k = 0; k < 16; k++)
                        {
                            double ang = k * 2.0 * Math.PI / 16.0;
                            cXs[k] = cx + r * Math.Cos(ang);
                            cYs[k] = cy + r * Math.Sin(ang);
                        }
                        mapSpawnZones.Add(new MapSpawnZone(cXs, cYs, people));
                        break;
                    case PointMapZoneItem pt when pt.Position is { Length: >= 2 }:
                        var (px, py) = ToWorld(pt.Position[0], pt.Position[1]);
                        mapSpawnZones.Add(new MapSpawnZone([px - 5, px + 5, px + 5, px - 5], [py - 5, py - 5, py + 5, py + 5], people));
                        break;
                }
            }
            if (mapSpawnZones.Count == 0)
            {
                mapSpawnZones.Add(new MapSpawnZone([0, worldWidth, worldWidth, 0], [0, 0, worldHeight, worldHeight], 100));
            }

            var mapScenario = builder.Build(
                maxLat,
                minLng,
                metersPerDegreeLat,
                metersPerDegreeLng,
                mapExits.ToArray(),
                mapSpawnZones.ToArray());

            // 4. Generate Occupant Agents inside Evacuation Zones
            var initialPositions = new List<(double X, double Y)>();
            if (_simConfig.UseGusCensus)
            {
                var occupants = await OccupantGenerator.GenerateOccupantsAsync(evacZones, cancellationToken: preparationToken);
                foreach (var occ in occupants)
                {
                    initialPositions.Add(ToWorld(occ.Latitude, occ.Longitude));
                }
            }

            if (initialPositions.Count == 0 || !_simConfig.UseGusCensus)
            {
                int targetCount = !_simConfig.UseGusCensus && _simConfig.AgentCount > 0
                    ? _simConfig.AgentCount
                    : Math.Max(100, _censusPopulation > 0 ? _censusPopulation : 500);
                initialPositions = await GenerateUniformZonePositionsAsync(evacZones, targetCount, ToWorld, preparationToken);
            }

            _simTotalAgents = initialPositions.Count;

            // 5. Initialize Unified Simulation Engine with MapScenario
            _engine = await CrowdSimulationEngine.CreateMapAsync(mapScenario, initialPositions,
                _simConfig.Granulation, _simConfig.SocialRepulsionWeight, cancellationToken: preparationToken);
            if (_disposed) return;
            _engine.WhiskerLength = _simConfig.WhiskerLength;
            _statsCollector.Reset(_engine.AgentCount);

            // 6. Initialize JS Canvas Overlay
            _isSimulating = true;
            StateHasChanged();
            await Task.Delay(25);

            _simModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/crowdSimulatorInterop.js");
            _simulator ??= await _simModule.InvokeAsync<IJSObjectReference>("initMapSimulator", _simCanvasRef, MapContainerId);

            await _simulator.InvokeVoidAsync("setWorldConfig", new
            {
                worldWidth = worldWidth,
                worldHeight = worldHeight,
                originLat = maxLat,
                originLng = minLng,
                minLat = minLat,
                maxLng = maxLng,
                obstacles = Array.Empty<object>(),
                targets = targets.Select(t => new { x = t.X, y = t.Y, width = t.Width, height = t.Height, name = t.Name, id = t.Id })
            });

            await _simulator.InvokeVoidAsync("setMapTerrain",
                Array.ConvertAll(mapScenario.Blocked, blocked => blocked ? (byte)1 : (byte)0),
                mapScenario.Columns, mapScenario.Rows, mapScenario.CellSize);

            _activeEngine = "wasm";
            try
            {
                string? preferredEngine = null;
                try { preferredEngine = await JS.InvokeAsync<string?>("localStorage.getItem", "plansafe_simulation_engine"); }
                catch (JSException) { }
                if (preferredEngine != "wasm" && await _simModule.InvokeAsync<bool>("checkWebGpuSupport"))
                {
                    await _simulator.InvokeVoidAsync("initMapGpu", MapGpuSnapshot.Capture(_engine));
                    _activeEngine = "webgpu";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlanSafe] Map WebGPU initialization failed; using WASM: {ex.Message}");
            }

            if (_disposed) return;
            // 7. Show agents on map immediately!
            await RequestRender();

            // 8. Start simulation loop
            StartSimulationLoop();
            StateHasChanged();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            _simWarningMessage = $"Failed to start simulation: {ex.Message}";
            StateHasChanged();
        }
        finally
        {
            _isStarting = false;
            _preparationCts?.Dispose();
            _preparationCts = null;
            if (!_disposed) StateHasChanged();
        }
    }

    private async Task RequestRender()
    {
        if (_simulator == null || _engine == null) return;
        try
        {
            if (_activeEngine == "webgpu")
            {
                await _simulator.InvokeVoidAsync("renderGpu", _simConfig.RenderMode, _simConfig.ShowWhiskers,
                    _simConfig.WhiskerLength, _simConfig.Granulation);
                return;
            }
            int count = _engine.SimulatedAgentCount;
            await _simulator.InvokeVoidAsync("render",
                _engine.AgentPositionX[..count],
                _engine.AgentPositionY[..count],
                _engine.AgentVelocityX[..count],
                _engine.AgentVelocityY[..count],
                _engine.AgentRadius[..count],
                count,
                _simConfig.RenderMode,
                _simConfig.ShowWhiskers,
                _simConfig.WhiskerLength,
                _simConfig.Granulation);
        }
        catch { }
    }

    private async Task RunSimulationLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long lastRenderTime = sw.ElapsedMilliseconds;
        long lastTelemetryTime = 0;
        long lastUiTime = 0;
        double lastStepTime = 0;
        double tickBudget = 0;

        while (!ct.IsCancellationRequested && _isRunning && _engine != null)
        {
            double stepTime = sw.Elapsed.TotalSeconds;
            if (_simConfig.Unlimited) tickBudget = 0;
            else tickBudget += (stepTime - lastStepTime) * _simConfig.TimeScale / 0.016;
            lastStepTime = stepTime;
            int batch = _simConfig.Unlimited ? 32 : Math.Clamp((int)Math.Floor(tickBudget), 0, 32);
            if (_activeEngine == "webgpu" && _simulator != null)
            {
                if (batch > 0) await _simulator.InvokeVoidAsync("stepGpu", batch);
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
                    _engine.Step(0.016, 1.0);
                    completed++;
                    if (sw.ElapsedMilliseconds - chunkStart >= 12) break;
                }
                batch = completed;
                _statsCollector.SampleTelemetry(_engine);
            }
            if (!_simConfig.Unlimited) tickBudget -= batch;

            if (_statsCollector.Stats.IsComplete)
            {
                _isRunning = false;
                await RequestRender();
                await InvokeAsync(StateHasChanged);
                break;
            }

            long now = sw.ElapsedMilliseconds;
            int frameInterval = _simConfig.RenderFps switch
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
        _simCts?.Dispose();
        _simCts = new CancellationTokenSource();
        _isRunning = true;
        _simulationLoopTask = RunSimulationLoopAsync(_simCts.Token);
    }

    private async Task StopSimulationLoopAsync()
    {
        _simCts?.Cancel();
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
        if (_simulator == null) return;
        var sample = await _simulator.InvokeAsync<GpuSimulationTelemetry?>("getGpuTelemetry");
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
        if (_isStarting || _isRunning || _engine == null) return;
        if (_activeEngine == "webgpu" && _simulator != null)
        {
            await _simulator.InvokeVoidAsync("stepGpu", 1);
            await SampleGpuTelemetry();
        }
        else
        {
            _engine.Step(0.016, 1.0);
            _statsCollector.SampleTelemetry(_engine);
        }
        await RequestRender();
        await InvokeAsync(StateHasChanged);
    }

    private async Task ResetSimulation()
    {
        if (_isStarting) return;
        await StopSimulationLoopAsync();
        if (_engine != null)
        {
            if (_activeEngine == "webgpu" && _simulator != null)
                await _simulator.InvokeVoidAsync("resetMapGpu");
            else _engine.Reset();
            _statsCollector.Reset(_engine.AgentCount);
        }
        await RequestRender();
        await InvokeAsync(StateHasChanged);
    }

    private async Task StopSimulation()
    {
        if (_isStarting) return;
        await StopSimulationLoopAsync();
        _isSimulating = false;
        if (_simulator != null)
        {
            try { await _simulator.InvokeVoidAsync("setWorldConfig", new { }); } catch { }
        }
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleRenderModeChanged(string mode)
    {
        _simConfig.RenderMode = mode;
        await RequestRender();
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleToggleWhiskers()
    {
        _simConfig.ShowWhiskers = !_simConfig.ShowWhiskers;
        await RequestRender();
        await InvokeAsync(StateHasChanged);
    }

    private Task HandleCycleSpeed()
    {
        if (_simConfig.Unlimited)
        {
            _simConfig.Unlimited = false;
            _simConfig.TimeScale = 1.0f;
        }
        else if (_simConfig.TimeScale < 2.0f)
        {
            _simConfig.TimeScale = 2.5f;
        }
        else if (_simConfig.TimeScale < 5.0f)
        {
            _simConfig.TimeScale = 5.0f;
        }
        else if (_simConfig.TimeScale < 10.0f)
        {
            _simConfig.TimeScale = 15.0f;
        }
        else
        {
            _simConfig.Unlimited = true;
        }
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task HandleSaveConfig(SimulationConfig config)
    {
        bool resume = _isRunning;
        await StopSimulationLoopAsync();
        bool granulationChanged = _simConfig.Granulation != config.Granulation;
        _simConfig = config.Clone();
        if (_activeSession != null)
        {
            _activeSession.SimulationConfig = _simConfig.Clone();
            await SessionService.SaveSessionAsync(_activeSession);
        }
        if (_engine != null)
        {
            _engine.SocialRepulsionWeight = _simConfig.SocialRepulsionWeight;
            _engine.WhiskerLength = _simConfig.WhiskerLength;
            _engine.WeightDistance = _simConfig.WeightDistance;
            _engine.WeightOccupancy = _simConfig.WeightOccupancy;
            _engine.PotentialFieldMap.RefineDynamicField();
            if (granulationChanged)
            {
                _engine.SetGranulation(_simConfig.Granulation);
                _statsCollector.Reset(_engine.AgentCount);
            }
            if (_activeEngine == "webgpu" && _simulator != null)
            {
                if (granulationChanged)
                    await _simulator.InvokeVoidAsync("initMapGpu", MapGpuSnapshot.Capture(_engine));
                else await _simulator.InvokeVoidAsync("setGpuWeight", _simConfig.SocialRepulsionWeight);
            }
        }
        await RequestRender();
        if (resume) StartSimulationLoop();
        StateHasChanged();
    }

    private static (double minLat, double maxLat, double minLng, double maxLng) ComputeGeographicBoundingBox(
        List<MapZoneItem> items,
        MapSession? session)
    {
        var lats = new List<double>();
        var lngs = new List<double>();

        foreach (var item in items)
        {
            switch (item)
            {
                case CircleMapZoneItem circle when circle.Center is { Length: >= 2 }:
                    double rDegLat = (circle.Radius ?? 50.0) / 111320.0;
                    double rDegLng = rDegLat / Math.Cos(circle.Center[0] * Math.PI / 180.0);
                    lats.Add(circle.Center[0] - rDegLat);
                    lats.Add(circle.Center[0] + rDegLat);
                    lngs.Add(circle.Center[1] - rDegLng);
                    lngs.Add(circle.Center[1] + rDegLng);
                    break;
                case PolygonMapZoneItem poly when poly.Coordinates != null:
                    foreach (var c in poly.Coordinates.Where(c => c is { Length: >= 2 }))
                    {
                        lats.Add(c[0]);
                        lngs.Add(c[1]);
                    }
                    break;
                case LineMapZoneItem line:
                    if (line.StartPoint is { Length: >= 2 }) { lats.Add(line.StartPoint[0]); lngs.Add(line.StartPoint[1]); }
                    if (line.EndPoint is { Length: >= 2 }) { lats.Add(line.EndPoint[0]); lngs.Add(line.EndPoint[1]); }
                    break;
                case PointMapZoneItem pt when pt.Position is { Length: >= 2 }:
                    lats.Add(pt.Position[0]);
                    lngs.Add(pt.Position[1]);
                    break;
            }
        }

        if (lats.Count < 2)
        {
            double cLat = session?.MapCenter is { Length: >= 2 } ? session.MapCenter[0] : 50.0614;
            double cLng = session?.MapCenter is { Length: >= 2 } ? session.MapCenter[1] : 19.9366;
            double margin = 0.005; // ~550m
            return (cLat - margin, cLat + margin, cLng - margin, cLng + margin);
        }

        double minLat = lats.Min();
        double maxLat = lats.Max();
        double minLng = lngs.Min();
        double maxLng = lngs.Max();

        double padLat = Math.Max(0.0010, (maxLat - minLat) * 0.15);
        double padLng = Math.Max(0.0010, (maxLng - minLng) * 0.15);

        return (minLat - padLat, maxLat + padLat, minLng - padLng, maxLng + padLng);
    }

    private static async Task<List<(double X, double Y)>> GenerateUniformZonePositionsAsync(
        List<MapZoneItem> evacZones,
        int count,
        Func<double, double, (double X, double Y)> toWorld,
        CancellationToken cancellationToken)
    {
        var rng = new Random(42);
        var result = new List<(double X, double Y)>();
        int attempts = 0;
        int maxAttempts = count * 50;
        var scheduler = new PreparationScheduler();

        while (result.Count < count && attempts < maxAttempts)
        {
            attempts++;
            if (attempts % 128 == 0) await scheduler.YieldAsync(cancellationToken);
            var zone = evacZones[rng.Next(evacZones.Count)];
            if (!GeoSpatialMath.GetZoneBoundingBox(zone, out double zMinLat, out double zMinLng, out double zMaxLat, out double zMaxLng))
                continue;

            double candLat = zMinLat + rng.NextDouble() * (zMaxLat - zMinLat);
            double candLng = zMinLng + rng.NextDouble() * (zMaxLng - zMinLng);

            if (GeoSpatialMath.IsPointInZone(candLat, candLng, zone))
            {
                result.Add(toWorld(candLat, candLng));
            }
        }

        while (result.Count < count && evacZones.Count > 0)
        {
            var zone = evacZones[0];
            if (GeoSpatialMath.GetZoneBoundingBox(zone, out double zMinLat, out double zMinLng, out double zMaxLat, out double zMaxLng))
            {
                result.Add(toWorld((zMinLat + zMaxLat) / 2.0, (zMinLng + zMaxLng) / 2.0));
            }
            else
            {
                break;
            }
        }

        return result;
    }

    private void RecalculateCensusData()
    {
        if (CensusService.IsLoaded)
        {
            var (pop, cells) = CensusService.CalculateEvacuationPopulation(_mapItems);
            _censusPopulation = pop;
            _censusCells = cells;
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
        isMobilePanelOpen = !isMobilePanelOpen;
        StateHasChanged();
        await Task.Delay(250);
        await MapInterop.InvalidateSizeAsync(MapContainerId);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _preparationCts?.Cancel();
        await StopSimulationLoopAsync();
        _simCts?.Dispose();

        if (_simulator != null)
        {
            try
            {
                await _simulator.InvokeVoidAsync("dispose");
                await _simulator.DisposeAsync();
            }
            catch
            {
                // Ignore disposal errors on teardown
            }
            _simulator = null;
        }
        if (isMapInitialized)
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
