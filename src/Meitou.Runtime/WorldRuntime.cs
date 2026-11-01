using Meitou.Simulation;

namespace Meitou.Runtime;

/// <summary>
/// World ownership and fixed-step execution shared by graphical and dedicated hosts. No camera, window or GPU dependency.
/// Hosts serialize calls; a failed tick is terminal until a world replacement succeeds.
/// </summary>
public sealed class WorldRuntime : IDisposable
{
    public const double TickSeconds = 1.0 / 30;
    readonly WorldSettings settings;
    readonly IWalkability walkability;
    bool disposed, faulted;
    double pendingSeconds;
    double speed = 1;
    readonly List<(World World, IRuntimeWorldService Service)> pendingSuspensions = [];
    readonly Lock checkpointGate = new();
    long checkpointOwner;

    public WorldRuntime(WorldSettings settings, IWalkability walkability, IEnumerable<ITickSystem>? systems = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(walkability);
        if (!float.IsFinite(settings.TickSeconds) || Math.Abs(settings.TickSeconds - TickSeconds) > 1e-6)
            throw new ArgumentException("The authoritative simulation requires a 30 Hz step.", nameof(settings));
        this.settings = settings;
        this.walkability = walkability;
        World = MakeWorld(systems);
        try { Bind(World); }
        catch
        {
            try { Unbind(World); } catch (Exception) { }
            try { Discard(World); } catch (Exception) { }
            throw;
        }
    }

    public World World { get; private set; }
    public bool Faulted => faulted;
    public double PendingGameSeconds => pendingSeconds;
    public double Speed
    {
        get => speed;
        set
        {
            if (value is not (0 or 1 or 2 or 5)) throw new ArgumentOutOfRangeException(nameof(value));
            speed = value;
        }
    }

