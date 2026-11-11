using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation;

public enum TalkHintStatus { Unavailable, Available, StaleAuthority, Unknown }
public sealed record PlayerTalkHint(TalkHintStatus Status, ulong StateRevision);

public sealed partial class World
{
    internal bool TalkBusy(CharacterId npc, PlayerId actor) => _dialogues?.IsBusy(this, npc, actor) == true;
    Vector3? ObservationAnchor(CharacterControl grant)
    {
        if (!Players.Validate(grant) || !Entities.TryResolve(grant.Entity, out var id) || Characters.Cold(id.Slot) is not { } cold) return null;
        int slot = Characters.TryResolveNext(cold.CarriedBy, out int carrier) ? carrier : id.Slot;
        return Characters.Current[slot].Position;
    }

    static bool InsideObservation(Vector3 position, IReadOnlySet<ZoneCoordinate>? zones, IEnumerable<Vector3> anchors, float radiusSquared)
        => (zones is null || zones.Contains(WorldLayout.ZoneOf(position.X, position.Z))) &&
            anchors.Any(a => Vector2.DistanceSquared(new(a.X, a.Z), new(position.X, position.Z)) <= radiusSquared);

    internal bool CanObserveCharacter(PlayerId player, CharacterId target, PlayerViewRequest request, PlayerObservationRules rules)
    {
        rules.Validate();
        if (request.Zones is { Count: > 256 } || request.Zones?.Any(z => !z.IsInsideGrid) == true) throw new ArgumentException("Invalid observation filter.", nameof(request));
        if (!Characters.TryResolveNext(target, out int slot)) return false;
        var anchors = Players.KnownRoles(player).Select(ObservationAnchor).OfType<Vector3>();
        return InsideObservation(Characters.Current[slot].Position, request.Zones?.ToHashSet(), anchors, rules.Radius * rules.Radius);
    }
}