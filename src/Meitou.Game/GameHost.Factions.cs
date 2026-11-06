namespace Meitou.Game;

sealed partial class GameHost
{
    void ConfigureFactionPresentation(PlayerInterface ui)
    {
        ui.FactionName = faction => session.DescribeFaction(faction)?.Name is { Length: > 0 } name ? name : null;
        ui.FactionNameTaken = name => session.FactionCatalogue.Any(f => f.Faction != session.ActiveFaction && f.Name == name);
        ui.PlayerFactionName = () => ui.FactionName?.Invoke(session.ActiveFaction) ?? "";
        ui.FactionInfo = () => session.FactionInformation;
        // Unknown remote identities never become an implicit enemy. Explicit attack commands still undergo server rules.
        ui.FactionRelations = () => session.ActiveFaction < 0 ? [] : session.FactionCatalogue
            .Where(f => f.Faction != session.ActiveFaction && session.CurrentSnapshot.KnownFactions.Contains(f.Faction) && !f.NotReal && f.RecordId != "GENERATION_TEMPLATE_FACTION")
            .Select(f => (Faction: f, Standing: session.StandingTowards(f.Faction))).Where(p => p.Standing is not null)
            .Select(p => (p.Faction.Name, p.Standing!.Relation)).ToArray();
        ui.MapFactionOf = id =>
        {
            if (id is null || session.DescribeFaction(id) is not { } faction) return null;
            var relation = session.StandingTowards(faction.Faction);
            var standing = faction.Faction == session.ActiveFaction ? MapStanding.Player : relation?.Ally == true ? MapStanding.Ally
                : relation?.Relation < -30 ? MapStanding.Enemy : MapStanding.Neutral;
            return new MapFaction(faction.Name, relation?.Relation ?? 0, standing, faction.NotReal);
        };
        ui.MapFactionId = faction => session.DescribeFaction(faction)?.RecordId;
    }
}
