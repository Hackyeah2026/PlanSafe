using System.Reflection;
using PlanSafe.App.Pages;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;
using PlanSafe.Contracts.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class EvacuationLocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TriangleStartingPointIsItsCentroidRegardlessOfWinding(bool reverse)
    {
        var zone = new List<GeoCoordinate>
        {
            new(50, 19), new(50, 19.0003), new(50.0003, 19)
        };
        if (reverse) zone.Reverse();
        zone.Add(zone[0]);

        var center = GeoMath.GetInteriorCenter(zone);
        Assert.NotNull(center);
        Assert.Equal(50.0001, center.Value.Latitude, 9);
        Assert.Equal(19.0001, center.Value.Longitude, 9);
        Assert.True(GeoMath.IsPointInPolygon(center.Value.Latitude, center.Value.Longitude, zone));
    }

    [Fact]
    public void MissingGpsStartsAtZoneCentroidWithVerificationWarning()
    {
        var page = new Evacuate();
        Set(page, "evacZones", new List<List<GeoCoordinate>>
        {
            new() { new(50, 19), new(50, 19.0003), new(50.0003, 19) }
        });
        var picked = typeof(Evacuate).GetMethod("TrySetProvisionalZoneLocation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, null);
        object? Get(string name) => typeof(Evacuate).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);

        Assert.Equal(true, picked);
        Assert.Equal(50.0001, (double)Get("gpsLatitude")!, 9);
        Assert.Equal(19.0001, (double)Get("gpsLongitude")!, 9);
        Assert.True((bool)Get("isProvisionalLocation")!);
        Assert.False((bool)Get("isLocationWarningDismissed")!);
    }

    [Fact]
    public void OutsideGpsUsesFirstZoneCenterAndValidGpsClearsProvisionalState()
    {
        var page = new Evacuate();
        Set(page, "evacZones", new List<List<GeoCoordinate>>
        {
            new() { new(50, 19), new(50, 20), new(51, 20), new(51, 19) },
            new() { new(52, 21), new(52, 22), new(53, 22), new(53, 21) }
        });
        var apply = typeof(Evacuate).GetMethod("ApplyGpsLocation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? Get(string name) => typeof(Evacuate).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);

        apply.Invoke(page, new object[] { 55.0, 25.0, 12.0 });
        Assert.Equal(50.5, (double)Get("gpsLatitude")!);
        Assert.Equal(19.5, (double)Get("gpsLongitude")!);
        Assert.True((bool)Get("isProvisionalLocation")!);
        Assert.Equal(0.0, (double)Get("gpsAccuracy")!);
        Assert.True((bool)typeof(Evacuate).GetProperty("HasValidLocation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!);

        apply.Invoke(page, new object[] { 52.2, 21.3, 8.0 });
        Assert.Equal(52.2, (double)Get("gpsLatitude")!);
        Assert.Equal(21.3, (double)Get("gpsLongitude")!);
        Assert.False((bool)Get("isProvisionalLocation")!);
        Assert.Equal(8.0, (double)Get("gpsAccuracy")!);
    }

    [Fact]
    public void OutsideGpsWithoutZonesDoesNotInventAStartingPoint()
    {
        var page = new Evacuate();
        typeof(Evacuate).GetMethod("ApplyGpsLocation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, new object[] { 55.0, 25.0, 12.0 });
        Assert.False((bool)typeof(Evacuate).GetField("isProvisionalLocation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!);
    }

    [Fact]
    public void ConcaveZoneGetsAnInteriorStartingPoint()
    {
        var zone = new List<GeoCoordinate>
        {
            new(0, 0), new(0, 4), new(1, 4), new(1, 1), new(4, 1), new(4, 0)
        };
        Assert.False(GeoMath.IsPointInPolygon(2, 2, zone));
        var center = GeoMath.GetInteriorCenter(zone);
        Assert.NotNull(center);
        Assert.True(GeoMath.IsPointInPolygon(center.Value.Latitude, center.Value.Longitude, zone));
    }

    [Fact]
    public void DegenerateZoneHasNoStartingPoint()
    {
        Assert.Null(GeoMath.GetInteriorCenter(new[] { new GeoCoordinate(1, 1), new(1, 2), new(1, 3) }));
    }

    private static void Set(Evacuate page, string name, object? value) =>
        typeof(Evacuate).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    [Theory]
    [InlineData(null, null, true, false)]
    [InlineData(51.0, 21.0, true, false)]
    [InlineData(50.5, 19.5, false, false)]
    [InlineData(50.5, 19.5, true, true)]
    public void NavigationRequiresActualLocationInsideConfiguredZone(
        double? latitude, double? longitude, bool hasZone, bool allowed)
    {
        var page = new Evacuate();
        var target = new EvacuationTarget("test", "Shelter", 19.5, 50.5, 1, 1, 100, 0);
        Set(page, "gpsLatitude", latitude);
        Set(page, "gpsLongitude", longitude);
        Set(page, "assignment", new TargetAssignmentResponse(target, 100));
        if (hasZone)
        {
            Set(page, "evacZones", new List<List<GeoCoordinate>>
            {
                new() { new(50, 19), new(50, 20), new(51, 20), new(51, 19) }
            });
        }

        var url = typeof(Evacuate).GetMethod("GetMapsUrl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, new object[] { target });
        Assert.Equal(allowed, url != null);
    }
}
