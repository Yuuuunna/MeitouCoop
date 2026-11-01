using System.Numerics;

namespace Meitou.Simulation.Items
{
    /// <summary>
    /// Open a container (<see cref="Locks.Fixture.Id"/>): the nearest of the characters (the selection when empty) who may walks to it and its window opens. Another faction's locked one:
    /// with <paramref name="Alt"/> its lock is picked (76 PICK_LOCK), else the hint and a walk to it; a shop's without Alt: the steal hint and a walk.
    /// </summary>
    public sealed record OpenContainerOrder(IReadOnlyList<CharacterId> Characters, string Fixture, bool Alt = false) : SimCommand;

    /// <summary>Closes the open container's window.</summary>
    public sealed record CloseContainer : SimCommand
    {
        public ulong SessionId { get; init; }
    }

    internal sealed record ContainerAccess(ulong Id, PlayerId? Player, CharacterId Actor, Locks.Fixture Fixture,
        int Owner, CharacterControl? Control, ulong ActorMembership, ulong OwnerMembership)
    {
        public bool Valid(World world)
        {
            if (!ReferenceEquals(world.Fixtures.Find(Fixture.Id), Fixture) || Fixture.Owner != Owner || Fixture.Contents is null
                || Fixture.Lock is { EffectivelyLocked: true }) return false;
            if (Player is not { } issuer) return true;
            if (!world.Characters.TryResolveNext(Actor, out int slot) || world.Characters.Cold(slot) is not
                { Medical.Incapacitated: false, Inventory: not null, IsImprisoned: false } actor || !actor.CarriedBy.IsNone) return false;
            return Control is { } grant && world.Players.Validate(grant) && world.Players.Find(issuer) is { } player
                && player.MembershipRevision(grant.Faction) == ActorMembership && player.MembershipRevision(Owner) == OwnerMembership
                && (!player.IsMember(Owner) || player.HasPermission(Owner, FactionPermission.ManageAssets));
        }

        public bool InRange(World world) => world.Characters.TryResolveNext(Actor, out int slot)
            && MathF.Abs(world.Characters.Next[slot].Position.Y - Fixture.Position.Y) <= PlayerSystem.ContainerFloorSlack
            && Vector2.Distance(new(world.Characters.Next[slot].Position.X, world.Characters.Next[slot].Position.Z), new(Fixture.Position.X, Fixture.Position.Z)) <= PlayerSystem.TradingRange;
    }
}

namespace Meitou.Simulation
{
    /// <summary>
    /// Opening a container of a building's interior (docs/game/locks.md "Containers"): a character walks up to it, its inventory shows in a window beside the character's, and items are
    /// moved with <see cref="Production.PutInMachine"/> / <see cref="Production.TakeFromMachine"/> addressed by the fixture id. <b>Engine choices</b>: the reach, a locked one refusing, a shop's
    /// containers staying the shop's (each its own rectangle of the shop's stock, <see cref="Trading.Shop.ForBuilding"/>). Taking from another faction's is stealing (<see cref="Crime.CrimeSystem.Steal"/>).
    /// </summary>
    public sealed partial class PlayerSystem
    {
        ulong nextContainerSession;
        /// <summary>A character opens or uses a container within this distance of it (an engine choice).</summary>
        public const float ContainerReach = 20;

        /// <summary>How far above or below a container a character may stand to open or use it (engine choice: a storey is about 66).</summary>
        public const float ContainerFloorSlack = 20;

        /// <summary>
        /// How far (X/Z) a character may be from a container it puts into or takes from: the original's limit for an item moved out of a character's inventory (60; out of a building's, its
        /// bounds radius + 40, which a chest's is close to; docs/game/economy.md "Trading range").
        /// </summary>
        public const float TradingRange = 60;

