using Meitou.Simulation.Player;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;
using Meitou.Simulation.Dialogue;

namespace Meitou.Networking;
public sealed record BoundPlayerCommand(SimCommand Command, ExpectedCommandAuthority Expected);
/// <summary>
/// Freezes UI intent against the displayed authorized view; never reads or mutates a World.
/// </summary>
public static class ClientCommandBinding
{
    public static BoundPlayerCommand Bind(PlayerWorldView view, SimCommand intent, IReadOnlyList<CharacterId> selection, int activeFaction)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(selection);
        if (!CommandProtocol.Supports(intent))
            throw new InvalidOperationException("This operation is not exposed by the remote command protocol.");
        CharacterId[] Selected() => selection.Distinct().ToArray();
        CharacterId Buyer() => selection.Count > 0 ? selection[0] : throw new InvalidOperationException("Select a buyer first.");
        var command = intent switch
        {
            AreaArrival c => c with
            {
                SquadId = c.SquadId < 0 ? view.Snapshot.ActiveSquad : c.SquadId,
                Leader = c.Leader.IsNone ? view.Snapshot.PlayerSquads.FirstOrDefault(s => s.Id == (c.SquadId < 0 ? view.Snapshot.ActiveSquad : c.SquadId))?.Members.FirstOrDefault(CharacterId.None) ?? CharacterId.None : c.Leader,
            },
            MoveOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            FollowOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            StopCommand c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            AttackOrder c when c.Attackers.Count == 0 => c with
            {
                Attackers = Selected()
            },
            ShootAtOrder c when c.Gunners.Count == 0 => c with
            {
                Gunners = Selected()
            },
            OrdersCommand c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            RunSpeedCommand c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            ManoeuvreCommand c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            DoorLockOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            StandUpCommand c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            TreatOrder c when c.Medics.Count == 0 => c with
            {
                Medics = Selected()
            },
            SplintOrder c when c.Medics.Count == 0 => c with
            {
                Medics = Selected()
            },
            RestOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            MedicJobCommand c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            PickUpOrder c when c.Carriers.Count == 0 => c with
            {
                Carriers = Selected()
            },
            PutDownOrder c when c.Carriers.Count == 0 => c with
            {
                Carriers = Selected()
            },
            LootOrder c when c.Looters.Count == 0 => c with
            {
                Looters = Selected()
            },
            OpenLootOrder c when c.Looters.Count == 0 => c with
            {
                Looters = Selected()
            },
            StealthKoOrder c when c.Attackers.Count == 0 => c with
            {
                Attackers = Selected()
            },
            RescueJobCommand c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            PickLockOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            GetOutOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            DoorOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            SetFreeOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            GetInCageOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            ForceLockOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            ShackleOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            PutShacklesOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            UseBedOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            PutInBedOrder c when c.Carriers.Count == 0 => c with
            {
                Carriers = Selected()
            },
            PickUpItemOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            DisposeCorpseOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            PutInCageOrder c when c.Carriers.Count == 0 => c with
            {
                Carriers = Selected()
            },
            PayBailOrder c when c.Payer.IsNone => c with
            {
                Payer = Buyer()
            },
            OpenContainerOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            AddOrderJob c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            OperateOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            UseTurretOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            AddJob c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            BuildOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            DismantleOrder c when c.Characters.Count == 0 => c with
            {
                Characters = Selected()
            },
            BuyBuilding c when c.Buyer.IsNone => c with
            {
                Buyer = Buyer()
            },
            _ => intent,
        };
        if (command is FactionCommand { Faction: < 0 } faction)
        {
            if (activeFaction < 0)
                throw new InvalidOperationException("No active faction is selected.");
            command = faction with
            {
                Faction = activeFaction
            };
        }

        if (command is HeldCommand held)
        {
            CharacterId actor = held is LiftItem lift ? lift.Actor : held.HolderActor.IsNone ? view.Snapshot.Held?.Actor ?? CharacterId.None : held.HolderActor;
            CharacterId target = held switch
            {
                LiftItem { From.Holder: ItemHolder.Character } c => c.From.Character,
                PlaceHeld { Into.Holder: ItemHolder.Character } c => c.Into.Character,
                GiveHeld c => c.Target,
                _ => CharacterId.None
            };
            command = held with
            {
                HolderActor = actor,
                ControlledCharacters = target != actor && view.Roles.Any(r => r.Character == target && r.Available && !r.Dead) ? [target] : []
            };
        }

