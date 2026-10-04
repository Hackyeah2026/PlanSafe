using System.Diagnostics;

namespace PlanSafe.App.Simulation;

/// <summary>Cooperatively schedules preparation on single-threaded browser WASM.</summary>
public sealed class PreparationScheduler
{
    private readonly Stopwatch _slice = Stopwatch.StartNew();

    public async ValueTask YieldAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_slice.ElapsedMilliseconds < 8) return;
        // Task.Yield can resume through microtasks without allowing a browser paint.
        await Task.Delay(1, cancellationToken);
        _slice.Restart();
    }
}
