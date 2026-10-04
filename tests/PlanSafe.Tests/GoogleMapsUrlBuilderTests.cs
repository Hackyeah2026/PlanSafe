using System.Globalization;
using PlanSafe.App.Services;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class GoogleMapsUrlBuilderTests
{
    private static GeoCoordinate AtMeters(double meters) =>
        new(0, meters / GeoMath.EarthRadiusMeters * 180 / Math.PI);

    private static string[] Waypoints(string url)
    {
        string? parameter = new Uri(url).Query.TrimStart('?').Split('&')
            .FirstOrDefault(p => p.StartsWith("waypoints="));
        return parameter == null ? [] : Uri.UnescapeDataString(parameter[10..]).Split('|');
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(249.99, 1)]
    [InlineData(250, 1)]
    [InlineData(499.99, 1)]
    [InlineData(500, 2)]
    [InlineData(500.01, 2)]
    [InlineData(999.99, 3)]
    [InlineData(1000, 4)]
    [InlineData(1000.01, 4)]
    [InlineData(1499.99, 5)]
    [InlineData(1500, 6)]
    [InlineData(1500.01, 6)]
    [InlineData(10000, 6)]
    public void CountDependsOnRouteLength(double meters, int expected)
    {
        var start = AtMeters(0);
        var end = AtMeters(meters);
        Assert.Equal(expected, Waypoints(GoogleMapsUrlBuilder.Build(end, start, [start, end])).Length);
    }

    [Fact]
    public void UnevenVerticesAreSampledByDistance()
    {
        var route = new[] { AtMeters(0), AtMeters(1), AtMeters(20), AtMeters(900) };
        string[] points = Waypoints(GoogleMapsUrlBuilder.Build(route[^1], route[0], route));
        Assert.Equal(3, points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            double longitude = double.Parse(points[i].Split(',')[1], CultureInfo.InvariantCulture);
            double distance = GeoMath.CalculateDistanceMeters(0, 0, 0, longitude);
            Assert.InRange(distance, 225 * (i + 1) - 0.1, 225 * (i + 1) + 0.1);
        }
    }

    [Fact]
    public void BentRouteFollowsSegmentsInOrder()
    {
        GeoCoordinate[] route = [new(0, 0), new(0, 0.006), new(0.006, 0.006)];
        Assert.Equal(new[]
        {
            "0.000000,0.002000", "0.000000,0.004000", "0.000000,0.006000",
            "0.002000,0.006000", "0.004000,0.006000"
        }, Waypoints(GoogleMapsUrlBuilder.Build(route[^1], route[0], route)));
    }

    [Fact]
    public void DateLineCrossingUsesShortLongitudeInterval()
    {
        GeoCoordinate start = new(0, 179.999);
        GeoCoordinate end = new(0, -179.999);
        Assert.Equal(new[] { "0.000000,-180.000000" },
            Waypoints(GoogleMapsUrlBuilder.Build(end, start, [start, end])));
    }

    [Fact]
    public void DuplicateVerticesDoNotChangeSampling()
    {
        var start = AtMeters(0);
        var middle = AtMeters(300);
        var end = AtMeters(900);
        Assert.Equal(
            GoogleMapsUrlBuilder.Build(end, start, [start, middle, end]),
            GoogleMapsUrlBuilder.Build(end, start, [start, start, middle, middle, end, end]));
    }

    [Fact]
    public void RoundedDuplicatesAndEndpointsAreExcluded()
    {
        GeoCoordinate start = new(0, 0);
        GeoCoordinate end = new(0, 0.006);
        // Three equal traversals produce repeated sampled coordinates and endpoints.
        GeoCoordinate[] loop = [start, end, start, end];
        string[] points = Waypoints(GoogleMapsUrlBuilder.Build(end, start, loop));
        Assert.Equal(points.Length, points.Distinct().Count());
        Assert.DoesNotContain("0.000000,0.000000", points);
        Assert.DoesNotContain("0.000000,0.006000", points);
        Assert.Empty(Waypoints(GoogleMapsUrlBuilder.Build(new(0, 0.0000004), start,
            [start, new(0, 0.0000004)])));
    }

    [Fact]
    public void UnusableRoutesKeepLinkWithoutWaypoints()
    {
        GeoCoordinate start = new(50, 20);
        GeoCoordinate end = new(51, 21);
        IReadOnlyList<GeoCoordinate>?[] routes =
        [null, [], [start], [start, start], [start, new(double.NaN, 20), end],
            [start, new(50, double.PositiveInfinity)], [start, new(91, 20)], [start, new(50, -181)]];
        foreach (var route in routes)
        {
            Assert.Equal(
                "https://www.google.com/maps/dir/?api=1&origin=50.000000%2C20.000000&destination=51.000000%2C21.000000&travelmode=walking",
                GoogleMapsUrlBuilder.Build(end, start, route));
        }
    }

    [Fact]
    public void MissingGpsOmitsOriginAndPolishCultureDoesNotAffectEncodedCoordinates()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            GeoCoordinate[] route = [new(50.06, 19.93), new(50.066, 19.936)];
            string url = GoogleMapsUrlBuilder.Build(route[^1], null, route);
            Assert.DoesNotContain("&origin=", url);
            Assert.Contains("&destination=50.066000%2C19.936000&travelmode=walking", url);
            Assert.Contains("%7C", url);
            Assert.DoesNotContain("|", url);
            Assert.DoesNotContain(",", url);
            Assert.Equal(3, Waypoints(url).Length);
            Assert.True(url.Length < 2048);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
