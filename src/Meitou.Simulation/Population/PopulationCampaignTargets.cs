using System.Numerics;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Population;

public sealed partial class PopulationSystem
{
    bool SpecialLeaderBusy(World world, Meitou.Data.Gameplay.CampaignData campaign) =>
        campaign.SpecialLeaders.Any(id => FindUniquePlatoon(world, id)?.Campaign is { Phase: not 2 });

    static IEnumerable<(Vector2 Position, bool Up)> StoredPlayers(World world, int faction)
    {
        foreach (var (position, up) in world.StoredPlayerRoles?.PendingFactionPositions(faction) ?? [])
            yield return (new Vector2(position.X, position.Z), up);
        foreach (var p in world.Platoons.All)
        {
            if (p.State == PlatoonState.Loaded) continue;
            if (p.Stored is { } stored)
            {
                foreach (var member in stored.Members)
                {
                    if (member.Cold.Faction != faction || member.Cold.DiedTick >= 0 || member.Cold.Medical is { Dead: true }) continue;
                    var position = p.Position + new Vector2(member.Position.X, member.Position.Z) - stored.Anchor;
                    yield return (position, member.Cold.Medical is not { Unconscious: true });
                }
            }
            else if (p.Faction == faction && p.Saved is { } saved)
                foreach (var member in saved.Members)
                    if (!member.Dead && !member.Gone && member.Medical is not { Dead: true } && member.Source.Medical?.Bools.GetValueOrDefault("dead") != true)
                        yield return (p.Position, member.Medical is { } medical ? !medical.Unconscious : member.Source.Medical?.Bools.GetValueOrDefault("unconcious") != true);
        }
    }
}