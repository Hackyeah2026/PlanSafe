using System;
using System.Collections.Generic;
using System.Linq;
using PlanSafe.Contracts.Models.Gus;
using PlanSafe.Contracts.Models.Map;

namespace PlanSafe.App.Services.Gus;

public class GusOccupantGenerator : IGusOccupantGenerator
{
    private readonly IGusCensusService _censusService;

    public GusOccupantGenerator(IGusCensusService censusService)
    {
        _censusService = censusService;
    }

    public IReadOnlyList<OccupantAgent> GenerateOccupants(IEnumerable<MapZoneItem> evacZones, int? randomSeed = null)
    {
        if (evacZones == null) return Array.Empty<OccupantAgent>();

        var validZones = evacZones.Where(z => z.Category == ZoneCategory.EvacuationZone).ToList();
        if (validZones.Count == 0) return Array.Empty<OccupantAgent>();

        var rng = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        var agents = new List<OccupantAgent>();
        var generatedPositions = new HashSet<(long, long)>();

        foreach (var zone in validZones)
        {
            if (!GeoSpatialMath.GetZoneBoundingBox(zone, out double zMinLat, out double zMinLng, out double zMaxLat, out double zMaxLng))
                continue;

            var intersecting = _censusService.GetIntersectingCells(zone);

            foreach (var (cell, ratio) in intersecting)
            {
                int count = (int)Math.Round(cell.Pop * ratio);
                if (count <= 0) continue;

                // Overlap bounding box between cell and zone
                double overlapMinLat = Math.Max(cell.MinLat, zMinLat);
                double overlapMaxLat = Math.Min(cell.MaxLat, zMaxLat);
                double overlapMinLng = Math.Max(cell.MinLng, zMinLng);
                double overlapMaxLng = Math.Min(cell.MaxLng, zMaxLng);

                if (overlapMinLat >= overlapMaxLat || overlapMinLng >= overlapMaxLng)
                {
                    overlapMinLat = cell.MinLat;
                    overlapMaxLat = cell.MaxLat;
                    overlapMinLng = cell.MinLng;
                    overlapMaxLng = cell.MaxLng;
                }

                for (int i = 0; i < count; i++)
                {
                    double agentLat = cell.Lat;
                    double agentLng = cell.Lng;
                    bool placed = false;

                    // Rejection sampling: ensure agent is strictly inside the zone
                    for (int attempt = 0; attempt < 50; attempt++)
                    {
                        double candLat = overlapMinLat + rng.NextDouble() * (overlapMaxLat - overlapMinLat);
                        double candLng = overlapMinLng + rng.NextDouble() * (overlapMaxLng - overlapMinLng);

                        if (GeoSpatialMath.IsPointInZone(candLat, candLng, zone))
                        {
                            agentLat = candLat;
                            agentLng = candLng;
                            placed = true;
                            break;
                        }
                    }

                    // Fallback if cell boundary is on the outer boundary curvature
                    if (!placed && GeoSpatialMath.IsPointInZone(cell.Lat, cell.Lng, zone))
                    {
                        agentLat = cell.Lat;
                        agentLng = cell.Lng;
                        placed = true;
                    }

                    if (placed)
                    {
                        agents.Add(new OccupantAgent
                        {
                            Id = $"occ-{Guid.NewGuid():N}",
                            Latitude = Math.Round(agentLat, 6),
                            Longitude = Math.Round(agentLng, 6),
                            CellId = cell.Id,
                            EvacZoneId = zone.Id
                        });
                    }
                }
            }
        }

        return agents;
    }
}
