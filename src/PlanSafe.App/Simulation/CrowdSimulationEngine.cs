using System;
using System.Collections.Generic;
using System.Linq;
using PlanSafe.Contracts.Models.Simulation;

namespace PlanSafe.App.Simulation;

public class CrowdSimulationEngine
{
    public const int MaxAllowedAgents = 100000;

    public double WorldWidth { get; private set; } = 200.0;
    public double WorldHeight { get; private set; } = 200.0;
    public int AgentCount { get; set; } = 1000;
    public double SocialRepulsionWeight { get; set; } = 4.5;
    public double WhiskerLength { get; set; } = 2.5;

    public double[] AgentPositionX { get; } = new double[MaxAllowedAgents];
    public double[] AgentPositionY { get; } = new double[MaxAllowedAgents];
    public double[] AgentVelocityX { get; } = new double[MaxAllowedAgents];
    public double[] AgentVelocityY { get; } = new double[MaxAllowedAgents];
    public double[] AgentRadius { get; } = new double[MaxAllowedAgents];
    public double[] AgentMaxSpeed { get; } = new double[MaxAllowedAgents];
    public double[] AgentLocalDensity { get; } = new double[MaxAllowedAgents];

    public Obstacle[] SimulationObstacles { get; private set; } = Array.Empty<Obstacle>();
    public Obstacle ExitZone { get; private set; } = new Obstacle(0, 0, 0, 0);
    public double TargetX { get; private set; }
    public double TargetY { get; private set; }

    public bool MultiTargetEnabled { get; private set; } = false;
    public double WeightDistance { get; set; } = 1.0;
    public double WeightOccupancy { get; set; } = 0.0;
    public List<EvacuationTarget> Targets { get; } = new();
    public int[] AgentTargetIndex { get; } = new int[MaxAllowedAgents];
    public int EvacuatedCount => Math.Min(AgentCount, _evacuatedCount * Math.Max(1, Granulation));

    public SpatialHashGrid SpatialHashGridIndex { get; private set; } = new(6.0, MaxAllowedAgents, 65536);
    public PotentialFieldGrid PotentialFieldMap { get; private set; } = new(8.0, 1000.0, 1000.0);
    public int FrameCounter { get; private set; } = 0;

    private readonly double[] _neighborDisplacementX = new double[2048];
    private readonly double[] _neighborDisplacementY = new double[2048];
    private readonly double[] _neighborDistanceSquared = new double[2048];
    private readonly double[] _neighborDistance = new double[2048];
    private readonly int[] _closeNeighborIndices = new int[2048];

    public int Granulation { get; set; } = 1;
    public int SimulatedAgentCount => Math.Max(1, (int)Math.Ceiling((double)AgentCount / Math.Max(1, Granulation)));

    // Map scenario mode: a walkability raster replaces the preset obstacles, several exits act as
    // sinks and evacuated agents are parked (radius 0) after the active prefix of the arrays.
    public MapScenario? MapScenario { get; private set; }
    public bool IsMapScenario => MapScenario is not null;
    /// <summary>
    /// When true, disables preset bottleneck corridor constraints and left-wall artificial repulsion,
    /// allowing natural open-field movement across geographic map scenarios.
    /// </summary>
    public bool IsMapMode { get; set; }
    public int ActiveAgentCount => IsMapScenario ? _activeMapAgents : _activePresetAgents;
    public double SimulationTime { get; private set; }
    public const double StalledEvacuationSeconds = 600.0;
    public const double StationaryRecoverySeconds = 180.0;
    private const double RecoveryMovementDistance = 0.25;
    private readonly double[] _recoveryAnchorX = new double[MaxAllowedAgents];
    private readonly double[] _recoveryAnchorY = new double[MaxAllowedAgents];
    private readonly double[] _lastRecoveryMovementTime = new double[MaxAllowedAgents];
    private readonly List<int> _recoveredThisStep = new();
    public const double FirstArrivalTimeoutSeconds = 3600.0;
    private int _activeMapAgents;
    private int _activePresetAgents;
    private Obstacle[] _mapExitZones = Array.Empty<Obstacle>();
    private int[] _evacuatedPerExit = Array.Empty<int>();
    private double[] _evacuationTimes = Array.Empty<double>();
    private int _evacuatedCount;
    private double _lastEvacuationTime;
    private List<int>[]? _spawnCells;
    private double _spawnClearance;
    private readonly SimulationObstacleQuery _obstacleQuery = new();
    private const int SpawnAttempts = 8;
    public const double BaseAgentRadius = 0.35;
    public static double GetAgentRadius(int granulation) => granulation > 1 ? BaseAgentRadius * (1.0 + 0.20 * Math.Sqrt(granulation - 1)) : BaseAgentRadius;

    public void SetGranulation(int granulation)
    {
        if (granulation is < 1 or > 25) throw new ArgumentOutOfRangeException(nameof(granulation));
        Granulation = granulation;
        if (_initialCustomPositions != null && _initialCustomPositions.Length > 0)
        {
            InitializeAgentsWithPositions(_initialCustomPositions);
            return;
        }
        InitializeAgents();
    }

    private CrowdSimulationEngine(MapScenario scenario, int agentCount)
    {
        AgentCount = agentCount;
        ConfigureMapScenario(scenario);
    }

    /// <summary>Prepares a map once, yielding browser time between batches; no demo or random agents are created.</summary>
    public static async Task<CrowdSimulationEngine> CreateMapAsync(MapScenario scenario,
        IReadOnlyList<(double X, double Y)> positions, int granulation = 1, double socialRepulsionWeight = 4.5,
        int? seed = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(positions);
        cancellationToken.ThrowIfCancellationRequested();
        if (granulation is < 1 or > 25) throw new ArgumentOutOfRangeException(nameof(granulation));
        if (positions.Count < 1 || Math.Ceiling((double)positions.Count / granulation) > MaxAllowedAgents)
            throw new ArgumentOutOfRangeException(nameof(positions));
        var engine = new CrowdSimulationEngine(scenario, positions.Count)
        {
            Granulation = granulation,
            IsMapMode = true,
            SocialRepulsionWeight = socialRepulsionWeight
        };
        var scheduler = new PreparationScheduler();
        foreach (var _ in engine.PrepareMapGridSteps(scenario)) await scheduler.YieldAsync(cancellationToken);
        await engine.PotentialFieldMap.BuildStaticFieldAsync(engine._mapExitZones, cancellationToken);
        foreach (var _ in engine.PrepareReachableMaskSteps()) await scheduler.YieldAsync(cancellationToken);
        foreach (var _ in engine.InitializeCustomAgentSteps(positions, seed)) await scheduler.YieldAsync(cancellationToken);
        return engine;
    }

    public CrowdSimulationEngine(double width = 200.0, double height = 200.0, int agentCount = 1000)
    {
        AgentCount = agentCount;
        SetupEnvironment(width, height);
        RebuildGrids();
        InitializeAgents();
    }

    public void SetMultiTargetEnabled(bool enabled)
    {
        if (MultiTargetEnabled == enabled) return;
        MultiTargetEnabled = enabled;
        SetupEnvironment(WorldWidth, WorldHeight);
        RebuildGrids();
        InitializeAgents();
    }

    public void SetTargets(IReadOnlyList<EvacuationTarget> newTargets)
    {
        if (newTargets == null || newTargets.Count == 0) return;
        MultiTargetEnabled = true;
        Targets.Clear();
        Targets.AddRange(newTargets);
        RebuildGrids();
        ReevaluateTargetAssignments();
    }

    public void SetEnvironment(
        IReadOnlyList<ObstacleDto>? obstacles,
        IReadOnlyList<EvacuationTarget>? targets,
        double? weightDistance = null,
        double? weightOccupancy = null)
    {
        if (obstacles != null)
        {
            SimulationObstacles = obstacles
                .Select(o => new Obstacle(o.X, o.Y, o.Width, o.Height, o.Id))
                .ToArray();
        }

        if (targets != null && targets.Count > 0)
        {
            Targets.Clear();
            Targets.AddRange(targets);
            MultiTargetEnabled = targets.Count > 1;
            ExitZone = new Obstacle(targets[0].X, targets[0].Y, targets[0].Width, targets[0].Height, targets[0].Id);
        }

        if (weightDistance.HasValue) WeightDistance = weightDistance.Value;
        if (weightOccupancy.HasValue) WeightOccupancy = weightOccupancy.Value;

        RebuildGrids();
        ReevaluateTargetAssignments(updateOccupancies: false);
    }

    public SimulationSnapshot CreateSnapshot(string sessionId = "", double timestamp = 0)
    {
        int activeCount = SimulatedAgentCount;
        var posX = new double[activeCount];
        var posY = new double[activeCount];
        Array.Copy(AgentPositionX, posX, activeCount);
        Array.Copy(AgentPositionY, posY, activeCount);

        var obstacleDtos = SimulationObstacles.Select((o, idx) =>
            new ObstacleDto(o.X, o.Y, o.Width, o.Height, o.Id ?? $"obs_{idx}")).ToList();

        return new SimulationSnapshot(
            SessionId: string.IsNullOrEmpty(sessionId) ? Guid.NewGuid().ToString("N")[..8] : sessionId,
            Timestamp: timestamp > 0 ? timestamp : (FrameCounter * 0.016),
            WorldWidth: WorldWidth,
            WorldHeight: WorldHeight,
            Obstacles: obstacleDtos,
            Targets: Targets.ToList(),
            WeightDistance: WeightDistance,
            WeightOccupancy: WeightOccupancy,
            ActiveAgentCount: activeCount,
            AgentPositionsX: posX,
            AgentPositionsY: posY,
            SmoothedDensities: PotentialFieldMap.GetSmoothedDensitiesCopy()
        );
    }

    public static CrowdSimulationEngine FromSnapshot(SimulationSnapshot snapshot)
    {
        int count = snapshot.ActiveAgentCount > 0 ? snapshot.ActiveAgentCount : 1000;
        var engine = new CrowdSimulationEngine(snapshot.WorldWidth, snapshot.WorldHeight, count);
        engine.ApplySnapshot(snapshot);
        return engine;
    }

    public void ApplySnapshot(SimulationSnapshot snapshot)
    {
        WorldWidth = snapshot.WorldWidth > 0 ? snapshot.WorldWidth : 200.0;
        WorldHeight = snapshot.WorldHeight > 0 ? snapshot.WorldHeight : 200.0;
        WeightDistance = snapshot.WeightDistance;
        WeightOccupancy = snapshot.WeightOccupancy;

        if (snapshot.Obstacles != null && snapshot.Obstacles.Count > 0)
        {
            SimulationObstacles = snapshot.Obstacles
                .Select(o => new Obstacle(o.X, o.Y, o.Width, o.Height, o.Id))
                .ToArray();
        }

        if (snapshot.Targets != null && snapshot.Targets.Count > 0)
        {
            Targets.Clear();
            Targets.AddRange(snapshot.Targets);
            MultiTargetEnabled = snapshot.Targets.Count > 1;
        }

        RebuildGrids();

        if (snapshot.AgentPositionsX != null && snapshot.AgentPositionsY != null && snapshot.ActiveAgentCount > 0)
        {
            int count = Math.Min(snapshot.ActiveAgentCount, Math.Min(snapshot.AgentPositionsX.Length, snapshot.AgentPositionsY.Length));
            AgentCount = count * Math.Max(1, Granulation);
            Array.Copy(snapshot.AgentPositionsX, AgentPositionX, count);
            Array.Copy(snapshot.AgentPositionsY, AgentPositionY, count);
            PotentialFieldMap.UpdateDynamicDensity(count, AgentPositionX, AgentPositionY, Granulation);
        }
        else if (snapshot.SmoothedDensities != null)
        {
            PotentialFieldMap.SetSmoothedDensities(snapshot.SmoothedDensities);
        }

        PotentialFieldMap.RefineDynamicField();
    }

