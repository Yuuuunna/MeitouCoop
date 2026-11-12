using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;

namespace Meitou.Simulation.Editing;

/// <summary>
/// Why the character editor is open (docs/characters.md "Character editor"): making the start's characters, or a plastic surgeon's work. The
/// editor's use: the new game's characters, a plastic surgeon, or a recruit (DA_JOIN_SQUAD_WITH_EDIT; free, race and gender kept as at a surgeon).
/// </summary>
public enum EditKind : byte
{
    /// <summary>
    /// The new game's characters.
    /// </summary>
    Creation,
    /// <summary>
    /// A plastic surgeon's work, paid.
    /// </summary>
    Surgery,
    /// <summary>
    /// A recruit's edit (DA_JOIN_SQUAD_WITH_EDIT): free, race and gender kept.
    /// </summary>
    Recruit
}

/// <summary>
/// The character editor's result for one character: its new look (a CHARACTER_APPEARANCE, normally a <see cref="CharacterDraft"/> of its current one)
/// and name. Validated when applied (<see cref="CharacterEditing.Apply"/>): a player character; at creation only while the new game's editor is open
/// (<see cref="PlayerState.InCreation"/>), in surgery only beside the <see cref="Surgeon"/> and with the race and gender unchanged. A null look
/// renames only: an animal's page in the editor offers its name and nothing else (docs/characters.md "Character editor"). Allowed in every kind: a
/// bought animal is named through the in-play (Recruit) editor; only a creation rename needs the creation to be open.
/// </summary>
public sealed record EditCharacter(CharacterId Character, FcsRecord? Appearance, string Name, EditKind Kind) : SimCommand
{
    /// <summary>
    /// The plastic surgeon doing the work (surgery only).
    /// </summary>
    public CharacterId Surgeon { get; init; } = CharacterId.None;

    /// <summary>
    /// The surgery was paid already (a surgeon's dialogue line takes the money, DA_TAKE_MONEY, before it opens the editor).
    /// </summary>
    public bool Paid { get; init; }
    /// <summary>
    /// Opaque permission issued by the server dialogue, not a client assertion of payment.
    /// </summary>
    public Guid SessionId { get; init; }

}

/// <summary>
/// Ends the new game's character creation: the start's characters cannot be remade any more.
/// </summary>
public sealed record FinishCreation : SimCommand;

/// <summary>
/// Applies <see cref="EditCharacter"/> (the player system's inputs).
/// </summary>
public static class CharacterEditing
{
    /// <summary>
    /// The longest name the editor takes (the layout's <c>NameText</c> <c>MaxTextLength</c>).
    /// </summary>
    public const int MaxNameLength = 30;

    /// <summary>
    /// How near (world units) the surgeon must stand: the talk range of a dialogue (engine choice, <b>Unknown</b> in the original).
    /// </summary>
    public const float SurgeonReach = 60;

    /// <summary>
    /// What <paramref name="id"/>'s surgery costs: its record's dialogue, or its squad template's leader dialogue when it leads the squad; null: not
    /// a surgeon.
    /// </summary>
    internal static int? SurgeryCost(World world, SurgeonDialogues surgeons, CharacterId id, CharacterCold cold)
    {
        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        return surgeons.CostOf(cold.RecordId, squad?.TemplateId, squad is not null && squad.Leader == id);
    }

