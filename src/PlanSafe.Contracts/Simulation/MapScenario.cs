namespace PlanSafe.Contracts.Simulation;

/// <summary>Evacuation target in world meters. Agents inside the square of half-side Radius are evacuated.</summary>
public sealed record MapExit(double X, double Y, double Radius, int Capacity = 1000, int InitialOccupancy = 0, double BasePotential = 0.0, string? TargetId = null);

/// <summary>Progress of a map evacuation. People counts include granulation; times are simulated seconds.</summary>
public sealed record MapEvacuationStatus(
    bool IsMap,
    double SimulationSeconds,
    int TotalPeople,
    int EvacuatedPeople,
    int RemainingAgents,
    int[] EvacuatedPerExit,
    double? HalfEvacuatedSeconds,
    double? NinetyPercentEvacuatedSeconds,
    double? LastEvacuationSeconds,
    bool Finished,
    bool Stalled);

/// <summary>Spawn polygon in world meters with the number of people living inside it.</summary>
public sealed record MapSpawnZone(double[] X, double[] Y, int People);

/// <summary>
/// Walkability raster of a real map area. World X grows east, world Y grows south and the
/// origin is the north-west corner, so geographic positions map to meters with a local
/// equirectangular projection around the area.
/// </summary>
public sealed class MapScenario
{
    public const int FormatVersion = 2;
    public const int MaximumDimension = 4096;
    private static readonly byte[] Magic = "EFMAP"u8.ToArray();

    public MapScenario(
        double originLatitude,
        double originLongitude,
        double metersPerDegreeLatitude,
        double metersPerDegreeLongitude,
        double cellSize,
        int columns,
        int rows,
        bool[] blocked,
        MapExit[] exits,
        MapSpawnZone[] spawnZones)
    {
        if (!double.IsFinite(originLatitude) || !double.IsFinite(originLongitude) ||
            !double.IsFinite(metersPerDegreeLatitude) || metersPerDegreeLatitude <= 0 ||
            !double.IsFinite(metersPerDegreeLongitude) || metersPerDegreeLongitude <= 0)
            throw new ArgumentException("Invalid map projection.");
        if (!double.IsFinite(cellSize) || cellSize is < 0.25 or > 25)
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        if (columns is < 5 or > MaximumDimension || rows is < 5 or > MaximumDimension)
            throw new ArgumentException("Map raster dimensions are out of range.");
        ArgumentNullException.ThrowIfNull(blocked);
        ArgumentNullException.ThrowIfNull(exits);
        ArgumentNullException.ThrowIfNull(spawnZones);
        if (blocked.Length != columns * rows) throw new ArgumentException("Map raster has an invalid length.", nameof(blocked));
        if (exits.Length == 0) throw new ArgumentException("At least one exit is required.", nameof(exits));
        if (spawnZones.Length == 0) throw new ArgumentException("At least one spawn zone is required.", nameof(spawnZones));

        double width = columns * cellSize;
        double height = rows * cellSize;
        foreach (var exit in exits)
        {
            if (!double.IsFinite(exit.X) || !double.IsFinite(exit.Y) || !double.IsFinite(exit.Radius) ||
                exit.Radius <= 0 || exit.X < 0 || exit.Y < 0 || exit.X > width || exit.Y > height)
                throw new ArgumentException("Exit lies outside the map area.", nameof(exits));
        }
        foreach (var zone in spawnZones)
        {
            if (zone.X is null || zone.Y is null || zone.X.Length < 3 || zone.X.Length != zone.Y.Length ||
                zone.People < 0 || zone.X.Any(v => !double.IsFinite(v)) || zone.Y.Any(v => !double.IsFinite(v)))
                throw new ArgumentException("Invalid spawn zone.", nameof(spawnZones));
        }

        OriginLatitude = originLatitude;
        OriginLongitude = originLongitude;
        MetersPerDegreeLatitude = metersPerDegreeLatitude;
        MetersPerDegreeLongitude = metersPerDegreeLongitude;
        CellSize = cellSize;
        Columns = columns;
        Rows = rows;
        Blocked = blocked;
        Exits = exits;
        SpawnZones = spawnZones;
    }

