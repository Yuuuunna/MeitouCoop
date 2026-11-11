using Meitou.Simulation.Bodies;
using Meitou.Simulation.Locks;
using Meitou.Simulation;

namespace Meitou.Runtime;

/// <summary>
/// World ownership and fixed-step execution shared by graphical and dedicated hosts. No camera, window or GPU dependency.
/// Hosts serialize calls; a failed tick is terminal until a world replacement succeeds.
/// </summary>
public sealed class WorldRuntime : IDisposable
{
    public const double TickSeconds = 1.0 / 30;
    readonly WorldSettings _settings;
    readonly IWalkability _walkability;
    bool _disposed, _faulted;
    double _pendingSeconds;
    double _speed = 1;
    readonly List<(World World, IRuntimeWorldService Service)> _pendingSuspensions = [];
    readonly Lock _checkpointGate = new();
    long _checkpointOwner;
    public WorldRuntime(WorldSettings settings, IWalkability walkability, IEnumerable<ITickSystem>? systems = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(walkability);
        if (!float.IsFinite(settings.TickSeconds) || Math.Abs(settings.TickSeconds - TickSeconds) > 1e-6)
            throw new ArgumentException("The authoritative simulation requires a 30 Hz step.", nameof(settings));
        this._settings = settings;
        this._walkability = walkability;
        World = MakeWorld(systems);
        try
        {
            Bind(World);
        }
        catch
        {
            try
            {
                Unbind(World);
            }
            catch (Exception)
            {
            }

            try
            {
                Discard(World);
            }
            catch (Exception)
            {
            }

            throw;
        }
    }

    public World World { get; private set; }
    public bool Faulted => _faulted;
    public double PendingGameSeconds => _pendingSeconds;

    public double Speed
    {
        get => _speed;
        set
        {
            if (value is not (0 or 1 or 2 or 5))
                throw new ArgumentOutOfRangeException(nameof(value));
            _speed = value;
        }
    }

    /// <summary>
    /// Dedicated-host scheduler. Budget limits work per call but never discards owed simulation steps.
    /// A paused call neither accumulates wall time nor drains existing debt. UI/authority commands have a separate boundary.
    /// </summary>
    public int Advance(double realSeconds, int maxTicks = 8)
    {
        EnsureUsable();
        if (!double.IsFinite(realSeconds) || realSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(realSeconds));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicks, 1);
        if (_speed == 0)
        {
            ApplyPausedInterfaceCommands();
            return 0;
        }

        double next = _pendingSeconds + realSeconds * _speed;
        if (!double.IsFinite(next))
            throw new ArgumentOutOfRangeException(nameof(realSeconds));
        _pendingSeconds = next;
        int ran = 0;
        while (ran < maxTicks && _pendingSeconds + 1e-10 >= TickSeconds)
        {
            Step();
            _pendingSeconds = Math.Max(0, _pendingSeconds - TickSeconds);
            ran++;
        }