    public static void Apply(World world, PopulationData data, EditCharacter edit)
    {
        void Post(string message) => world.Post(edit.Issuer ?? PlayerId.Local, message);
        if (!Enum.IsDefined(edit.Kind) || edit.Name is null || !world.MayOrder(edit, edit.Character)) return;
        var player = world.CommandPlayer(edit);
        if (player is null) return;
        var session = edit.Kind == EditKind.Creation ? null : CharacterEditSessions.Find(world, edit);
        if (edit.Kind != EditKind.Creation && session is null) { Post("The character editor session has expired."); return; }
        if (session?.Applied == true) return;
        if (edit.Kind == EditKind.Creation && (edit.Issuer is not null || world.Players.RequiresExplicitControl))
        { Post("Use the server creation request to create characters."); return; }

        var table = world.Characters;
        if (!table.IsAlive(edit.Character) || !table.TryResolveNext(edit.Character, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold || cold.Appearance is not { } look)
        {
            Post("That character cannot be edited.");
            return;
        }
        if (edit.Appearance is null)
        {
            if (edit.Kind == EditKind.Creation && !player.InCreation) { Post("Characters can only be made at the start of a new game."); return; }
            if (edit.Kind == EditKind.Surgery)
            {
                if (!table.TryResolveNext(edit.Surgeon, out int surgeon) || !table.IsAlive(edit.Surgeon)
                    || System.Numerics.Vector3.Distance(table.Next[surgeon].Position, table.Next[slot].Position) > SurgeonReach)
                { Post("The surgeon is not here any more."); return; }
                if (session is { Paid: false })
                {
                    var cost = SurgeryCost(world, data.Surgeons, edit.Surgeon, table.Cold(surgeon)!);
                    if (cost is null || cost.Value > 0 && (!world.MayManage(edit, cold.Faction, FactionPermission.SpendFunds)
                        || world.Factions.Find(cold.Faction) is not { } funds || funds.Money <= cost.Value || !funds.TryDebit(cost.Value)))
                    { Post("Surgery payment is no longer authorized."); return; }
                }
            }
            string renamed = edit.Name.Trim();
            if (renamed.Length > MaxNameLength) renamed = renamed[..MaxNameLength];
            if (renamed.Length > 0) cold.Name = renamed;
            if (session is not null) session.Applied = true;
            return;
        }
        if (look.Animal is not null) { Post("An animal's appearance cannot be changed."); return; }
        var currentRace = look.Race;
        var proposedRace = edit.Appearance.References.TryGetValue("race", out var refs) && refs.Count > 0 ? data.Db.Find(refs[0].TargetStringId) : null;
        if (proposedRace is not { Type: FcsRecordType.Race }) { Post("The edited look has no race."); return; }
        if (edit.Kind == EditKind.Creation)
        {
            if (!player.InCreation) { Post("Characters can only be made at the start of a new game."); return; }
            if (!PlayableRaces.Of(data.Db, player.CreationRaces).Any(g => g.Races.Any(r => r.StringId == proposedRace.StringId)))
            {
                Post($"{proposedRace.Name} is not a playable race.");
                return;
            }
        }
        else if (edit.Kind == EditKind.Recruit)
        {
            bool female = edit.Appearance.Bools.TryGetValue("sex female", out var f) && f;
            if (proposedRace.StringId != currentRace.StringId || female != look.Female) { Post("A recruit's race and gender cannot be changed."); return; }
        }
        else
        {
            if (!table.TryResolveNext(edit.Surgeon, out int surgeon) || !table.IsAlive(edit.Surgeon) ||
                System.Numerics.Vector3.Distance(table.Next[surgeon].Position, table.Next[slot].Position) > SurgeonReach)
            {
                Post("The surgeon is not here any more.");
                return;
            }
            int? known = session!.Paid ? 0 : SurgeryCost(world, data.Surgeons, edit.Surgeon, table.Cold(surgeon)!);
            if (known is not { } cost) { Post("That is not a surgeon."); return; }
            // The dialogue's gate is DC_PLAYERMONEY > cost (strict), then DA_TAKE_MONEY takes it.
            if (cost > 0 && (world.Factions.Find(cold.Faction)?.Money ?? 0) <= cost) { Post($"Surgery costs c.{cost}: not enough money."); return; }
            bool female = edit.Appearance.Bools.TryGetValue("sex female", out var f) && f;
            if (proposedRace.StringId != currentRace.StringId || female != look.Female)
            {
                Post("A surgeon cannot change race or gender.");
                return;
            }

        }
        if (look.Loadout?.Appearance.Record is null && look.Body?.Record is not { }) { Post("The original appearance is unavailable."); return; }
        var basis = look.Loadout?.Appearance.Record ?? look.Body!.Record;
        // Read only server-defined editor controls from the proposed look, then apply those
        // choices to the original. Arbitrary FCS fields, metadata and hidden values cannot enter the world.
        var sanitized = data.Appearances?.Sanitize(edit.Appearance, proposedRace);
        var controls = sanitized is null ? null : data.Appearances?.CreationEditor(sanitized);
        if (controls is null) { Post("The edited look could not be validated."); return; }
        FcsRecord? record;
        try
        {
            var choice = new CreationAppearanceChoice(proposedRace.StringId, controls.Female,
                controls.Controls.Select(c => new CreationSlider(c.Tab, c.Kind, c.Label, c.Value)).ToArray(),
                controls.Pickers.Select(p => new CreationPicker(p.Kind, p.Selected)).ToArray());
            record = data.Appearances?.CreationAppearance(basis, choice, look.Loadout?.Seed ?? 0);
        }
        catch (InvalidDataException) { Post("The edited look has an invalid control."); return; }
        if (record is null) { Post("The edited look could not be validated."); return; }

        bool changedRace = proposedRace.StringId != currentRace.StringId;
        var body = changedRace
            ? data.Bodies.Create(proposedRace, data.Db.Find(cold.RecordId), world.Seed, Rng.Key(edit.Character))
            : (Race: cold.Race!, Stats: cold.Stats!, Medical: cold.Medical!);

        // Recreating an organic body keeps the start's hunger and fitted limbs (#515, #360).
        if (changedRace)
        {
            if (!body.Race.IsRobot && cold.Medical is { } previous) body.Medical.Hunger = previous.Hunger;
            foreach (var (limb, item) in Prosthetics(cold.Medical)) body.Medical.FitReplacement(limb, item);
        }

        // The look: the new record wearing what the character wears now.
        var worn = cold.Inventory is { } inventory ? SavedKit.FromInventory(inventory) : [];
        var kit = SavedKit.LoadoutOf(record, worn, data.Db, look.Loadout?.Seed ?? 0);
        if (kit is not null) SavedKit.SetLimbs(kit, body.Medical, data.Db);   // stumps and prosthetics stay on the edited look
        if (kit is not null && data.Appearances?.Create(cold.RecordId, kit) is { } updated)
        {
            if (session is { Paid: false } && edit.Kind == EditKind.Surgery)
            {
                int? cost = table.TryResolveNext(edit.Surgeon, out int surgeon) ? SurgeryCost(world, data.Surgeons, edit.Surgeon, table.Cold(surgeon)!) : null;
                if (cost is null || cost.Value > 0 && (!world.MayManage(edit, cold.Faction, FactionPermission.SpendFunds)
                    || world.Factions.Find(cold.Faction) is not { } funds || funds.Money <= cost.Value || !funds.TryDebit(cost.Value)))
                { Post("Surgery payment is no longer authorized."); return; }
            }
            if (changedRace)
            {
                cold.Race = body.Race;
                cold.Stats = body.Stats;
                cold.Medical = body.Medical;
                cold.FootprintRadius = body.Race.PathfindFootprintRadius;
                ref var hot = ref table.Next[slot];
                hot.MaxSpeed = Bodies.Speed.Run(body.Race, body.Stats, body.Medical, 1);
                hot.WalkSpeed = body.Race.WalkSpeed;
            }
            cold.Appearance = updated;
            cold.LookEdited = true;
        }
        else { Post("The edited look could not be made."); return; }
        string name = edit.Name.Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        if (name.Length > 0) cold.Name = name;
        if (session is not null) session.Applied = true;
    }

    /// <summary>
    /// The race record the edited look names (its first <c>race</c> reference); null when it names none.
    /// </summary>
    static GameRecord? ProposedRace(PopulationData data, FcsRecord appearance) =>
        appearance.References.TryGetValue("race", out var refs) && refs.Count > 0 ? data.Db.Find(refs[0].TargetStringId) : null;

    /// <summary>
    /// The prosthetics a body wears (a start's <c>robot limbs</c>), to fit on the body a race switched in the creator gives it: without them the new
    /// body has natural limbs while their items still sit in the limb sections, so only the default race kept them (#360).
    /// </summary>
    static List<(Meitou.Data.Gameplay.Bodies.LimbReplacement Limb, Items.ItemInstance? Item)> Prosthetics(MedicalState? medical)
    {
        var list = new List<(Meitou.Data.Gameplay.Bodies.LimbReplacement, Items.ItemInstance?)>();
        for (int k = 0; k < 4; k++)
        {
            if (medical?.LimbPart(k) is { Limb: LimbState.Replaced, Replacement: { } limb } part) list.Add((limb, part.ReplacementItem));
        }

        return list;
    }

    /// <summary>
    /// The end of the new game's creation (<see cref="FinishCreation"/>; the editor's close in mode 0, <c>FUN_1405f5d10</c>): every human of the
    /// start's squad has its inventory sections validated for the race it has now (<see cref="Items.InventoryCommands.ValidateSections"/>,
    /// <c>validateInventorySections</c>), so one switched in the editor to a race with <c>no hats</c>, <c>no shirts</c> or <c>no shoes</c> loses what
    /// it wore there, and its worn look and gear are refreshed.
    /// </summary>
    internal static void EndCreation(World world, CharacterAssembly? assembly)
    {
        if (world.Players.RequiresExplicitControl || !world.Player.InCreation || world.Squads.Find(world.Player.Squad) is not { } squad) return;
        foreach (var id in squad.Members)
        {
            if (!world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot) is not { IsPlayer: true, Inventory: { } inventory } cold
                || cold.Faction != world.Player.Faction || SquadRegistry.IsAnimal(cold)) continue;

            int before = inventory.Items.Count;
            Items.InventoryCommands.ValidateSections(world, slot, cold);
            if (inventory.Items.Count != before)
            {
                assembly?.RefreshEquipment(cold);
            }
        }
    }

    public static Guid OpenSession(World world, PlayerId? issuer, CharacterId character, CharacterId surgeon, EditKind kind, bool paid = true)
        => CharacterEditSessions.Open(world, issuer, character, surgeon, kind, paid);


    public static IReadOnlyDictionary<CharacterId, Guid> OpenAnimalSessions(World world, PlayerId? issuer, IReadOnlyList<CharacterId> characters) =>
        CharacterEditSessions.OpenAnimals(world, issuer, characters);


    public static void CloseForPlayer(World world, PlayerId player) => CharacterEditSessions.CloseForPlayer(world, player);

}