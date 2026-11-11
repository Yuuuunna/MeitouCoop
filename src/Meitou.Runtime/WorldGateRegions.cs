using Meitou.Simulation.Navigation;
using Meitou.Simulation;
using Meitou.Simulation.Locks;

namespace Meitou.Runtime;

/// <summary>
/// Builds gate connectivity from captured navigation inputs, publishing only at a matching world's boundary.
/// </summary>
public sealed class WorldGateRegions(Func<GateRegionBuild> capture) : IRuntimeWorldService, IDisposable
{
    World? _active;
    object? _applied;
    Job? _job;
    bool _disposed;
    public WorldGateRegions(NavSystem navigation) : this(navigation.CaptureGateRegions)
    {
    }

    sealed record Job(World Owner, GateRegionBuild Input, CancellationTokenSource Cancellation, Task<IGateRegions> Result);
    public bool Pending => _job is not null;

    public void Synchronize(World world)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (world.HasPendingDurableMutation)
            return;
        if (_active is not null && !ReferenceEquals(_active, world))
            throw new InvalidOperationException("Gate regions still belong to another world.");
        _active = world;
        var locks = world.Systems.OfType<LockSystem>().ToArray();
        if (locks.Length == 0)
            return;
        var input = capture();
        if (ReferenceEquals(_applied, input.Identity))
            return;
        _applied = null;
        foreach (var system in locks)
            system.SetGateRegions(null, pending: true);
        if (_job is { } previous)
        {
            bool current = ReferenceEquals(previous.Owner, world) && ReferenceEquals(previous.Input.Identity, input.Identity);
            if (!current)
                previous.Cancellation.Cancel();
            if (!previous.Result.IsCompleted)
                return;
            _job = null;
            try
            {
                if (current)
                {
                    var regions = previous.Result.GetAwaiter().GetResult() ?? throw new InvalidDataException("Missing gate regions.");
                    foreach (var system in locks)
                        system.SetGateRegions(regions);
                    _applied = input.Identity;
                    return;
                }

                _ = previous.Result.Exception; // observe a failed obsolete job without publishing it
            }
            finally
            {
                previous.Cancellation.Dispose();
            }
        }

        var cancellation = new CancellationTokenSource();
        _job = new(world, input, cancellation, Task.Run(() => input.Build(cancellation.Token), cancellation.Token));
    }

    public void Suspend(World world)
    {
        if (!ReferenceEquals(_active, world))
            return;
        _job?.Cancellation.Cancel();
        _applied = null;
        _active = null;
        foreach (var system in world.Systems.OfType<LockSystem>())
            system.SetGateRegions(null, pending: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_job is not { } pending)
            return;
        pending.Cancellation.Cancel();
        try
        {
            pending.Result.GetAwaiter().GetResult();
        }
        catch (Exception)
        { /* Closing cannot publish this result; active failures are surfaced by Synchronize. */
        }
        finally
        {
            pending.Cancellation.Dispose();
            _job = null;
        }
    }
}