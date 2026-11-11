using System.Text.Json;
using Meitou.Data.Fcs;
using Meitou.Simulation.Population;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

/// <summary>
/// Natural-resource ownership is world state independent of queued orders and transient work-site versions.
/// </summary>
static class NaturalClaimsSave
{
    internal const string Key = "meitou natural claims";
    sealed record Claim(string Site, string Faction);
    sealed record Document(int Version, Claim[] Claims);

    internal static void Write(FcsRecord camera, World world, PopulationData data)
    {
        if (world.SystemOf<ProductionSystem>() is not { } production) return;
        var claims = production.NaturalClaims.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c =>
            c.Owner >= 0 && c.Owner < data.Factions.Count ? new Claim(c.Id, data.Factions[c.Owner].Id)
            : throw new InvalidDataException("A natural resource has an unknown owner.")).ToArray();
        if (claims.Length == 0) camera.Strings.Remove(Key);
        else camera.Strings[Key] = JsonSerializer.Serialize(new Document(1, claims));
    }

    internal static void Read(FcsRecord camera, World world, PopulationData data)
    {
        if (!camera.Strings.TryGetValue(Key, out var text)) return;
        Document saved;
        try { saved = JsonSerializer.Deserialize<Document>(text) ?? throw new InvalidDataException("Missing natural ownership data."); }
        catch (JsonException error) { throw new InvalidDataException("Invalid natural ownership data.", error); }
        if (saved.Version != 1 || saved.Claims is null) throw new InvalidDataException("Unsupported natural ownership data.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var claims = new List<(string Id, int Owner)>();
        foreach (var claim in saved.Claims)
        {
            if (claim is null || string.IsNullOrEmpty(claim.Site) || string.IsNullOrEmpty(claim.Faction)
                || !ProductionSystem.IsNatural(claim.Site) || !seen.Add(claim.Site))
                throw new InvalidDataException("Invalid or duplicate natural ownership claim.");
            int owner = data.FactionIndex(claim.Faction);
            if (owner < 0) throw new InvalidDataException("Natural ownership names an unknown faction.");
            claims.Add((claim.Site, owner));
        }
        if (claims.Count > 0 && world.SystemOf<ProductionSystem>() is null) throw new InvalidDataException("Natural ownership requires production systems.");
        if (world.SystemOf<ProductionSystem>() is { } production)
            foreach (var claim in claims) production.RestoreNaturalClaim(world, claim.Id, claim.Owner);
    }
}