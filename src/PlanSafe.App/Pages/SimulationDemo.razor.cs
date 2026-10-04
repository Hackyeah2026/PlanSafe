using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Diagnostics;
using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;
using PlanSafe.App.Components;
using PlanSafe.Contracts.Models.Stats;

namespace PlanSafe.App.Pages;

public partial class SimulationDemo : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private ElementReference canvas;
    private IJSObjectReference? module;
    private IJSObjectReference? simulator;
    private CrowdSimulationEngine? _engine;
    private CancellationTokenSource? _simCts;
    private Task? _simulationLoopTask;

    private bool _showStats = false;
    private readonly SimulationStatsCollector _statsCollector = new();

    private int agentCount = 500;
    private int granulation = 1;
    private int SimulatedAgentCount => Math.Max(1, (int)Math.Ceiling((double)agentCount / Math.Max(1, granulation)));

    private double socialRepulsionWeight = 4.5;
    private double whiskerLength = 2.5;
    private double worldWidth = 200.0;
    private double worldHeight = 200.0;
    private double timeScale = 1.0;
    private bool unlimited;
    private string renderFps = "30";

    private double ActualSpeed => simulationStepsPerSecond * 0.016;
    private const string EnginePreferenceStorageKey = "plansafe_simulation_engine";
    private bool SimulationComplete => _statsCollector.Stats.IsComplete;
    private int _gpuActiveDots;
    private string activeEngine = "webgpu";
    private bool isWebGpuSupported;
    private string renderMode = "agents";
    private bool showWhiskers;
    private bool showFlowParticles = true;
    private bool isRunning;
    private bool showDiagnostics;
    private double renderFramesPerSecond;
    private double simulationStepsPerSecond;
    private bool ready;
    private bool disposed;

    private List<ObstacleDto> obstacles = new();
    private List<EvacuationTarget> targets = new();
    private PublishPlanResponse? publishedPlan;
    private bool showPublishModal;
    private bool isPublishing;
    private Func<string?>? publishError;

    private double weightDistance = 0.5;
    private double weightOccupancy = 0.5;

    protected override void OnInitialized()
    {
        InitDefaultEnvironment();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !disposed)
        {
            await InitializeEngine();
        }
    }

    private string TargetName(EvacuationTarget target) => target.Id switch
    {
        "shelter-main" => L.Demo.EastExit,
        "shelter-north" => L.Demo.NorthShelter,
        "shelter-south" => L.Demo.SouthShelter,
        _ => target.Name
    };

    private void InitDefaultEnvironment()
    {
        obstacles.Clear();
        targets.Clear();
        if (worldWidth == 60 && worldHeight == 40)
        {
            obstacles.Add(new ObstacleDto(20.0, 0.0, 8.0, 14.0, "obs_north"));
            obstacles.Add(new ObstacleDto(20.0, 26.0, 8.0, 14.0, "obs_south"));
            targets.Add(new EvacuationTarget("shelter-main", "Main exit (East)", 56.0, 15.0, 4.0, 10.0, 0, 0, true));
        }
        else
        {
            obstacles.Add(new ObstacleDto(worldWidth * 0.35, worldHeight * 0.18, worldWidth * 0.12, worldHeight * 0.28, "obs_north"));
            obstacles.Add(new ObstacleDto(worldWidth * 0.35, worldHeight * 0.54, worldWidth * 0.12, worldHeight * 0.28, "obs_south"));
            targets.Add(new EvacuationTarget("shelter-north", "North shelter (Gate A)", worldWidth * 0.90, worldHeight * 0.12, worldWidth * 0.08, worldHeight * 0.20, 0, 0, true));
            targets.Add(new EvacuationTarget("shelter-south", "South shelter (Gate B)", worldWidth * 0.90, worldHeight * 0.68, worldWidth * 0.08, worldHeight * 0.20, 0, 0, true));
        }
    }

    private object GetWorldConfigDto() => new
    {
        worldWidth = worldWidth,
        worldHeight = worldHeight,
        obstacles = obstacles.Select(o => new { x = o.X, y = o.Y, width = o.Width, height = o.Height }),
        targets = (_engine?.Targets ?? targets).Select(t => new { x = t.X, y = t.Y, width = t.Width, height = t.Height, name = t.Name, id = t.Id, capacity = t.Capacity, currentOccupancy = t.CurrentOccupancy, isActive = t.IsActive }),
        weightDistance,
        weightOccupancy,
        exitZone = targets.FirstOrDefault() is { } ez ? new { x = ez.X, y = ez.Y, width = ez.Width, height = ez.Height } : null
    };

    private async Task InitializeEngine()
    {
        try
        {
            module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/crowdSimulatorInterop.js");
            simulator ??= await module.InvokeAsync<IJSObjectReference>("initSimulator", canvas);

            try
            {
                isWebGpuSupported = await module.InvokeAsync<bool>("checkWebGpuSupport");
            }
            catch
            {
                isWebGpuSupported = false;
            }

            string? preferredEngine = null;
            try
            {
                preferredEngine = await JS.InvokeAsync<string?>("localStorage.getItem", EnginePreferenceStorageKey);
            }
            catch (JSException)
            {
                // Storage can be unavailable; automatic engine selection still works.
            }
            activeEngine = isWebGpuSupported && preferredEngine != "wasm" ? "webgpu" : "wasm";

            _engine = new CrowdSimulationEngine(worldWidth, worldHeight, agentCount);
            _engine.Granulation = granulation;
            _engine.SocialRepulsionWeight = socialRepulsionWeight;
            _engine.WhiskerLength = whiskerLength;
            _engine.SetEnvironment(obstacles, targets, weightDistance, weightOccupancy);
            _engine.InitializeAgents();
            _statsCollector.Reset(_engine.AgentCount);

            await SyncWorldConfigAsync();
            _gpuActiveDots = SimulatedAgentCount;
            if (activeEngine == "webgpu") await InitializeGpuEngineAsync();
            ready = true;
            await RequestRender();
        }
        catch (Exception ex)
        {
            publishError = () => L.Common.ViewFailed(ex.Message);
        }
        await InvokeAsync(StateHasChanged);
    }

    private async Task SetEngine(string engine)
    {
        if (!ready || isRunning) return;
        if (engine == "webgpu" && !isWebGpuSupported) return;
        ready = false;
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", EnginePreferenceStorageKey, engine);
        }
        catch (JSException)
        {
            // A manual selection also works for this visit without persistent storage.
        }
        if (activeEngine == engine)
        {
            ready = true;
            return;
        }
        _engine?.Reset();
        _engine?.InitializeAgents();
        _statsCollector.Reset(agentCount);
        _gpuActiveDots = SimulatedAgentCount;
        simulationStepsPerSecond = 0;
        renderFramesPerSecond = 0;
        activeEngine = engine;
        if (activeEngine == "webgpu") await InitializeGpuEngineAsync();
        ready = true;
        await RequestRender();
        await InvokeAsync(StateHasChanged);
    }

    private async Task InitializeGpuEngineAsync()
    {
        if (simulator == null) return;
        try
        {
            await simulator.InvokeVoidAsync("initGpu", agentCount, granulation, socialRepulsionWeight, GetWorldConfigDto());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlanSafe] Failed to initialize WebGPU: {ex.Message}");
            activeEngine = "wasm";
        }
    }

    private async Task SyncWorldConfigAsync()
    {
        if (simulator == null) return;
        await simulator.InvokeVoidAsync("setWorldConfig", new
        {
            worldWidth = worldWidth,
            worldHeight = worldHeight,
            obstacles = obstacles.Select(o => new { x = o.X, y = o.Y, width = o.Width, height = o.Height }),
            targets = (_engine?.Targets ?? targets).Select(t => new { x = t.X, y = t.Y, width = t.Width, height = t.Height, name = t.Name, id = t.Id, capacity = t.Capacity, currentOccupancy = t.CurrentOccupancy, isActive = t.IsActive }),
            weightDistance,
            weightOccupancy,
            exitZone = targets.FirstOrDefault() is { } ez ? new { x = ez.X, y = ez.Y, width = ez.Width, height = ez.Height } : null
        });
    }

    private async Task ToggleSimulation()
    {
        if (!ready) return;
        if (!isRunning && _engine != null && SimulationComplete)
        {
            _engine.Reset();
            _engine.InitializeAgents();
            _statsCollector.Reset(_engine.AgentCount);
            _gpuActiveDots = SimulatedAgentCount;
            if (activeEngine == "webgpu" && simulator != null)
            {
                await simulator.InvokeVoidAsync("resetGpu", agentCount, granulation, socialRepulsionWeight);
            }
            await RequestRender();
        }

        isRunning = !isRunning;
        if (isRunning)
        {
            _simCts?.Dispose();
            _simCts = new CancellationTokenSource();
            _simulationLoopTask = RunSimulationLoopAsync(_simCts.Token);
        }
        else
        {
            _simCts?.Cancel();
            if (_simulationLoopTask != null) await _simulationLoopTask;
            if (activeEngine == "webgpu") await SampleGpuTelemetry();
            renderFramesPerSecond = 0;
            simulationStepsPerSecond = 0;
        }
        await InvokeAsync(StateHasChanged);
    }

    private async Task StepSimulation()
    {
        if (activeEngine == "webgpu" && simulator != null)
        {
            await simulator.InvokeVoidAsync("stepGpu", 1);
            await SampleGpuTelemetry();
        }
        else if (_engine != null)
        {
            _engine.Step(0.016, 1.0);
            _statsCollector.SampleTelemetry(_engine);
        }
        await RequestRender();
        await InvokeAsync(StateHasChanged);
    }

    private async Task ResetSimulation()
    {
        _simCts?.Cancel();
        isRunning = false;
        if (_simulationLoopTask != null) await _simulationLoopTask;
        renderFramesPerSecond = 0;
        simulationStepsPerSecond = 0;
        _gpuActiveDots = SimulatedAgentCount;

        if (activeEngine == "webgpu" && simulator != null)
        {
            await simulator.InvokeVoidAsync("resetGpu", agentCount, granulation, socialRepulsionWeight);
        }

        if (_engine != null)
        {
            _engine.Reset();
            _engine.InitializeAgents();
            _statsCollector.Reset(_engine.AgentCount);
        }
        await RequestRender();
        await InvokeAsync(StateHasChanged);
    }

    private async Task RunSimulationLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long lastRenderTime = sw.ElapsedMilliseconds;
        long lastStatsRenderTime = sw.ElapsedMilliseconds;
        long lastTelemetryTime = sw.ElapsedMilliseconds;
        double lastStepTime = 0;
        double tickBudget = 0;
        var fpsTimer = Stopwatch.StartNew();
        int frameCount = 0;
        int stepCount = 0;

        while (!ct.IsCancellationRequested && isRunning)
        {
            double stepTime = sw.Elapsed.TotalSeconds;
            if (unlimited) tickBudget = 0;
            else tickBudget += (stepTime - lastStepTime) * timeScale / 0.016;
            lastStepTime = stepTime;
            int batch = unlimited ? 32 : Math.Clamp((int)Math.Floor(tickBudget), 0, 32);
            if (activeEngine == "webgpu" && simulator != null)
            {
                if (batch > 0) await simulator.InvokeVoidAsync("stepGpu", batch);
                if (sw.ElapsedMilliseconds - lastTelemetryTime >= 100)
                {
                    await SampleGpuTelemetry();
                    lastTelemetryTime = sw.ElapsedMilliseconds;
                }
            }
            else if (_engine != null && batch > 0)
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
            if (!unlimited) tickBudget -= batch;
            stepCount += batch;

            if (SimulationComplete)
            {
                isRunning = false;
                renderFramesPerSecond = 0;
                simulationStepsPerSecond = 0;
                await RequestRender();
                await InvokeAsync(StateHasChanged);
                break;
            }

            long now = sw.ElapsedMilliseconds;
            int frameInterval = renderFps switch
            {
                "15" => 66,
                "30" => 33,
                "60" => 16,
                _ => 16
            };

            if (now - lastRenderTime >= frameInterval)
            {
                lastRenderTime = now;
                frameCount++;
                await RequestRender();
            }

            if (_showStats && now - lastStatsRenderTime >= 100)
            {
                lastStatsRenderTime = now;
                await InvokeAsync(StateHasChanged);
            }

            if (fpsTimer.ElapsedMilliseconds >= 1000)
            {
                renderFramesPerSecond = frameCount * 1000.0 / fpsTimer.ElapsedMilliseconds;
                simulationStepsPerSecond = stepCount * 1000.0 / fpsTimer.ElapsedMilliseconds;
                frameCount = 0;
                stepCount = 0;
                fpsTimer.Restart();
                await InvokeAsync(StateHasChanged);
            }

            // Yield to browser input and painting, including unlimited GPU batches.
            await Task.Delay(1);
        }
    }

    private async Task SampleGpuTelemetry()
    {
        if (simulator == null) return;
        var sample = await simulator.InvokeAsync<GpuSimulationTelemetry?>("getGpuTelemetry");
        if (sample == null) return;
        _gpuActiveDots = sample.ActiveDots;
        _statsCollector.SampleTelemetry(sample);
    }

    private async Task RequestRender()
    {
        if (simulator == null) return;
        try
        {
            if (activeEngine == "webgpu")
            {
                await simulator.InvokeVoidAsync("renderGpu", renderMode, showWhiskers, whiskerLength, granulation, showFlowParticles);
            }
            else if (_engine != null)
            {
                int count = _engine.SimulatedAgentCount;
                await simulator.InvokeVoidAsync("render",
                    _engine.AgentPositionX[..count],
                    _engine.AgentPositionY[..count],
                    _engine.AgentVelocityX[..count],
                    _engine.AgentVelocityY[..count],
                    _engine.AgentRadius[..count],
                    count,
                    renderMode,
                    showWhiskers,
                    whiskerLength,
                    granulation,
                    showFlowParticles);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlanSafe] RequestRender error: {ex.Message}");
        }
    }

    private async Task SetWorldPreset(double width, double height)
    {
        bool resume = isRunning;
        await StopSimulationLoopAsync();
        worldWidth = width;
        worldHeight = height;
        InitDefaultEnvironment();

        if (_engine != null)
        {
            _engine = new CrowdSimulationEngine(worldWidth, worldHeight, agentCount);
            _engine.Granulation = granulation;
            _engine.SocialRepulsionWeight = socialRepulsionWeight;
            _engine.WhiskerLength = whiskerLength;
            _engine.SetEnvironment(obstacles, targets, weightDistance, weightOccupancy);
            _engine.InitializeAgents();
            _statsCollector.Reset(_engine.AgentCount);
        }

        await SyncWorldConfigAsync();
        if (activeEngine == "webgpu" && simulator != null)
        {
            await simulator.InvokeVoidAsync("setGpuWorldConfig", new
            {
                worldWidth = worldWidth,
                worldHeight = worldHeight,
                obstacles = obstacles.Select(o => new { x = o.X, y = o.Y, width = o.Width, height = o.Height }),
                targets = (_engine?.Targets ?? targets).Select(t => new { x = t.X, y = t.Y, width = t.Width, height = t.Height, name = t.Name, id = t.Id, capacity = t.Capacity, currentOccupancy = t.CurrentOccupancy, isActive = t.IsActive }),
                weightDistance,
                weightOccupancy,
                exitZone = targets.FirstOrDefault() is { } ez ? new { x = ez.X, y = ez.Y, width = ez.Width, height = ez.Height } : null,
                count = agentCount,
                granulation = granulation
            });
        }
        await RequestRender();
        _gpuActiveDots = SimulatedAgentCount;
        if (resume) await ToggleSimulation();
    }

    private async Task ApplyCount()
    {
        bool resume = isRunning;
        await StopSimulationLoopAsync();
        int capacity = 0;
        for (int i = 0; i < targets.Count; i++)
            targets[i] = targets[i] with { Capacity = capacity, CurrentOccupancy = 0 };
        if (_engine != null)
        {
            _engine.AgentCount = agentCount;
            _engine.SetEnvironment(obstacles, targets, weightDistance, weightOccupancy);
            _engine.InitializeAgents();
            _statsCollector.Reset(_engine.AgentCount);
        }
        if (activeEngine == "webgpu" && simulator != null)
        {
            await simulator.InvokeVoidAsync("setGpuEnvironment", GetWorldConfigDto());
            await simulator.InvokeVoidAsync("resetGpu", agentCount, granulation, socialRepulsionWeight);
        }
        await SyncWorldConfigAsync();
        _gpuActiveDots = SimulatedAgentCount;
        await RequestRender();
        if (resume) await ToggleSimulation();
    }

    private async Task ApplyGranulation()
    {
        bool resume = isRunning;
        await StopSimulationLoopAsync();
        if (_engine != null)
        {
            _engine.SetGranulation(granulation);
            _statsCollector.Reset(_engine.AgentCount);
        }
        if (activeEngine == "webgpu" && simulator != null)
        {
            await simulator.InvokeVoidAsync("resetGpu", agentCount, granulation, socialRepulsionWeight);
        }
        _gpuActiveDots = SimulatedAgentCount;
        await RequestRender();
        if (resume) await ToggleSimulation();
    }

    private async Task StopSimulationLoopAsync()
    {
        isRunning = false;
        _simCts?.Cancel();
        if (_simulationLoopTask != null) await _simulationLoopTask;
        renderFramesPerSecond = 0;
        simulationStepsPerSecond = 0;
    }

    private async Task ApplyWeight()
    {
        if (_engine != null)
        {
            _engine.SocialRepulsionWeight = socialRepulsionWeight;
        }
        if (activeEngine == "webgpu" && simulator != null)
        {
            await simulator.InvokeVoidAsync("setGpuWeight", socialRepulsionWeight);
        }
    }

    private async Task SetScenario(double wDist, double wOcc)
    {
        await SetWeightDist(wDist);
    }

    private async Task AdjustWeightDist(double delta)
    {
        await SetWeightDist(weightDistance + delta);
    }

    private async Task AdjustWeightOcc(double delta)
    {
        await SetWeightOcc(weightOccupancy + delta);
    }

    private async Task SetWeightDist(double val)
    {
        weightDistance = Math.Clamp(Math.Round(val, 2), 0.0, 1.0);
        weightOccupancy = Math.Round(1.0 - weightDistance, 2);
        await ApplyWeights();
    }

    private async Task SetWeightOcc(double val)
    {
        weightOccupancy = Math.Clamp(Math.Round(val, 2), 0.0, 1.0);
        weightDistance = Math.Round(1.0 - weightOccupancy, 2);
        await ApplyWeights();
    }

    private async Task OnWeightDistInput(ChangeEventArgs e)
    {
        if (TryParseDoubleInvariant(e.Value, out var val))
        {
            await SetWeightDist(val);
        }
    }

    private async Task OnWeightOccInput(ChangeEventArgs e)
    {
        if (TryParseDoubleInvariant(e.Value, out var val))
        {
            await SetWeightOcc(val);
        }
    }

    private async Task ApplyWeights()
    {
        if (_engine != null)
        {
            _engine.WeightDistance = weightDistance;
            _engine.WeightOccupancy = weightOccupancy;
            _engine.RebuildGrids();
            _engine.PotentialFieldMap.RefineDynamicField();
        }
        if (activeEngine == "webgpu" && simulator != null)
        {
            await simulator.InvokeVoidAsync("setGpuEnvironment", GetWorldConfigDto());
        }
    }

    private async Task ResetShelters()
    {
        if (_engine != null)
        {
            for (int i = 0; i < _engine.Targets.Count; i++)
            {
                _engine.Targets[i] = _engine.Targets[i] with { CurrentOccupancy = 0 };
            }
            for (int i = 0; i < targets.Count; i++)
            {
                targets[i] = targets[i] with { CurrentOccupancy = 0 };
            }
            _engine.RebuildGrids();
        }
        if (activeEngine == "webgpu" && simulator != null)
        {
            await simulator.InvokeVoidAsync("setGpuEnvironment", GetWorldConfigDto());
        }
    }

    private async Task ZoomIn()
    {
        if (simulator != null) await simulator.InvokeVoidAsync("zoom", 1.25);
    }

    private async Task ZoomOut()
    {
        if (simulator != null) await simulator.InvokeVoidAsync("zoom", 0.8);
    }

    private async Task ToggleStats()
    {
        _showStats = !_showStats;
        await InvokeAsync(StateHasChanged);
    }

    private async Task ResetCamera()
    {
        if (simulator != null) await simulator.InvokeVoidAsync("resetView");
    }

    private void PublishCurrentPlan()
    {
        isPublishing = true;
        publishError = null;

        string baseUrl = Nav?.BaseUri?.TrimEnd('/') ?? "http://localhost:5000";
        string evacuateUrl = $"{baseUrl}/evacuate";

        publishedPlan = new PublishPlanResponse(
            SessionId: Guid.NewGuid().ToString("N")[..8],
            EvacuateUrl: evacuateUrl,
            QrCodeSvg: GenerateSimpleQrCodeSvg(evacuateUrl),
            CreatedAtUtc: DateTime.UtcNow
        );

        isPublishing = false;
        showPublishModal = true;
    }

    private void ClosePublishModal()
    {
        showPublishModal = false;
    }

    private static bool TryParseDoubleInvariant(object? value, out double result)
    {
        result = 0.0;
        if (value is null) return false;
        var str = value.ToString()?.Trim().Replace(',', '.');
        return double.TryParse(str, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static string GenerateSimpleQrCodeSvg(string url)
    {
        int size = 25;
        bool[,] grid = new bool[size, size];

        void DrawFinder(int r0, int c0)
        {
            for (int r = 0; r < 7; r++)
            {
                for (int c = 0; c < 7; c++)
                {
                    bool border = (r == 0 || r == 6 || c == 0 || c == 6);
                    bool center = (r >= 2 && r <= 4 && c >= 2 && c <= 4);
                    grid[r0 + r, c0 + c] = border || center;
                }
            }
        }
        DrawFinder(0, 0);
        DrawFinder(0, size - 7);
        DrawFinder(size - 7, 0);

        for (int i = 8; i < size - 8; i++)
        {
            grid[6, i] = (i % 2 == 0);
            grid[i, 6] = (i % 2 == 0);
        }

        uint hash = 2166136261;
        foreach (char ch in url) hash = (hash ^ ch) * 16777619;
        var rng = new Random((int)hash);

        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                bool inFinder = (r < 8 && c < 8) || (r < 8 && c >= size - 8) || (r >= size - 8 && c < 8);
                bool inTiming = (r == 6 || c == 6);
                if (!inFinder && !inTiming)
                {
                    grid[r, c] = (rng.Next(100) < 48);
                }
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size + 4} {size + 4}\" shape-rendering=\"crispEdges\">");
        sb.Append($"<rect width=\"{size + 4}\" height=\"{size + 4}\" fill=\"white\"/>");
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                if (grid[r, c])
                {
                    sb.Append($"<rect x=\"{c + 2}\" y=\"{r + 2}\" width=\"1\" height=\"1\" fill=\"black\"/>");
                }
            }
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        _simCts?.Cancel();
        isRunning = false;
        if (_simulationLoopTask != null) await _simulationLoopTask;
        _simCts?.Dispose();

        try
        {
            if (simulator != null)
            {
                await simulator.InvokeVoidAsync("dispose");
                await simulator.DisposeAsync();
                simulator = null;
            }
            if (module != null)
            {
                await module.DisposeAsync();
                module = null;
            }
        }
        catch (JSDisconnectedException)
        {
        }
    }
}