        void OpenContainerInput(World world, Items.OpenContainerOrder order)
        {
            if (world.CommandPlayer(order) is not { } player || world.Fixtures.Find(order.Fixture) is not { Contents: not null } fixture) return;
            var member = order.Issuer is { } issuer ? world.Players.Find(issuer) : null;
            if (order.Issuer is not null && (member is null || member.MembershipRevision(fixture.Owner) != order.FactionGrantRevision
                || member.IsMember(fixture.Owner) && !member.HasPermission(fixture.Owner, FactionPermission.ManageAssets))) return;
            var table = world.Characters;
            int best = -1;
            float bestDistance = float.MaxValue;
            foreach (var id in world.CommandCharacters(order, order.Characters))
            {
                if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false, Inventory: not null, IsImprisoned: false } c || !c.CarriedBy.IsNone) continue;
                float d = Vector2.Distance(Flat(table.Next[s].Position), Flat(fixture.Position));
                if (d < bestDistance) (best, bestDistance) = (s, d);
            }
            if (best < 0) return;
            var walker = table.Cold(best)!;
            bool theirs = fixture.Owner >= 0 && fixture.Owner != walker.Faction && member?.HasPermission(fixture.Owner, FactionPermission.ManageAssets) != true;
            void Notice(string text) => world.Post(order.Issuer ?? PlayerId.Local, text);
            if (fixture.Lock is { EffectivelyLocked: true })
            {
                if (!theirs)
                {
                    Notice($"{walker.Name}: It's locked.");
                    return;
                }
                // Another faction's locked container (the original's FUN_1407fc6e0): Alt picks the lock, else the hint and a walk to it.
                walker.Shackles = null;
                walker.PickingLock = order.Alt ? fixture.Id : null;
                if (!order.Alt) Notice("Hold down the ALT key to pick the lock.");
                MovementSystem.GoToFloor(world, best, fixture.Position, walker.RunSpeed);
                return;
            }
            if (ShopOf(world, fixture) is not null && !order.Alt && theirs)
            {
                // A shop's stock without Alt: the original's hint and a move; with Alt (the menu's Loot) the walker steals from it (the window shows that container's part of the shop's stock).
                Notice("Hold down the ALT key to steal items");
                MovementSystem.GoToFloor(world, best, fixture.Position, walker.RunSpeed);
                return;
            }
            walker.PickingLock = null;
            walker.Shackles = null;
            walker.PickingUpItem = null;
            player.OpenContainer = null;
            player.OpeningContainer = order.Fixture;
            player.ContainerWalker = table.IdOf(best);
            player.ContainerAccess = new(checked(++nextContainerSession), order.Issuer, player.ContainerWalker, fixture, fixture.Owner,
                order.Issuer is null ? null : world.Players.ControlOf(player.ContainerWalker),
                member?.MembershipRevision(walker.Faction) ?? 0, member?.MembershipRevision(fixture.Owner) ?? 0);
            if (bestDistance > ContainerReach || !SameFloor(table.Next[best].Position, fixture.Position)) MovementSystem.GoToFloor(world, best, fixture.Position, walker.RunSpeed);
            else ContainerWorker(world, player);
        }

        /// <summary>Each tick: the walker in reach opens its container; one given another order, downed or gone gives up. An open window stays until the player closes it; it closes when its opener is down or gone.</summary>
        static void ContainerWorkers(World world)
        {
            foreach (var owner in world.Players.All) ContainerWorker(world, owner.State);
        }

        static void ContainerWorker(World world, PlayerState player)
        {
            var table = world.Characters;
            if (player.ContainerAccess is { } access && !access.Valid(world)) { CloseContainer(player); return; }
            if (player.OpeningContainer is { } wanted)
            {
                if (world.Fixtures.Find(wanted) is not { Contents: not null } fixture || !table.TryResolveNext(player.ContainerWalker, out int s) || table.Cold(s) is not { Medical.Incapacitated: false } || !table.Cold(s)!.CarriedBy.IsNone)
                    player.OpeningContainer = null;
                else
                {
                    var at = Flat(fixture.Position);
                    if (Vector2.Distance(Flat(table.Next[s].Position), at) <= ContainerReach && SameFloor(table.Next[s].Position, fixture.Position))
                    {
                        if (table.Next[s].Task == (byte)CharacterTask.GoTo) MovementSystem.Stop(table, s);
                        (player.OpenContainer, player.ContainerOpener, player.OpeningContainer) = (wanted, player.ContainerWalker, null);
                    }
                    else if (table.Next[s].Task != (byte)CharacterTask.GoTo || Vector2.Distance(table.Next[s].Goal, at) > ContainerReach) player.OpeningContainer = null;
                }
            }
            if (player.OpenContainer is { } open)
            {
                // Legacy windows keep the original walk-away behaviour; attributed windows also require legal observation range.
                int o = -1;
                bool keep = world.Fixtures.Find(open) is { Contents: not null } && table.TryResolveNext(player.ContainerOpener, out o) && table.Cold(o) is { Medical.Incapacitated: false };
                if (keep && player.ContainerAccess?.Player is not null)
                    keep = SameFloor(table.Next[o].Position, world.Fixtures.Find(open)!.Position)
                        && Vector2.Distance(Flat(table.Next[o].Position), Flat(world.Fixtures.Find(open)!.Position)) <= TradingRange;
                if (!keep) CloseContainer(player);
            }
            if (player.OpenContainer is null && player.OpeningContainer is null) player.ContainerAccess = null;
        }

        internal static void CloseContainer(PlayerState player)
        {
            player.OpenContainer = player.OpeningContainer = null;
            player.ContainerOpener = player.ContainerWalker = CharacterId.None;
            player.ContainerAccess = null;
        }

        internal static void CloseContainerInput(World world, Items.CloseContainer command)
        {
            if (world.CommandPlayer(command) is not { } player) return;
            if (command.SessionId != 0 && command.SessionId != player.ContainerSessionId) return;
            if (command.Issuer is not null && (player.ContainerAccess?.Player != command.Issuer || command.SessionId == 0 || command.SessionId != player.ContainerSessionId)) return;
            CloseContainer(player);
            if (command.Issuer is null) player.OpenLoot = player.OpeningLoot = CharacterId.None;
        }

        /// <summary>Fresh authorization for a specific open window, never inferred from the current selection.</summary>
        internal static bool MayUseContainer(World world, SimCommand command, ulong sessionId, Locks.Fixture fixture, CharacterId actor)
        {
            if (command.Issuer is null) return true;
            return world.CommandPlayer(command) is { } player && player.OpenContainer == fixture.Id
                && player.ContainerAccess is { } access && access.Player == command.Issuer && access.Actor == actor
                && sessionId != 0 && access.Id == sessionId && access.Valid(world) && access.InRange(world) && world.MayOrder(command, actor);
        }

        /// <summary>The building shop <paramref name="container"/> is a stock container of, if any.</summary>
        internal static Trading.Shop? ShopOf(World world, Locks.Fixture container) =>
            world.Squads.All.Select(s => s.Shop).FirstOrDefault(s => s?.Building == container.Building && s.RegionOf(container.Id, out _));

        /// <summary>
        /// The grid a container window works on: the container's own, but for a building shop's stock container the shop's stock (<see cref="Trading.Shop.ForBuilding"/>), of which the
        /// container's grid is <paramref name="region"/>: the window shows and takes only the items there (at cells relative to its corner), taking them is theft.
        /// </summary>
        internal static Items.Inventory GridOf(World world, Locks.Fixture container, out Items.GridRegion region)
        {
            if (ShopOf(world, container) is { } shop && shop.RegionOf(container.Id, out region)) return shop.Stock;
            region = new(0, 0, container.Contents!.Width, container.Contents.Height);
            return container.Contents;
        }

        internal static Items.Inventory GridOf(World world, Locks.Fixture container) => GridOf(world, container, out _);


        static bool SameFloor(Vector3 a, Vector3 b) => MathF.Abs(a.Y - b.Y) <= ContainerFloorSlack;

        /// <summary>The container Take/Put address by <paramref name="site"/> (a fixture id) and the character, when it is open and the character is in reach; posts "Out of trading range." otherwise (<see cref="TradingRange"/>). Null when <paramref name="site"/> is not a container.</summary>
        internal static Locks.Fixture? ContainerFor(World world, string site, CharacterId who, out CharacterCold? cold, SimCommand? command = null, ulong sessionId = 0)
        {
            cold = null;
            if (world.Fixtures.Find(site) is not { Contents: not null } f) return null;
            if (command is not null && !MayUseContainer(world, command, sessionId, f, who)) return f;
            var table = world.Characters;
            if (!table.TryResolveNext(who, out int slot) || table.Cold(slot) is not { IsPlayer: true, Inventory: not null } c) return f;
            if (command?.Issuer is not null && (c.Medical?.Incapacitated != false || c.IsImprisoned || !c.CarriedBy.IsNone || f.Lock is { EffectivelyLocked: true })) return f;
            if (!SameFloor(table.Next[slot].Position, f.Position) || Vector2.Distance(Flat(table.Next[slot].Position), Flat(f.Position)) > TradingRange)
            { world.Post(command?.Issuer ?? PlayerId.Local, "Out of trading range."); return f; }
            cold = c;
            return f;
        }
    }
}
