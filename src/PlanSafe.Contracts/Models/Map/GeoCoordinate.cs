namespace PlanSafe.Contracts.Models.Map;

/// <summary>
/// Type-safe geographic coordinate representing (Latitude, Longitude).
/// </summary>
public readonly record struct GeoCoordinate(double Latitude, double Longitude)
{
    public static GeoCoordinate? FromArray(double[]? coords)
    {
        if (coords is { Length: >= 2 })
        {
            return new GeoCoordinate(coords[0], coords[1]);
        }
        return null;
    }

    public double[] ToArray() => [Latitude, Longitude];

    public override string ToString() => $"({Latitude:F5}°, {Longitude:F5}°)";
}
