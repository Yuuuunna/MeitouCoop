using Meitou.Simulation;

namespace Meitou.Engine;

public sealed partial class RemotePlayerSession
{
    readonly ProjectionRevision<string> buildingProjection = new();
    readonly ProjectionRevision<string> machineProjection = new();
    readonly ProjectionRevision<int> groundProjection = new();

    WorldSnapshot ProjectRevisions(WorldSnapshot snapshot) => snapshot with
    {
        BuildingsRevision = buildingProjection.Update(snapshot.BuildingsRevision, snapshot.Buildings, b => b.Id),
        MachinesVersion = machineProjection.Update(snapshot.MachinesVersion, snapshot.Machines, m => m.Site),
        GroundItemsRevision = groundProjection.Update(snapshot.GroundItemsRevision, snapshot.GroundItems, g => g.Id),
    };
    void ClearProjectionRevisions() { buildingProjection.Clear(); machineProjection.Clear(); groundProjection.Clear(); }

    // Registry revisions describe the whole server world. A recipient's membership can change without a registry mutation.
    // Keep authority stamps on individual records untouched; these counters serve display caches only.
    sealed class ProjectionRevision<TKey> where TKey : notnull
    {
        long? authorityRevision;
        long revision;
        object? source;
        HashSet<TKey> keys = [];
        public long Update<T>(long authority, IReadOnlyList<T> values, Func<T, TKey> key)
        {
            if (authorityRevision == authority && ReferenceEquals(source, values)) return revision;
            var next = values.Select(key).ToHashSet();
            if (authorityRevision != authority || !keys.SetEquals(next)) revision = checked(revision + 1);
            authorityRevision = authority; source = values; keys = next;
            return revision;
        }
        public void Clear() { authorityRevision = null; source = null; keys.Clear(); }
    }
}
