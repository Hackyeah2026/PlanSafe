using PlanSafe.App.Simulation;
using PlanSafe.Contracts.Models.Session;
using PlanSafe.Contracts.Models.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class CrowdSimulationEngineMapTests
{
    [Fact]
    public void InitializeAgentsWithPositions_SetsExactCoordinatesAndCount()
    {
        var engine = new CrowdSimulationEngine(100.0, 100.0, 50);
        engine.SetTargets(new[]
        {
            new EvacuationTarget("shelter-1", "Shelter", 85.0, 85.0, 10.0, 10.0, 50, 0, true)
        });

        var customPositions = new List<(double X, double Y)>
        {
            (10.5, 15.2),
            (20.1, 25.8),
            (30.0, 35.0),
            (40.2, 45.9),
            (50.0, 55.0)
        };

        engine.InitializeAgentsWithPositions(customPositions, seed: 42);

        Assert.Equal(5, engine.AgentCount);
        Assert.Equal(5, engine.SimulatedAgentCount);

        for (int i = 0; i < customPositions.Count; i++)
        {
            Assert.Equal(customPositions[i].X, engine.AgentPositionX[i], precision: 4);
            Assert.Equal(customPositions[i].Y, engine.AgentPositionY[i], precision: 4);
        }
    }

    [Fact]
    public void Reset_RestoresCustomInitialPositions()
    {
        var engine = new CrowdSimulationEngine(100.0, 100.0, 50);
        engine.SetTargets(new[]
        {
            new EvacuationTarget("shelter-1", "Shelter", 80.0, 80.0, 15.0, 15.0, 100, 0, true)
        });

        var initialPositions = new List<(double X, double Y)>
        {
            (15.0, 15.0),
            (25.0, 25.0)
        };

        engine.InitializeAgentsWithPositions(initialPositions, seed: 123);

        // Step simulation forward so agents move
        for (int step = 0; step < 10; step++)
        {
            engine.UpdatePhysics(0.1);
        }

        // Agents should have moved from initial coordinates
        Assert.NotEqual(15.0, engine.AgentPositionX[0]);

        // Reset should return agents to initial custom positions
        engine.Reset();

        Assert.Equal(2, engine.SimulatedAgentCount);
        Assert.Equal(15.0, engine.AgentPositionX[0], precision: 4);
        Assert.Equal(15.0, engine.AgentPositionY[0], precision: 4);
        Assert.Equal(25.0, engine.AgentPositionX[1], precision: 4);
        Assert.Equal(25.0, engine.AgentPositionY[1], precision: 4);
        Assert.Equal(0, engine.EvacuatedCount);
    }

    [Fact]
    public void SetGranulation_ScalesCustomAgentDistribution()
    {
        var engine = new CrowdSimulationEngine(100.0, 100.0, 50);
        engine.SetTargets(new[]
        {
            new EvacuationTarget("shelter-1", "Shelter", 90.0, 90.0, 8.0, 8.0, 200, 0, true)
        });

        var positions = new List<(double X, double Y)>
        {
            (10.0, 10.0),
            (20.0, 20.0),
            (30.0, 30.0),
            (40.0, 40.0)
        };

        engine.InitializeAgentsWithPositions(positions, seed: 99);
        Assert.Equal(4, engine.AgentCount);
        Assert.Equal(4, engine.SimulatedAgentCount);

        // Granulation = 2 means each agent represents 2 people, so agent count is halved
        engine.SetGranulation(2);
        Assert.Equal(2, engine.SimulatedAgentCount);

        // Granulation back to 1 restores full agent count from initial custom positions
        engine.SetGranulation(1);
        Assert.Equal(4, engine.SimulatedAgentCount);
    }

    [Fact]
    public void SimulationConfig_Clone_PreservesAllMapAndEngineProperties()
    {
        var original = new SimulationConfig
        {
            AgentCount = 1250,
            Granulation = 5,
            TimeScale = 2.0f,
            Unlimited = true,
            SocialRepulsionWeight = 6.0,
            WhiskerLength = 3.5,
            ShowWhiskers = true,
            RenderMode = "speed",
            RenderFps = "60",
            WeightDistance = 0.8,
            WeightOccupancy = 0.2,
            UseGusCensus = false
        };

        var clone = original.Clone();

        Assert.Equal(original.AgentCount, clone.AgentCount);
        Assert.Equal(original.Granulation, clone.Granulation);
        Assert.Equal(original.TimeScale, clone.TimeScale);
        Assert.Equal(original.Unlimited, clone.Unlimited);
        Assert.Equal(original.SocialRepulsionWeight, clone.SocialRepulsionWeight);
        Assert.Equal(original.WhiskerLength, clone.WhiskerLength);
        Assert.Equal(original.ShowWhiskers, clone.ShowWhiskers);
        Assert.Equal(original.RenderMode, clone.RenderMode);
        Assert.Equal(original.RenderFps, clone.RenderFps);
        Assert.Equal(original.WeightDistance, clone.WeightDistance);
        Assert.Equal(original.WeightOccupancy, clone.WeightOccupancy);
        Assert.Equal(original.UseGusCensus, clone.UseGusCensus);
    }

    [Fact]
    public void Simulation_AgentsProgressTowardsTargetAndEvacuate()
    {
        var engine = new CrowdSimulationEngine(50.0, 50.0, 10);
        // Place target covering [20, 30] x [20, 30]
        engine.SetTargets(new[]
        {
            new EvacuationTarget("shelter-goal", "Goal", 20.0, 20.0, 10.0, 10.0, 10, 0, true)
        });

        // Place agent 1 meter before shelter entrance
        var positions = new List<(double X, double Y)>
        {
            (19.0, 25.0)
        };

        engine.InitializeAgentsWithPositions(positions, seed: 1);
        Assert.Equal(1, engine.SimulatedAgentCount);
        Assert.Equal(0, engine.EvacuatedCount);

        // Step simulation: agent moves toward target and reaches evacuation zone
        for (int i = 0; i < 50; i++)
        {
            engine.UpdatePhysics(0.1);
            if (engine.EvacuatedCount > 0)
                break;
        }

        Assert.Equal(1, engine.EvacuatedCount);
    }

    [Fact]
    public void SingleTarget_SetEnvironment_EvacuatesAgents()
    {
        var engine = new CrowdSimulationEngine(100.0, 100.0, 10);
        engine.IsMapMode = true;
        var target = new EvacuationTarget("safe-single", "Safe Zone", 70.0, 70.0, 15.0, 15.0, 100, 0, true);
        engine.SetEnvironment(null, new[] { target });

        // Verify single target was registered and ExitZone was synced
        Assert.False(engine.MultiTargetEnabled);
        Assert.Single(engine.Targets);
        Assert.Equal(70.0, engine.ExitZone.X);
        Assert.Equal(70.0, engine.ExitZone.Y);

        // Place agent near safe zone entrance
        var positions = new List<(double X, double Y)> { (68.0, 75.0) };
        engine.InitializeAgentsWithPositions(positions, seed: 10);

        Assert.Equal(0, engine.EvacuatedCount);

        for (int i = 0; i < 40; i++)
        {
            engine.UpdatePhysics(0.1);
            if (engine.EvacuatedCount > 0) break;
        }

        Assert.Equal(1, engine.EvacuatedCount);
    }

    [Fact]
    public void MultiDirectionalApproach_EvacuatesFromNorthSouthEastWest()
    {
        var engine = new CrowdSimulationEngine(100.0, 100.0, 10);
        engine.IsMapMode = true;
        // Target located at [40, 60] x [40, 60]
        var target = new EvacuationTarget("center-shelter", "Shelter", 40.0, 40.0, 20.0, 20.0, 100, 0, true);
        engine.SetEnvironment(null, new[] { target });

        // Place 4 agents approaching from 4 opposite directions:
        // West, East, North, South
        var positions = new List<(double X, double Y)>
        {
            (38.0, 50.0), // West
            (62.0, 50.0), // East
            (50.0, 38.0), // North
            (50.0, 62.0)  // South
        };

        engine.InitializeAgentsWithPositions(positions, seed: 42);
        Assert.Equal(4, engine.SimulatedAgentCount);
        Assert.Equal(0, engine.EvacuatedCount);

        for (int i = 0; i < 60; i++)
        {
            engine.UpdatePhysics(0.1);
            if (engine.EvacuatedCount == 4) break;
        }

        Assert.Equal(4, engine.EvacuatedCount);
    }

    [Fact]
    public void ThinObstacle_AABB_DetectionInPotentialGrid()
    {
        // 8-meter grid cells over a 100x100 world
        var grid = new PotentialFieldGrid(8.0, 100.0, 100.0);
        // Thin 3m x 3m barricade positioned between grid centers
        var barricade = new Obstacle(33.0, 33.0, 3.0, 3.0, "barricade_1");

        grid.InitializeStaticObstacles(new[] { barricade });

        // The cell covering [32, 40] x [32, 40] is col=4, row=4
        int col = (int)(33.0 / 8.0);
        int row = (int)(33.0 / 8.0);

        Assert.True(grid.IsObstacleCell(col, row), "Thin obstacle must be marked in the obstacle mask via AABB intersection");
    }

    [Fact]
    public void FlowDirection_InsideSafeZone_PointsTowardsCenter()
    {
        var grid = new PotentialFieldGrid(4.0, 100.0, 100.0);
        var target = new Obstacle(40.0, 40.0, 20.0, 20.0); // Center: (50.0, 50.0)

        grid.BuildStaticField(new Obstacle[0], target);

        // Agent inside target to the West of center (45.0, 50.0)
        var (fxWest, fyWest) = grid.GetFlowDirection(45.0, 50.0);
        Assert.True(fxWest > 0.90, $"fxWest should point East towards center, got {fxWest}");
        Assert.True(Math.Abs(fyWest) < 0.10);

        // Agent inside target to the East of center (55.0, 50.0)
        var (fxEast, fyEast) = grid.GetFlowDirection(55.0, 50.0);
        Assert.True(fxEast < -0.90, $"fxEast should point West towards center, got {fxEast}");
        Assert.True(Math.Abs(fyEast) < 0.10);

        // Agent inside target to the North of center (50.0, 45.0)
        var (fxNorth, fyNorth) = grid.GetFlowDirection(50.0, 45.0);
        Assert.True(fyNorth > 0.90, $"fyNorth should point South towards center, got {fyNorth}");
        Assert.True(Math.Abs(fxNorth) < 0.10);

        // Agent inside target to the South of center (50.0, 55.0)
        var (fxSouth, fySouth) = grid.GetFlowDirection(50.0, 55.0);
        Assert.True(fySouth < -0.90, $"fySouth should point North towards center, got {fySouth}");
        Assert.True(Math.Abs(fxSouth) < 0.10);
    }

    [Fact]
    public void AngledBuildingPolygon_ActsAsTotalBlockade_AgentsNavigateAround()
    {
        double width = 100.0;
        double height = 100.0;
        var builder = new MapScenarioBuilder(width, height, cellSize: 1.0);
        builder.Fill(isWalkable: true);

        // Angled polygon (diamond / rotated building) between (40, 50), (50, 40), (60, 50), (50, 60)
        double[] polyXs = [40.0, 50.0, 60.0, 50.0];
        double[] polyYs = [50.0, 40.0, 50.0, 60.0];
        builder.SetPolygon(polyXs, polyYs, isWalkable: false);

        // Exit on the east side: center at (85, 50), radius 8
        var exits = new[] { new MapExit(85.0, 50.0, 8.0) };
        builder.SetDisk(85.0, 50.0, 8.0, isWalkable: true);

        var spawnZones = new[]
        {
            new MapSpawnZone([10.0, 30.0, 30.0, 10.0], [40.0, 40.0, 60.0, 60.0], 10)
        };

        var scenario = builder.Build(50.0, 20.0, 111320.0, 111320.0, exits, spawnZones);

        // Verify the diamond polygon center is blocked
        Assert.True(scenario.IsBlockedAt(50.0, 50.0));
        Assert.True(scenario.IsBlockedAt(48.0, 50.0));
        Assert.True(scenario.IsBlockedAt(52.0, 50.0));

        var engine = new CrowdSimulationEngine(width, height, 5);
        engine.IsMapMode = true;
        engine.LoadMapScenario(scenario);

        // Place 3 agents directly west of the angled building aiming directly across it to the exit
        var customPositions = new List<(double X, double Y)>
        {
            (35.0, 50.0),
            (35.0, 48.0),
            (35.0, 52.0)
        };

        engine.InitializeAgentsWithPositions(customPositions, seed: 42);
        Assert.Equal(3, engine.ActiveAgentCount);
        Assert.Equal(3, engine.SimulatedAgentCount);

        // Simulate physics steps so agents walk around obstacle to the exit (~50m journey at ~1.3m/s takes ~35s)
        for (int step = 0; step < 800; step++)
        {
            engine.UpdatePhysics(0.05);

            // Verify total blockade: at NO point can any agent penetrate inside the building polygon!
            int active = engine.ActiveAgentCount;
            for (int i = 0; i < active; i++)
            {
                double ax = engine.AgentPositionX[i];
                double ay = engine.AgentPositionY[i];
                Assert.False(scenario.IsBlockedAt(ax, ay),
                    $"Step {step}: Agent {i} at ({ax:F2}, {ay:F2}) penetrated inside blocked building cell!");
            }

            if (engine.EvacuatedCount == 3) break;
        }

        // All agents navigated around the angled building and evacuated
        Assert.Equal(3, engine.EvacuatedCount);
    }

    [Fact]
    public void Building_CornerCollision_DoesNotLockAgentOrZeroVelocity()
    {
        // Setup 80x80 scenario with an obstacle box at [35, 45] x [35, 45]
        double width = 80.0, height = 80.0;
        var builder = new MapScenarioBuilder(width, height, cellSize: 1.0);
        builder.Fill(isWalkable: true);

        // Block building at [35, 45] x [35, 45]
        builder.SetPolygon([35.0, 45.0, 45.0, 35.0], [35.0, 35.0, 45.0, 45.0], isWalkable: false);

        var exits = new[] { new MapExit(70.0, 40.0, 4.0) };
        builder.SetDisk(70.0, 40.0, 4.0, isWalkable: true);
        var spawnZones = new[]
        {
            new MapSpawnZone([20.0, 30.0, 30.0, 20.0], [35.0, 35.0, 45.0, 45.0], 5)
        };

        var scenario = builder.Build(50.0, 20.0, 111320.0, 111320.0, exits, spawnZones);
        var engine = new CrowdSimulationEngine(width, height, 2);
        engine.IsMapMode = true;
        engine.LoadMapScenario(scenario);

        // Place agent directly in front of the building hitting near the corner (30.0, 35.0) heading towards exit (70, 40)
        var customPositions = new List<(double X, double Y)>
        {
            (30.0, 35.0)
        };

        engine.InitializeAgentsWithPositions(customPositions, seed: 42);

        // Run simulation steps
        for (int step = 0; step < 800; step++)
        {
            engine.UpdatePhysics(0.05);

            if (engine.ActiveAgentCount > 0)
            {
                double ax = engine.AgentPositionX[0];
                double ay = engine.AgentPositionY[0];
                Assert.False(scenario.IsBlockedAt(ax, ay), $"Agent penetrated inside wall at step {step}: ({ax:F2}, {ay:F2})");
            }

            if (engine.EvacuatedCount == 1) break;
        }

        // Agent must not be locked or stuck; should have slid past corner and evacuated
        Assert.Equal(1, engine.EvacuatedCount);
    }

    [Fact]
    public void Building_MultipolygonWithCourtyard_OuterBlockedInnerWalkable()
    {
        // Setup 100x100 scenario with a multipolygon monastery complex:
        // Outer ring: [30, 70] x [30, 70]
        // Inner courtyard: [40, 60] x [40, 60]
        double width = 100.0, height = 100.0;
        var builder = new MapScenarioBuilder(width, height, cellSize: 1.0);
        builder.Fill(isWalkable: true);

        double[][] outerAndInnerXs = [
            [30.0, 70.0, 70.0, 30.0], // outer
            [40.0, 60.0, 60.0, 40.0]  // inner courtyard
        ];
        double[][] outerAndInnerYs = [
            [30.0, 30.0, 70.0, 70.0], // outer
            [40.0, 40.0, 60.0, 60.0]  // inner courtyard
        ];

        builder.SetRings(outerAndInnerXs, outerAndInnerYs, isWalkable: false);

        var exits = new[] { new MapExit(90.0, 50.0, 4.0) };
        builder.SetDisk(90.0, 50.0, 4.0, isWalkable: true);
        var spawnZones = new[]
        {
            new MapSpawnZone([10.0, 20.0, 20.0, 10.0], [45.0, 45.0, 55.0, 55.0], 5)
        };

        var scenario = builder.Build(50.0, 20.0, 111320.0, 111320.0, exits, spawnZones);

        // Wall cells (between 30-40 or 60-70) must be blocked
        Assert.True(scenario.IsBlockedAt(35.0, 35.0), "Outer wall should be blocked");
        Assert.True(scenario.IsBlockedAt(65.0, 65.0), "Outer wall should be blocked");
        Assert.True(scenario.IsBlockedAt(35.0, 50.0), "Outer wall should be blocked");

        // Courtyard center (50, 50) must remain walkable!
        Assert.False(scenario.IsBlockedAt(50.0, 50.0), "Inner courtyard must be walkable");
        Assert.False(scenario.IsBlockedAt(45.0, 45.0), "Inner courtyard must be walkable");

        // Simulate agent navigating around the monastery complex
        var engine = new CrowdSimulationEngine(width, height, 1);
        engine.IsMapMode = true;
        engine.LoadMapScenario(scenario);

        engine.InitializeAgentsWithPositions(new List<(double X, double Y)> { (20.0, 50.0) }, seed: 42);

        for (int step = 0; step < 1200; step++)
        {
            engine.UpdatePhysics(0.05);

            if (engine.ActiveAgentCount > 0)
            {
                double ax = engine.AgentPositionX[0];
                double ay = engine.AgentPositionY[0];
                Assert.False(scenario.IsBlockedAt(ax, ay), $"Agent penetrated monastery walls at step {step}: ({ax:F2}, {ay:F2})");
            }

            if (engine.EvacuatedCount == 1) break;
        }

        Assert.Equal(1, engine.EvacuatedCount);
    }

    [Fact]
    public async Task RasterizeTerrain_RealKrakowOsm_BlocksAllBuildingsAndAgentsNeverEnterThem()
    {
        var osm = new PlanSafe.App.Services.Osm.OsmObstacleService(new System.Net.Http.HttpClient { BaseAddress = new Uri("http://localhost/") });
        await osm.EnsureLoadedAsync();
        Assert.True(osm.IsLoaded, "krakow_osm.bin must load");

        // Carmelite street area (west of Planty), ~300m x 300m
        double minLat = 50.0630, maxLat = 50.0657, minLng = 19.9285, maxLng = 19.9327;
        double mLat = 111320.0;
        double mLng = 111320.0 * Math.Cos(((minLat + maxLat) / 2) * Math.PI / 180.0);
        double width = (maxLng - minLng) * mLng, height = (maxLat - minLat) * mLat;
        (double X, double Y) ToWorld(double lat, double lng) => ((lng - minLng) * mLng, (maxLat - lat) * mLat);

        var builder = new MapScenarioBuilder(width, height, cellSize: 1.0);
        await osm.RasterizeTerrainAsync(builder, minLat, maxLat, minLng, maxLng, ToWorld);

        var buildings = await osm.GetBuildingsAsync(minLat, maxLat, minLng, maxLng);
        Assert.True(buildings.Count > 40, $"Expected dense building coverage, got {buildings.Count}");

        // Every building of meaningful size must have its interior blocked (centroid of outer ring check)
        int blockedCount = 0, checkedCount = 0;
        foreach (var b in buildings.Where(b => b.BuildingType == "building" && b.Polygon is { Count: >= 4 }))
        {
            var pts = b.Polygon!.Select(p => ToWorld(p[0], p[1])).ToArray();
            var xs = pts.Select(p => p.X).ToArray();
            var ys = pts.Select(p => p.Y).ToArray();
            if (xs.Min() < 2 || ys.Min() < 2 || xs.Max() > width - 2 || ys.Max() > height - 2) continue;
            if (xs.Max() - xs.Min() < 6 || ys.Max() - ys.Min() < 6) continue;
            double cx = xs.Average(), cy = ys.Average();
            if (!MapScenario.IsPointInPolygon(cx, cy, xs, ys)) continue;
            checkedCount++;
            var scenarioProbe = builder;
            int col = (int)cx, row = (int)cy;
            if (!scenarioProbe.IsWalkable(col, row)) blockedCount++;
        }
        Assert.True(checkedCount > 10);
        Assert.True(blockedCount >= checkedCount * 0.95, $"Only {blockedCount}/{checkedCount} building interiors are blocked");
    }
}

