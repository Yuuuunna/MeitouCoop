using System.Numerics;
using System.Text.Json.Serialization;
using Meitou.Data.Characters;
using Meitou.Simulation.Population;

namespace Meitou.Simulation;

public sealed record PlayerCreationRequest(Guid Id, ulong Qualification, ulong QualificationRevision, string Start, string? FactionName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CreationMemberChoice>? Choices = null);
public enum CreationRequestStatus { Unknown, PendingPersistence, Committed, Busy }
public sealed record CreationRequestResult(CreationRequestStatus Status, CreationReceipt? Receipt = null);
public sealed class CreationRequestConflictException(string message) : InvalidOperationException(message);
public sealed record CreationMemberPreview(int Member, string Name, string Race, bool Female, bool Animal,
    CharacterAppearance? Appearance, float[] Skills, InventoryItemView[] Equipment, CreationEditorView? Editor);
public sealed record PlayerCreationPreview(Guid Request, string Fingerprint, Vector2 Centre, long Cost, int StartMoney,
    bool IncludesStartResources, CreationMemberPreview[] Members);