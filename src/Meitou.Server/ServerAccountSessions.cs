using System.Diagnostics;
using Meitou.Networking;
using Meitou.Runtime;
using Meitou.Simulation;

namespace Meitou.Server;
public sealed record AccountBindingPersistenceDiagnostics(double CaptureMilliseconds, double CommitMilliseconds);
public sealed record AuthenticatedPlayerSession(AuthenticatedAccount Account, AuthorityConnection Connection)
{
    internal World? SourceWorld { get; init; }
}

/// <summary>
/// World-owner authentication/binding boundary. Critical registration commits through the active world's checkpoint coordinator.
/// </summary>
public sealed class ServerAccountSessions
{
    readonly TokenAccountStore _accounts;
    readonly WorldRuntime _runtime;
    readonly Action _persistRegistration;
    readonly WorldCheckpointCoordinator? _checkpoints;
    readonly SemaphoreSlim _registration = new(1, 1);
    public ServerAccountSessions(TokenAccountStore accounts, WorldRuntime runtime, WorldCheckpointCoordinator checkpoints) : this(accounts, runtime, () =>
    {
        checkpoints.VerifyOwner(runtime);
        checkpoints.Commit(checkpoints.Capture());
    })
    {
        checkpoints.VerifyOwner(runtime);
        this._checkpoints = checkpoints;
    }

    internal ServerAccountSessions(TokenAccountStore accounts, WorldRuntime runtime, Action persistRegistration) => (this._accounts, this._runtime, this._persistRegistration) = (accounts, runtime, persistRegistration);
    internal Action<CheckpointStage>? RegistrationCommitObserver { get; init; }

    public AuthenticatedPlayerSession? OpenDevice(AuthenticationRequest request) => _accounts.RegisterDevice(request) ? Open(request) : null;
    public AuthenticatedPlayerSession? Open(AuthenticationRequest request)
    {
        var principal = _accounts.Authenticate(request);
        if (principal is null)
            return null;
        var world = _runtime.World;
        if (!world.AtTickBoundary)
            throw new InvalidOperationException("Authentication binding requires the world boundary.");
        if (world.HasPendingDurableMutation)
        {
            var existing = world.Players.FindAccount(principal.Identity);
            if (existing is null || world.Players.AccountRequiresCheckpoint(principal.Identity))
                throw new InvalidOperationException("New account binding is unavailable during a durable transaction.");
            return _accounts.IsCurrent(principal) ? new(principal, world.Authority.Open(existing.Id))
            {
                SourceWorld = world
            }

            : null;
        }

        var(player, _) = world.Players.ResolveAccount(principal.Identity);
        if (world.Players.AccountRequiresCheckpoint(principal.Identity))
        {
            _persistRegistration();
            if (!ReferenceEquals(_runtime.World, world))
                throw new InvalidOperationException("World changed while persisting account registration.");
            world.Players.ConfirmAccountCheckpoint(principal.Identity);
        }

        if (!_accounts.IsCurrent(principal))
            return null;
        world.ResetPlayerInteractions(player.Id);
        return new(principal, world.Authority.Open(player.Id))
        {
            SourceWorld = world
        };
    }

    /// <summary>Credential IO and detached checkpoint commit run off owner. The account is confirmed and
    /// its authority connection opened only after successful durable commit and current-principal checks.</summary>
    public async Task<AuthenticatedPlayerSession?> OpenAsync(ServerWorldLoop loop, AuthenticationRequest request, bool device, Func<Task> persistencePending, CancellationToken cancellation, Action<AccountBindingPersistenceDiagnostics>? persistenceObserved = null)
    {
        var principal = await Task.Run(() => device && !_accounts.RegisterDevice(request) ? null : _accounts.Authenticate(request), cancellation).ConfigureAwait(false);
        if (principal is null)
            return null;
        if (_checkpoints is null)
            return await loop.InvokeAsync(() => Open(request), cancellation).ConfigureAwait(false);
        bool announced = false;
        async Task Announce()
        {
            if (announced)
                return;
            announced = true;
            await persistencePending().ConfigureAwait(false);
        }

        await Announce().ConfigureAwait(false);
        if (!_registration.Wait(0))
        {
            await Announce().ConfigureAwait(false);
            await _registration.WaitAsync(cancellation).ConfigureAwait(false);
        }

        try
        {
            var prepared = await loop.InvokeAsync(() =>
            {
                var world = _runtime.World;
                if (world.HasPendingDurableMutation)
                {
                    var existing = world.Players.FindAccount(principal.Identity);
                    if (existing is null || world.Players.AccountRequiresCheckpoint(principal.Identity))
                        throw new InvalidOperationException("New account binding is unavailable during a durable transaction.");
                    return (World: world, Player: existing.Id, Persist: false);
                }

                var player = world.Players.ResolveAccount(principal.Identity).Player;
                return (World: world, Player: player.Id, Persist: world.Players.AccountRequiresCheckpoint(principal.Identity));
            }, cancellation).ConfigureAwait(false);
            if (prepared.Persist)
            {
                await Announce().ConfigureAwait(false);
                var captured = await loop.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(_runtime.World, prepared.World))
                        throw new InvalidOperationException("World changed before account registration capture.");
                    if (prepared.World.HasPendingDurableMutation)
                        throw new InvalidOperationException("A durable transaction started before registration capture.");
                    long started = Stopwatch.GetTimestamp();
                    var checkpoint = _checkpoints.Capture();
                    return (Checkpoint: checkpoint, Milliseconds: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }, cancellation).ConfigureAwait(false);
                // A captured durable registration is completed even when its caller stops waiting.
                // The coordinator serializes commits and rejects stale captures; no bespoke save writer is used.
                long committing = Stopwatch.GetTimestamp();
                await Task.Run(() => _checkpoints.Commit(captured.Checkpoint, RegistrationCommitObserver)).ConfigureAwait(false);
                try
                {
                    persistenceObserved?.Invoke(new(captured.Milliseconds, Stopwatch.GetElapsedTime(committing).TotalMilliseconds));
                }
                catch
                { /* Numeric observations cannot change binding durability. */
                }
            }

            return await loop.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_runtime.World, prepared.World))
                    throw new InvalidOperationException("World changed while persisting account registration.");
                if (prepared.Persist)
                    prepared.World.Players.ConfirmAccountCheckpoint(principal.Identity);
                if (cancellation.IsCancellationRequested || !_accounts.IsCurrent(principal))
                    return null;
                if (!prepared.World.HasPendingDurableMutation)
                    prepared.World.ResetPlayerInteractions(prepared.Player);
                return new AuthenticatedPlayerSession(principal, prepared.World.Authority.Open(prepared.Player))
                {
                    SourceWorld = prepared.World
                };
            }).ConfigureAwait(false);
        }
        finally
        {
            _registration.Release();
        }
    }

    public bool IsCurrent(AuthenticatedPlayerSession session) => _accounts.IsCurrent(session.Account) && _runtime.World.Authority.GetPhase(session.Connection)is not null;
}