    public double OriginLatitude { get; }
    public double OriginLongitude { get; }
    public double MetersPerDegreeLatitude { get; }
    public double MetersPerDegreeLongitude { get; }
    public double CellSize { get; }
    public int Columns { get; }
    public int Rows { get; }
    public bool[] Blocked { get; }
    public MapExit[] Exits { get; }
    public MapSpawnZone[] SpawnZones { get; }
    public double WorldWidth => Columns * CellSize;
    public double WorldHeight => Rows * CellSize;

    /// <summary>Checks the full movement segment, including thin walls between two open endpoints.</summary>
    public bool CrossesBlockedCell(double fromX, double fromY, double toX, double toY)
    {
        int minCol = Math.Max(0, (int)Math.Floor(Math.Min(fromX, toX) / CellSize));
        int maxCol = Math.Min(Columns - 1, (int)Math.Floor(Math.Max(fromX, toX) / CellSize));
        int minRow = Math.Max(0, (int)Math.Floor(Math.Min(fromY, toY) / CellSize));
        int maxRow = Math.Min(Rows - 1, (int)Math.Floor(Math.Max(fromY, toY) / CellSize));
        double dx = toX - fromX, dy = toY - fromY;
        for (int row = minRow; row <= maxRow; row++)
            for (int col = minCol; col <= maxCol; col++)
            {
                if (!Blocked[row * Columns + col]) continue;
                double enter = 0, leave = 1;
                if (Clip(fromX, dx, col * CellSize, (col + 1) * CellSize, ref enter, ref leave) &&
                    Clip(fromY, dy, row * CellSize, (row + 1) * CellSize, ref enter, ref leave) &&
                    enter <= leave && leave > 0 && enter < 1) return true;
            }
        return false;

        static bool Clip(double from, double delta, double lo, double hi, ref double enter, ref double leave)
        {
            if (Math.Abs(delta) < 0.000001) return from >= lo && from <= hi;
            double a = (lo - from) / delta, b = (hi - from) / delta;
            enter = Math.Max(enter, Math.Min(a, b));
            leave = Math.Min(leave, Math.Max(a, b));
            return enter <= leave;
        }
    }
    public int TotalPeople => SpawnZones.Sum(zone => zone.People);

    /// <summary>Anything outside the raster counts as blocked.</summary>
    public bool IsBlockedAt(double x, double y)
    {
        if (!(x >= 0) || !(y >= 0)) return true;
        int column = (int)(x / CellSize);
        int row = (int)(y / CellSize);
        if (column >= Columns || row >= Rows) return true;
        return Blocked[row * Columns + column];
    }

    /// <summary>Center of the nearest walkable raster cell within maxDistance, or null.</summary>
    public (double X, double Y)? NearestOpenCell(double x, double y, double maxDistance)
    {
        int centerColumn = Math.Clamp((int)(x / CellSize), 0, Columns - 1);
        int centerRow = Math.Clamp((int)(y / CellSize), 0, Rows - 1);
        int reach = (int)Math.Ceiling(maxDistance / CellSize);
        double best = maxDistance * maxDistance;
        (double X, double Y)? result = null;
        for (int row = Math.Max(0, centerRow - reach); row <= Math.Min(Rows - 1, centerRow + reach); row++)
        {
            for (int column = Math.Max(0, centerColumn - reach); column <= Math.Min(Columns - 1, centerColumn + reach); column++)
            {
                if (Blocked[row * Columns + column]) continue;
                double cx = (column + 0.5) * CellSize, cy = (row + 0.5) * CellSize;
                double distance = (cx - x) * (cx - x) + (cy - y) * (cy - y);
                if (distance <= best) { best = distance; result = (cx, cy); }
            }
        }
        return result;
    }

    public (double X, double Y) ToWorld(double latitude, double longitude) =>
        ((longitude - OriginLongitude) * MetersPerDegreeLongitude,
         (OriginLatitude - latitude) * MetersPerDegreeLatitude);

    public (double Latitude, double Longitude) ToGeo(double x, double y) =>
        (OriginLatitude - y / MetersPerDegreeLatitude,
         OriginLongitude + x / MetersPerDegreeLongitude);

    /// <summary>Compact binary form; the raster is run-length encoded starting with a walkable run.</summary>
    public byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(OriginLatitude);
            writer.Write(OriginLongitude);
            writer.Write(MetersPerDegreeLatitude);
            writer.Write(MetersPerDegreeLongitude);
            writer.Write(CellSize);
            writer.Write(Columns);
            writer.Write(Rows);

