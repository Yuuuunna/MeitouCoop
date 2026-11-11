namespace Meitou.Simulation;

public sealed partial class World
{
    internal sealed class DurableMutation(World world)
    {
        internal void Complete() => world.FinishDurableMutation(this);
    }
    DurableMutation? _durableMutation;
    public bool HasPendingDurableMutation => _durableMutation is not null;
    internal DurableMutation BeginDurableMutation()
    {
        if (!AtTickBoundary || _durableMutation is not null) throw new InvalidOperationException("A durable mutation requires an idle world boundary.");
        return _durableMutation = new DurableMutation(this);
    }
    internal void VerifyDurableMutation(DurableMutation token)
    {
        if (!ReferenceEquals(_durableMutation, token) || !AtTickBoundary) throw new InvalidOperationException("Invalid durable mutation owner.");
    }
    internal void AdvanceDurableRevision(DurableMutation token)
    {
        VerifyDurableMutation(token); StateRevision = checked(StateRevision + 1);
    }
    void FinishDurableMutation(DurableMutation token)
    {
        VerifyDurableMutation(token); _durableMutation = null;
    }
    internal void RequireNoDurableMutation()
    {
        if (_durableMutation is not null) throw new InvalidOperationException("A creation result is waiting for durable persistence.");
    }
}