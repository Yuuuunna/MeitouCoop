using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Characters;

/// <summary>Presentation only: no generation seed, private loadout, source body file, diagnostics or raw game records.</summary>
public sealed record CharacterDisplay
{
    public required GameRecord Race { get; init; }
    public GameRecord? Character { get; init; }
    public GameRecord? Animal { get; init; }
    public GameRecord? Faction { get; init; }
    public GameRecord? Head { get; init; }
    public GameRecord? Hair { get; init; }
    public GameRecord? Beard { get; init; }
    public bool Female { get; init; }
    public bool Headless { get; init; }
    public bool HeadOnly { get; init; }
    public required string BodyMesh { get; init; }
    public string? BodyTexture { get; init; }
    public string? BodyNormal { get; init; }
    public string? BodyNormalStrong { get; init; }
    public string? BodyNormalSkinny { get; init; }
    public string? BodyMask { get; init; }
    public string? PartMap { get; init; }
    public required CharacterShapeInput Shape { get; init; }
    public Vector3 SkinToneParameter { get; init; }
    public Vector3 HairColour { get; init; }
    public int HiddenLimbs { get; init; }
    public int HiddenParts { get; init; }
    public required CharacterPart[] Parts { get; init; }
    public required BodyLayer[] BodyLayers { get; init; }
    public required CarriedLight[] Lights { get; init; }
    public required Dictionary<string, AttachmentPoint> AttachmentPoints { get; init; }
    public required Dictionary<string, float> PoseWeights { get; init; }
}

public sealed partial class CharacterAppearance
{
    public CharacterDisplay CaptureDisplay(GameDatabase db) => new()
    {
        Race = Race, Character = Character, Animal = Animal, Faction = Faction, Head = Head, Hair = Hair, Beard = Beard,
        Female = Female, Headless = Headless, HeadOnly = HeadOnly, BodyMesh = BodyMesh, BodyTexture = BodyTexture, BodyNormal = BodyNormal,
        BodyNormalStrong = BodyNormalStrong, BodyNormalSkinny = BodyNormalSkinny, BodyMask = BodyMask, PartMap = PartMap,
        Shape = Shape ?? CharacterShape.InputFor(db, this), SkinToneParameter = SkinToneParameter, HairColour = HairColour,
        HiddenLimbs = HiddenLimbs, HiddenParts = HiddenParts, Parts = [.. Parts], BodyLayers = [.. BodyLayers], Lights = [.. Lights],
        AttachmentPoints = new(AttachmentPoints, StringComparer.Ordinal), PoseWeights = new(PoseWeights, StringComparer.Ordinal),
    };

    public static CharacterAppearance FromDisplay(CharacterDisplay d)
    {
        if (d.Race.Type != FcsRecordType.RACE || d.Parts.Length > 512 || d.BodyLayers.Length > 128 || d.Lights.Length > 128 ||
            d.AttachmentPoints.Count > 1024 || d.PoseWeights.Count > 4096 || d.Parts.Any(p => p is null) || d.BodyLayers.Any(p => p is null) ||
            d.Lights.Any(p => p is null) || d.AttachmentPoints.Any(p => p.Value is null))
            throw new InvalidDataException("Invalid character display descriptor.");
        var appearance = new CharacterAppearance
        {
            Race = d.Race, Character = d.Character, Animal = d.Animal, Faction = d.Faction, Head = d.Head, Hair = d.Hair, Beard = d.Beard,
            Female = d.Female, Headless = d.Headless, HeadOnly = d.HeadOnly, BodyMesh = d.BodyMesh, BodyTexture = d.BodyTexture, BodyNormal = d.BodyNormal,
            BodyNormalStrong = d.BodyNormalStrong, BodyNormalSkinny = d.BodyNormalSkinny, BodyMask = d.BodyMask, PartMap = d.PartMap,
            Shape = d.Shape, SkinToneParameter = d.SkinToneParameter, HairColour = d.HairColour, HiddenLimbs = d.HiddenLimbs, HiddenParts = d.HiddenParts,
        };
        appearance.Parts.AddRange(d.Parts); appearance.BodyLayers.AddRange(d.BodyLayers); appearance.Lights.AddRange(d.Lights);
        foreach (var pair in d.AttachmentPoints) appearance.AttachmentPoints.Add(pair.Key, pair.Value);
        foreach (var pair in d.PoseWeights) appearance.PoseWeights.Add(pair.Key, pair.Value);
        return appearance;
    }
}
