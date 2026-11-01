namespace Meitou.Simulation;

/// <summary>What a row of the Faction tab's info panel is (docs/game/ui-screens.md "Faction tab"): it picks the row's colours.</summary>
public enum FactionInfoKind : byte { Heading, Name, Detail, Spacer, Queued, Rule }

/// <summary>
/// A row of the Faction tab's Active Contracts or Upcoming Events: a label and a value, and for a time the game hours left (<see cref="Hours"/>, NaN: none; the interface
/// prints it as <c>TimeOfDay::getTotalTimeString</c> does).
/// </summary>
public readonly record struct FactionInfoLine(FactionInfoKind Kind, string Label, string Value = "", double Hours = double.NaN);
/// <summary>Wire-safe faction panel row; absence of a numeric countdown uses null instead of NaN.</summary>
public sealed record FactionInfoView(FactionInfoKind Kind, string Label, string Value, double? Hours)
{
    public static FactionInfoView From(FactionInfoLine line) => new(line.Kind, line.Label, line.Value, double.IsFinite(line.Hours) ? line.Hours : null);
    public FactionInfoLine ToLine() => new(Kind, Label, Value, Hours ?? double.NaN);
}

public sealed partial class World
{
    /// <summary>The AI signals of the contracts the Faction tab lists: <c>Contract_HiredAlly</c> (bodyguards) and <c>Contract_DefendPlayerBase</c> (base defenders).</summary>
    const int HiredAllySignal = 12, DefendBaseSignal = 15;

    /// <summary>
    /// Active Contracts (<c>FUN_1404927f0</c>, <c>getGUIData</c> of the two contract packages): per squad running one for a player character, "Bodyguard Contract" or
    /// "Base Defence Contract" with the hired squad's faction, "-Protecting:" and the target's name, "-Expires in:" and the time left, then a rule. One entry per hired squad
    /// (the original lists each hired character's package; Meitou's packages are the squad's).
    /// </summary>
    public List<FactionInfoLine> ActiveContracts(Func<int, string?> factionName, int? protectedFaction = null, Func<CharacterId, bool>? allowedTarget = null)
    {
        var lines = new List<FactionInfoLine>();
        var table = Characters;
        foreach (var squad in Squads.All)
        {
            if (squad.Ai is not { Contract.Signal: HiredAllySignal or DefendBaseSignal } board || board.MissionTarget is not { IsNone: false } target
                || !table.TryResolveNext(target, out int slot) || table.Cold(slot) is not { IsPlayer: true } protectedOne) continue;
            if (protectedFaction is { } faction && protectedOne.Faction != faction || allowedTarget is not null && !allowedTarget(target)) continue;
            lines.Add(new(FactionInfoKind.Name, board.Contract.Signal == HiredAllySignal ? "Bodyguard Contract" : "Base Defence Contract", factionName(squad.Faction) ?? ""));
            lines.Add(new(FactionInfoKind.Detail, "-Protecting:", protectedOne.Name));
            lines.Add(new(FactionInfoKind.Detail, "-Expires in:", "", board.ContractEnd - GameHours));
            lines.Add(new(FactionInfoKind.Rule, "-----------"));
        }
        return lines;
    }
}

public sealed partial class PopulationSystem
{
    /// <summary>
    /// Upcoming Events (<c>FUN_1407f2ef0</c> per faction's <c>FactionWarMgr</c>, <c>FUN_1407ac9c0</c>): for a faction with running or queued campaigns a "-{name}-" heading; per running
    /// campaign against the player's faction that is not ending, its name, "Target:" with the target's faction and name (??? when gone) and a spacer; per queued one (no player
    /// filter) its display name and the time until it starts.
    /// </summary>
    public List<FactionInfoLine> UpcomingEvents(World world, Func<int, string?> factionName, int? targetFaction = null)
    {
        var lines = new List<FactionInfoLine>();
        int player = targetFaction ?? world.Player.Faction;
        foreach (var war in Wars)
        {
            if (war.Actives.Count == 0 && war.Requests.Count == 0) continue;
            if (targetFaction is not null && !war.Actives.Any(i => i.Phase != 2 && i.Enemy == player) && !war.Requests.Any(r => r.Enemy == player)) continue;
            lines.Add(new(FactionInfoKind.Heading, "-" + (factionName(war.Faction) ?? "") + "-"));
            foreach (var inst in war.Actives)
            {
                // Phase 2 is Meitou's ending phase (the original's RETREATING and VICTORY are left out).
                if (inst.Phase == 2 || player < 0 || inst.Enemy != player) continue;
                lines.Add(new(FactionInfoKind.Name, CampaignOf(inst.CampaignId)?.Name ?? inst.CampaignId));
                string where = Exists(world, inst.Target) && (targetFaction is null || FactionOfTown(world, inst.Target) == player) ? NameOf(world, inst.Target) : "???";
                lines.Add(new(FactionInfoKind.Detail, "Target:", (factionName(inst.Enemy) ?? "") + "- " + where));
                lines.Add(new(FactionInfoKind.Spacer, ""));
            }
            foreach (var request in war.Requests)
                if (targetFaction is null || request.Enemy == player)
                    lines.Add(new(FactionInfoKind.Queued, CampaignOf(request.CampaignId)?.DisplayName ?? request.CampaignId, "", request.Due - world.GameHours));
        }
        return lines;
    }
}
