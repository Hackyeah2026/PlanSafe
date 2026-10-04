using System;
using System.Collections.Generic;
using PlanSafe.Api.Services;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;
using Xunit;

namespace PlanSafe.Api.Tests;

public sealed class EvacuationStateServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OccupancyOnlyAlternatesNewArrivalsBetweenUnlimitedZones(bool geographic)
    {
        using var service = new EvacuationStateService();
        var targets = new List<EvacuationTarget>
        {
            new("near", "Near", 19.93, 50.06, 0, 0, 0, 0, true, 50.06, 19.93),
            new("far", "Far", 19.95, 50.08, 0, 0, 0, 0, true, 50.08, 19.95)
        };
        service.PublishPlan(new PublishPlanRequest(targets, WeightDistance: 0, WeightOccupancy: 1));
        var request = new TargetAssignmentRequest(19.93, 50.06,
            Latitude: geographic ? 50.06 : null, Longitude: geographic ? 19.93 : null);
        for (int i = 0; i < 6; i++)
        {
            var result = service.AssignTarget(request);
            Assert.Equal(i % 2 == 0 ? "near" : "far", result.Target!.Id);
            Assert.False(result.Target.IsFull);
            service.CheckIn(new CheckInRequest(result.Target.Id, 1));
            var refresh = service.AssignTarget(request with { CurrentTargetId = result.Target.Id });
            Assert.Equal(result.Target.Id, refresh.Target!.Id);
        }
        Assert.All(service.GetTargets(), target => Assert.Equal(3, target.CurrentOccupancy));
        // Switching to distance-only must still choose the nearest zone regardless of load.
        service.CheckIn(new CheckInRequest("near", 10000));
        Assert.Equal("near", service.AssignTarget(request with { WeightDistance = 1, WeightOccupancy = 0 }).Target!.Id);
    }

    [Fact]
    public void RepublishingCreatesNewSessionAndUsesCurrentPlanSettings()
    {
        using var service = new EvacuationStateService();
        var targets = new List<EvacuationTarget>
        {
            new("s-1", "Shelter", 19.93, 50.06, 20, 20, 300, 0, true, 50.06, 19.93)
        };
        var first = service.PublishPlan(new PublishPlanRequest(targets, WeightDistance: 0.7, WeightOccupancy: 0.3));
        service.CheckIn(new CheckInRequest("s-1", 1));

        var second = service.PublishPlan(new PublishPlanRequest(targets, WeightDistance: 0.2, WeightOccupancy: 0.8));

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.NotEqual(first.EvacuateUrl, second.EvacuateUrl);
        Assert.Equal(second.SessionId, service.CurrentSessionId);
        var config = service.GetConfig();
        Assert.NotNull(config);
        Assert.Equal(second.SessionId, config.SessionId);
        Assert.Equal(0.2, config.WeightDistance);
        Assert.Equal(0.8, config.WeightOccupancy);
        Assert.Equal(0, Assert.Single(service.GetTargets()).CurrentOccupancy);
    }

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
        Assert.Equal(response.EvacuateUrl, QrCodeSvgTests.DecodeSvg(response.QrCodeSvg));
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

        // Unlimited safe zones must still balance arrivals on the street-routing path.
        targets = targets.Select(t => t with { Capacity = 0, CurrentOccupancy = 0 }).ToList();
        var assignments = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var result = pathfinder.EvaluateAssignment(50.0615, 19.9365, targets,
                weightDistance: 0, weightOccupancy: 1);
            assignments.Add(result.Target!.Id);
            int selected = targets.FindIndex(t => t.Id == result.Target.Id);
            targets[selected] = targets[selected] with { CurrentOccupancy = targets[selected].CurrentOccupancy + 1 };
            Assert.All(result.TargetEvaluations!, evaluation => Assert.False(evaluation.IsFull));
        }
        Assert.NotEqual(assignments[0], assignments[1]);
        Assert.Equal(assignments[0], assignments[2]);
        Assert.Equal(assignments[1], assignments[3]);
    }
}