        if (command is DisposeCorpseOrder { Bodies.Count: 0 } disposal)
            command = disposal with
            {
                Bodies = disposal.Characters.Select(id => view.Snapshot.Characters.FirstOrDefault(c => c.Id == id)?.Carrying ?? CharacterId.None).ToArray()
            };
        if (command is FixtureCommand fixture)
        {
            string? id = fixture switch
            {
                PickLockOrder c => c.Cage ?? view.Snapshot.Characters.FirstOrDefault(p => p.Id == c.Prisoner)?.Prison?.CageId,
                DoorOrder c => c.Door,
                DoorLockOrder c => c.Door,
                GetInCageOrder c => c.Cage,
                ForceLockOrder c => c.Fixture,
                UseBedOrder c => c.Bed,
                PutInBedOrder c => c.Bed,
                PutInCageOrder c => c.Cage,
                PayBailOrder c => view.Snapshot.Characters.FirstOrDefault(p => p.Id == c.Prisoner)?.Prison?.CageId,
                _ => null
            };
            if (id is not null && fixture.ExpectedFixtureRevision == 0)
                command = fixture with
                {
                    ExpectedFixtureRevision = FixtureRevision(id)
                };
        }

        command = command switch
        {
            ToggleDoorsOrder c when c.ExpectedDoorGroupRevision == 0 => c with
            {
                ExpectedDoorGroupRevision = view.Snapshot.DoorGroupRevisions.GetValueOrDefault(c.Building)
            },
            ToggleDoorLocksOrder c when c.ExpectedDoorGroupRevision == 0 => c with
            {
                ExpectedDoorGroupRevision = view.Snapshot.DoorGroupRevisions.GetValueOrDefault(c.Building)
            },
            _ => command,
        };
        var grants = new List<ExpectedRoleGrant>();
        foreach (var actor in PlayerCommandRouting.Characters(command).Distinct())
        {
            var role = view.Roles.FirstOrDefault(r => r.Character == actor && r.Available && !r.Dead);
            if (actor.IsNone || role is null || !view.Snapshot.Characters.Any(c => c.Id == actor))
                throw new InvalidOperationException("An acting character is not currently controlled and available.");
            grants.Add(new(role.Entity, role.Faction, role.Revision));
        }

        var scope = PlayerCommandRouting.FactionScope(command);
        int owner = scope.Source switch
        {
            CommandFactionSource.None => -1,
            CommandFactionSource.Explicit => scope.Faction,
            CommandFactionSource.Character => view.Snapshot.Characters.FirstOrDefault(c => c.Id == scope.Character)?.Faction ?? throw Missing(),
            CommandFactionSource.ConstructionRoot => view.Snapshot.Buildings.FirstOrDefault(b => b.Id == scope.Site)?.AuthorityFaction ?? view.Snapshot.DoorPicks.FirstOrDefault(d => d.Building == scope.Site)?.Owner ?? throw Missing(),
            CommandFactionSource.Container => view.Snapshot.Containers.FirstOrDefault(c => c.Id == scope.Site)?.Owner ?? throw Missing(),
            CommandFactionSource.WorkSite => WorkOwner(scope.Site!),
            _ => throw new InvalidOperationException("Unknown command authority source."),
        };
        var membership = view.Factions.FirstOrDefault(f => f.Faction == owner);
        return new(command, new(grants.ToArray(), owner, membership?.MembershipRevision ?? 0));
        int WorkOwner(string site)
        {
            if (view.Snapshot.Machines.FirstOrDefault(m => m.Site == site)is { } machine)
                return machine.AuthorityFaction;
            if (view.Snapshot.Containers.FirstOrDefault(c => c.Id == site)is { } container)
                return container.Owner;
            if (view.Snapshot.Buildings.FirstOrDefault(b => b.Id == site)is { } building)
                return building.Owner;
            if (view.Snapshot.Dummies.FirstOrDefault(d => d.Id == site)is { } dummy)
                return dummy.Owner;
            if (view.Snapshot.Seats.FirstOrDefault(s => s.Id == site)is { } seat)
                return seat.Owner;
            if (ProductionSystem.IsNatural(site))
                return -1;
            throw Missing();
        }

        static InvalidOperationException Missing() => new("The operation's target is outside the current view.");
        ulong FixtureRevision(string id) => view.Snapshot.DoorPicks.FirstOrDefault(d => d.Id == id)?.Revision ?? view.Snapshot.Beds.FirstOrDefault(b => b.Id == id)?.Revision ?? view.Snapshot.Cages.FirstOrDefault(c => c.Id == id)?.Revision ?? view.Snapshot.Containers.FirstOrDefault(c => c.Id == id)?.Revision ?? throw Missing();
    }
}