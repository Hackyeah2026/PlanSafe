using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Session;

namespace PlanSafe.App.Simulation;

/// <summary>Geographic extent and the original local metric projection used by map playback.</summary>
public sealed class MapSimulationBounds
{
    public double MinimumLatitude { get; }
    public double MaximumLatitude { get; }
    public double MinimumLongitude { get; }
    public double MaximumLongitude { get; }
    public double MetersPerDegreeLatitude { get; } = 111320.0;
    public double MetersPerDegreeLongitude { get; }
    public double WorldWidth { get; }
    public double WorldHeight { get; }

    public MapSimulationBounds(List<MapZoneItem> items, MapSession? session, MapSimulationBounds? previous = null)
    {
        (MinimumLatitude, MaximumLatitude, MinimumLongitude, MaximumLongitude) = ComputeBoundingBox(items, session);
        if (previous is not null)
        {
            MinimumLatitude = Math.Min(MinimumLatitude, previous.MinimumLatitude);
            MaximumLatitude = Math.Max(MaximumLatitude, previous.MaximumLatitude);
            MinimumLongitude = Math.Min(MinimumLongitude, previous.MinimumLongitude);
            MaximumLongitude = Math.Max(MaximumLongitude, previous.MaximumLongitude);
        }
        double midpointLatitude = (MinimumLatitude + MaximumLatitude) / 2.0;
        MetersPerDegreeLongitude = previous?.MetersPerDegreeLongitude
            ?? MetersPerDegreeLatitude * Math.Cos(midpointLatitude * Math.PI / 180.0);
        WorldWidth = Math.Max(50.0, (MaximumLongitude - MinimumLongitude) * MetersPerDegreeLongitude);
        WorldHeight = Math.Max(50.0, (MaximumLatitude - MinimumLatitude) * MetersPerDegreeLatitude);
    }

    public (double X, double Y) ToWorld(double latitude, double longitude) =>
        ((longitude - MinimumLongitude) * MetersPerDegreeLongitude,
         (MaximumLatitude - latitude) * MetersPerDegreeLatitude);

    private static (double minimumLatitude, double maximumLatitude, double minimumLongitude, double maximumLongitude) ComputeBoundingBox(
        List<MapZoneItem> items,
        MapSession? session)
    {
        var latitudes = new List<double>();
        var longitudes = new List<double>();

        foreach (var item in items)
        {
            switch (item)
            {
                case CircleMapZoneItem circle when circle.Center is { Length: >= 2 }:
                    double latitudeRadius = (circle.Radius ?? 50.0) / 111320.0;
                    double longitudeRadius = latitudeRadius / Math.Cos(circle.Center[0] * Math.PI / 180.0);
                    latitudes.Add(circle.Center[0] - latitudeRadius);
                    latitudes.Add(circle.Center[0] + latitudeRadius);
                    longitudes.Add(circle.Center[1] - longitudeRadius);
                    longitudes.Add(circle.Center[1] + longitudeRadius);
                    break;
                case PolygonMapZoneItem polygon when polygon.Coordinates != null:
                    foreach (var coordinate in polygon.Coordinates.Where(coordinate => coordinate is { Length: >= 2 }))
                    {
                        latitudes.Add(coordinate[0]);
                        longitudes.Add(coordinate[1]);
                    }
                    break;
                case LineMapZoneItem line:
                    if (line.StartPoint is { Length: >= 2 }) { latitudes.Add(line.StartPoint[0]); longitudes.Add(line.StartPoint[1]); }
                    if (line.EndPoint is { Length: >= 2 }) { latitudes.Add(line.EndPoint[0]); longitudes.Add(line.EndPoint[1]); }
                    break;
                case PointMapZoneItem point when point.Position is { Length: >= 2 }:
                    latitudes.Add(point.Position[0]);
                    longitudes.Add(point.Position[1]);
                    break;
            }
        }

        if (latitudes.Count < 2)
        {
            double centerLatitude = session?.MapCenter is { Length: >= 2 } ? session.MapCenter[0] : 50.0614;
            double centerLongitude = session?.MapCenter is { Length: >= 2 } ? session.MapCenter[1] : 19.9366;
            double margin = 0.005; // ~550m
            return (centerLatitude - margin, centerLatitude + margin, centerLongitude - margin, centerLongitude + margin);
        }

        double minimumLatitude = latitudes.Min();
        double maximumLatitude = latitudes.Max();
        double minimumLongitude = longitudes.Min();
        double maximumLongitude = longitudes.Max();

        double latitudePadding = Math.Max(0.0010, (maximumLatitude - minimumLatitude) * 0.15);
        double longitudePadding = Math.Max(0.0010, (maximumLongitude - minimumLongitude) * 0.15);

        return (minimumLatitude - latitudePadding, maximumLatitude + latitudePadding, minimumLongitude - longitudePadding, maximumLongitude + longitudePadding);
    }
}
