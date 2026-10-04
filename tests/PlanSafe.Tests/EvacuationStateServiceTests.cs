using System;
using System.Collections.Generic;
using PlanSafe.Api.Services;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public sealed class EvacuationStateServiceTests
{
    [Fact]
    public void PublishPlan_GeneratesValidSessionToken_AndVectorSvgQr()
    {
        using var service = new EvacuationStateService();

        var targets = new List<EvacuationTarget>
        {
            new EvacuationTarget("s-1", "Schron 1", 19.93, 50.06, 20, 20, 300, 0, true, 50.06, 19.93)
        };

        var request = new PublishPlanRequest(
            Targets: targets,
            Obstacles: null,
            Roadblocks: null,
            EvacuationZones: null,
            BaseUrl: "http://127.0.0.1:5000",
            WeightDistance: 0.7,
            WeightOccupancy: 0.3
        );

        var response = service.PublishPlan(request);

        Assert.NotNull(response);
        Assert.Equal(8, response.SessionId.Length);
        Assert.Contains($"/evacuate?session={response.SessionId}", response.EvacuateUrl);
        Assert.StartsWith("<svg", response.QrCodeSvg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("viewBox=", response.QrCodeSvg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</svg>", response.QrCodeSvg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckIn_IncrementsAndDecrements_OccupancyCorrectly()
    {
        using var service = new EvacuationStateService();

        var targets = new List<EvacuationTarget>
        {
            new EvacuationTarget("shelter-test", "Schron Test", 19.93, 50.06, 20, 20, 100, 10, true, 50.06, 19.93)
        };

        service.PublishPlan(new PublishPlanRequest(targets));

        // +1 CheckIn
        var res1 = service.CheckIn(new CheckInRequest("shelter-test", 1));
        Assert.True(res1.Success);
        Assert.Equal(11, res1.Target?.CurrentOccupancy);

        // -1 CheckOut
        var res2 = service.CheckIn(new CheckInRequest("shelter-test", -1));
        Assert.True(res2.Success);
        Assert.Equal(10, res2.Target?.CurrentOccupancy);

        // Floor at 0
        var res3 = service.CheckIn(new CheckInRequest("shelter-test", -50));
        Assert.True(res3.Success);
        Assert.Equal(0, res3.Target?.CurrentOccupancy);
    }

    [Fact]
    public void AssignTarget_ReturnsOptimalShelter_AndRoute()
    {
        using var service = new EvacuationStateService();

        var targets = new List<EvacuationTarget>
        {
            new EvacuationTarget("s-north", "Północ", 19.93, 50.08, 20, 20, 100, 0, true, 50.08, 19.93),
            new EvacuationTarget("s-south", "Południe", 19.93, 50.04, 20, 20, 100, 0, true, 50.04, 19.93)
        };

        service.PublishPlan(new PublishPlanRequest(targets));

        // Citizen closer to North
        var req = new TargetAssignmentRequest(
            X: 19.93,
            Y: 50.075,
            Latitude: 50.075,
            Longitude: 19.93,
            WeightDistance: 1.0,
            WeightOccupancy: 0.0
        );

        var result = service.AssignTarget(req);
        Assert.NotNull(result.Target);
        Assert.Equal("s-north", result.Target.Id);
        Assert.NotNull(result.RoutePath);
        Assert.True(result.RoutePath.Count >= 2);
    }

    [Fact]
    public void UpdateTargetOccupancy_DirectlyModifiesOccupancy()
    {
        using var service = new EvacuationStateService();
        var targets = new List<EvacuationTarget>
        {
            new EvacuationTarget("shelter-occ", "Schron", 19.93, 50.06, 20, 20, 100, 10, true, 50.06, 19.93)
        };
        service.PublishPlan(new PublishPlanRequest(targets));

        bool updated = service.UpdateTargetOccupancy("shelter-occ", 75);
        Assert.True(updated);

        var retrieved = service.GetTargets();
        var t = Assert.Single(retrieved);
        Assert.Equal(75, t.CurrentOccupancy);
    }

    [Fact]
    public void Reset_ClearsSessionAndRestoresDefaults()
    {
        using var service = new EvacuationStateService();
        service.PublishPlan(new PublishPlanRequest(new List<EvacuationTarget>
        {
            new EvacuationTarget("custom", "Schron", 19.9, 50.0, 10, 10, 50, 0, true, 50.0, 19.9)
        }));

        Assert.NotNull(service.CurrentSessionId);

        service.Reset();

        Assert.Null(service.CurrentSessionId);
        var config = service.GetConfig();
        Assert.NotNull(config);
        Assert.Equal("default", config.SessionId);
    }

    [Fact]
    public void QrCodeSvgGenerator_ProducesValidScalableSvg()
    {
        string sampleUrl = "http://127.0.0.1:5000/evacuate?session=abcdef12";
        string svg = QrCodeSvgGenerator.GenerateSvg(sampleUrl);

        Assert.False(string.IsNullOrWhiteSpace(svg));
        Assert.StartsWith("<svg", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("viewBox=", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shape-rendering=\"crispEdges\"", svg, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("</svg>", svg.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapPathfinder_WithKrakowOsm_ComputesValidDistancesToAllExits()
    {
        string? osmPath = null;
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "data", "osm", "krakow_osm.bin");
            if (System.IO.File.Exists(candidate))
            {
                osmPath = candidate;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(osmPath);
        Assert.True(System.IO.File.Exists(osmPath));

        var osm = PlanSafe.Api.Map.KrakowOsmService.TryLoad(osmPath);
        Assert.NotNull(osm);

        var factory = new PlanSafe.Api.Map.MapScenarioFactory(osm);
        var targets = new List<EvacuationTarget>
        {
            new EvacuationTarget("safe-zone-rynek", "Rynek", 19.9366, 50.0614, 30, 30, 100, 90, true, 50.0614, 19.9366),
            new EvacuationTarget("safe-zone-dworzec", "Dworzec", 19.9440, 50.0645, 30, 30, 500, 10, true, 50.0645, 19.9440)
        };

        var scenario = factory.BuildForTargets(targets);
        Assert.NotNull(scenario);
        Assert.Equal(2, scenario.Exits.Length);

        var pathfinder = new PlanSafe.Contracts.Simulation.MapPathfinder(scenario);

        var res1 = pathfinder.EvaluateAssignment(
            citizenLat: 50.0615,
            citizenLng: 19.9365,
            candidateTargets: targets,
            weightDistance: 0.1,
            weightOccupancy: 0.9
        );

        Assert.NotNull(res1.Target);
        Assert.NotNull(res1.TargetEvaluations);
        Assert.Equal(2, res1.TargetEvaluations.Count);
        var evalRynek1 = res1.TargetEvaluations.First(e => e.TargetId == "safe-zone-rynek");
        var evalDworzec1 = res1.TargetEvaluations.First(e => e.TargetId == "safe-zone-dworzec");

        Assert.True(evalRynek1.WalkableDistance < 999990.0, $"Rynek distance: {evalRynek1.WalkableDistance}");
        Assert.True(evalDworzec1.WalkableDistance < 999990.0, $"Dworzec distance: {evalDworzec1.WalkableDistance}");
        // With wocc=0.9 and Rynek 90% full vs Dworzec 2% full, Dworzec must win!
        Assert.Equal("safe-zone-dworzec", res1.Target.Id);

        // Test 2: Distance-priority (wdist=1.0, wocc=0.0) -> Rynek is right next to citizen, must win!
        var res2 = pathfinder.EvaluateAssignment(
            citizenLat: 50.0615,
            citizenLng: 19.9365,
            candidateTargets: targets,
            weightDistance: 1.0,
            weightOccupancy: 0.0
        );
        Assert.NotNull(res2.Target);
        Assert.Equal("safe-zone-rynek", res2.Target.Id);
    }
}
