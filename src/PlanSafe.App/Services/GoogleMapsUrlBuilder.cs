using System.Globalization;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Simulation;

namespace PlanSafe.App.Services;

public static class GoogleMapsUrlBuilder
{
    public static string Build(GeoCoordinate destination, GeoCoordinate? origin,
        IReadOnlyList<GeoCoordinate>? routePath)
    {
        string url = "https://www.google.com/maps/dir/?api=1";
        if (origin.HasValue)
        {
            url += "&origin=" + Uri.EscapeDataString(Format(origin.Value));
        }
        url += "&destination=" + Uri.EscapeDataString(Format(destination)) + "&travelmode=walking";

        var waypoints = SelectWaypoints(routePath, origin, destination);
        if (waypoints.Count > 0)
        {
            url += "&waypoints=" + Uri.EscapeDataString(string.Join("|", waypoints));
        }
        return url;
    }

    private static List<string> SelectWaypoints(IReadOnlyList<GeoCoordinate>? route,
        GeoCoordinate? origin, GeoCoordinate destination)
    {
        var result = new List<string>();
        if (route == null || route.Count < 2 || route.Any(p => !IsValid(p))) return result;

        var distances = new double[route.Count];
        for (int i = 1; i < route.Count; i++)
        {
            distances[i] = distances[i - 1] + GeoMath.CalculateDistanceMeters(
                route[i - 1].Latitude, route[i - 1].Longitude,
                route[i].Latitude, route[i].Longitude);
        }
        double length = distances[^1];
        if (!double.IsFinite(length) || length <= 0) return result;

        // Absorb floating-point distance error at exact 250 m thresholds.
        int count = (int)Math.Clamp(Math.Floor((length + 1e-9) / 250), 1, 6);
        var seen = new HashSet<string>
        {
            Format(destination), Format(route[0]), Format(route[^1])
        };
        if (origin.HasValue) seen.Add(Format(origin.Value));

        int segment = 1;
        for (int i = 1; i <= count; i++)
        {
            double offset = length * i / (count + 1);
            while (segment < route.Count - 1 && distances[segment] <= offset) segment++;
            double fraction = (offset - distances[segment - 1]) /
                (distances[segment] - distances[segment - 1]);
            var start = route[segment - 1];
            var end = route[segment];
            // Interpolate across the short longitude interval, including the date line.
            double longitudeDelta = (end.Longitude - start.Longitude + 540) % 360 - 180;
            double longitude = (start.Longitude + fraction * longitudeDelta + 540) % 360 - 180;
            var point = new GeoCoordinate(
                start.Latitude + fraction * (end.Latitude - start.Latitude), longitude);
            string formatted = Format(point);
            if (seen.Add(formatted)) result.Add(formatted);
        }
        return result;
    }

    private static bool IsValid(GeoCoordinate point) =>
        double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude) &&
        point.Latitude is >= -90 and <= 90 && point.Longitude is >= -180 and <= 180;

    private static string Format(GeoCoordinate point) =>
        point.Latitude.ToString("F6", CultureInfo.InvariantCulture) + "," +
        point.Longitude.ToString("F6", CultureInfo.InvariantCulture);
}
