using System.Reflection;
using PlanSafe.App.Pages;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;
using Xunit;

namespace PlanSafe.Tests;

public class EvacuationLocationTests
{
    private static void Set(Evacuate page, string name, object? value) =>
        typeof(Evacuate).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    [Theory]
    [InlineData(null, null, true, false)]
    [InlineData(51, 21, true, false)]
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
