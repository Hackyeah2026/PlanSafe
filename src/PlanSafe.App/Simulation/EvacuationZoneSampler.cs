using PlanSafe.Contracts.Models.Map;
using PlanSafe.App.Services.Gus;

namespace PlanSafe.App.Simulation;

/// <summary>Deterministic population sampling with cooperative preparation yields.</summary>
public static class EvacuationZoneSampler
{
    public static async Task<List<(double X, double Y)>> GenerateAsync(
        List<MapZoneItem> evacuationZones,
        int count,
        Func<double, double, (double X, double Y)> toWorld,
        CancellationToken cancellationToken)
    {
        var random = new Random(42);
        var positions = new List<(double X, double Y)>();
        int attempts = 0;
        int maximumAttempts = count * 50;
        var scheduler = new PreparationScheduler();

        while (positions.Count < count && attempts < maximumAttempts)
        {
            attempts++;
            if (attempts % 128 == 0) await scheduler.YieldAsync(cancellationToken);
            var zone = evacuationZones[random.Next(evacuationZones.Count)];
            if (!GeoSpatialMath.GetZoneBoundingBox(zone, out double zoneMinimumLatitude, out double zoneMinimumLongitude, out double zoneMaximumLatitude, out double zoneMaximumLongitude))
                continue;

            double candidateLatitude = zoneMinimumLatitude + random.NextDouble() * (zoneMaximumLatitude - zoneMinimumLatitude);
            double candidateLongitude = zoneMinimumLongitude + random.NextDouble() * (zoneMaximumLongitude - zoneMinimumLongitude);

            if (GeoSpatialMath.IsPointInZone(candidateLatitude, candidateLongitude, zone))
            {
                positions.Add(toWorld(candidateLatitude, candidateLongitude));
            }
        }

        while (positions.Count < count && evacuationZones.Count > 0)
        {
            var zone = evacuationZones[0];
            if (GeoSpatialMath.GetZoneBoundingBox(zone, out double zoneMinimumLatitude, out double zoneMinimumLongitude, out double zoneMaximumLatitude, out double zoneMaximumLongitude))
            {
                positions.Add(toWorld((zoneMinimumLatitude + zoneMaximumLatitude) / 2.0, (zoneMinimumLongitude + zoneMaximumLongitude) / 2.0));
            }
            else
            {
                break;
            }
        }

        return positions;
    }
}
