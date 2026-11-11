using Meitou.Simulation.Items;
using Meitou.Simulation;

namespace Meitou.Game.Host;
public sealed partial class RemotePlayerSession
{
    readonly ProjectionRevision<string> _buildingProjection = new();
    readonly ProjectionRevision<string> _machineProjection = new();
    readonly ProjectionRevision<int> _groundProjection = new();
    WorldSnapshot ProjectRevisions(WorldSnapshot snapshot) => snapshot with
    {
        BuildingsRevision = _buildingProjection.Update(snapshot.BuildingsRevision, snapshot.Buildings, b => b.Id),
        MachinesVersion = _machineProjection.Update(snapshot.MachinesVersion, snapshot.Machines, m => m.Site),
        GroundItemsRevision = _groundProjection.Update(snapshot.GroundItemsRevision, snapshot.GroundItems, g => g.Id),
    };
    void ClearProjectionRevisions()
    {
        _buildingProjection.Clear();
        _machineProjection.Clear();
        _groundProjection.Clear();
    }

    // Registry revisions describe the whole server world. A recipient's membership can change without a registry mutation.
    // Keep authority stamps on individual records untouched; these counters serve display caches only.
    sealed class ProjectionRevision<TKey>
        where TKey : notnull
    {
        long? _authorityRevision;
        long _revision;
        object? _source;
        HashSet<TKey> _keys = [];
        public long Update<T>(long authority, IReadOnlyList<T> values, Func<T, TKey> key)
        {
            if (_authorityRevision == authority && ReferenceEquals(_source, values))
                return _revision;
            var next = values.Select(key).ToHashSet();
            if (_authorityRevision != authority || !_keys.SetEquals(next))
                _revision = checked(_revision + 1);
            _authorityRevision = authority;
            _source = values;
            _keys = next;
            return _revision;
        }

        public void Clear()
        {
            _authorityRevision = null;
            _source = null;
            _keys.Clear();
        }
    }
}