        return ran;
    }

    /// <summary>
    /// One complete tick. The graphical host may supply its existing calendar values; no scheduler debt is consumed.
    /// </summary>
    public void Step(bool publish = true, double? gameHours = null, bool? daytime = null)
    {
        EnsureUsable();
        if (gameHours is { } hours)
        {
            if (!double.IsFinite(hours))
                throw new ArgumentOutOfRangeException(nameof(gameHours));
            World.GameHours = hours;
        }

        double hour = ((World.GameHours % 24) + 24) % 24;
        float sunrise = _settings.Sunrise, sunset = _settings.Sunset;
        if (sunset <= sunrise)
            (sunrise, sunset) = (6, 20);
        World.Daytime = daytime ?? (hour > sunrise && hour < sunset);
        try
        {
            World.RunTick(publish);
        }
        catch
        {
            _faulted = true;
            World.Authority.MarkFaulted();
            throw;
        }
    }

    public void ApplyPausedInterfaceCommands()
    {
        EnsureUsable();
        try
        {
            World.ApplyPausedInterfaceCommands();
        }
        catch
        {
            _faulted = true;
            World.Authority.MarkFaulted();
            throw;
        }
    }

    public void Synchronize()
    {
        EnsureUsable();
        try
        {
            World.Authority.ProcessPending();
            OnBoundary(World);
        }
        catch
        {
            _faulted = true;
            World.Authority.MarkFaulted();
            throw;
        }
    }

    /// <summary>
    /// Builds and initializes a replacement before releasing the current world. Setup failure keeps the old world.
    /// </summary>
    public World ReplaceWorld(IEnumerable<ITickSystem> systems, Action<World>? setup = null)
    {
        lock (_checkpointGate)
            return ReplaceWorldCore(systems, setup);
    }

    World ReplaceWorldCore(IEnumerable<ITickSystem> systems, Action<World>? setup)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = MakeWorld(systems, attachDoors: false);
        try
        {
            setup?.Invoke(next);
        }
        catch
        {
            // Preserve the setup error while still releasing all candidate resources.
            try
            {
                Discard(next);
            }
            catch (Exception)
            {
            }

            throw;
        }

        var old = World;
        bool wasFaulted = _faulted;
        try
        {
            Unbind(old);
            Bind(next);
        }
        catch (Exception activation)
        {
            var errors = new List<Exception>
            {
                activation
            };
            try
            {
                Unbind(next);
            }
            catch (Exception e)
            {
                errors.Add(e);
            }

            try
            {
                Discard(next);
            }
            catch (Exception e)
            {
                errors.Add(e);
            }

            try
            {
                Bind(old);
                _faulted = wasFaulted;
            }
            catch (Exception e)
            {
                _faulted = true;
                errors.Add(e);
            }

            throw new AggregateException("Could not activate the replacement world.", errors);
        }

        World = next;
        _pendingSeconds = 0;
        _faulted = false;
        if (_walkability is Meitou.Simulation.Locks.IDoorGate gate)
        {
            gate.ResetDoors();
            next.Fixtures.Doors = gate;
        }

        Discard(old);
        return next;
    }

    World MakeWorld(IEnumerable<ITickSystem>? systems, bool attachDoors = true)
    {
        var world = new World(_settings, _walkability, systems);
        if (attachDoors)
            world.Fixtures.Doors = _walkability as Meitou.Simulation.Locks.IDoorGate;
        return world;
    }

    void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
            throw new InvalidOperationException("The world tick failed; restore a consistent world before continuing.");
        World.RequireNoDurableMutation();
    }

    internal long ClaimCheckpointOwner(World expected)
    {
        lock (_checkpointGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            World.RequireNoDurableMutation();
            if (!ReferenceEquals(World, expected))
                throw new InvalidOperationException("The checkpoint world has changed.");
            return Interlocked.Increment(ref _checkpointOwner);
        }
    }

    internal void VerifyCheckpointOwner(World expected, long owner)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(World, expected) || Interlocked.Read(ref _checkpointOwner) != owner)
            throw new InvalidOperationException("The checkpoint producer belongs to a replaced world or session.");
    }

    internal void FaultDurableMutation()
    {
        _faulted = true;
        World.Authority.MarkFaulted();
    }

    internal T WithCheckpointOwner<T>(World expected, long owner, Func<T> operation)
    {
        lock (_checkpointGate)
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
            foreach (var service in world.Systems.OfType<IRuntimeWorldService>())
                service.Synchronize(world);
        }
        catch
        {
            _faulted = true;
            world.Authority.MarkFaulted();
            throw;
        }
    }

    void Unbind(World world)
    {
        world.RuntimeBoundary -= OnBoundary;
        List<Exception>? errors = null;
        foreach (var service in world.Systems.OfType<IRuntimeWorldService>())
            try
            {
                service.Suspend(world);
                _pendingSuspensions.RemoveAll(p => ReferenceEquals(p.World, world) && ReferenceEquals(p.Service, service));
            }
            catch (Exception e)
            {
                if (!_pendingSuspensions.Any(p => ReferenceEquals(p.World, world) && ReferenceEquals(p.Service, service)))
                    _pendingSuspensions.Add((world, service));
                (errors ??= []).Add(e);
            }

        if (errors is not null)
            throw new AggregateException("Failed to suspend world services.", errors);
    }

    void DrainSuspensions()
    {
        List<Exception>? errors = null;
        for (int i = _pendingSuspensions.Count - 1; i >= 0; i--)
        {
            var pending = _pendingSuspensions[i];
            try
            {
                pending.Service.Suspend(pending.World);
                _pendingSuspensions.RemoveAt(i);
            }
            catch (Exception e)
            {
                (errors ??= []).Add(e);
            }
        }

        if (errors is not null)
            throw new AggregateException("External world cleanup must finish before activation.", errors);
    }

    static void Discard(World world)
    {
        List<Exception>? errors = null;
        try
        {
            world.Dispose();
        }
        catch (Exception e)
        {
            (errors ??= []).Add(e);
        }

        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var system in world.Systems)
            if (system is IDisposable disposable && seen.Add(system))
                try
                {
                    disposable.Dispose();
                }
                catch (Exception e)
                {
                    (errors ??= []).Add(e);
                }

        if (errors is not null)
            throw new AggregateException("Failed to release world resources.", errors);
    }

    public void Dispose()
    {
        lock (_checkpointGate)
            DisposeCore();
    }

    void DisposeCore()
    {
        if (_disposed)
            return;
        _disposed = true;
        Exception? failure = null;
        try
        {
            Unbind(World);
        }
        catch (Exception e)
        {
            failure = e;
        }

        try
        {
            DrainSuspensions();
        }
        catch (Exception e)
        {
            failure = failure is null ? e : new AggregateException(failure, e);
        }

        try
        {
            Discard(World);
        }
        catch (Exception e)
        {
            if (failure is not null)
                throw new AggregateException(failure, e);
            throw;
        }

        if (failure is not null)
            throw failure;
    }
}