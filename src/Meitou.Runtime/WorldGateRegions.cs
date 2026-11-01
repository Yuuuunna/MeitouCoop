using Meitou.Navigation;
using Meitou.Simulation;
using Meitou.Simulation.Locks;

namespace Meitou.Runtime;

/// <summary>Builds gate connectivity from captured navigation inputs, publishing only at a matching world's boundary.</summary>
public sealed class WorldGateRegions(Func<GateRegionBuild> capture) : IRuntimeWorldService, IDisposable
{
    sealed record Job(World Owner, GateRegionBuild Input, CancellationTokenSource Cancellation, Task<IGateRegions> Result);
    World? active;
    object? applied;
    Job? job;
    bool disposed;
    public WorldGateRegions(NavSystem navigation) : this(navigation.CaptureGateRegions) { }
    public bool Pending => job is not null;

    public void Synchronize(World world)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (world.HasPendingDurableMutation) return;
        if (active is not null && !ReferenceEquals(active, world)) throw new InvalidOperationException("Gate regions still belong to another world.");
        active = world;
        var locks = world.Systems.OfType<LockSystem>().ToArray();
        if (locks.Length == 0) return;
        var input = capture();
        if (ReferenceEquals(applied, input.Identity)) return;
        applied = null;
        foreach (var system in locks) system.SetGateRegions(null, pending: true);
        if (job is { } previous)
        {
            bool current = ReferenceEquals(previous.Owner, world) && ReferenceEquals(previous.Input.Identity, input.Identity);
            if (!current) previous.Cancellation.Cancel();
            if (!previous.Result.IsCompleted) return;
            job = null;
            try
            {
                if (current)
                {
                    var regions = previous.Result.GetAwaiter().GetResult() ?? throw new InvalidDataException("Missing gate regions.");
                    foreach (var system in locks) system.SetGateRegions(regions);
                    applied = input.Identity; return;
                }
                _ = previous.Result.Exception; // observe a failed obsolete job without publishing it
            }
            finally { previous.Cancellation.Dispose(); }
        }
        var cancellation = new CancellationTokenSource();
        job = new(world, input, cancellation, Task.Run(() => input.Build(cancellation.Token), cancellation.Token));
    }

    public void Suspend(World world)
    {
        if (!ReferenceEquals(active, world)) return;
        job?.Cancellation.Cancel(); applied = null; active = null;
        foreach (var system in world.Systems.OfType<LockSystem>()) system.SetGateRegions(null, pending: true);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (job is not { } pending) return;
        pending.Cancellation.Cancel();
        try { pending.Result.GetAwaiter().GetResult(); }
        catch (Exception) { /* Closing cannot publish this result; active failures are surfaced by Synchronize. */ }
        finally { pending.Cancellation.Dispose(); job = null; }
    }
}
