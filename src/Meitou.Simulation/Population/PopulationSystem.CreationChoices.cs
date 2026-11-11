using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Editing;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Saving;

namespace Meitou.Simulation.Population;

public sealed record CreationMemberChoice(int Member, string? Name = null, CreationAppearanceChoice? Appearance = null);

public sealed partial class PopulationSystem
{
    public CreationMemberPreview[] PreviewPlayerSquad(World world, PreparedPlayerSquad prepared, NewGameStart start)
    {
        CreationBoundary(world);
        if (!ReferenceEquals(prepared.World, world) || !ReferenceEquals(prepared.Producer, this) || prepared.Used)
            throw new InvalidOperationException("Invalid creation preview recipe.");
        ValidatePrepared(world, prepared);
        return prepared.Built.Members.Select((member, index) =>
        {
            var hot = new CharacterHot { Health = 100, Mode = SpeedMode.Free, Task = (byte)CharacterTask.Idle };
            var cold = AssembleMember(world, member, prepared.Faction, 0, true, new CharacterId(member.Plan.Index, 1), ref hot, prepared.Seed);
            var look = cold.Appearance;
            var appearance = look?.Loadout?.Appearance.Record ?? look?.Body?.Record;
            var editor = look is { Animal: null } && appearance is not null ? _data.Appearances?.CreationEditor(appearance) : null;
            if (editor is not null && start.ForceRace.Count != 0) editor = editor with { AllowedRaces = editor.AllowedRaces.Where(id => start.ForceRace.Any(r => r.Id == id)).ToArray() };
            var equipment = cold.Inventory?.Shown().Select(s => new InventoryItemView(s.Index, s.Item.Record, s.Item.Name, s.Item.Section, s.Item.X, s.Item.Y,
                s.Item.Width, s.Item.Height, s.Item.Quantity, s.Item.Weight, s.Item.Quality)
            {
                Material = s.Item.MaterialId,
                Colour = s.Item.ColourId,
                Company = s.Item.CompanyId,
                Level = s.Item.Level,
                Charge = s.Item.ChargeFraction,
                Charges = s.Item.Charges,
                MaxCharges = s.Item.MaxCharges
            }).ToArray() ?? [];
            return new CreationMemberPreview(index, cold.Name, cold.Race?.StringId ?? look?.Race.StringId ?? "", look?.Female ?? false, member.Plan.IsAnimal,
                look, cold.Stats?.ToArray() ?? [], equipment, editor);
        }).ToArray();
    }

    internal PreparedPlayerSquad CustomizePlayerSquad(World world, PreparedPlayerSquad prepared, NewGameStart start, IReadOnlyList<CreationMemberChoice>? choices)
    {
        CreationBoundary(world);
        if (!ReferenceEquals(prepared.World, world) || !ReferenceEquals(prepared.Producer, this) || prepared.Used)
            throw new InvalidOperationException("Invalid creation recipe.");
        var members = prepared.Built.Members.ToArray();
        var indices = new HashSet<int>();
        foreach (var choice in choices ?? [])
        {
            if (choice.Member < 0 || choice.Member >= members.Length || !indices.Add(choice.Member)) throw new InvalidDataException("Invalid or duplicate creation member.");
            var member = members[choice.Member];
            string? name = choice.Name;
            if (name is not null && (name.Trim().Length == 0 || name.Length > CharacterEditing.MaxNameLength || name.Any(char.IsControl)))
                throw new InvalidDataException("Invalid character name.");
            var look = member.Appearance;
            if (choice.Appearance is { } appearance)
            {
                if (look is null || look.Animal is not null || _data.Appearances is null) throw new InvalidDataException("This character has no editable appearance.");
                var race = _data.Db.Find(appearance.Race);
                if (race is not { Type: FcsRecordType.Race } ||
                    appearance.Race != look.Race.StringId && !PlayableRaces.Of(_data.Db).Any(g => g.Races.Any(r => r.StringId == appearance.Race)))
                    throw new InvalidDataException("The selected race is not playable.");
                var original = look.Loadout?.Appearance.Record ?? look.Body?.Record ?? throw new InvalidDataException("No server appearance template.");
                var edited = _data.Appearances.CreationAppearance(original, appearance, look.Loadout?.Seed ?? (int)(prepared.Seed & int.MaxValue))
                    ?? throw new InvalidDataException("Server appearance validation is unavailable.");
                if (CharacterDraft.RaceOf(_data.Db, edited)?.StringId != appearance.Race || edited.Bools.GetValueOrDefault("sex female") != appearance.Female)
                    throw new InvalidDataException("Appearance validation changed race/gender.");
                // Equipment is rebuilt only from the server-generated recipe, never from the incoming editor payload.
                var inventory = _data.Items.Build(_data.Db.Find(member.Plan.RecordId), look.Loadout);
                var kit = SavedKit.LoadoutOf(edited, SavedKit.FromInventory(inventory), _data.Db, look.Loadout?.Seed ?? 0)
                    ?? throw new InvalidDataException("Edited loadout could not be assembled.");
                look = _data.Appearances.Create(member.Plan.RecordId, kit) ?? throw new InvalidDataException("Edited appearance could not be assembled.");
                if (look.Race.StringId != appearance.Race || look.Female != appearance.Female) throw new InvalidDataException("Edited appearance differs from the validated choice.");
            }
            members[choice.Member] = member with { Appearance = look, CreationName = name?.Trim(), LookEdited = choice.Appearance is not null };
        }
        if (start.ForceRace.Count > 0)
            foreach (var member in members)
                if (!member.Plan.IsAnimal && _assembly.RaceOf(member.Plan.RecordId, member.Appearance) is { } race && !start.ForceRace.Any(r => r.Id == race.StringId))
                    throw new InvalidDataException("The selected race is not allowed by this start.");
        var result = new PreparedPlayerSquad(this, world, prepared.Built with { Members = members.ToList() }, prepared.Town, prepared.Seed, prepared.FactionIdentity)
        { NewFaction = prepared.NewFaction };
        ValidatePrepared(world, result); prepared.Used = true;
        return result;
    }
}