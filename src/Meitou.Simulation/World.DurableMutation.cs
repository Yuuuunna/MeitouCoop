namespace Meitou.Simulation;

public sealed partial class World
{
    internal sealed class DurableMutation(World world)
    {
        internal void Complete() => world.FinishDurableMutation(this);
    }
    DurableMutation? durableMutation;
    public bool HasPendingDurableMutation => durableMutation is not null;
    internal DurableMutation BeginDurableMutation()
    {
        if (!AtTickBoundary || durableMutation is not null) throw new InvalidOperationException("A durable mutation requires an idle world boundary.");
        return durableMutation = new DurableMutation(this);
    }
    internal void VerifyDurableMutation(DurableMutation token)
    {
        if (!ReferenceEquals(durableMutation, token) || !AtTickBoundary) throw new InvalidOperationException("Invalid durable mutation owner.");
    }
    internal void AdvanceDurableRevision(DurableMutation token)
    {
        VerifyDurableMutation(token); StateRevision = checked(StateRevision + 1);
    }
    void FinishDurableMutation(DurableMutation token)
    {
        VerifyDurableMutation(token); durableMutation = null;
    }
    internal void RequireNoDurableMutation()
    {
        if (durableMutation is not null) throw new InvalidOperationException("A creation result is waiting for durable persistence.");
    }
}
