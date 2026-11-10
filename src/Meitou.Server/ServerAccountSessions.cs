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

/// <summary>World-owner authentication/binding boundary. Critical registration commits through the active world's checkpoint coordinator.</summary>
public sealed class ServerAccountSessions
{
    readonly TokenAccountStore accounts;
    readonly WorldRuntime runtime;
    readonly Action persistRegistration;
    readonly WorldCheckpointCoordinator? checkpoints;
    readonly SemaphoreSlim registration = new(1, 1);
    internal Action<CheckpointStage>? RegistrationCommitObserver { get; init; }
    public ServerAccountSessions(TokenAccountStore accounts, WorldRuntime runtime, WorldCheckpointCoordinator checkpoints)
        : this(accounts, runtime, () => { checkpoints.VerifyOwner(runtime); checkpoints.Commit(checkpoints.Capture()); })
        { checkpoints.VerifyOwner(runtime); this.checkpoints = checkpoints; }
    internal ServerAccountSessions(TokenAccountStore accounts, WorldRuntime runtime, Action persistRegistration)
        => (this.accounts, this.runtime, this.persistRegistration) = (accounts, runtime, persistRegistration);

    public AuthenticatedPlayerSession? OpenDevice(AuthenticationRequest request) => accounts.RegisterDevice(request) ? Open(request) : null;

    public AuthenticatedPlayerSession? Open(AuthenticationRequest request)
    {
        var principal = accounts.Authenticate(request);
        if (principal is null) return null;
        var world = runtime.World;
        if (!world.AtTickBoundary) throw new InvalidOperationException("Authentication binding requires the world boundary.");
        if (world.HasPendingDurableMutation)
        {
            var existing = world.Players.FindAccount(principal.Identity);
            if (existing is null || world.Players.AccountRequiresCheckpoint(principal.Identity)) throw new InvalidOperationException("New account binding is unavailable during a durable transaction.");
            return accounts.IsCurrent(principal) ? new(principal, world.Authority.Open(existing.Id)) { SourceWorld = world } : null;
        }
        var (player, _) = world.Players.ResolveAccount(principal.Identity);
        if (world.Players.AccountRequiresCheckpoint(principal.Identity))
        {
            persistRegistration();
            if (!ReferenceEquals(runtime.World, world)) throw new InvalidOperationException("World changed while persisting account registration.");
            world.Players.ConfirmAccountCheckpoint(principal.Identity);
        }
        if (!accounts.IsCurrent(principal)) return null;
        world.ResetPlayerInteractions(player.Id);
        return new(principal, world.Authority.Open(player.Id)) { SourceWorld = world };
    }
    /// <summary>Credential IO and detached checkpoint commit run off owner. The account is confirmed and
    /// its authority connection opened only after successful durable commit and current-principal checks.</summary>
    public async Task<AuthenticatedPlayerSession?> OpenAsync(ServerWorldLoop loop, AuthenticationRequest request,
        bool device, Func<Task> persistencePending, CancellationToken cancellation, Action<AccountBindingPersistenceDiagnostics>? persistenceObserved = null)
    {
        var principal = await Task.Run(() => device && !accounts.RegisterDevice(request) ? null : accounts.Authenticate(request), cancellation).ConfigureAwait(false);
        if (principal is null) return null;
        if (checkpoints is null) return await loop.InvokeAsync(() => Open(request), cancellation).ConfigureAwait(false);
        bool announced = false;
        async Task Announce() { if (announced) return; announced = true; await persistencePending().ConfigureAwait(false); }
        await Announce().ConfigureAwait(false);
        if (!registration.Wait(0)) { await Announce().ConfigureAwait(false); await registration.WaitAsync(cancellation).ConfigureAwait(false); }
        try
        {
            var prepared = await loop.InvokeAsync(() =>
            {
                var world = runtime.World;
                if (world.HasPendingDurableMutation)
                {
                    var existing = world.Players.FindAccount(principal.Identity);
                    if (existing is null || world.Players.AccountRequiresCheckpoint(principal.Identity)) throw new InvalidOperationException("New account binding is unavailable during a durable transaction.");
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
                    if (!ReferenceEquals(runtime.World, prepared.World)) throw new InvalidOperationException("World changed before account registration capture.");
                    if (prepared.World.HasPendingDurableMutation) throw new InvalidOperationException("A durable transaction started before registration capture.");
                    long started = Stopwatch.GetTimestamp(); var checkpoint = checkpoints.Capture();
                    return (Checkpoint: checkpoint, Milliseconds: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }, cancellation).ConfigureAwait(false);
                // A captured durable registration is completed even when its caller stops waiting.
                // The coordinator serializes commits and rejects stale captures; no bespoke save writer is used.
                long committing = Stopwatch.GetTimestamp();
                await Task.Run(() => checkpoints.Commit(captured.Checkpoint, RegistrationCommitObserver)).ConfigureAwait(false);
                try { persistenceObserved?.Invoke(new(captured.Milliseconds, Stopwatch.GetElapsedTime(committing).TotalMilliseconds)); } catch { /* Numeric observations cannot change binding durability. */ }
            }
            return await loop.InvokeAsync(() =>
            {
                if (!ReferenceEquals(runtime.World, prepared.World)) throw new InvalidOperationException("World changed while persisting account registration.");
                if (prepared.Persist) prepared.World.Players.ConfirmAccountCheckpoint(principal.Identity);
                if (cancellation.IsCancellationRequested || !accounts.IsCurrent(principal)) return null;
                if (!prepared.World.HasPendingDurableMutation) prepared.World.ResetPlayerInteractions(prepared.Player);
                return new AuthenticatedPlayerSession(principal, prepared.World.Authority.Open(prepared.Player)) { SourceWorld = prepared.World };
            }).ConfigureAwait(false);
        }
        finally { registration.Release(); }
    }
    public bool IsCurrent(AuthenticatedPlayerSession session) => accounts.IsCurrent(session.Account)
        && runtime.World.Authority.GetPhase(session.Connection) is not null;
}
