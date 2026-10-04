using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PlanSafe.Contracts.Models.Simulation;
using Xunit;

namespace PlanSafe.Api.Tests;

public sealed class EvacuationEndpointTests : IDisposable
{
    // A fresh host per test prevents the singleton evacuation state leaking between tests.
    private readonly WebApplicationFactory<Program> _factory = new();

    [Fact]
    public async Task Health_ReturnsOkText()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("OK", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PublishedPlan_IsAvailableThroughConfigAndTargets()
    {
        using var client = _factory.CreateClient();
        var published = await PublishAsync(client);
        var config = await client.GetFromJsonAsync<EvacuationPlanConfig>("/api/evacuate/config");
        using var targetsResponse = await client.GetAsync("/api/evacuate/targets");
        var targets = await targetsResponse.Content.ReadFromJsonAsync<List<EvacuationTarget>>();

        Assert.NotNull(config);
        Assert.Equal(published.SessionId, config.SessionId);
        Assert.Equal(0.7, config.WeightDistance);
        Assert.Equal(0.3, config.WeightOccupancy);
        Assert.Contains($"/evacuate?session={published.SessionId}", published.EvacuateUrl);
        Assert.StartsWith("<svg", published.QrCodeSvg);
        Assert.Equal("shelter", Assert.Single(Assert.IsType<List<EvacuationTarget>>(targets)).Id);
        Assert.True(targetsResponse.Headers.CacheControl?.NoStore);
        Assert.True(targetsResponse.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task CheckInAndOccupancyUpdate_AreVisibleToSubsequentRequests()
    {
        using var client = _factory.CreateClient();
        await PublishAsync(client);
        using var checkInResponse = await client.PostAsJsonAsync("/api/evacuate/checkin", new CheckInRequest("shelter", 2));
        checkInResponse.EnsureSuccessStatusCode();
        var checkIn = await checkInResponse.Content.ReadFromJsonAsync<CheckInResponse>();
        Assert.NotNull(checkIn);
        Assert.True(checkIn.Success);
        Assert.Equal(12, checkIn.Target?.CurrentOccupancy);

        using var updateResponse = await client.PostAsJsonAsync("/api/evacuate/targets/shelter/occupancy", new TargetOccupancyUpdateRequest(30));
        updateResponse.EnsureSuccessStatusCode();
        var result = await updateResponse.Content.ReadFromJsonAsync<GenericActionResult>();
        Assert.True(result?.Success);
        var targets = await client.GetFromJsonAsync<List<EvacuationTarget>>("/api/evacuate/targets");
        Assert.Equal(30, Assert.Single(Assert.IsType<List<EvacuationTarget>>(targets)).CurrentOccupancy);
    }

    [Fact]
    public async Task UnknownOccupancyTarget_ReturnsNotFoundJson()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/evacuate/targets/missing/occupancy", new TargetOccupancyUpdateRequest(30));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<GenericActionResult>();
        Assert.NotNull(result);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Assign_ReturnsPublishedTargetAndDisablesCaching()
    {
        using var client = _factory.CreateClient();
        await PublishAsync(client);
        using var response = await client.PostAsJsonAsync("/api/evacuate/assign", new TargetAssignmentRequest(10, 10));
        response.EnsureSuccessStatusCode();
        var assignment = await response.Content.ReadFromJsonAsync<TargetAssignmentResponse>();

        Assert.NotNull(assignment);
        Assert.Equal("shelter", assignment.Target?.Id);
        Assert.True(double.IsFinite(assignment.Distance));
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("/api/evacuate/checkin", "{broken")]
    [InlineData("/api/evacuate/targets/shelter/occupancy", "{\"occupancy\":\"invalid\"}")]
    public async Task MalformedJson_ReturnsBadRequest(string endpoint, string body)
    {
        using var client = _factory.CreateClient();
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(endpoint, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CorsPreflight_AllowsBrowserOrigin()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/evacuate/publish-plan");
        request.Headers.Add("Origin", "http://127.0.0.1:5171");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Reset_ClearsPublishedSession()
    {
        using var client = _factory.CreateClient();
        await PublishAsync(client);
        using var response = await client.PostAsync("/api/evacuate/reset", null);
        response.EnsureSuccessStatusCode();
        var config = await client.GetFromJsonAsync<EvacuationPlanConfig>("/api/evacuate/config");
        Assert.Equal("default", config?.SessionId);
    }

    private static async Task<PublishPlanResponse> PublishAsync(HttpClient client)
    {
        var target = new EvacuationTarget("shelter", "Test shelter", 100, 100, 20, 20, 100, 10);
        using var response = await client.PostAsJsonAsync("/api/evacuate/publish-plan",
            new PublishPlanRequest([target], BaseUrl: "http://127.0.0.1:5171", WeightDistance: 0.7, WeightOccupancy: 0.3));
        response.EnsureSuccessStatusCode();
        return Assert.IsType<PublishPlanResponse>(await response.Content.ReadFromJsonAsync<PublishPlanResponse>());
    }

    /// <summary>Releases the test host and its evacuation cleanup timer.</summary>
    public void Dispose() => _factory.Dispose();
}
