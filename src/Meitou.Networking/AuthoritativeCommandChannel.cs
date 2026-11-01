using Meitou.Simulation;

namespace Meitou.Networking;

/// <summary>Transport-side bridge. The host authenticates an account and opens the authority handle on the world owner before binding it here.</summary>
public sealed class AuthoritativeCommandChannel : IDisposable
{
    readonly CompatibilityHandshake compatibility;
    readonly AuthorityGateway authority;
    readonly Lock gate = new();
    AuthorityConnection? connection;
    bool closed;

    public AuthoritativeCommandChannel(CompatibilityHello expected, AuthorityGateway authority)
        => (compatibility, this.authority) = (new CompatibilityHandshake(expected), authority);

    public CompatibilityFailure ReceiveHello(WireFrame frame)
    {
        lock (gate) { ObjectDisposedException.ThrowIf(closed, this); return compatibility.Receive(frame); }
    }

    public void BindAuthenticated(AuthorityConnection authenticated)
    {
        if (authority.GetPhase(authenticated) is null) throw new InvalidOperationException("Authority handle is not current for this gateway.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (compatibility.Phase != CompatibilityPhase.Compatible || connection is not null) throw new InvalidOperationException("Authenticate only after compatibility and only once per channel.");
            connection = authenticated;
        }
    }

    public Task<AuthorityReceipt> ReceiveCommand(WireFrame frame)
    {
        AuthorityConnection current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (compatibility.Phase != CompatibilityPhase.Compatible || connection is null) throw new InvalidOperationException("Gameplay requires a bound authenticated connection.");
            current = connection;
        }
        var message = CommandProtocol.Decode(frame);
        if (message.World != current.World) throw new InvalidDataException("Command belongs to another world instance.");
        var context = new AuthorityContext(message.Expected.Roles.Select(r => new CharacterControl(current.Player, r.Entity, r.Faction, r.Revision)).ToArray(),
            message.Expected.Faction, message.Expected.MembershipRevision);
        return authority.Submit(current, message.Request, message.Command, context);
    }

    public void Dispose()
    {
        AuthorityConnection? old;
        lock (gate) { if (closed) return; closed = true; old = connection; connection = null; }
        if (old is not null) authority.Close(old);
    }
}
