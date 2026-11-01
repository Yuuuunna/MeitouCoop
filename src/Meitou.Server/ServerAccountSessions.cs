using Meitou.Networking;
using Meitou.Runtime;
using Meitou.Simulation;

namespace Meitou.Server;

public sealed record AuthenticatedPlayerSession(AuthenticatedAccount Account, AuthorityConnection Connection);

/// <summary>World-owner authentication/binding boundary. Critical registration commits through the active world's checkpoint coordinator.</summary>
public sealed class ServerAccountSessions
{
    readonly TokenAccountStore accounts;
    readonly WorldRuntime runtime;
    readonly Action persistRegistration;
    public ServerAccountSessions(TokenAccountStore accounts, WorldRuntime runtime, WorldCheckpointCoordinator checkpoints)
        : this(accounts, runtime, () => { checkpoints.VerifyOwner(runtime); checkpoints.Commit(checkpoints.Capture()); })
        => checkpoints.VerifyOwner(runtime);
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
            return accounts.IsCurrent(principal) ? new(principal, world.Authority.Open(existing.Id)) : null;
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
        return new(principal, world.Authority.Open(player.Id));
    }
    public bool IsCurrent(AuthenticatedPlayerSession session) => accounts.IsCurrent(session.Account)
        && runtime.World.Authority.GetPhase(session.Connection) is not null;
}
