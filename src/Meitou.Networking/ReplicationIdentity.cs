using Meitou.Simulation;

namespace Meitou.Networking;
public sealed record ReplicationIdentity(Guid World, PlayerId Player, ulong Generation)
{
    public static ReplicationIdentity Of(PlayerWorldView view) => new(view.World, view.Player, view.ConnectionGeneration);
    internal void Validate()
    {
        if (World == Guid.Empty || Player.IsNone || Generation == 0)
            throw new InvalidDataException("Invalid replication identity.");
    }
}