            var runs = new List<int>();
            bool current = false;
            int length = 0;
            foreach (bool cell in Blocked)
            {
                if (cell == current) { length++; continue; }
                runs.Add(length);
                current = cell;
                length = 1;
            }
            runs.Add(length);
            writer.Write(runs.Count);
            foreach (int run in runs) writer.Write(run);

            writer.Write(Exits.Length);
            foreach (var exit in Exits)
            {
                writer.Write(exit.X);
                writer.Write(exit.Y);
                writer.Write(exit.Radius);
                writer.Write(exit.Capacity);
                writer.Write(exit.InitialOccupancy);
                writer.Write(exit.BasePotential);
            }

            writer.Write(SpawnZones.Length);
            foreach (var zone in SpawnZones)
            {
                writer.Write(zone.People);
                writer.Write(zone.X.Length);
                for (int i = 0; i < zone.X.Length; i++)
                {
                    writer.Write(zone.X[i]);
                    writer.Write(zone.Y[i]);
                }
            }
        }
        return stream.ToArray();
    }

    public static MapScenario Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data, writable: false));
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
                throw new ArgumentException("Unsupported map scenario format.", nameof(data));
            int version = reader.ReadInt32();
            if (version is not (1 or 2))
                throw new ArgumentException("Unsupported map scenario format.", nameof(data));
            double originLatitude = reader.ReadDouble();
            double originLongitude = reader.ReadDouble();
            double metersPerDegreeLatitude = reader.ReadDouble();
            double metersPerDegreeLongitude = reader.ReadDouble();
            double cellSize = reader.ReadDouble();
            int columns = reader.ReadInt32();
            int rows = reader.ReadInt32();
            if (columns is < 5 or > MaximumDimension || rows is < 5 or > MaximumDimension)
                throw new ArgumentException("Map raster dimensions are out of range.", nameof(data));

            var blocked = new bool[columns * rows];
            int runCount = reader.ReadInt32();
            if (runCount < 1 || runCount > blocked.Length + 1) throw new ArgumentException("Invalid raster encoding.", nameof(data));
            int offset = 0;
            bool value = false;
            for (int i = 0; i < runCount; i++)
            {
                int run = reader.ReadInt32();
                if (run < 0 || run > blocked.Length - offset) throw new ArgumentException("Invalid raster encoding.", nameof(data));
                if (value) blocked.AsSpan(offset, run).Fill(true);
                offset += run;
                value = !value;
            }
            if (offset != blocked.Length) throw new ArgumentException("Invalid raster encoding.", nameof(data));

            int exitCount = reader.ReadInt32();
            if (exitCount is < 1 or > 256) throw new ArgumentException("Invalid exit count.", nameof(data));
            var exits = new MapExit[exitCount];
            for (int i = 0; i < exitCount; i++)
            {
                double ex = reader.ReadDouble();
                double ey = reader.ReadDouble();
                double er = reader.ReadDouble();
                int cap = 1000;
                int occ = 0;
                double bp = 0.0;
                if (version >= 2)
                {
                    cap = reader.ReadInt32();
                    occ = reader.ReadInt32();
                    bp = reader.ReadDouble();
                }
                exits[i] = new MapExit(ex, ey, er, cap, occ, bp);
            }

            int zoneCount = reader.ReadInt32();
            if (zoneCount is < 1 or > 256) throw new ArgumentException("Invalid spawn zone count.", nameof(data));
            var zones = new MapSpawnZone[zoneCount];
            for (int i = 0; i < zoneCount; i++)
            {
                int people = reader.ReadInt32();
                int points = reader.ReadInt32();
                if (points is < 3 or > 1024) throw new ArgumentException("Invalid spawn zone.", nameof(data));
                var xs = new double[points];
                var ys = new double[points];
                for (int p = 0; p < points; p++)
                {
                    xs[p] = reader.ReadDouble();
                    ys[p] = reader.ReadDouble();
                }
                zones[i] = new MapSpawnZone(xs, ys, people);
            }

            return new MapScenario(originLatitude, originLongitude, metersPerDegreeLatitude, metersPerDegreeLongitude,
                cellSize, columns, rows, blocked, exits, zones);
        }
        catch (EndOfStreamException exception)
        {
            throw new ArgumentException("Map scenario data is truncated.", nameof(data), exception);
        }
    }

    public static bool IsPointInPolygon(double x, double y, double[] xs, double[] ys)
    {
        bool inside = false;
        for (int i = 0, j = xs.Length - 1; i < xs.Length; j = i++)
        {
            if ((ys[i] > y) != (ys[j] > y) &&
                x < (xs[j] - xs[i]) * (y - ys[i]) / (ys[j] - ys[i]) + xs[i])
                inside = !inside;
        }
        return inside;
    }
}

