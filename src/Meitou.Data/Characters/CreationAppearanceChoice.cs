using Meitou.Data.Fcs;

namespace Meitou.Data.Characters;

public sealed record CreationSlider(EditorTab Tab, EditorControlKind Kind, string Label, int Value);
public sealed record CreationPicker(EditorControlKind Kind, string? Record);
public sealed record CreationAppearanceChoice(string Race, bool Female, IReadOnlyList<CreationSlider> Sliders, IReadOnlyList<CreationPicker> Pickers);
public sealed record CreationEditorControl(EditorTab Tab, EditorControlKind Kind, string Label, int Min, int Max, int Value);
public sealed record CreationEditorOption(string? Record, string Label);
public sealed record CreationEditorPicker(EditorControlKind Kind, string? Selected, CreationEditorOption[] Options);
public sealed record CreationEditorView(string Race, bool Female, bool CanChangeGender, string[] AllowedRaces,
    CreationEditorControl[] Controls, CreationEditorPicker[] Pickers);

/// <summary>Only editor controls are accepted. All record keys and bounds come from server content.</summary>
public static class CreationAppearanceEditing
{
    public static CreationEditorView? Describe(GameDatabase db, string installRoot, FcsRecord original)
    {
        var draft = new CharacterDraft(db, installRoot, original, "");
        if (draft.Limits is null) return null;
        var controls = Enum.GetValues<EditorTab>().SelectMany(tab => draft.Controls(tab).Where(c => !c.IsPicker)
            .Select(c => new CreationEditorControl(tab, c.Kind, c.Label, c.Min, c.Max, draft.Value(c)))).ToArray();
        var pickers = Enum.GetValues<EditorTab>().SelectMany(draft.Controls).Where(c => c.IsPicker).Select(c => c.Kind).Distinct().Select(kind =>
        {
            var choices = draft.Options(kind);
            return new CreationEditorPicker(kind, choices.ElementAtOrDefault(draft.OptionIndex(kind))?.StringId,
                choices.Select(r => new CreationEditorOption(r?.StringId, r?.Name ?? "None")).ToArray());
        }).ToArray();
        return new(draft.Race.StringId, draft.Female, !draft.Race.GetBool("single gender"),
            PlayableRaces.Of(db).SelectMany(g => g.Races).Select(r => r.StringId).Prepend(draft.Race.StringId).Distinct(StringComparer.Ordinal).ToArray(), controls, pickers);
    }

    public static FcsRecord Apply(GameDatabase db, string installRoot, FcsRecord original, CreationAppearanceChoice choice, int seed)
    {
        var race = db.Find(choice.Race);
        if (race is not { Type: FcsRecordType.Race } || choice.Female && race.GetBool("single gender"))
            throw new InvalidDataException("Invalid creation race/gender.");
        var draft = new CharacterDraft(db, installRoot, original, "");
        draft.SetRace(race, seed); draft.SetFemale(choice.Female, seed);
        if (draft.Limits is null) throw new InvalidDataException("Creation editor limits are unavailable.");
        var sliders = new HashSet<(EditorTab, EditorControlKind, string)>();
        foreach (var value in choice.Sliders)
        {
            if (!Enum.IsDefined(value.Tab) || !Enum.IsDefined(value.Kind) || !sliders.Add((value.Tab, value.Kind, value.Label)))
                throw new InvalidDataException("Invalid or duplicate creation control.");
            var control = draft.Controls(value.Tab).SingleOrDefault(c => !c.IsPicker && c.Kind == value.Kind && c.Label == value.Label);
            if (control is null || value.Value < control.Min || value.Value > control.Max)
                throw new InvalidDataException("Creation control is unavailable or outside its limits.");
            draft.Set(control, value.Value);
        }
        var pickers = new HashSet<EditorControlKind>();
        foreach (var value in choice.Pickers)
        {
            if (value.Kind is not (EditorControlKind.Head or EditorControlKind.HairStyle or EditorControlKind.Beard) || !pickers.Add(value.Kind) ||
                choice.Female && value.Kind == EditorControlKind.Beard && !string.IsNullOrEmpty(value.Record))
                throw new InvalidDataException("Invalid creation picker.");
            var options = draft.Options(value.Kind);
            int index = options.ToList().FindIndex(r => (r?.StringId ?? "") == (value.Record ?? ""));
            if (index < 0) throw new InvalidDataException("Creation picker option is not allowed for this race.");
            draft.SetOption(value.Kind, index);
        }
        return CharacterDraft.Sanitize(db, installRoot, draft.Record, race);
    }
}
