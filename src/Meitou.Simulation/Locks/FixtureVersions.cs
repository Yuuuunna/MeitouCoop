using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Locks;

/// <summary>Client-observed fixture identity, independent of opening/lockpick progress.</summary>
public abstract record FixtureCommand : OrderCommand
{
    public ulong ExpectedFixtureRevision { get; init; }
}

public sealed partial class FixtureRegistry
{
    sealed class Version
    {
        public ulong Revision;
        public int Owner;
        public string Record = "";
        public Vector3 Position;
    }
    readonly ConditionalWeakTable<Fixture, Version> _fixtureVersions = new();
    ulong _fixtureRevision;

    public ulong RevisionOf(Fixture fixture)
    {
        var version = _fixtureVersions.GetValue(fixture, static _ => new Version());
        if (version.Revision == 0 || version.Owner != fixture.Owner || version.Record != fixture.RecordId || version.Position != fixture.Position)
        {
            if (_fixtureRevision == ulong.MaxValue) throw new InvalidOperationException("Fixture revisions exhausted.");
            version.Revision = ++_fixtureRevision; version.Owner = fixture.Owner; version.Record = fixture.RecordId; version.Position = fixture.Position;
        }
        return version.Revision;
    }

    public bool Matches(FixtureCommand command, Fixture fixture) => command.Issuer is null && command.ExpectedFixtureRevision == 0
        || command.ExpectedFixtureRevision != 0 && command.ExpectedFixtureRevision == RevisionOf(fixture);

    /// <summary>Invalidates a queued panel toggle if any door identity/owner changes.</summary>
    public ulong DoorGroupRevision(string building)
    {
        ulong revision = 0;
        foreach (var fixture in OfBuilding(building))
            if (fixture.Kind == FixtureKind.Door) revision = Rng.Mix(revision ^ RevisionOf(fixture));
        return revision;
    }
}