using Meitou.Game.Host;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Locks;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>
/// Binds fixture command actors and expected revisions from the recipient's displayed snapshot.
/// </summary>
static class FixtureIntents
{
    /// <summary>
    /// Freezes selected actors and fills missing fixture or door-group revision expectations.
    /// </summary>
    internal static SimCommand Prepare(IPlayerSession session, IReadOnlyList<CharacterId> selected, SimCommand command)
    {
        var snapshot = session.CurrentSnapshot;
        CharacterId[] Actors(IReadOnlyList<CharacterId> ids) => ids.Count > 0 ? [.. ids] : [.. selected];
        ulong Revision(string? id) => id is null ? 0 : snapshot.DoorPicks.FirstOrDefault(f => f.Id == id)?.Revision
            ?? snapshot.Cages.FirstOrDefault(f => f.Id == id)?.Revision ?? snapshot.Beds.FirstOrDefault(f => f.Id == id)?.Revision
            ?? snapshot.Containers.FirstOrDefault(f => f.Id == id)?.Revision ?? 0;
        ulong Expected(ulong supplied, string? id) => supplied != 0 ? supplied : Revision(id);
        ulong CageOf(CharacterId occupant) => snapshot.Cages.FirstOrDefault(f => f.Occupant == occupant)?.Revision ?? 0;
        int Owner(string building) => snapshot.DoorPicks.FirstOrDefault(f => f.Building == building)?.Owner ?? -1;
        return command switch
        {
            PickLockOrder q => q with { Characters = Actors(q.Characters), ExpectedFixtureRevision = q.ExpectedFixtureRevision != 0 ? q.ExpectedFixtureRevision : q.Cage is { } cage ? Revision(cage) : CageOf(q.Prisoner) },
            GetOutOrder q => q with { Characters = Actors(q.Characters) },
            DoorLockOrder q => q with { Characters = Actors(q.Characters), ExpectedFixtureRevision = Expected(q.ExpectedFixtureRevision, q.Door) },
            DoorOrder q => q with { Characters = Actors(q.Characters), ExpectedFixtureRevision = Expected(q.ExpectedFixtureRevision, q.Door) },
            ForceLockOrder q => q with { Characters = Actors(q.Characters), ExpectedFixtureRevision = Expected(q.ExpectedFixtureRevision, q.Fixture) },
            GetInCageOrder q => q with { Characters = Actors(q.Characters), ExpectedFixtureRevision = Expected(q.ExpectedFixtureRevision, q.Cage) },
            SetFreeOrder q => q with { Characters = Actors(q.Characters) },
            ShackleOrder q => q with { Characters = Actors(q.Characters) },
            PutShacklesOrder q => q with { Characters = Actors(q.Characters) },
            UseBedOrder q => q with { Characters = Actors(q.Characters), ExpectedFixtureRevision = Expected(q.ExpectedFixtureRevision, q.Bed) },
            PutInBedOrder q => q with { Carriers = Actors(q.Carriers), ExpectedFixtureRevision = Expected(q.ExpectedFixtureRevision, q.Bed) },
            PutInCageOrder q => q with { Carriers = Actors(q.Carriers), ExpectedFixtureRevision = Expected(q.ExpectedFixtureRevision, q.Cage) },
            PayBailOrder q => q with { Payer = q.Payer.IsNone ? selected.Count > 0 ? selected[0] : CharacterId.None : q.Payer, ExpectedFixtureRevision = q.ExpectedFixtureRevision != 0 ? q.ExpectedFixtureRevision : CageOf(q.Prisoner) },
            ToggleDoorsOrder q => q with { Faction = q.Faction >= 0 ? q.Faction : Owner(q.Building), ExpectedDoorGroupRevision = q.ExpectedDoorGroupRevision != 0 ? q.ExpectedDoorGroupRevision : snapshot.DoorGroupRevisions.GetValueOrDefault(q.Building) },
            ToggleDoorLocksOrder q => q with { Faction = q.Faction >= 0 ? q.Faction : Owner(q.Building), ExpectedDoorGroupRevision = q.ExpectedDoorGroupRevision != 0 ? q.ExpectedDoorGroupRevision : snapshot.DoorGroupRevisions.GetValueOrDefault(q.Building) },
            _ => command,
        };
    }
}