using Meitou.Simulation;

namespace Meitou.Runtime;

/// <summary>Serialized acceptance transaction. Membership, transfers, optional creation approval and receipt commit in one checkpoint.</summary>
public sealed class PlayerInvitationService
{
    readonly WorldCheckpointCoordinator checkpoints;
    readonly World world;
    Pending? pending;
    sealed record Pending(PlayerId Recipient, Guid Invitation, World.DurableMutation Barrier)
    {
        public CapturedCheckpoint? Capture;
    }
    public PlayerInvitationService(WorldCheckpointCoordinator checkpoints)
    {
        this.checkpoints = checkpoints; world = checkpoints.Source;
        if (world.HasPendingDurableMutation) throw new InvalidOperationException("Invitation service requires an idle world.");
    }
    void Check(AuthorityConnection connection)
    {
        checkpoints.VerifyActive();
        if (world.Authority.GetPhase(connection) is null) throw new InvalidOperationException("Invitation connection was replaced.");
    }
    public FactionInvitation Offer(AuthorityConnection connection, FactionInviteRequest offer)
    {
        Check(connection); return world.Invitations.Offer(connection.Player, offer);
    }
    public FactionInvitation Accept(AuthorityConnection connection, Guid id)
    {
        Check(connection);
        if (pending is { } current)
        {
            if (current.Recipient != connection.Player || current.Invitation != id) throw new InvalidOperationException("An invitation is waiting for persistence.");
            return Persist(current);
        }
        world.RequireNoDurableMutation();
        var prepared = world.Invitations.PrepareAcceptance(connection.Player, id);
        if (prepared.Status == InvitationStatus.Accepted) return prepared;
        var barrier = checkpoints.BeginDurableMutation();
        try
        {
            world.Invitations.AcceptPrepared(prepared);
            world.AdvanceDurableRevision(barrier);
            pending = new(connection.Player, id, barrier);
        }
        catch { checkpoints.Runtime.FaultDurableMutation(); throw; }
        return Persist(pending);
    }
    public Guid? PendingFor(AuthorityConnection connection)
    {
        Check(connection); return pending is { } current && current.Recipient == connection.Player ? current.Invitation : null;
    }
    public FactionInvitation? Lookup(AuthorityConnection connection, Guid id)
    {
        Check(connection);
        if (world.HasPendingDurableMutation) return null; // in-memory accepted outcomes are not durable success
        return world.Invitations.FindFor(connection.Player, id);
    }
    FactionInvitation Persist(Pending operation)
    {
        operation.Capture ??= checkpoints.CaptureDurable(operation.Barrier);
        checkpoints.Commit(operation.Capture); operation.Barrier.Complete(); pending = null;
        return world.Invitations.FindFor(operation.Recipient, operation.Invitation)!;
    }
}
