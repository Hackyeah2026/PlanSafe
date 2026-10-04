using System.Diagnostics;
using System.Text.Json;
using PlanSafe.Contracts.Simulation;
using NativeEngine = PlanSafe.App.Simulation.CrowdSimulationEngine;
using ProbeEngine = PerfProbe.CrowdSimulationEngine;

var output = new List<object>();
foreach (var (name, people, granulation, spread, walls) in new[] {
    ("sparse-10k",10000,1,700.0,false),
    ("dense-10k",10000,1,60.0,false),
    ("city-10k",10000,1,700.0,true),
    ("city-16k-g1",16000,1,700.0,true),
    ("city-16k-g3",16000,3,700.0,true),
    ("city-16k-g16",16000,16,700.0,true),
})
{
    const double cell = 2.5;
    const int columns = 600, rows = 400;
    var blocked = new bool[columns * rows];
    if (walls) for (int r = 0; r < rows; r++) for (int c = 0; c < columns; c++)
        blocked[r * columns + c] = c % 40 >= 10 && c % 40 <= 30 && r % 40 >= 10 && r % 40 <= 30;
    var scenario = new MapScenario(50, 19, 111320, 71555, cell, columns, rows, blocked,
        [new MapExit(1490, 990, 5)], [new MapSpawnZone([5, 800, 800, 5], [5, 5, 800, 800], people)]);
    var random = new Random(42);
    var positions = new List<(double X, double Y)>();
    while (positions.Count < people)
    {
        double x = 5 + random.NextDouble() * spread, y = 5 + random.NextDouble() * spread;
        if (scenario.IsBlockedAt(x, y)) continue;
        positions.Add((x, y));
    }
    var native = await NativeEngine.CreateMapAsync(scenario, positions, granulation, seed: 42);
    var probe = await ProbeEngine.CreateMapAsync(scenario, positions, granulation, seed: 42);
    var variant = granulation == 16 ? await PerfVariant.CrowdSimulationEngine.CreateMapAsync(scenario, positions, granulation, seed: 42) : null;
    for (int i = 0; i < 30; i++) { native.UpdatePhysics(.016); probe.UpdatePhysics(.016); variant?.UpdatePhysics(.016); }
    const int ticks = 80;
    var watch = new Stopwatch();
    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    watch.Start();
    for (int i = 0; i < ticks; i++) native.UpdatePhysics(.016);
    watch.Stop();
    double nativeMs = watch.Elapsed.TotalMilliseconds / ticks;
    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    double? variantMs = null;
    bool? identical = null;
    if (variant != null)
    {
        watch.Restart(); for (int i = 0; i < ticks; i++) variant.UpdatePhysics(.016); watch.Stop();
        variantMs = watch.Elapsed.TotalMilliseconds / ticks;
        identical = native.AgentPositionX.SequenceEqual(variant.AgentPositionX) && native.AgentPositionY.SequenceEqual(variant.AgentPositionY) && native.PotentialFieldMap.SmoothedDensity.SequenceEqual(variant.PotentialFieldMap.SmoothedDensity);
    }
    PerfProbe.Profile.Reset();
    watch.Restart();
    for (int i = 0; i < ticks; i++) probe.UpdatePhysics(.016);
    watch.Stop();
    var entry = new
    {
        name,
        people,
        granulation,
        agents = native.ActiveAgentCount,
        nativeMs,
        simulatedSpeed = .016 / (nativeMs / 1000),
        allocatedPerTick = allocated / ticks,
        variantMs,
        identical,
        instrumentedMs = watch.Elapsed.TotalMilliseconds / ticks,
        phases = PerfProbe.Profile.Names.Select((n, i) => new { name = n, ms = PerfProbe.Profile.Ticks[i] * 1000.0 / Stopwatch.Frequency / ticks }).ToArray()
    };
    output.Add(entry);
    Console.WriteLine(JsonSerializer.Serialize(entry));
    if (name == "city-16k-g1" || name == "city-16k-g3" || name == "city-16k-g16" || name == "dense-10k" || name == "sparse-10k")
    {
        var snap = PlanSafe.App.Simulation.MapGpuSnapshot.Capture(native);
        File.WriteAllText($".run/simulation-perf/{name}.json", JsonSerializer.Serialize(snap));
    }
}
File.WriteAllText(".run/simulation-perf/cpu-results.json", JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));

namespace PerfProbe
{
    public static class Profile
    {
        public static readonly string[] Names = ["density-grid", "hash-rebuild", "flow-and-walls", "neighbors-pass1", "density-resolution", "neighbors-pass2", "integration", "collision", "evacuations"];
        public static readonly long[] Ticks = new long[9];
        public static void Add(int phase, long start, int multiplier = 1) => Ticks[phase] += (Stopwatch.GetTimestamp() - start) * multiplier;
        public static void Reset() => Array.Clear(Ticks);
    }
}
