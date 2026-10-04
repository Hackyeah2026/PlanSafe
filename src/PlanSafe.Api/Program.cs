using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PlanSafe.Api;
using PlanSafe.Api.Services;
using PlanSafe.Contracts.Models.Simulation;

// Fail startup if the bundled SQLite native library cannot be loaded.
SQLitePCL.Batteries_V2.Init();
_ = SQLitePCL.raw.sqlite3_libversion();

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, EvacuationApiJsonContext.Default);
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<IEvacuationStateService, EvacuationStateService>();

var app = builder.Build();

app.UseCors();

app.MapGet("/api/health", () => Results.Text("OK"));

// Evacuation API Endpoints
var evacGroup = app.MapGroup("/api/evacuate");

evacGroup.MapPost("/publish-plan", (PublishPlanRequest request, IEvacuationStateService stateService) =>
{
    var response = stateService.PublishPlan(request);
    return Results.Ok(response);
});

evacGroup.MapPost("/assign", (TargetAssignmentRequest request, IEvacuationStateService stateService, HttpResponse httpResponse) =>
{
    httpResponse.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    httpResponse.Headers.Pragma = "no-cache";
    httpResponse.Headers.Expires = "0";

    var response = stateService.AssignTarget(request);
    return Results.Ok(response);
});

evacGroup.MapPost("/checkin", (CheckInRequest request, IEvacuationStateService stateService) =>
{
    var response = stateService.CheckIn(request);
    return Results.Ok(response);
});

evacGroup.MapGet("/targets", (IEvacuationStateService stateService, HttpResponse httpResponse) =>
{
    httpResponse.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    httpResponse.Headers.Pragma = "no-cache";
    httpResponse.Headers.Expires = "0";

    var targets = stateService.GetTargets();
    return Results.Ok(targets);
});

evacGroup.MapGet("/config", (IEvacuationStateService stateService) =>
{
    var config = stateService.GetConfig();
    return Results.Ok(config);
});

evacGroup.MapGet("/obstacles", (IEvacuationStateService stateService) =>
{
    var obstacles = stateService.GetObstacles();
    return Results.Ok(obstacles);
});

evacGroup.MapPost("/targets/{id}/occupancy", (string id, TargetOccupancyUpdateRequest request, IEvacuationStateService stateService) =>
{
    bool success = stateService.UpdateTargetOccupancy(id, request.Occupancy);
    return success ? Results.Ok(new GenericActionResult(true, "Zaktualizowano occupancy.")) : Results.NotFound(new GenericActionResult(false, "Target not found"));
});

evacGroup.MapPost("/reset", (IEvacuationStateService stateService) =>
{
    stateService.Reset();
    return Results.Ok(new GenericActionResult(true, "Evacuation state has been reset."));
});

evacGroup.MapPost("/cleanup-inactive", (IEvacuationStateService stateService) =>
{
    bool cleaned = stateService.CleanIfInactive();
    return Results.Ok(new CleanupActionResult(cleaned, stateService.CurrentSessionId));
});

app.Run();

/// <summary>Exposes the API entry point to the in-process HTTP integration tests.</summary>
public partial class Program;