/// <summary>
/// Builds a walkability raster: everything starts blocked, walkways open cells and buildings
/// close them again. Coordinates are world meters.
/// </summary>
public sealed class MapScenarioBuilder
{
    private readonly bool[] walkable;

    public MapScenarioBuilder(double width, double height, double cellSize)
    {
        if (!double.IsFinite(cellSize) || cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
        CellSize = cellSize;
        Columns = Math.Max(5, (int)Math.Ceiling(width / cellSize));
        Rows = Math.Max(5, (int)Math.Ceiling(height / cellSize));
        if (Columns > MapScenario.MaximumDimension || Rows > MapScenario.MaximumDimension)
            throw new ArgumentException("Map area is too large for the requested resolution.");
        walkable = new bool[Columns * Rows];
    }

    public double CellSize { get; }
    public int Columns { get; }
    public int Rows { get; }

    public bool IsWalkable(int column, int row) => walkable[row * Columns + column];

    /// <summary>
    /// Marks every cell whose center lies within halfWidth of the polyline; with onlyWhere, only cells
    /// set in that mask (same layout as this raster) change.
    /// </summary>
    public void SetCorridor(double[] xs, double[] ys, double halfWidth, bool isWalkable = true, bool[]? onlyWhere = null)
    {
        if (xs.Length != ys.Length) throw new ArgumentException("Polyline coordinates do not match.");
        if (onlyWhere is not null && onlyWhere.Length != walkable.Length) throw new ArgumentException("Mask has an invalid length.", nameof(onlyWhere));
        if (xs.Length == 1)
        {
            SetSegment(xs[0], ys[0], xs[0], ys[0], halfWidth, isWalkable, onlyWhere);
            return;
        }
        for (int i = 0; i + 1 < xs.Length; i++)
            SetSegment(xs[i], ys[i], xs[i + 1], ys[i + 1], halfWidth, isWalkable, onlyWhere);
    }

    public void SetDisk(double x, double y, double radius, bool isWalkable = true) =>
        SetSegment(x, y, x, y, radius, isWalkable, null);

    /// <summary>A copy of the raster: true where walkable.</summary>
    public bool[] ToMask() => (bool[])walkable.Clone();

    /// <summary>Scanline fill of cells whose center lies inside the polygon.</summary>
    public void SetPolygon(double[] xs, double[] ys, bool isWalkable) => SetRings([xs], [ys], isWalkable);

    /// <summary>
    /// Even-odd scanline fill of several rings, so inner rings (courtyards, islands) stay untouched.
    /// </summary>
    public void SetRings(double[][] ringXs, double[][] ringYs, bool isWalkable)
    {
        if (ringXs.Length == 0 || ringXs.Length != ringYs.Length) return;
        double minY = double.MaxValue, maxY = double.MinValue;
        for (int r = 0; r < ringXs.Length; r++)
        {
            if (ringXs[r].Length != ringYs[r].Length) return;
            if (ringYs[r].Length < 3) continue;
            minY = Math.Min(minY, ringYs[r].Min());
            maxY = Math.Max(maxY, ringYs[r].Max());
        }
        if (minY > maxY) return;
        int firstRow = Math.Max(0, (int)Math.Floor(minY / CellSize - 0.5));
        int lastRow = Math.Min(Rows - 1, (int)Math.Ceiling(maxY / CellSize - 0.5));
        var crossings = new List<double>(16);
        for (int row = firstRow; row <= lastRow; row++)
        {
            double y = (row + 0.5) * CellSize;
            crossings.Clear();
            for (int r = 0; r < ringXs.Length; r++)
            {
                double[] xs = ringXs[r], ys = ringYs[r];
                if (xs.Length < 3) continue;
                for (int i = 0, j = xs.Length - 1; i < xs.Length; j = i++)
                {
                    if ((ys[i] > y) != (ys[j] > y))
                        crossings.Add(xs[i] + (y - ys[i]) * (xs[j] - xs[i]) / (ys[j] - ys[i]));
                }
            }
            if (crossings.Count < 2) continue;
            crossings.Sort();
            for (int k = 0; k + 1 < crossings.Count; k += 2)
            {
                int start = Math.Max(0, (int)Math.Ceiling(crossings[k] / CellSize - 0.5));
                int end = Math.Min(Columns - 1, (int)Math.Floor(crossings[k + 1] / CellSize - 0.5));
                if (end >= start) walkable.AsSpan(row * Columns + start, end - start + 1).Fill(isWalkable);
            }
        }
    }

    /// <summary>Marks every cell walkable (open ground) before obstacles are drawn.</summary>
    public void Fill(bool isWalkable) => Array.Fill(walkable, isWalkable);

    /// <summary>Nearest walkable cell center within maxDistance, or null.</summary>
    public (double X, double Y)? FindNearestWalkable(double x, double y, double maxDistance)
    {
        int centerColumn = Math.Clamp((int)(x / CellSize), 0, Columns - 1);
        int centerRow = Math.Clamp((int)(y / CellSize), 0, Rows - 1);
        int reach = (int)Math.Ceiling(maxDistance / CellSize);
        double bestDistance = double.MaxValue;
        (double X, double Y)? best = null;
        for (int row = Math.Max(0, centerRow - reach); row <= Math.Min(Rows - 1, centerRow + reach); row++)
        {
            for (int column = Math.Max(0, centerColumn - reach); column <= Math.Min(Columns - 1, centerColumn + reach); column++)
            {
                if (!walkable[row * Columns + column]) continue;
                double cx = (column + 0.5) * CellSize, cy = (row + 0.5) * CellSize;
                double distance = (cx - x) * (cx - x) + (cy - y) * (cy - y);
                if (distance < bestDistance && distance <= maxDistance * maxDistance)
                {
                    bestDistance = distance;
                    best = (cx, cy);
                }
            }
        }
        return best;
    }

    public MapScenario Build(
        double originLatitude,
        double originLongitude,
        double metersPerDegreeLatitude,
        double metersPerDegreeLongitude,
        MapExit[] exits,
        MapSpawnZone[] spawnZones)
    {
        var blocked = new bool[walkable.Length];
        for (int i = 0; i < walkable.Length; i++) blocked[i] = !walkable[i];
        return new MapScenario(originLatitude, originLongitude, metersPerDegreeLatitude, metersPerDegreeLongitude,
            CellSize, Columns, Rows, blocked, exits, spawnZones);
    }

    private void SetSegment(double ax, double ay, double bx, double by, double halfWidth, bool isWalkable, bool[]? onlyWhere)
    {
        double minX = Math.Min(ax, bx) - halfWidth, maxX = Math.Max(ax, bx) + halfWidth;
        double minY = Math.Min(ay, by) - halfWidth, maxY = Math.Max(ay, by) + halfWidth;
        int firstColumn = Math.Max(0, (int)Math.Floor(minX / CellSize));
        int lastColumn = Math.Min(Columns - 1, (int)Math.Floor(maxX / CellSize));
        int firstRow = Math.Max(0, (int)Math.Floor(minY / CellSize));
        int lastRow = Math.Min(Rows - 1, (int)Math.Floor(maxY / CellSize));
        double dx = bx - ax, dy = by - ay;
        double lengthSquared = dx * dx + dy * dy;
        double limit = halfWidth * halfWidth;
        for (int row = firstRow; row <= lastRow; row++)
        {
            double cy = (row + 0.5) * CellSize;
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                double cx = (column + 0.5) * CellSize;
                double t = lengthSquared > 1e-12 ? Math.Clamp(((cx - ax) * dx + (cy - ay) * dy) / lengthSquared, 0, 1) : 0;
                double nx = ax + t * dx - cx, ny = ay + t * dy - cy;
                int index = row * Columns + column;
                if (nx * nx + ny * ny <= limit && (onlyWhere is null || onlyWhere[index])) walkable[index] = isWalkable;
            }
        }
    }
}
