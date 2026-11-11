using Meitou.Simulation;

namespace Meitou.Runtime;

/// <summary>
/// Serialized acceptance transaction. Membership, transfers, optional creation approval and receipt commit in one checkpoint.
/// </summary>
public sealed class PlayerInvitationService
{
    readonly WorldCheckpointCoordinator _checkpoints;
    readonly World _world;
    Pending? _pending;
    public PlayerInvitationService(WorldCheckpointCoordinator checkpoints)
    {
        this._checkpoints = checkpoints;
        _world = checkpoints.Source;
        if (_world.HasPendingDurableMutation)
            throw new InvalidOperationException("Invitation service requires an idle world.");
    }

    sealed record Pending(PlayerId Recipient, Guid Invitation, World.DurableMutation Barrier)
    {
        public CapturedCheckpoint? Capture;
    }

    void Check(AuthorityConnection connection)
    {
        _checkpoints.VerifyActive();
        if (_world.Authority.GetPhase(connection)is null)
            throw new InvalidOperationException("Invitation connection was replaced.");
    }

    public FactionInvitation Offer(AuthorityConnection connection, FactionInviteRequest offer)
    {
        Check(connection);
        return _world.Invitations.Offer(connection.Player, offer);
    }

    public FactionInvitation Accept(AuthorityConnection connection, Guid id)
    {
        Check(connection);
        if (_pending is { } current)
        {
            if (current.Recipient != connection.Player || current.Invitation != id)
                throw new InvalidOperationException("An invitation is waiting for persistence.");
            return Persist(current);
        }

        _world.RequireNoDurableMutation();
        var prepared = _world.Invitations.PrepareAcceptance(connection.Player, id);
        if (prepared.Status == InvitationStatus.Accepted)
            return prepared;
        var barrier = _checkpoints.BeginDurableMutation();
        try
        {
            _world.Invitations.AcceptPrepared(prepared);
            _world.AdvanceDurableRevision(barrier);
            _pending = new(connection.Player, id, barrier);
        }
        catch
        {
            _checkpoints.Runtime.FaultDurableMutation();
            throw;
        }

        return Persist(_pending);
    }

    public Guid? PendingFor(AuthorityConnection connection)
    {
        Check(connection);
        return _pending is { } current && current.Recipient == connection.Player ? current.Invitation : null;
    }

    public FactionInvitation? Lookup(AuthorityConnection connection, Guid id)
    {
        Check(connection);
        if (_world.HasPendingDurableMutation)
            return null; // in-memory accepted outcomes are not durable success
        return _world.Invitations.FindFor(connection.Player, id);
    }

    FactionInvitation Persist(Pending operation)
    {
        operation.Capture ??= _checkpoints.CaptureDurable(operation.Barrier);
        _checkpoints.Commit(operation.Capture);
        operation.Barrier.Complete();
        _pending = null;
        return _world.Invitations.FindFor(operation.Recipient, operation.Invitation)!;
    }
}