    /// <summary>
    /// Dedicated-host scheduler. Budget limits work per call but never discards owed simulation steps.
    /// A paused call neither accumulates wall time nor drains existing debt. UI/authority commands have a separate boundary.
    /// </summary>
    public int Advance(double realSeconds, int maxTicks = 8)
    {
        EnsureUsable();
        if (!double.IsFinite(realSeconds) || realSeconds < 0) throw new ArgumentOutOfRangeException(nameof(realSeconds));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicks, 1);
        if (speed == 0) { ApplyPausedInterfaceCommands(); return 0; }
        double next = pendingSeconds + realSeconds * speed;
        if (!double.IsFinite(next)) throw new ArgumentOutOfRangeException(nameof(realSeconds));
        pendingSeconds = next;
        int ran = 0;
        while (ran < maxTicks && pendingSeconds + 1e-10 >= TickSeconds)
        {
            Step();
            pendingSeconds = Math.Max(0, pendingSeconds - TickSeconds);
            ran++;
        }
        return ran;
    }

    /// <summary>One complete tick. The graphical host may supply its existing calendar values; no scheduler debt is consumed.</summary>
    public void Step(bool publish = true, double? gameHours = null, bool? daytime = null)
    {
        EnsureUsable();
        if (gameHours is { } hours)
        {
            if (!double.IsFinite(hours)) throw new ArgumentOutOfRangeException(nameof(gameHours));
            World.GameHours = hours;
        }
        double hour = ((World.GameHours % 24) + 24) % 24;
        float sunrise = settings.Sunrise, sunset = settings.Sunset;
        if (sunset <= sunrise) (sunrise, sunset) = (6, 20);
        World.Daytime = daytime ?? (hour > sunrise && hour < sunset);
        try { World.RunTick(publish); }
        catch { faulted = true; World.Authority.MarkFaulted(); throw; }
    }

    public void ApplyPausedInterfaceCommands()
    {
        EnsureUsable();
        try { World.ApplyPausedInterfaceCommands(); }
        catch { faulted = true; World.Authority.MarkFaulted(); throw; }
    }

    public void Synchronize()
    {
        EnsureUsable();
        try { World.Authority.ProcessPending(); OnBoundary(World); }
        catch { faulted = true; World.Authority.MarkFaulted(); throw; }
    }

    /// <summary>Builds and initializes a replacement before releasing the current world. Setup failure keeps the old world.</summary>
    public World ReplaceWorld(IEnumerable<ITickSystem> systems, Action<World>? setup = null)
    {
        lock (checkpointGate) return ReplaceWorldCore(systems, setup);
    }

    World ReplaceWorldCore(IEnumerable<ITickSystem> systems, Action<World>? setup)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var next = MakeWorld(systems, attachDoors: false);
        try { setup?.Invoke(next); }
        catch
        {
            // Preserve the setup error while still releasing all candidate resources.
            try { Discard(next); } catch (Exception) { }
            throw;
        }
        var old = World;
        bool wasFaulted = faulted;
        try { Unbind(old); Bind(next); }
        catch (Exception activation)
        {
            var errors = new List<Exception> { activation };
            try { Unbind(next); } catch (Exception e) { errors.Add(e); }
            try { Discard(next); } catch (Exception e) { errors.Add(e); }
            try { Bind(old); faulted = wasFaulted; }
            catch (Exception e) { faulted = true; errors.Add(e); }
            throw new AggregateException("Could not activate the replacement world.", errors);
        }
        World = next;
        pendingSeconds = 0;
        faulted = false;
        if (walkability is Meitou.Simulation.Locks.IDoorGate gate)
        {
            gate.ResetDoors();
            next.Fixtures.Doors = gate;
        }
        Discard(old);
        return next;
    }

    World MakeWorld(IEnumerable<ITickSystem>? systems, bool attachDoors = true)
    {
        var world = new World(settings, walkability, systems);
        if (attachDoors) world.Fixtures.Doors = walkability as Meitou.Simulation.Locks.IDoorGate;
        return world;
    }

    void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (faulted) throw new InvalidOperationException("The world tick failed; restore a consistent world before continuing.");
        World.RequireNoDurableMutation();
    }

    internal long ClaimCheckpointOwner(World expected)
    {
        lock (checkpointGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            World.RequireNoDurableMutation();
            if (!ReferenceEquals(World, expected)) throw new InvalidOperationException("The checkpoint world has changed.");
            return Interlocked.Increment(ref checkpointOwner);
        }
    }

    internal void VerifyCheckpointOwner(World expected, long owner)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(World, expected) || Interlocked.Read(ref checkpointOwner) != owner)
            throw new InvalidOperationException("The checkpoint producer belongs to a replaced world or session.");
    }

    internal void FaultDurableMutation()
    {
        faulted = true;
        World.Authority.MarkFaulted();
    }

    internal T WithCheckpointOwner<T>(World expected, long owner, Func<T> operation)
    {
        lock (checkpointGate)
        {
            VerifyCheckpointOwner(expected, owner);
            return operation();
        }
    }

    void Bind(World world)
    {
        DrainSuspensions();
        world.RuntimeBoundary -= OnBoundary;
        world.RuntimeBoundary += OnBoundary;
        OnBoundary(world);
    }

    void OnBoundary(World world)
    {
        try
        {
            foreach (var service in world.Systems.OfType<IRuntimeWorldService>()) service.Synchronize(world);
        }
        catch { faulted = true; world.Authority.MarkFaulted(); throw; }
    }

    void Unbind(World world)
    {
        world.RuntimeBoundary -= OnBoundary;
        List<Exception>? errors = null;
        foreach (var service in world.Systems.OfType<IRuntimeWorldService>())
            try
            {
                service.Suspend(world);
                pendingSuspensions.RemoveAll(p => ReferenceEquals(p.World, world) && ReferenceEquals(p.Service, service));
            }
            catch (Exception e)
            {
                if (!pendingSuspensions.Any(p => ReferenceEquals(p.World, world) && ReferenceEquals(p.Service, service))) pendingSuspensions.Add((world, service));
                (errors ??= []).Add(e);
            }
        if (errors is not null) throw new AggregateException("Failed to suspend world services.", errors);
    }

    void DrainSuspensions()
    {
        List<Exception>? errors = null;
        for (int i = pendingSuspensions.Count - 1; i >= 0; i--)
        {
            var pending = pendingSuspensions[i];
            try { pending.Service.Suspend(pending.World); pendingSuspensions.RemoveAt(i); }
            catch (Exception e) { (errors ??= []).Add(e); }
        }
        if (errors is not null) throw new AggregateException("External world cleanup must finish before activation.", errors);
    }

    static void Discard(World world)
    {
        List<Exception>? errors = null;
        try { world.Dispose(); } catch (Exception e) { (errors ??= []).Add(e); }
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var system in world.Systems)
            if (system is IDisposable disposable && seen.Add(system))
                try { disposable.Dispose(); } catch (Exception e) { (errors ??= []).Add(e); }
        if (errors is not null) throw new AggregateException("Failed to release world resources.", errors);
    }

    public void Dispose()
    {
        lock (checkpointGate) DisposeCore();
    }

    void DisposeCore()
    {
        if (disposed) return;
        disposed = true;
        Exception? failure = null;
        try { Unbind(World); } catch (Exception e) { failure = e; }
        try { DrainSuspensions(); } catch (Exception e) { failure = failure is null ? e : new AggregateException(failure, e); }
        try { Discard(World); }
        catch (Exception e) { if (failure is not null) throw new AggregateException(failure, e); throw; }
        if (failure is not null) throw failure;
    }
}