    public void UpdateTargetOccupancy(string targetId, int newOccupancy)
    {
        var idx = Targets.FindIndex(t => string.Equals(t.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            Targets[idx] = Targets[idx] with { CurrentOccupancy = Math.Max(0, newOccupancy) };
            if (MultiTargetEnabled && Targets.Count > 1)
            {
                PotentialFieldMap.BuildStaticField(SimulationObstacles, Targets, WeightDistance, WeightOccupancy, WorldWidth);
                PotentialFieldMap.RefineDynamicField();
            }
        }
    }

    public TargetAssignmentResponse EvaluateTargetAssignment(
        double px,
        double py,
        double? customWeightDistance = null,
        double? customWeightOccupancy = null,
        IReadOnlyList<EvacuationTarget>? customTargets = null,
        double? maxDistance = null)
    {
        var targetList = (customTargets != null && customTargets.Count > 0)
            ? customTargets.Where(t => t.IsActive).ToList()
            : Targets.Where(t => t.IsActive).ToList();

        if (targetList.Count == 0)
        {
            return new TargetAssignmentResponse(
                Target: null,
                Distance: 0.0,
                OccupancyRatio: 0.0,
                Instructions: "Brak dostępnych punktów ewakuacji.",
                CalculatedCost: 0.0,
                IsSimulationEngineBased: true);
        }

        double wDist = Math.Max(0.0, customWeightDistance ?? WeightDistance);
        double wOcc = Math.Max(0.0, customWeightOccupancy ?? WeightOccupancy);

        double dMax = maxDistance.GetValueOrDefault(0.0);
        if (dMax <= 0.0)
        {
            double maxTargetDist = targetList.Max(t => PotentialFieldMap.GetWalkableDistance(t.Id, px, py));
            dMax = Math.Max(1.0, maxTargetDist > 0.0 ? maxTargetDist : Math.Max(WorldWidth, WorldHeight));
        }

        bool hasAnyTargetWithCapacity = targetList.Any(t => t.CurrentOccupancy < t.Capacity);

        EvacuationTarget? bestTarget = null;
        double minCost = double.PositiveInfinity;
        double bestDistance = 0.0;
        double bestOccupancyRatio = 0.0;
        var evaluations = new List<TargetEvaluationDto>();

        // Sample potential field flow direction for navigation azimuth
        var (fx, fy) = PotentialFieldMap.GetFlowDirection(px, py);
        double flowRad = Math.Atan2(fy, fx);
        double bearingDeg = Math.Round((flowRad * (180.0 / Math.PI) + 360.0) % 360.0);

        foreach (var target in targetList)
        {
            double walkableDist = PotentialFieldMap.GetWalkableDistance(target.Id, px, py);
            double normDist = Math.Clamp(walkableDist / dMax, 0.0, 10.0);

            int capacity = Math.Max(1, target.Capacity);
            double occRatio = (double)target.CurrentOccupancy / capacity;

            double distCost = wDist * normDist;
            double occCost = wOcc * occRatio;
            double cost = distCost + occCost;

            bool isFull = target.CurrentOccupancy >= capacity;
            if (isFull && hasAnyTargetWithCapacity)
            {
                cost += TargetSelector.CapacityOverflowPenalty;
            }

            if (cost < minCost)
            {
                minCost = cost;
                bestTarget = target;
                bestDistance = walkableDist;
                bestOccupancyRatio = occRatio;
            }

            evaluations.Add(new TargetEvaluationDto(
                TargetId: target.Id,
                TargetName: target.Name,
                WalkableDistance: Math.Round(walkableDist, 1),
                NormalizedDistance: Math.Round(normDist, 3),
                OccupancyRatio: Math.Round(occRatio, 3),
                DistanceCost: Math.Round(distCost, 4),
                OccupancyCost: Math.Round(occCost, 4),
                TotalCost: Math.Round(cost, 4),
                IsFull: isFull,
                IsSelected: false
            ));
        }

        if (bestTarget == null)
        {
            bestTarget = targetList[0];
            bestDistance = PotentialFieldMap.GetWalkableDistance(bestTarget.Id, px, py);
            bestOccupancyRatio = bestTarget.OccupancyRatio;
        }

        // Mark selected in evaluations
        for (int i = 0; i < evaluations.Count; i++)
        {
            if (evaluations[i].TargetId == bestTarget.Id)
            {
                evaluations[i] = evaluations[i] with { IsSelected = true };
                break;
            }
        }

        string instructions = TargetSelector.FormatInstructions(bestTarget, bestDistance, bestOccupancyRatio);

        return new TargetAssignmentResponse(
            Target: bestTarget,
            Distance: Math.Round(bestDistance, 1),
            OccupancyRatio: Math.Round(bestOccupancyRatio, 3),
            Instructions: instructions,
            CalculatedCost: Math.Round(minCost, 4),
            BearingDegrees: bearingDeg,
            FlowDirectionX: fx,
            FlowDirectionY: fy,
            IsSimulationEngineBased: true,
            TargetEvaluations: evaluations
        );
    }

    public void SetupEnvironment(double width, double height)
    {
        MapScenario = null;
        _spawnCells = null;
        WorldWidth = width;
        WorldHeight = height;

        SimulationObstacles = new[]
        {
            new Obstacle(width * 0.35, height * 0.18, width * 0.12, height * 0.28),
            new Obstacle(width * 0.35, height * 0.54, width * 0.12, height * 0.28)
        };

        ExitZone = new Obstacle(width * 0.92, height * 0.40, width * 0.06, height * 0.20);
        TargetX = width * 0.95;
        TargetY = height * 0.50;
        WhiskerLength = 2.5;

        Targets.Clear();
        if (MultiTargetEnabled)
        {
            int cap = Math.Max(50, AgentCount / 2);
            Targets.Add(new EvacuationTarget(
                Id: "exit-north",
                Name: "Schron Północny (Brama A)",
                X: width * 0.90,
                Y: height * 0.12,
                Width: width * 0.08,
                Height: height * 0.20,
                Capacity: cap,
                CurrentOccupancy: 0,
                IsActive: true));

            Targets.Add(new EvacuationTarget(
                Id: "exit-south",
                Name: "Schron Południowy (Brama B)",
                X: width * 0.90,
                Y: height * 0.68,
                Width: width * 0.08,
                Height: height * 0.20,
                Capacity: cap,
                CurrentOccupancy: 0,
                IsActive: true));
        }
        else
        {
            Targets.Add(new EvacuationTarget(
                Id: "exit-main",
                Name: "Strefa Ewakuacji",
                X: ExitZone.X,
                Y: ExitZone.Y,
                Width: ExitZone.Width,
                Height: ExitZone.Height,
                Capacity: AgentCount,
                CurrentOccupancy: 0,
                IsActive: true));
        }
    }

    public void RebuildGrids()
    {
        if (MapScenario is { } scenario)
        {
            RebuildMapGrids(scenario);
            return;
        }

        double cellSize = Math.Max(2.0, Math.Min(WorldWidth, WorldHeight) / 100.0);
        PotentialFieldMap = new PotentialFieldGrid(cellSize, WorldWidth, WorldHeight);
        SpatialHashGridIndex = new SpatialHashGrid(Math.Max(3.6, cellSize * 1.2), MaxAllowedAgents, 65536);

        PotentialFieldMap.InitializeStaticObstacles(SimulationObstacles);
        if (MultiTargetEnabled && Targets.Count > 1)
        {
            PotentialFieldMap.BuildStaticField(SimulationObstacles, Targets, WeightDistance, WeightOccupancy, WorldWidth);
        }
        else if (Targets.Count == 1)
        {
            PotentialFieldMap.BuildStaticField(SimulationObstacles, Targets, WeightDistance, WeightOccupancy, WorldWidth);
        }
        else
        {
            PotentialFieldMap.BuildStaticField(SimulationObstacles, ExitZone);
        }
        _dynamicFieldTimer = 0.20;
    }

    public void ReevaluateTargetAssignments(bool updateOccupancies = false)
    {
        if (!MultiTargetEnabled || Targets.Count <= 1) return;

        int[] counts = new int[Targets.Count];
        int activeCount = SimulatedAgentCount;
        for (int i = 0; i < activeCount; i++)
        {
            double px = AgentPositionX[i];
            double py = AgentPositionY[i];
            var assignment = EvaluateTargetAssignment(px, py);
            int bestIdx = 0;
            for (int t = 0; t < Targets.Count; t++)
            {
                if (assignment.Target != null && Targets[t].Id == assignment.Target.Id)
                {
                    bestIdx = t;
                    break;
                }
            }
            AgentTargetIndex[i] = bestIdx;
            counts[bestIdx]++;
        }

        if (updateOccupancies)
        {
            for (int t = 0; t < Targets.Count; t++)
            {
                Targets[t] = Targets[t] with { CurrentOccupancy = counts[t] };
            }

            PotentialFieldMap.BuildStaticField(SimulationObstacles, Targets, WeightDistance, WeightOccupancy, WorldWidth);
        }
    }

    private (double X, double Y)[]? _initialCustomPositions;

    /// <summary>
    /// Initializes agents with precomputed positions (e.g. derived from GUS census data or map evacuation zones).
    /// </summary>
    public void InitializeAgentsWithPositions(IReadOnlyList<(double X, double Y)> positions, int? seed = null)
    {
        foreach (var _ in InitializeCustomAgentSteps(positions, seed)) { }
    }

    private IEnumerable<int> InitializeCustomAgentSteps(IReadOnlyList<(double X, double Y)> positions, int? seed)
    {
        ArgumentNullException.ThrowIfNull(positions);
        _initialCustomPositions = positions.ToArray();
        Random random = seed.HasValue ? new Random(seed.Value) : new Random();

        int totalCount = positions.Count;
        AgentCount = totalCount;
        int activeCount = SimulatedAgentCount;
        double radius = GetAgentRadius(Granulation);
        _evacuatedCount = 0;
        _lastEvacuationTime = 0;
        SimulationTime = 0;
        FrameCounter = 0;

        _activePresetAgents = activeCount;
        _activeMapAgents = activeCount;
        if (MapScenario != null && (_evacuatedPerExit == null || _evacuatedPerExit.Length != MapScenario.Exits.Length))
        {
            _evacuatedPerExit = new int[MapScenario.Exits.Length];
        }
        if (_evacuationTimes == null || _evacuationTimes.Length < activeCount)
        {
            _evacuationTimes = new double[activeCount];
        }

        // When Granulation > 1, sample evenly from the custom positions to place macro-dots
        double step = activeCount > 0 ? (double)totalCount / activeCount : 1.0;
        for (int i = 0; i < activeCount && i < MaxAllowedAgents; i++)
        {
            if (i % 128 == 0) yield return i;
            int posIdx = Math.Min(totalCount - 1, (int)(i * step));
            var (px, py) = positions[posIdx];

            if (IsMapScenario)
            {
                double clearance = radius + 0.6;
                if (!IsSafeMapSpawn(px, py, clearance))
                {
                    var open = FindNearestReachable(px, py, 50.0, clearance)
                        ?? throw new InvalidOperationException("No exit-connected spawn position has sufficient wall clearance.");
                    px = open.X;
                    py = open.Y;
                }
            }

            AgentPositionX[i] = Math.Clamp(px, radius, WorldWidth - radius);
            AgentPositionY[i] = Math.Clamp(py, radius, WorldHeight - radius);
            AgentVelocityX[i] = 0.0;
            AgentVelocityY[i] = 0.0;
            AgentRadius[i] = radius;
            AgentMaxSpeed[i] = 1.3 + random.NextDouble() * 0.4;
            AgentLocalDensity[i] = 0.0;
        }

        ResetRecoveryTracking(activeCount);
        if (MultiTargetEnabled && Targets.Count > 1)
        {
            ReevaluateTargetAssignments();
        }

        if (!IsMapScenario)
        {
            PotentialFieldMap.UpdateDynamicDensity(activeCount, AgentPositionX, AgentPositionY, Granulation);
            PotentialFieldMap.RefineDynamicField();
        }
        else if (Granulation > 1)
        {
            PotentialFieldMap.UpdateDynamicDensity(activeCount, AgentPositionX, AgentPositionY, Granulation, updatePenalty: false);
        }
        _dynamicFieldTimer = 0.0;
    }

    public void InitializeAgents(int? seed = null)
    {
        Random random = seed.HasValue ? new Random(seed.Value) : new Random();
        double spawnMinX = WorldWidth * 0.03;
        double spawnMaxX = WorldWidth * 0.18;
        double spawnMinY = WorldHeight * 0.15;
        double spawnMaxY = WorldHeight * 0.85;

        int activeCount = SimulatedAgentCount;
        double radius = GetAgentRadius(Granulation);
        _evacuatedCount = 0;
        _lastEvacuationTime = 0;
        SimulationTime = 0;
        FrameCounter = 0;

        if (IsMapScenario)
        {
            SpawnMapAgents(random, activeCount, radius);
            if (Granulation > 1)
                PotentialFieldMap.UpdateDynamicDensity(activeCount, AgentPositionX, AgentPositionY, Granulation, updatePenalty: false);
            _dynamicFieldTimer = 0.0;
            return;
        }

        _activePresetAgents = activeCount;
        if (_evacuationTimes == null || _evacuationTimes.Length < activeCount)
        {
            _evacuationTimes = new double[activeCount];
        }

        for (int i = 0; i < activeCount && i < MaxAllowedAgents; i++)
        {
            AgentPositionX[i] = spawnMinX + random.NextDouble() * (spawnMaxX - spawnMinX);
            AgentPositionY[i] = spawnMinY + random.NextDouble() * (spawnMaxY - spawnMinY);
            AgentVelocityX[i] = 1.2;
            AgentVelocityY[i] = 0.0;
            AgentRadius[i] = radius;
            AgentMaxSpeed[i] = 1.3 + random.NextDouble() * 0.4;
            AgentLocalDensity[i] = 0.0;
        }

        if (MultiTargetEnabled && Targets.Count > 1)
        {
            ReevaluateTargetAssignments();
        }

        PotentialFieldMap.UpdateDynamicDensity(activeCount, AgentPositionX, AgentPositionY, Granulation);
        PotentialFieldMap.RefineDynamicField();
        _dynamicFieldTimer = 0.0;
    }

    private double _dynamicFieldTimer = 0.20;

    public void UpdatePhysics(double dt)
    {
        int activeCount = ActiveAgentCount;
        SimulationTime += dt;
        _recoveredThisStep.Clear();

        // Aktualizacja gęstości komórkowej na siatce przy każdym kroku fizyki dla ciągłego samplingu (tylko makro dla Granulation > 1):
        if (Granulation > 1)
        {
            PotentialFieldMap.UpdateDynamicDensity(activeCount, AgentPositionX, AgentPositionY, Granulation, updatePenalty: false);
        }

        _dynamicFieldTimer += dt;
        // Map routing is prepared once from terrain and exits; local crowd forces still run every tick.
        if (!IsMapScenario && (_dynamicFieldTimer >= 0.20 || FrameCounter == 0))
        {
            _dynamicFieldTimer = 0.0;
            // Aktualizacja kar tłumu dla Dijkstry co 0.20s z zachowaniem skalibrowanego tempa EMA:
            PotentialFieldMap.UpdateDynamicDensity(activeCount, AgentPositionX, AgentPositionY, Granulation, updatePenalty: true);
            PotentialFieldMap.RefineDynamicField();
        }
        FrameCounter++;

        SpatialHashGridIndex.Clear();
        for (int agentIndex = 0; agentIndex < activeCount; agentIndex++)
        {
            SpatialHashGridIndex.Insert(agentIndex, AgentPositionX[agentIndex], AgentPositionY[agentIndex]);
        }

        double scaleSqrtG = Math.Sqrt(Granulation);
        double powG025 = Math.Pow(Granulation, 0.25);
        double h = 2.0 * powG025;
        double h2 = h * h;
        double invH2 = 1.0 / h2;
        double spatialSearchRadius = (Granulation == 1) ? 2.8 : Math.Max(4.5, 2.2 * scaleSqrtG);
        double spatialSearchRadiusSquared = spatialSearchRadius * spatialSearchRadius;

        double radialCoeff = 0.764 * scaleSqrtG;
        double forwardCoeff = 1.528 * scaleSqrtG;

        // The preset bottleneck corridor does not exist on a map (NaN bounds never match).
        double corridorMinX = (IsMapScenario || IsMapMode) ? double.NaN : WorldWidth * 0.35 - 0.5;
        double corridorMaxX = WorldWidth * 0.47 + 1.0;
        double corridorMinY = WorldHeight * 0.455;
        double corridorMaxY = WorldHeight * 0.545;

        double effectiveWhisker = (Granulation == 1) ? WhiskerLength : WhiskerLength * powG025;
        double brakeMargin = Math.Min(4.5, 1.80 * scaleSqrtG);
        double rearPushThreshold = 0.30 * scaleSqrtG;
        double lateralRampScale = 0.40 * scaleSqrtG;
        double lateralRampInv = 1.0 / lateralRampScale;
        double lateralBackCutoff = -0.5 * scaleSqrtG;
        double invScaleSqrtG = 1.0 / scaleSqrtG;
        double inertiaDamping = Granulation > 1 ? Math.Min(1.75, Math.Pow(Granulation, 0.18)) : 1.0;

        for (int agentIndex = 0; agentIndex < activeCount; agentIndex++)
        {
            // 1. Bazowy kierunek z pola potencjału
            var (flowDirectionX, flowDirectionY) = PotentialFieldMap!.GetFlowDirection(AgentPositionX[agentIndex], AgentPositionY[agentIndex]);

            double currentAgentSpeed = Math.Sqrt(AgentVelocityX[agentIndex] * AgentVelocityX[agentIndex] + AgentVelocityY[agentIndex] * AgentVelocityY[agentIndex]);
            bool hasSignificantVelocity = currentAgentSpeed > 0.05;
            double flowMag = Math.Sqrt(flowDirectionX * flowDirectionX + flowDirectionY * flowDirectionY);
            double normalizedVelocityX = hasSignificantVelocity ? AgentVelocityX[agentIndex] / currentAgentSpeed : (flowMag > 0.001 ? flowDirectionX / flowMag : 1.0);
            double normalizedVelocityY = hasSignificantVelocity ? AgentVelocityY[agentIndex] / currentAgentSpeed : (flowMag > 0.001 ? flowDirectionY / flowMag : 0.0);

            // 2. Bazowa siła celu i ślizg po ścianach
            double travelDirectionX = flowDirectionX;
            double travelDirectionY = flowDirectionY;

            // 3. Odpychanie od ścian i przeszkód
            double wallRepulsionForceX = 0;
            double wallRepulsionForceY = 0;
            double minDistanceToObstacle = double.MaxValue;

            if (!IsMapScenario && !IsMapMode && AgentPositionX[agentIndex] < 6.0)
            {
                wallRepulsionForceX += Math.Max(1.0, (6.0 - AgentPositionX[agentIndex]) * 2.0);
            }

            double px = AgentPositionX[agentIndex];
            double py = AgentPositionY[agentIndex];
            double comfortDistance = AgentRadius[agentIndex] + 0.6;
            double comfortDistanceSquared = comfortDistance * comfortDistance;

            double obstacleBlockage = 0.0;
            int nearbyObstacleCount = CollectNearbyObstacles(px, py, comfortDistance, contactsOnly: true, out var nearbyObstacles);
            for (int obstacleIndex = 0; obstacleIndex < nearbyObstacleCount; obstacleIndex++)
            {
                var obstacle = nearbyObstacles[obstacleIndex];
                if (px < obstacle.X - 2.5 || px > obstacle.X + obstacle.Width + 2.5 ||
                    py < obstacle.Y - 2.5 || py > obstacle.Y + obstacle.Height + 2.5)
                {
                    continue;
                }

                double nearestBoxPointX = Math.Max(obstacle.X, Math.Min(px, obstacle.X + obstacle.Width));
                double nearestBoxPointY = Math.Max(obstacle.Y, Math.Min(py, obstacle.Y + obstacle.Height));
                double distanceToBoxVectorX = px - nearestBoxPointX;
                double distanceToBoxVectorY = py - nearestBoxPointY;
                double distanceToBoxSquared = distanceToBoxVectorX * distanceToBoxVectorX + distanceToBoxVectorY * distanceToBoxVectorY;

                if (distanceToBoxSquared < comfortDistanceSquared && distanceToBoxSquared > 0.000001)
                {
                    double distanceToBox = Math.Sqrt(distanceToBoxSquared);
                    if (distanceToBox < minDistanceToObstacle) minDistanceToObstacle = distanceToBox;

                    double normalX = distanceToBoxVectorX / distanceToBox;
                    double normalY = distanceToBoxVectorY / distanceToBox;

                    double wallRepulsionStrength = 3.5 * (comfortDistance - distanceToBox) / comfortDistance;
                    // Odpychanie normalne od przeszkody/narożnika (bez sztucznego zerowania w osi przepływu)
                    wallRepulsionForceX += normalX * wallRepulsionStrength;
                    wallRepulsionForceY += normalY * wallRepulsionStrength;

                    // Wall sliding: ześlizg kierunku marszu wzdłuż krawędzi/narożnika
                    double dotWithNormal = travelDirectionX * normalX + travelDirectionY * normalY;
                    if (dotWithNormal < 0)
                    {
                        obstacleBlockage = Math.Max(obstacleBlockage, -dotWithNormal);
                        travelDirectionX -= dotWithNormal * normalX;
                        travelDirectionY -= dotWithNormal * normalY;

                        double slideSpeed = Math.Sqrt(travelDirectionX * travelDirectionX + travelDirectionY * travelDirectionY);
                        if (slideSpeed > 0.05)
                        {
                            travelDirectionX /= slideSpeed;
                            travelDirectionY /= slideSpeed;
                        }
                        else
                        {
                            // Czołowe zderzenie ze ścianą: nadaj wektor styczny do ściany omijający przeszkodę
                            double tan1X = -normalY;
                            double tan1Y = normalX;

                            double sampleBaseX = px + normalX * 0.5;
                            double sampleBaseY = py + normalY * 0.5;
                            float pot1 = PotentialFieldMap != null ? PotentialFieldMap.SamplePotential(sampleBaseX + tan1X * 1.5, sampleBaseY + tan1Y * 1.5) : float.MaxValue;
                            float pot2 = PotentialFieldMap != null ? PotentialFieldMap.SamplePotential(sampleBaseX - tan1X * 1.5, sampleBaseY - tan1Y * 1.5) : float.MaxValue;

                            if (pot1 < pot2)
                            {
                                travelDirectionX = tan1X;
                                travelDirectionY = tan1Y;
                            }
                            else if (pot2 < pot1)
                            {
                                travelDirectionX = -tan1X;
                                travelDirectionY = -tan1Y;
                            }
                            else
                            {
                                double dotFlow = flowDirectionX * tan1X + flowDirectionY * tan1Y;
                                if (dotFlow > 0.001)
                                {
                                    travelDirectionX = tan1X;
                                    travelDirectionY = tan1Y;
                                }
                                else if (dotFlow < -0.001)
                                {
                                    travelDirectionX = -tan1X;
                                    travelDirectionY = -tan1Y;
                                }
                                else
                                {
                                    double dotVel = normalizedVelocityX * tan1X + normalizedVelocityY * tan1Y;
                                    if (dotVel >= 0)
                                    {
                                        travelDirectionX = tan1X;
                                        travelDirectionY = tan1Y;
                                    }
                                    else
                                    {
                                        travelDirectionX = -tan1X;
                                        travelDirectionY = -tan1Y;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 4. Interakcje z sąsiadami (ciśnienie tłumu, rozprężanie na boki & hamowanie)
            double crowdAvoidanceForceX = 0;
            double crowdAvoidanceForceY = 0;
            const double crowdAvoidanceForceMultiplier = 0.45;

            int nearbyAgentCount = SpatialHashGridIndex.QueryNearby(
                px, py, spatialSearchRadius, out var nearbyIndices);

            // Anizotropowe próbkowanie gęstości (niezmiennicze względem granulacji):
            double totalRadialDensity = 0.0;
            double forwardKernelSum = 0.0;
            double closestForwardDistance = 10.0;
            double closestLeaderRadius = BaseAgentRadius;
            double crowdGradX = 0.0;
            double crowdGradY = 0.0;
            bool blocked = false;
            double closestBlockDist = effectiveWhisker;
            double densityLeft = 0;
            double densityRight = 0;
            int pass2Count = 0;
            bool hasRecoveryNeighbor = false;

            for (int i = 0; i < nearbyAgentCount; i++)
            {
                int neighborIndex = nearbyIndices[i];
                if (neighborIndex == agentIndex) continue;

                double dx = AgentPositionX[neighborIndex] - px;
                double dy = AgentPositionY[neighborIndex] - py;
                double distSq = dx * dx + dy * dy;
                double recoveryNeighborDistance = AgentRadius[agentIndex] + AgentRadius[neighborIndex] + 1.0;
                if (distSq < recoveryNeighborDistance * recoveryNeighborDistance) hasRecoveryNeighbor = true;

                if (distSq > spatialSearchRadiusSquared || distSq < 0.000001) continue;

                double dist = Math.Sqrt(distSq);
                _neighborDisplacementX[i] = dx;
                _neighborDisplacementY[i] = dy;
                _neighborDistance[i] = dist;

                double minDist = AgentRadius[agentIndex] + AgentRadius[neighborIndex];
                double repulsionThreshold = minDist * 2.2;
                if (distSq <= repulsionThreshold * repulsionThreshold)
                {
                    _closeNeighborIndices[pass2Count++] = i;
                }

                double forwardDist = dx * normalizedVelocityX + dy * normalizedVelocityY;
                double signedLateral = dx * (-normalizedVelocityY) + dy * normalizedVelocityX;
                double lateralDist = Math.Abs(signedLateral);

                if (distSq < h2)
                {
                    double w = 1.0 - distSq * invH2;
                    totalRadialDensity += radialCoeff * (w * w);

                    // Wektor środka ciężkości tłumu wokół agenta (kierunek do wyższej gęstości)
                    crowdGradX += scaleSqrtG * (dx / dist) * w;
                    crowdGradY += scaleSqrtG * (dy / dist) * w;

                    if (forwardDist > 0.05)
                    {
                        double cosAngle = forwardDist / dist;
                        if (cosAngle > 0.50)
                        {
                            double wf = w * cosAngle;
                            forwardKernelSum += forwardCoeff * (wf * wf);
                        }

                        if (forwardDist < closestForwardDistance && lateralDist < minDist * 0.9)
                        {
                            closestForwardDistance = forwardDist;
                            closestLeaderRadius = AgentRadius[neighborIndex];
                        }
                    }
                }

                // Bilans gęstości lewo / prawo dla rozprężania poprzecznego tłumu z ciągłym przejściem (brak drgań/jitteru)
                if (forwardDist > lateralBackCutoff)
                {
                    double w = 1.0 - (dist / spatialSearchRadius);
                    if (Granulation == 1)
                    {
                        if (signedLateral > 0.1) densityLeft += w;
                        else if (signedLateral < -0.1) densityRight += w;
                    }
                    else
                    {
                        double lateralRamp = Math.Clamp(signedLateral * lateralRampInv, -1.0, 1.0);
                        if (lateralRamp > 0)
                        {
                            densityLeft += Granulation * w * lateralRamp;
                        }
                        else if (lateralRamp < 0)
                        {
                            densityRight += Granulation * w * (-lateralRamp);
                        }
                    }
                }

                if (forwardDist > 0.05 && forwardDist < effectiveWhisker)
                {
                    double collisionThreshold = (Granulation == 1) ? minDist * 0.8 : minDist * 1.0;
                    if (lateralDist < collisionThreshold)
                    {
                        blocked = true;
                        if (forwardDist < closestBlockDist)
                        {
                            closestBlockDist = forwardDist;
                        }
                    }
                }
            }

            // Siła gradientu ciśnienia tłumu (-grad rho): wypycha agentów ze ścisku w pustą przestrzeń (np. we wnękę w środku)
            double reliefForceX = 0.0;
            double reliefForceY = 0.0;
            double gradLen = 0.0;
            bool isInsideCorridor = px >= corridorMinX && px <= corridorMaxX &&
                                   py >= corridorMinY && py <= corridorMaxY;

            if (totalRadialDensity > 0.70)
            {
                gradLen = Math.Sqrt(crowdGradX * crowdGradX + crowdGradY * crowdGradY);
                if (gradLen > 0.01)
                {
                    double pressureCoeff = Math.Min(2.5, (totalRadialDensity - 0.70) * 0.75);
                    if (isInsideCorridor && minDistanceToObstacle < 1.5)
                    {
                        pressureCoeff *= 0.25; // W wąskim korytarzu ściany tłumią poprzeczne parcie
                    }
                    reliefForceX = -(crowdGradX / gradLen) * pressureCoeff;
                    reliefForceY = -(crowdGradY / gradLen) * pressureCoeff;
                }
            }

            // Ciągłe próbkowanie gęstości makroskopowej z siatki (niezmiennicze względem granulacji):
            double effectiveForwardKernel;
            if (Granulation == 1)
            {
                effectiveForwardKernel = forwardKernelSum;
            }
            else
            {
                double forwardMacroDensity = PotentialFieldMap!.SampleSmoothedDensity(
                    px + 1.8 * normalizedVelocityX,
                    py + 1.8 * normalizedVelocityY);
                double currentMacroDensity = PotentialFieldMap!.SampleSmoothedDensity(
                    px,
                    py);
                effectiveForwardKernel = Math.Max(Math.Max(currentMacroDensity, forwardMacroDensity), forwardKernelSum);
            }

            // Odstęp do bezpośredniego lidera i gęstość w stożku przednim:
            double surfaceGap = closestForwardDistance - (AgentRadius[agentIndex] + closestLeaderRadius);
            double effectiveHeadway = (closestForwardDistance < 9.0)
                ? (Granulation == 1 ? closestForwardDistance : 0.44 + Math.Max(0.0, surfaceGap / scaleSqrtG))
                : 10.0;

            double localDensity = PedestrianFundamentalDiagram.CalculateEffectiveDensity(effectiveForwardKernel, effectiveHeadway);
            AgentLocalDensity[agentIndex] = localDensity;

            double crowdPressureFactor = 1.0 + Math.Min(3.5, Math.Max(0.0, (Math.Max(localDensity, totalRadialDensity) - 0.7) * 1.2));
            double wallComfortDistance = AgentRadius[agentIndex] + 0.6;
            double wallClearanceFactor = 1.0;
            if (minDistanceToObstacle < wallComfortDistance)
            {
                wallClearanceFactor = 1.0 + 1.5 * (wallComfortDistance - minDistanceToObstacle) / wallComfortDistance;
            }

            double totalPressureFactor = crowdPressureFactor * wallClearanceFactor;

            double rearPushForceX = 0;
            double rearPushForceY = 0;
            double totalRearPushMagnitude = 0;

            for (int k = 0; k < pass2Count; k++)
            {
                int neighborListIndex = _closeNeighborIndices[k];
                int neighborAgentIndex = nearbyIndices[neighborListIndex];

                double neighborOffsetX = _neighborDisplacementX[neighborListIndex];
                double neighborOffsetY = _neighborDisplacementY[neighborListIndex];
                double neighborDistance = _neighborDistance[neighborListIndex];

                double minimumDistance = AgentRadius[agentIndex] + AgentRadius[neighborAgentIndex];
                double repulsionThreshold = minimumDistance * 2.2;

                if (neighborDistance <= repulsionThreshold && neighborDistance > 0.000001)
                {
                    double proximityWeight = (repulsionThreshold - neighborDistance) / repulsionThreshold;
                    double baseRepel = (SocialRepulsionWeight * 0.45 * totalPressureFactor) * proximityWeight;

                    // Asymetryczne rozpychanie się gęstego tłumu:
                    // Tłum w wysokim ścisku (totalRadialDensity > 1.2) wywiera dodatkowe parcie na otoczenie,
                    // robiąc sobie miejsce wśród luźniejszego tłumu zamiast czekać bez ruchu:
                    double denseCrowdPush = 0.0;
                    if (totalRadialDensity > 1.2)
                    {
                        denseCrowdPush = Math.Min(1.8, (totalRadialDensity - 1.2) * 0.55) * proximityWeight;
                    }

                    double totalRepel = baseRepel + denseCrowdPush;
                    crowdAvoidanceForceX -= (neighborOffsetX / neighborDistance) * totalRepel;
                    crowdAvoidanceForceY -= (neighborOffsetY / neighborDistance) * totalRepel;
                }

                double forwardDist = neighborOffsetX * normalizedVelocityX + neighborOffsetY * normalizedVelocityY;
                if (forwardDist < -0.05)
                {
                    double signedLateral = neighborOffsetX * (-normalizedVelocityY) + neighborOffsetY * normalizedVelocityX;
                    double lateralDist = Math.Abs(signedLateral);
                    double collisionThreshold = minimumDistance * 0.85;
                    if (lateralDist < collisionThreshold)
                    {
                        double rearForwardDrive = AgentVelocityX[neighborAgentIndex] * normalizedVelocityX + AgentVelocityY[neighborAgentIndex] * normalizedVelocityY;
                        if (rearForwardDrive < 0.2)
                        {
                            rearForwardDrive = AgentMaxSpeed[neighborAgentIndex] * 0.5;
                        }

                        double pushMag = PedestrianFundamentalDiagram.CalculateRearPushingForce(
                            localDensity, neighborDistance, minimumDistance, rearPushThreshold, rearForwardDrive);

                        if (pushMag > 0.0)
                        {
                            rearPushForceX += normalizedVelocityX * pushMag;
                            rearPushForceY += normalizedVelocityY * pushMag;
                            totalRearPushMagnitude += pushMag;
                        }
                    }
                }
            }

            double lateralFanForceX = 0;
            double lateralFanForceY = 0;

            double leftNormalX = -normalizedVelocityY;
            double leftNormalY = normalizedVelocityX;

            double contactBrakeFactor = 1.0;
            if (blocked)
            {
                if (Granulation == 1)
                {
                    double contactBrakeDist = AgentRadius[agentIndex] * 2.0 * 1.5;
                    if (closestBlockDist < contactBrakeDist)
                    {
                        contactBrakeFactor = Math.Clamp(closestBlockDist / contactBrakeDist, 0.05, 1.0);
                        if (totalRearPushMagnitude > 0.0)
                        {
                            double pushRelief = Math.Min(1.0, totalRearPushMagnitude / 2.5);
                            contactBrakeFactor = contactBrakeFactor + (1.0 - contactBrakeFactor) * pushRelief;
                        }
                    }
                }
                else
                {
                    double contactBrakeDist = AgentRadius[agentIndex] * 2.0 + brakeMargin;
                    if (closestBlockDist < contactBrakeDist)
                    {
                        double contactGap = Math.Max(0.0, closestBlockDist - AgentRadius[agentIndex] * 2.0);
                        contactBrakeFactor = Math.Clamp(0.15 + 0.85 * (contactGap / brakeMargin), 0.08, 1.0);
                        if (totalRearPushMagnitude > 0.0)
                        {
                            double pushRelief = Math.Min(1.0, totalRearPushMagnitude / 2.5);
                            contactBrakeFactor = contactBrakeFactor + (1.0 - contactBrakeFactor) * pushRelief;
                        }
                    }
                }

                // Aktywne rozprężanie / omijanie zatoru (Front Queue Fanning)
                double steerDir;
                if (Granulation == 1)
                {
                    if (densityLeft < densityRight - 0.15)
                    {
                        steerDir = 1.0; // w lewo (gdzie jest luźniej)
                    }
                    else if (densityRight < densityLeft - 0.15)
                    {
                        steerDir = -1.0; // w prawo (gdzie jest luźniej)
                    }
                    else
                    {
                        // Symetrycznie: rozbijamy zbieganie się w jedną linię parytetem indeksu
                        steerDir = ((agentIndex & 1) == 0) ? 1.0 : -1.0;
                    }
                }
                else
                {
                    // Płynne, ciągłe sterowanie dla makro-kropek (eliminacja flip-floppingu i drgań):
                    double diffNorm = (densityRight - densityLeft) * invScaleSqrtG;
                    double parityBias = ((agentIndex & 1) == 0) ? 0.25 : -0.25;
                    steerDir = Math.Clamp(diffNorm * 0.85 + parityBias * 0.20, -1.0, 1.0);
                }

                // Rozprężanie poprzeczne działa tylko w zatorze (przy podwyższonej gęstości),
                // aby w swobodnym marszu (Regime I) agenci nie drgali na boki w miejscu ("wiggle"):
                double congestionFactor = Math.Clamp((localDensity - 0.70) / 1.5, 0.0, 1.0);
                double fanStrength = (1.0 - closestBlockDist / effectiveWhisker) * (1.6 * congestionFactor);
                if (minDistanceToObstacle < 1.2)
                {
                    fanStrength *= 0.4;
                }

                lateralFanForceX += leftNormalX * (steerDir * fanStrength);
                lateralFanForceY += leftNormalY * (steerDir * fanStrength);
            }

            // Poprzeczne ciśnienie tłumu (Continuum Crowd Decompression):
            // Różnica gęstości lewo-prawo naturalnie rozszerza tłum w wolne obszary
            double effDensityDiff = (Granulation == 1) ? (densityRight - densityLeft) : ((densityRight - densityLeft) * invScaleSqrtG);
            if (Math.Abs(effDensityDiff) > 0.08 && localDensity > 0.5)
            {
                double decompMag = Math.Clamp(effDensityDiff * 0.45 * Math.Min(2.5, localDensity), -1.8, 1.8);
                if (minDistanceToObstacle < 1.2)
                {
                    decompMag *= 0.4;
                }
                lateralFanForceX += leftNormalX * decompMag;
                lateralFanForceY += leftNormalY * decompMag;
            }

            // 5. Empiryczny diagram fundamentalny (Weidmann / Seyfried)
            double effectiveGoalSpeed = PedestrianFundamentalDiagram.CalculateSpeed(localDensity, AgentMaxSpeed[agentIndex]);

            // Składowa wzdłużna (forward along flow direction)
            // Zgodnie z diagramem fundamentalnym Weidmanna/Seyfrieda, prędkość marszu wzdłuż strumienia
            // jest bezpośrednią funkcją gęstości tłumu:
            double forwardSpeed = effectiveGoalSpeed * contactBrakeFactor;

            // Składowa pchania z tyłu wzdłuż strumienia (lekki surge dopuszczalny tylko w płynnym ruchu)
            if (totalRearPushMagnitude > 0.1 && localDensity < PedestrianFundamentalDiagram.DenseCrowdThreshold)
            {
                double pushSurge = Math.Min(0.10, totalRearPushMagnitude * 0.05);
                forwardSpeed *= (1.0 + pushSurge);
            }

            // Ograniczenie dolne prędkości: w Regime IV pozwalamy prędkości spaść do 0.002 - 0.02 m/s
            double minSpeedFloor = localDensity >= PedestrianFundamentalDiagram.DenseCrowdThreshold ? 0.002 : 0.05;
            forwardSpeed = Math.Max(minSpeedFloor, Math.Min(AgentMaxSpeed[agentIndex], forwardSpeed));

            // Siły poprzeczne tłumu (orthogonal to travel direction: unikanie kolizji, rozprężanie tłumu, parcie ciśnienia w pustkę)
            double perpFlowX = -travelDirectionY;
            double perpFlowY = travelDirectionX;

            double crowdLateralTotalX = crowdAvoidanceForceX * crowdAvoidanceForceMultiplier + lateralFanForceX + reliefForceX;
            double crowdLateralTotalY = crowdAvoidanceForceY * crowdAvoidanceForceMultiplier + lateralFanForceY + reliefForceY;
            double perpComponent = crowdLateralTotalX * perpFlowX + crowdLateralTotalY * perpFlowY;

            // Budżet prędkości poprzecznej ściśle zgodny z diagramem fundamentalnym Weidmanna/Seyfrieda:
            // W luźnym tłumie duża swoboda boczna, w gęstym zatorze ruch poprzeczny to powolne szuranie / rozpychanie
            double maxPerpSpeed;
            if (localDensity >= PedestrianFundamentalDiagram.DenseCrowdThreshold)
            {
                // W ścisku (Regime IV) ruch poprzeczny to powolne szuranie
                maxPerpSpeed = Math.Min(0.025, Math.Max(0.005, effectiveGoalSpeed * 0.40));
            }
            else if (localDensity >= 3.50)
            {
                maxPerpSpeed = Math.Min(0.20, Math.Max(0.05, effectiveGoalSpeed * 0.65));
            }
            else if (localDensity >= PedestrianFundamentalDiagram.ConstrainedDensityThreshold)
            {
                maxPerpSpeed = Math.Min(0.40, Math.Max(0.10, effectiveGoalSpeed * 0.75));
            }
            else
            {
                maxPerpSpeed = Math.Min(0.70, AgentMaxSpeed[agentIndex] * 0.45);
            }
            if (minDistanceToObstacle > 1.2 && Granulation > 1)
            {
                // Ciągłe skalowanie prędkości poprzecznej eliminuje skoki i drgania ("jitter"):
                double lateralRatio = Math.Clamp(0.35 + 0.30 * (localDensity - 0.50), 0.35, 0.70);
                double smoothPerpCap = Math.Max(0.02, forwardSpeed * lateralRatio);
                maxPerpSpeed = Math.Min(maxPerpSpeed, smoothPerpCap);
            }
            perpComponent = Math.Clamp(perpComponent, -maxPerpSpeed, maxPerpSpeed);

            // Odpychanie od ścian:
            // Siła normalna zapobiegająca przenikaniu ścian, skalowana prędkością agenta (aby nie nadawać pędu pocisku w ścisku)
            double wallSpeedRatio = Math.Max(0.05, effectiveGoalSpeed / AgentMaxSpeed[agentIndex]);
            double scaledWallRepulsionX = wallRepulsionForceX * wallSpeedRatio;
            double scaledWallRepulsionY = wallRepulsionForceY * wallSpeedRatio;

            // Wypadkowa siła: marsz wzdłuż kierunku (ślizg po ścianie) + ruch poprzeczny + odpychanie od ścian
            double finalForceX = travelDirectionX * forwardSpeed + perpFlowX * perpComponent + scaledWallRepulsionX;
            double finalForceY = travelDirectionY * forwardSpeed + perpFlowY * perpComponent + scaledWallRepulsionY;

            // Całkowita prędkość i SpeedCap:
            // Całkowita prędkość agenta musi ściśle podlegać diagramowi fundamentalnemu Weidmanna/Seyfrieda:
            // Sumaryczny wektor prędkości (wzdłużny i poprzeczny) nie może przekraczać teoretycznej prędkości dla danej gęstości
            double calculatedSpeed = Math.Sqrt(finalForceX * finalForceX + finalForceY * finalForceY);
            double speedCap = Math.Min(AgentMaxSpeed[agentIndex], Math.Max(0.002, Math.Min(effectiveGoalSpeed, Math.Sqrt(forwardSpeed * forwardSpeed + perpComponent * perpComponent))));
            if (calculatedSpeed > speedCap && calculatedSpeed > 0.0001)
            {
                finalForceX = (finalForceX / calculatedSpeed) * speedCap;
                finalForceY = (finalForceY / calculatedSpeed) * speedCap;
            }

            // Przy wchodzeniu do zatoru (Regime IV) agent natychmiast wyhamowuje, eliminując sztuczne opóźnienie bezwładności
            double currentVelMag = Math.Sqrt(AgentVelocityX[agentIndex] * AgentVelocityX[agentIndex] + AgentVelocityY[agentIndex] * AgentVelocityY[agentIndex]);
            double velocitySmoothingInertia;
            if (localDensity >= PedestrianFundamentalDiagram.DenseCrowdThreshold)
            {
                velocitySmoothingInertia = (currentVelMag > speedCap) ? 0.85 : 0.35;
            }
            else if (isInsideCorridor)
            {
                velocitySmoothingInertia = 0.55;
            }
            else
            {
                velocitySmoothingInertia = 0.25;
            }

            if (Granulation > 1)
            {
                // Większe makro-cząstki reprezentują agregaty ludzi o większej bezwładności;
                // łagodne filtrowanie usuwa mikrodrgania numeryczne ("jitter") i wygładza ruch:
                velocitySmoothingInertia = Math.Max(0.12, velocitySmoothingInertia / inertiaDamping);
            }

            AgentVelocityX[agentIndex] = AgentVelocityX[agentIndex] * (1.0 - velocitySmoothingInertia) + finalForceX * velocitySmoothingInertia;
            AgentVelocityY[agentIndex] = AgentVelocityY[agentIndex] * (1.0 - velocitySmoothingInertia) + finalForceY * velocitySmoothingInertia;

            AgentPositionX[agentIndex] += AgentVelocityX[agentIndex] * dt;
            AgentPositionY[agentIndex] += AgentVelocityY[agentIndex] * dt;

            // 7. Circle-box collision
            double radius = AgentRadius[agentIndex];
            double pxAfter = AgentPositionX[agentIndex];
            double pyAfter = AgentPositionY[agentIndex];
            int collisionObstacleCount = CollectNearbyObstacles(pxAfter, pyAfter, radius + 0.1, contactsOnly: false, out var collisionObstacles);
            for (int obstacleIndex = 0; obstacleIndex < collisionObstacleCount; obstacleIndex++)
            {
                var obstacle = collisionObstacles[obstacleIndex];
                if (pxAfter < obstacle.X - radius - 0.1 || pxAfter > obstacle.X + obstacle.Width + radius + 0.1 ||
                    pyAfter < obstacle.Y - radius - 0.1 || pyAfter > obstacle.Y + obstacle.Height + radius + 0.1)
                {
                    continue;
                }

                double nearestPointX = Math.Clamp(pxAfter, obstacle.X, obstacle.X + obstacle.Width);
                double nearestPointY = Math.Clamp(pyAfter, obstacle.Y, obstacle.Y + obstacle.Height);

                double deltaX = pxAfter - nearestPointX;
                double deltaY = pyAfter - nearestPointY;
                double distSquared = deltaX * deltaX + deltaY * deltaY;

                if (distSquared < radius * radius)
                {
                    if (distSquared > 0.000001)
                    {
                        double dist = Math.Sqrt(distSquared);
                        double normalX = deltaX / dist;
                        double normalY = deltaY / dist;
                        double penetration = radius - dist;

                        AgentPositionX[agentIndex] += normalX * penetration;
                        AgentPositionY[agentIndex] += normalY * penetration;

                        double normalVelocity = AgentVelocityX[agentIndex] * normalX + AgentVelocityY[agentIndex] * normalY;
                        if (normalVelocity < 0)
                        {
                            AgentVelocityX[agentIndex] -= normalVelocity * normalX;
                            AgentVelocityY[agentIndex] -= normalVelocity * normalY;
                        }
                    }
                    else
                    {
                        double dLeft = AgentPositionX[agentIndex] - obstacle.X;
                        double dRight = (obstacle.X + obstacle.Width) - AgentPositionX[agentIndex];
                        double dTop = AgentPositionY[agentIndex] - obstacle.Y;
                        double dBottom = (obstacle.Y + obstacle.Height) - AgentPositionY[agentIndex];
                        double minD = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));

                        if (minD == dLeft) { AgentPositionX[agentIndex] = obstacle.X - radius; if (AgentVelocityX[agentIndex] > 0) AgentVelocityX[agentIndex] = 0; }
                        else if (minD == dRight) { AgentPositionX[agentIndex] = obstacle.X + obstacle.Width + radius; if (AgentVelocityX[agentIndex] < 0) AgentVelocityX[agentIndex] = 0; }
                        else if (minD == dTop) { AgentPositionY[agentIndex] = obstacle.Y - radius; if (AgentVelocityY[agentIndex] > 0) AgentVelocityY[agentIndex] = 0; }
                        else { AgentPositionY[agentIndex] = obstacle.Y + obstacle.Height + radius; if (AgentVelocityY[agentIndex] < 0) AgentVelocityY[agentIndex] = 0; }
                    }
                }
            }

            AgentPositionX[agentIndex] = Math.Clamp(AgentPositionX[agentIndex], AgentRadius[agentIndex], WorldWidth - AgentRadius[agentIndex]);
            AgentPositionY[agentIndex] = Math.Clamp(AgentPositionY[agentIndex], AgentRadius[agentIndex], WorldHeight - AgentRadius[agentIndex]);

            // A collision correction must not jump across a thin wall or cut a
            // blocked corner, even when the final cell happens to be open.
            if (IsMapScenario && !MapScenario!.IsBlockedAt(px, py) &&
                MapScenario.CrossesBlockedCell(px, py, AgentPositionX[agentIndex], AgentPositionY[agentIndex]))
            {
                AgentPositionX[agentIndex] = px;
                AgentPositionY[agentIndex] = py;
                AgentVelocityX[agentIndex] = 0;
                AgentVelocityY[agentIndex] = 0;
            }
            // Recover invalid starting state without allowing a normal step to
            // teleport to the other side of a building.
            else if (IsMapScenario && MapScenario!.IsBlockedAt(AgentPositionX[agentIndex], AgentPositionY[agentIndex]))
            {
                if (FindNearestReachable(AgentPositionX[agentIndex], AgentPositionY[agentIndex], 20.0, radius) is { } openCell)
                {
                    double nudgeX = openCell.X - AgentPositionX[agentIndex];
                    double nudgeY = openCell.Y - AgentPositionY[agentIndex];
                    AgentPositionX[agentIndex] = openCell.X;
                    AgentPositionY[agentIndex] = openCell.Y;

                    double nudgeLen = Math.Sqrt(nudgeX * nudgeX + nudgeY * nudgeY);
                    if (nudgeLen > 0.0001)
                    {
                        double nX = nudgeX / nudgeLen;
                        double nY = nudgeY / nudgeLen;
                        // Cancel any velocity component directed into the obstacle (opposite to nudge)
                        double normalV = AgentVelocityX[agentIndex] * (-nX) + AgentVelocityY[agentIndex] * (-nY);
                        if (normalV > 0)
                        {
                            AgentVelocityX[agentIndex] += normalV * nX;
                            AgentVelocityY[agentIndex] += normalV * nY;
                        }
                    }
                    else
                    {
                        AgentVelocityX[agentIndex] = 0.0;
                        AgentVelocityY[agentIndex] = 0.0;
                    }
                }
            }
            if (IsMapScenario) RecoverStationaryAgent(agentIndex, hasRecoveryNeighbor);
        }

        ProcessEvacuations();
    }

    /// <summary>Switches to a real map area. The scenario raster defines walls and every exit is a sink.</summary>
    public void LoadMapScenario(MapScenario scenario)
    {
        ConfigureMapScenario(scenario);
        RebuildGrids();
        InitializeAgents();
    }

    private void ConfigureMapScenario(MapScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        MapScenario = scenario;
        WorldWidth = scenario.WorldWidth;
        WorldHeight = scenario.WorldHeight;
        SimulationObstacles = Array.Empty<Obstacle>();
        _mapExitZones = scenario.Exits
            .Select(exit => new Obstacle(exit.X - exit.Radius, exit.Y - exit.Radius, exit.Radius * 2.0, exit.Radius * 2.0))
            .ToArray();
        ExitZone = _mapExitZones[0];
        TargetX = scenario.Exits[0].X;
        TargetY = scenario.Exits[0].Y;
        WhiskerLength = 2.5;
    }

    public MapEvacuationStatus GetMapEvacuationStatus()
    {
        var scenario = MapScenario;
        if (scenario is null)
            return new MapEvacuationStatus(false, 0, 0, 0, 0, [], null, null, null, false, false);

        int dots = SimulatedAgentCount;
        int peoplePerDot = Math.Max(1, Granulation);
        int evacuatedPeople = Math.Min(AgentCount, _evacuatedCount * peoplePerDot);
        var perExit = new int[_evacuatedPerExit.Length];
        for (int i = 0; i < perExit.Length; i++) perExit[i] = _evacuatedPerExit[i] * peoplePerDot;
        bool finished = _activeMapAgents == 0;
        // Distant points take a while to reach: before anyone arrives, wait up to an hour; afterwards a
        // gap of StalledEvacuationSeconds without a single arrival means the rest cannot get out.
        bool stalled = !finished && (_evacuatedCount == 0
            ? SimulationTime > FirstArrivalTimeoutSeconds
            : SimulationTime - _lastEvacuationTime > StalledEvacuationSeconds);
        return new MapEvacuationStatus(
            true,
            SimulationTime,
            AgentCount,
            evacuatedPeople,
            _activeMapAgents,
            perExit,
            EvacuationTimeForFraction(dots, 0.5),
            EvacuationTimeForFraction(dots, 0.9),
            _evacuatedCount > 0 ? _evacuationTimes[_evacuatedCount - 1] : null,
            finished,
            stalled);
    }

    private double? EvacuationTimeForFraction(int dots, double fraction)
    {
        int required = Math.Max(1, (int)Math.Ceiling(dots * fraction));
        return _evacuatedCount >= required ? _evacuationTimes[required - 1] : null;
    }

    private int CollectNearbyObstacles(double positionX, double positionY, double range, bool contactsOnly, out Obstacle[] obstacles)
        => _obstacleQuery.Collect(MapScenario, SimulationObstacles, positionX, positionY, range, contactsOnly, out obstacles);

    private bool[]? _reachableMask;
    private MapScenario? _reachableMaskScenario;

    /// <summary>8-connected flood fill over walkable raster cells starting at every exit (computed once per scenario).</summary>
    private bool[]? GetReachableMask()
    {
        var scenario = MapScenario;
        if (scenario is null) return null;
        if (_reachableMask != null && ReferenceEquals(_reachableMaskScenario, scenario)) return _reachableMask;

        foreach (var _ in PrepareReachableMaskSteps()) { }
        return _reachableMask;
    }

    private IEnumerable<int> PrepareReachableMaskSteps()
    {
        var scenario = MapScenario;
        if (scenario is null) yield break;
        int cols = scenario.Columns, rows = scenario.Rows;
        var blocked = scenario.Blocked;
        var reachable = new bool[blocked.Length];
        var queue = new Queue<int>();
        foreach (var exit in scenario.Exits)
        {
            int ec = Math.Clamp((int)(exit.X / scenario.CellSize), 0, cols - 1);
            int er = Math.Clamp((int)(exit.Y / scenario.CellSize), 0, rows - 1);
            int start = er * cols + ec;
            if (!blocked[start] && !reachable[start]) { reachable[start] = true; queue.Enqueue(start); }
        }
        int processed = 0;
        while (queue.Count > 0)
        {
            if (++processed % 512 == 0) yield return processed;
            int index = queue.Dequeue();
            int c = index % cols, r = index / cols;
            for (int dy = -1; dy <= 1; dy++)
            {
                int nr = r + dy;
                if (nr < 0 || nr >= rows) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nc = c + dx;
                    if (nc < 0 || nc >= cols) continue;
                    int next = nr * cols + nc;
                    if (blocked[next] || reachable[next]) continue;
                    if (dx != 0 && dy != 0 && blocked[r * cols + nc] && blocked[nr * cols + c]) continue;
                    reachable[next] = true;
                    queue.Enqueue(next);
                }
            }
        }
        _reachableMask = reachable;
        _reachableMaskScenario = scenario;
    }

    public bool IsReachable(double px, double py)
    {
        var scenario = MapScenario;
        var mask = GetReachableMask();
        if (scenario is null || mask is null) return true;
        if (!(px >= 0) || !(py >= 0)) return false;
        int c = (int)(px / scenario.CellSize), r = (int)(py / scenario.CellSize);
        if (c >= scenario.Columns || r >= scenario.Rows) return false;
        return mask[r * scenario.Columns + c];
    }

    /// <summary>Center of the nearest raster cell connected to an exit, within maxDistance.</summary>
    public (double X, double Y)? FindNearestReachable(double px, double py, double maxDistance = 50.0)
        => FindNearestReachable(px, py, maxDistance, 0);

    private (double X, double Y)? FindNearestReachable(double px, double py, double maxDistance, double spawnClearance, int recoveringAgent = -1)
    {
        var scenario = MapScenario;
        var mask = GetReachableMask();
        if (scenario is null || mask is null) return (px, py);
        double cell = scenario.CellSize;
        int centerCol = Math.Clamp((int)(px / cell), 0, scenario.Columns - 1);
        int centerRow = Math.Clamp((int)(py / cell), 0, scenario.Rows - 1);
        int reach = (int)Math.Ceiling(maxDistance / cell);
        double bestDistSq = maxDistance * maxDistance;
        (double X, double Y)? best = null;
        int bestIndex = int.MaxValue;
        void Consider(int c, int r)
        {
            if (c < 0 || c >= scenario.Columns || r < 0 || r >= scenario.Rows) return;
            int index = r * scenario.Columns + c;
            if (!mask[index]) return;
            if (recoveringAgent >= 0 && scenario.StreetMask is { } streets && !streets[index]) return;
            double cx = (c + 0.5) * cell, cy = (r + 0.5) * cell;
            if (spawnClearance > 0 && !IsSafeMapSpawn(cx, cy, spawnClearance)) return;
            double distSq = (cx - px) * (cx - px) + (cy - py) * (cy - py);
            if (recoveringAgent >= 0)
            {
                double minimumMove = Math.Max(0.5, AgentRadius[recoveringAgent] * 2);
                if (distSq < minimumMove * minimumMove || !HasRecoverySpace(recoveringAgent, cx, cy)) return;
            }
            if (distSq < bestDistSq || (best.HasValue && distSq == bestDistSq && index < bestIndex))
            {
                bestDistSq = distSq; best = (cx, cy); bestIndex = index;
            }
        }
        for (int ring = 0; ring <= reach; ring++)
        {
            int minRow = centerRow - ring, maxRow = centerRow + ring;
            int minCol = centerCol - ring, maxCol = centerCol + ring;
            for (int c = Math.Max(0, minCol); c <= Math.Min(scenario.Columns - 1, maxCol); c++)
            {
                Consider(c, minRow);
                if (maxRow != minRow) Consider(c, maxRow);
            }
            for (int r = Math.Max(0, minRow + 1); r <= Math.Min(scenario.Rows - 1, maxRow - 1); r++)
            {
                Consider(minCol, r);
                if (maxCol != minCol) Consider(maxCol, r);
            }
            // Every unvisited center lies beyond one of these four edges. Stop only
            // when its lower bound is strictly larger, preserving row-major ties.
            double remainingDistance = Math.Min(
                Math.Min(px - (centerCol - ring - 0.5) * cell, (centerCol + ring + 1.5) * cell - px),
                Math.Min(py - (centerRow - ring - 0.5) * cell, (centerRow + ring + 1.5) * cell - py));
            if (best.HasValue && remainingDistance > 0 && remainingDistance * remainingDistance > bestDistSq) break;
        }
        if (best.HasValue) return best;
        if (spawnClearance > 0)
        {
            double searchLimit = Math.Sqrt(scenario.WorldWidth * scenario.WorldWidth + scenario.WorldHeight * scenario.WorldHeight)
                + Math.Abs(px) + Math.Abs(py);
            return maxDistance < searchLimit
                ? FindNearestReachable(px, py, Math.Min(searchLimit, maxDistance * 4.0), spawnClearance, recoveringAgent)
                : null;
        }
        if (maxDistance < 400.0) return FindNearestReachable(px, py, Math.Min(400.0, maxDistance * 4.0));
        return scenario.NearestOpenCell(px, py, maxDistance);
    }

    private void ResetRecoveryTracking(int count)
    {
        AgentPositionX.AsSpan(0, count).CopyTo(_recoveryAnchorX);
        AgentPositionY.AsSpan(0, count).CopyTo(_recoveryAnchorY);
        _lastRecoveryMovementTime.AsSpan(0, count).Clear();
    }

    private void RecoverStationaryAgent(int index, bool hasNeighbor)
    {
        double x = AgentPositionX[index], y = AgentPositionY[index];
        foreach (int other in _recoveredThisStep)
        {
            double separation = AgentRadius[index] + AgentRadius[other] + 1;
            double nx = AgentPositionX[other] - x, ny = AgentPositionY[other] - y;
            if (nx * nx + ny * ny < separation * separation) hasNeighbor = true;
        }
        double dx = x - _recoveryAnchorX[index], dy = y - _recoveryAnchorY[index];
        if (hasNeighbor || dx * dx + dy * dy >= RecoveryMovementDistance * RecoveryMovementDistance)
        {
            _recoveryAnchorX[index] = x;
            _recoveryAnchorY[index] = y;
            _lastRecoveryMovementTime[index] = SimulationTime;
            return;
        }
        if (SimulationTime - _lastRecoveryMovementTime[index] + 1e-9 < StationaryRecoverySeconds) return;
        // Arrival takes precedence over recovery at the threshold.
        foreach (var exit in MapScenario!.Exits)
            if (Math.Abs(x - exit.X) <= exit.Radius + PotentialFieldMap.CellSize &&
                Math.Abs(y - exit.Y) <= exit.Radius + PotentialFieldMap.CellSize) return;
        var nearest = FindNearestReachable(x, y, 20, AgentRadius[index] + 0.6, index);
        if (nearest is { } target)
        {
            AgentPositionX[index] = target.X;
            AgentPositionY[index] = target.Y;
            AgentVelocityX[index] = AgentVelocityY[index] = 0;
            AgentLocalDensity[index] = 0;
            _recoveredThisStep.Add(index);
        }
        _recoveryAnchorX[index] = AgentPositionX[index];
        _recoveryAnchorY[index] = AgentPositionY[index];
        _lastRecoveryMovementTime[index] = SimulationTime;
    }

    private bool HasRecoverySpace(int index, double x, double y)
    {
        double radius = AgentRadius[index];
        int count = SpatialHashGridIndex.QueryNearby(x, y, radius * 2 + 0.1, out var neighbors);
        for (int i = 0; i < count; i++)
        {
            int neighbor = neighbors[i];
            if (neighbor == index || neighbor >= ActiveAgentCount) continue;
            double dx = AgentPositionX[neighbor] - x, dy = AgentPositionY[neighbor] - y;
            double separation = radius + AgentRadius[neighbor] + 0.1;
            if (dx * dx + dy * dy < separation * separation) return false;
        }
        foreach (int other in _recoveredThisStep)
        {
            double dx = AgentPositionX[other] - x, dy = AgentPositionY[other] - y;
            double separation = radius + AgentRadius[other] + 0.1;
            if (dx * dx + dy * dy < separation * separation) return false;
        }
        return true;
    }

    // Keep the whole agent outside the wall-repulsion range, including raster corners.
    private bool IsSafeMapSpawn(double x, double y, double clearance)
    {
        var scenario = MapScenario!;
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            x < clearance || y < clearance ||
            x > WorldWidth - clearance || y > WorldHeight - clearance || !IsReachable(x, y)) return false;
        var grid = PotentialFieldMap;
        int fieldCol = Math.Clamp((int)(x / grid.CellSize), 0, grid.ColumnCount - 1);
        int fieldRow = Math.Clamp((int)(y / grid.CellSize), 0, grid.RowCount - 1);
        if (grid.StaticPotentialFieldMatrix[fieldRow * grid.ColumnCount + fieldCol] >= float.MaxValue * 0.5f) return false;
        double cell = scenario.CellSize;
        int minCol = Math.Max(0, (int)((x - clearance) / cell));
        int maxCol = Math.Min(scenario.Columns - 1, (int)((x + clearance) / cell));
        int minRow = Math.Max(0, (int)((y - clearance) / cell));
        int maxRow = Math.Min(scenario.Rows - 1, (int)((y + clearance) / cell));
        for (int row = minRow; row <= maxRow; row++)
            for (int col = minCol; col <= maxCol; col++)
            {
                if (!scenario.Blocked[row * scenario.Columns + col]) continue;
                double dx = x - Math.Clamp(x, col * cell, (col + 1) * cell);
                double dy = y - Math.Clamp(y, row * cell, (row + 1) * cell);
                if (dx * dx + dy * dy < clearance * clearance) return false;
            }
        return true;
    }

    private void RebuildMapGrids(MapScenario scenario)
    {
        foreach (var _ in PrepareMapGridSteps(scenario)) { }
        PotentialFieldMap.BuildStaticField(_mapExitZones);
    }

    private IEnumerable<int> PrepareMapGridSteps(MapScenario scenario)
    {
        // The field shares the raster resolution whenever possible so its gradient never points
        // into a wall the agents collide with; very large areas fall back to coarser field cells.
        double extent = Math.Max(WorldWidth, WorldHeight);
        double cellSize = Math.Max(scenario.CellSize, extent / 600.0);
        PotentialFieldMap = new PotentialFieldGrid(cellSize, WorldWidth, WorldHeight);
        SpatialHashGridIndex = new SpatialHashGrid(Math.Max(3.6, cellSize * 1.2), MaxAllowedAgents, 65536);

        // A coarser field cell is a wall only when most of its raster cells are blocked, so narrow
        // footways stay routable; per-agent wall forces always use the full-resolution raster.
        var grid = PotentialFieldMap;
        var mask = new bool[grid.TotalCells];
        double fine = scenario.CellSize;
        for (int row = 0; row < grid.RowCount; row++)
        {
            if (row % 8 == 0) yield return row;
            int firstFineRow = (int)(row * grid.CellSize / fine + 1e-9);
            int lastFineRow = Math.Max(firstFineRow, (int)Math.Ceiling((row + 1) * grid.CellSize / fine - 1e-9) - 1);
            for (int col = 0; col < grid.ColumnCount; col++)
            {
                int firstFineCol = (int)(col * grid.CellSize / fine + 1e-9);
                int lastFineCol = Math.Max(firstFineCol, (int)Math.Ceiling((col + 1) * grid.CellSize / fine - 1e-9) - 1);
                int total = 0, blockedCount = 0;
                for (int fineRow = firstFineRow; fineRow <= lastFineRow; fineRow++)
                {
                    for (int fineCol = firstFineCol; fineCol <= lastFineCol; fineCol++)
                    {
                        total++;
                        if (fineRow >= scenario.Rows || fineCol >= scenario.Columns ||
                            scenario.Blocked[fineRow * scenario.Columns + fineCol])
                            blockedCount++;
                    }
                }
                mask[row * grid.ColumnCount + col] = blockedCount * 2 >= total;
            }
        }
        grid.InitializeStaticMask(mask);
        _spawnCells = null;
        _dynamicFieldTimer = 0.20;
    }

    /// <summary>Exit-connected field cells inside each spawn polygon with safe wall clearance.</summary>
    private List<int>[] BuildSpawnCells(MapScenario scenario, double clearance)
    {
        var grid = PotentialFieldMap;
        const float maxValidPotential = float.MaxValue * 0.5f;
        var result = new List<int>[scenario.SpawnZones.Length];
        for (int zoneIndex = 0; zoneIndex < result.Length; zoneIndex++)
        {
            var zone = scenario.SpawnZones[zoneIndex];
            var cells = new List<int>();
            int minCol = Math.Clamp((int)(zone.X.Min() / grid.CellSize), 0, grid.ColumnCount - 1);
            int maxCol = Math.Clamp((int)(zone.X.Max() / grid.CellSize), 0, grid.ColumnCount - 1);
            int minRow = Math.Clamp((int)(zone.Y.Min() / grid.CellSize), 0, grid.RowCount - 1);
            int maxRow = Math.Clamp((int)(zone.Y.Max() / grid.CellSize), 0, grid.RowCount - 1);
            for (int row = minRow; row <= maxRow; row++)
            {
                double cy = (row + 0.5) * grid.CellSize;
                for (int col = minCol; col <= maxCol; col++)
                {
                    double cx = (col + 0.5) * grid.CellSize;
                    int index = row * grid.ColumnCount + col;
                    if (grid.StaticPotentialFieldMatrix[index] < maxValidPotential &&
                        IsSafeMapSpawn(cx, cy, clearance) &&
                        MapScenario.IsPointInPolygon(cx, cy, zone.X, zone.Y))
                        cells.Add(index);
                }
            }
            result[zoneIndex] = cells;
        }
        return result;
    }

    private void SpawnMapAgents(Random random, int activeCount, double radius)
    {
        var scenario = MapScenario!;
        var grid = PotentialFieldMap;
        double clearance = radius + 0.6;
        if (_spawnCells is null || _spawnClearance != clearance)
        {
            _spawnCells = BuildSpawnCells(scenario, clearance);
            _spawnClearance = clearance;
        }

        // Largest-remainder split of agents between zones by population (equal weights if all are empty).
        var zones = scenario.SpawnZones;
        double totalWeight = 0;
        var weights = new double[zones.Length];
        for (int z = 0; z < zones.Length; z++)
            if (_spawnCells[z].Count > 0) totalWeight += weights[z] = zones[z].People;
        if (totalWeight <= 0)
        {
            for (int z = 0; z < zones.Length; z++)
                if (_spawnCells[z].Count > 0) totalWeight += weights[z] = 1;
        }
        if (totalWeight <= 0)
            throw new InvalidOperationException("No street inside the evacuation zones is connected to an evacuation point with sufficient spawn clearance.");

        var allocation = new int[zones.Length];
        var remainders = new List<(double Remainder, int Zone)>();
        int assigned = 0;
        for (int z = 0; z < zones.Length; z++)
        {
            double exact = activeCount * weights[z] / totalWeight;
            allocation[z] = (int)Math.Floor(exact);
            assigned += allocation[z];
            if (weights[z] > 0) remainders.Add((exact - allocation[z], z));
        }
        foreach (var (_, zone) in remainders.OrderByDescending(item => item.Remainder).Take(activeCount - assigned))
            allocation[zone]++;

        int agentIndex = 0;
        for (int z = 0; z < zones.Length; z++)
        {
            var cells = _spawnCells[z];
            for (int k = 0; k < allocation[z]; k++, agentIndex++)
            {
                int cellIndex = cells[random.Next(cells.Count)];
                int col = cellIndex % grid.ColumnCount, row = cellIndex / grid.ColumnCount;
                double x = (col + 0.5) * grid.CellSize, y = (row + 0.5) * grid.CellSize;
                for (int attempt = 0; attempt < SpawnAttempts; attempt++)
                {
                    double candidateX = (col + random.NextDouble()) * grid.CellSize;
                    double candidateY = (row + random.NextDouble()) * grid.CellSize;
                    if (!IsSafeMapSpawn(candidateX, candidateY, clearance) ||
                        !MapScenario.IsPointInPolygon(candidateX, candidateY, zones[z].X, zones[z].Y)) continue;
                    x = candidateX;
                    y = candidateY;
                    break;
                }
                AgentPositionX[agentIndex] = x;
                AgentPositionY[agentIndex] = y;
                AgentVelocityX[agentIndex] = 0.0;
                AgentVelocityY[agentIndex] = 0.0;
                AgentRadius[agentIndex] = radius;
                AgentMaxSpeed[agentIndex] = 1.3 + random.NextDouble() * 0.4;
                AgentLocalDensity[agentIndex] = 0.0;
            }
        }

        _activeMapAgents = activeCount;
        _evacuatedPerExit = new int[scenario.Exits.Length];
        _evacuationTimes = new double[activeCount];
        _evacuatedCount = 0;
        _lastEvacuationTime = 0;
        SimulationTime = 0;
        FrameCounter = 0;
        ResetRecoveryTracking(activeCount);
    }

    /// <summary>Agents inside an exit area leave the crowd and are parked after the active prefix.</summary>
    private void ProcessEvacuations()
    {
        if (IsMapScenario)
        {
            ProcessMapEvacuations();
            return;
        }

        ProcessPresetEvacuations();
    }

    private void ProcessMapEvacuations()
    {
        var exits = MapScenario!.Exits;
        double catchMargin = PotentialFieldMap.CellSize;
        int agentIndex = 0;
        while (agentIndex < _activeMapAgents)
        {
            double px = AgentPositionX[agentIndex], py = AgentPositionY[agentIndex];
            int exitIndex = -1;
            // The zero-potential sink covers whole field cells around the exit square and has no
            // gradient inside, so the catch area extends by one field cell to include all of it.
            for (int e = 0; e < exits.Length; e++)
            {
                double reach = exits[e].Radius + catchMargin;
                if (Math.Abs(px - exits[e].X) <= reach && Math.Abs(py - exits[e].Y) <= reach) { exitIndex = e; break; }
            }
            if (exitIndex < 0) { agentIndex++; continue; }

            int last = --_activeMapAgents;
            SwapAgents(agentIndex, last);
            AgentRadius[last] = 0.0;
            AgentVelocityX[last] = 0.0;
            AgentVelocityY[last] = 0.0;
            AgentLocalDensity[last] = 0.0;
            _evacuatedPerExit[exitIndex]++;
            if (_evacuationTimes != null && _evacuatedCount < _evacuationTimes.Length)
            {
                _evacuationTimes[_evacuatedCount] = SimulationTime;
            }
            _evacuatedCount++;
            _lastEvacuationTime = SimulationTime;
        }
    }

    private void ProcessPresetEvacuations()
    {
        if (_activePresetAgents <= 0) return;

        int agentIndex = 0;
        double catchMargin = Math.Max(2.5, PotentialFieldMap?.CellSize * 0.5 ?? 2.5);
        while (agentIndex < _activePresetAgents)
        {
            double px = AgentPositionX[agentIndex];
            double py = AgentPositionY[agentIndex];
            bool evacuated = false;

            if (Targets.Count > 0)
            {
                for (int t = 0; t < Targets.Count; t++)
                {
                    var target = Targets[t];
                    if (!target.IsActive) continue;
                    if (px >= target.X - catchMargin && px <= target.X + target.Width + catchMargin &&
                        py >= target.Y - catchMargin && py <= target.Y + target.Height + catchMargin)
                    {
                        evacuated = true;
                        break;
                    }
                }
            }
            else
            {
                if (px >= ExitZone.X - catchMargin && px <= ExitZone.X + ExitZone.Width + catchMargin &&
                    py >= ExitZone.Y - catchMargin && py <= ExitZone.Y + ExitZone.Height + catchMargin)
                {
                    evacuated = true;
                }
            }

            if (!evacuated)
            {
                agentIndex++;
                continue;
            }

            int last = --_activePresetAgents;
            SwapAgents(agentIndex, last);
            AgentRadius[last] = 0.0;
            AgentVelocityX[last] = 0.0;
            AgentVelocityY[last] = 0.0;
            AgentLocalDensity[last] = 0.0;
            if (_evacuationTimes != null && _evacuatedCount < _evacuationTimes.Length)
            {
                _evacuationTimes[_evacuatedCount] = SimulationTime;
            }
            _evacuatedCount++;
            _lastEvacuationTime = SimulationTime;
        }
    }

    private void SwapAgents(int a, int b)
    {
        if (a == b) return;
        (AgentPositionX[a], AgentPositionX[b]) = (AgentPositionX[b], AgentPositionX[a]);
        (AgentPositionY[a], AgentPositionY[b]) = (AgentPositionY[b], AgentPositionY[a]);
        (AgentVelocityX[a], AgentVelocityX[b]) = (AgentVelocityX[b], AgentVelocityX[a]);
        (AgentVelocityY[a], AgentVelocityY[b]) = (AgentVelocityY[b], AgentVelocityY[a]);
        (AgentRadius[a], AgentRadius[b]) = (AgentRadius[b], AgentRadius[a]);
        (AgentMaxSpeed[a], AgentMaxSpeed[b]) = (AgentMaxSpeed[b], AgentMaxSpeed[a]);
        (AgentLocalDensity[a], AgentLocalDensity[b]) = (AgentLocalDensity[b], AgentLocalDensity[a]);
        (AgentTargetIndex[a], AgentTargetIndex[b]) = (AgentTargetIndex[b], AgentTargetIndex[a]);
        (_recoveryAnchorX[a], _recoveryAnchorX[b]) = (_recoveryAnchorX[b], _recoveryAnchorX[a]);
        (_recoveryAnchorY[a], _recoveryAnchorY[b]) = (_recoveryAnchorY[b], _recoveryAnchorY[a]);
        (_lastRecoveryMovementTime[a], _lastRecoveryMovementTime[b]) = (_lastRecoveryMovementTime[b], _lastRecoveryMovementTime[a]);
    }

    public double TimeScale { get; set; } = 1.0;

    public void Reset()
    {
        if (_initialCustomPositions != null && _initialCustomPositions.Length > 0)
        {
            InitializeAgentsWithPositions(_initialCustomPositions);
            return;
        }
        InitializeAgents();
    }

    public void WriteRenderValues(Span<double> destination)
    {
        int count = SimulatedAgentCount;
        if (destination.Length != checked(count * 5))
            throw new ArgumentException("Render destination has an invalid length.", nameof(destination));
        AgentPositionX.AsSpan(0, count).CopyTo(destination);
        AgentPositionY.AsSpan(0, count).CopyTo(destination[count..]);
        AgentVelocityX.AsSpan(0, count).CopyTo(destination[(count * 2)..]);
        AgentVelocityY.AsSpan(0, count).CopyTo(destination[(count * 3)..]);
        AgentRadius.AsSpan(0, count).CopyTo(destination[(count * 4)..]);
    }

    public RenderFrame CreateRenderFrame() => new(
        AgentPositionX[..SimulatedAgentCount],
        AgentPositionY[..SimulatedAgentCount],
        AgentVelocityX[..SimulatedAgentCount],
        AgentVelocityY[..SimulatedAgentCount],
        AgentRadius[..SimulatedAgentCount]);

    public void Step(double baseDeltaTime = 0.016, double timeScale = 1.0)
    {
        FrameCounter++;
        double totalSimTime = baseDeltaTime * timeScale;
        // Speed changes the number of ticks, never the physics integration step.
        // WebGPU uses the same 16 ms step at every playback speed.
        int steps = Math.Max(1, (int)Math.Ceiling(totalSimTime / 0.016 - 1e-9));
        double subDt = totalSimTime / steps;
        for (int step = 0; step < steps; step++)
        {
            UpdatePhysics(subDt);
        }
    }
}
