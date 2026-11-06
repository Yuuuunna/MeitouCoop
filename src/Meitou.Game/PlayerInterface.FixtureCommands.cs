using Meitou.Simulation;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Locks;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    SimCommand PrepareFixtureIntent(SimCommand command)
    {
        var snapshot = session.CurrentSnapshot;
        CharacterId[] Actors(IReadOnlyList<CharacterId> ids) => ids.Count > 0 ? [.. ids] : [.. SelectedIds()];
        ulong Revision(string? id) => id is null ? 0 : snapshot.DoorPicks.FirstOrDefault(f => f.Id == id)?.Revision
            ?? snapshot.Cages.FirstOrDefault(f => f.Id == id)?.Revision ?? snapshot.Beds.FirstOrDefault(f => f.Id == id)?.Revision
            ?? snapshot.Containers.FirstOrDefault(f => f.Id == id)?.Revision ?? 0;
        ulong CageOf(CharacterId occupant) => snapshot.Cages.FirstOrDefault(f => f.Occupant == occupant)?.Revision ?? 0;
        int Owner(string building) => snapshot.DoorPicks.FirstOrDefault(f => f.Building == building)?.Owner ?? -1;
        return command switch
        {
            PickLockOrder q => q with { Characters = Actors(q.Characters), ExpectedFixtureRevision = q.ExpectedFixtureRevision != 0 ? q.ExpectedFixtureRevision : q.Cage is { } cage ? Revision(cage) : CageOf(q.Prisoner) },
            GetOutOrder q => q with { Characters = Actors(q.Characters) },
            DoorLockOrder q => q with { Characters = Actors(q.Characters), ExpectedFixtureRevision = q.ExpectedFixtureRevision != 0 ? q.ExpectedFixtureRevision : Revision(q.Door) },
            DoorOrder q => q with { Characters = Actors(q.Characters), ExpectedFixtureRevision = q.ExpectedFixtureRevision != 0 ? q.ExpectedFixtureRevision : Revision(q.Door) },
            ForceLockOrder q => q with { Characters = Actors(q.Characters), ExpectedFixtureRevision = q.ExpectedFixtureRevision != 0 ? q.ExpectedFixtureRevision : Revision(q.Fixture) },
            GetInCageOrder q => q with { Characters = Actors(q.Characters), ExpectedFixtureRevision = q.ExpectedFixtureRevision != 0 ? q.ExpectedFixtureRevision : Revision(q.Cage) },
            SetFreeOrder q => q with { Characters = Actors(q.Characters) },
            ShackleOrder q => q with { Characters = Actors(q.Characters) },
            PutShacklesOrder q => q with { Characters = Actors(q.Characters) },
            UseBedOrder q => q with { Characters = Actors(q.Characters), ExpectedFixtureRevision = q.ExpectedFixtureRevision != 0 ? q.ExpectedFixtureRevision : Revision(q.Bed) },
            PutInBedOrder q => q with { Carriers = Actors(q.Carriers), ExpectedFixtureRevision = q.ExpectedFixtureRevision != 0 ? q.ExpectedFixtureRevision : Revision(q.Bed) },
            PutInCageOrder q => q with { Carriers = Actors(q.Carriers), ExpectedFixtureRevision = q.ExpectedFixtureRevision != 0 ? q.ExpectedFixtureRevision : Revision(q.Cage) },
            PayBailOrder q => q with { Payer = q.Payer.IsNone ? SelectedIds().Count > 0 ? SelectedIds()[0] : CharacterId.None : q.Payer, ExpectedFixtureRevision = q.ExpectedFixtureRevision != 0 ? q.ExpectedFixtureRevision : CageOf(q.Prisoner) },
            ToggleDoorsOrder q => q with { Faction = q.Faction >= 0 ? q.Faction : Owner(q.Building), ExpectedDoorGroupRevision = q.ExpectedDoorGroupRevision != 0 ? q.ExpectedDoorGroupRevision : snapshot.DoorGroupRevisions.GetValueOrDefault(q.Building) },
            ToggleDoorLocksOrder q => q with { Faction = q.Faction >= 0 ? q.Faction : Owner(q.Building), ExpectedDoorGroupRevision = q.ExpectedDoorGroupRevision != 0 ? q.ExpectedDoorGroupRevision : snapshot.DoorGroupRevisions.GetValueOrDefault(q.Building) },
            _ => command,
        };
    }
}
