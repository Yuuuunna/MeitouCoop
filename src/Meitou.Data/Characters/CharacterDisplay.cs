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
