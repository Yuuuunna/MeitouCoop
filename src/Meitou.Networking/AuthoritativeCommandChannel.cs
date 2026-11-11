using Meitou.Simulation;

namespace Meitou.Networking;

/// <summary>
/// Transport-side bridge. The host authenticates an account and opens the authority handle on the world owner before binding it here.
/// </summary>
public sealed class AuthoritativeCommandChannel : IDisposable
{
    readonly CompatibilityHandshake _compatibility;
    readonly AuthorityGateway _authority;
    readonly Lock _gate = new();
    AuthorityConnection? _connection;
    bool _closed;
    public AuthoritativeCommandChannel(CompatibilityHello expected, AuthorityGateway authority) => (_compatibility, this._authority) = (new CompatibilityHandshake(expected), authority);
    public CompatibilityFailure ReceiveHello(WireFrame frame)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _compatibility.Receive(frame);
        }
    }

    public void BindAuthenticated(AuthorityConnection authenticated)
    {
        if (_authority.GetPhase(authenticated)is null)
            throw new InvalidOperationException("Authority handle is not current for this gateway.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_compatibility.Phase != CompatibilityPhase.Compatible || _connection is not null)
                throw new InvalidOperationException("Authenticate only after compatibility and only once per channel.");
            _connection = authenticated;
        }
    }

    public Task<AuthorityReceipt> ReceiveCommand(WireFrame frame)
    {
        AuthorityConnection current;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_compatibility.Phase != CompatibilityPhase.Compatible || _connection is null)
                throw new InvalidOperationException("Gameplay requires a bound authenticated connection.");
            current = _connection;
        }

        var message = CommandProtocol.Decode(frame);
        if (message.World != current.World)
            throw new InvalidDataException("Command belongs to another world instance.");
        var context = new AuthorityContext(message.Expected.Roles.Select(r => new CharacterControl(current.Player, r.Entity, r.Faction, r.Revision)).ToArray(), message.Expected.Faction, message.Expected.MembershipRevision);
        return _authority.Submit(current, message.Request, message.Command, context);
    }

    public void Dispose()
    {
        AuthorityConnection? old;
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            old = _connection;
            _connection = null;
        }

        if (old is not null)
            _authority.Close(old);
    }
}