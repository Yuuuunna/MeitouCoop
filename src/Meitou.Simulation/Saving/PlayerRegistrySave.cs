using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// Versioned authority and faction-resource checkpoint metadata. Does not replace character records or account authentication.
/// </summary>
public static class PlayerRegistrySave
{
    sealed record Document(int Version, FactionCatalogueState Catalogue, FactionDiplomacyState Diplomacy, ulong EntityHighWater,
        ulong[] Entities, PlayerRegistryState Authority, SavedFactionResources[] Resources, CreationQualificationState? Qualifications = null, InvitationState? Invitations = null,
        CampaignTargetState? Campaigns = null, Dialogue.AreaArrivalMemory[]? AreaArrivals = null);
    static readonly JsonSerializerOptions Json = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static byte[] Capture(World world, PopulationData data)
    {
        var authority = world.Players.Capture();
        world.Players.ValidateState(authority, data.Factions.Count);
        var resources = FactionResourceSave.Capture(world);
        FactionResourceSave.Validate(resources, data);
        var catalogue = FactionCatalogueSave.Capture(data);
        var diplomacy = data.Relations.Capture();
        FactionRelations.ValidateState(diplomacy, catalogue.Factions.Select(f => f.Id).ToArray());
        var qualifications = world.CreationQualifications.Capture();
        CreationQualifications.Validate(qualifications, authority, data.Factions.Count, world.Entities.All.ToHashSet());
        var invitations = world.Invitations.Capture();
        FactionInvitations.Validate(invitations, authority, qualifications, data.Factions.Count, world.Entities.All.ToHashSet());
        var campaigns = CampaignTargetSave.Capture(world);
        CampaignTargetSave.Validate(campaigns, data);
        var arrivals = world.Systems.OfType<Dialogue.DialogueSystem>().FirstOrDefault()?.CaptureAreaArrivals() ?? [];
        return JsonSerializer.SerializeToUtf8Bytes(new Document(9, catalogue, diplomacy,
            world.Entities.LastAllocated, world.Entities.All.Select(e => e.Value).ToArray(), authority, resources, qualifications, invitations, campaigns, arrivals), Json);
    }

    /// <summary>
    /// On exclusive candidate data, before SaveLoader.Load. Does not restore diplomacy until after the legacy loader.
    /// </summary>
    public static void PrepareCatalogue(PopulationData data, ReadOnlySpan<byte> bytes)
    {
        var document = Decode(bytes);
        FactionCatalogueSave.Restore(data, document.Catalogue);
    }

    /// <summary>
    /// Player-faction classifications for the world loader, after catalogue preparation.
    /// </summary>
    public static IReadOnlySet<string>? PlayerFactionIds(PopulationData data, ReadOnlySpan<byte> bytes)
    {
        var document = Decode(bytes);
        FactionCatalogueSave.Validate(data, document.Catalogue, complete: true);
        if (!document.Authority.ExplicitControl) return null;
        var factions = document.Authority.PlayerOwnedFactions;
        if (factions is null || factions.Any(f => f < 0 || f >= data.Factions.Count) || factions.Distinct().Count() != factions.Length)
            throw new InvalidDataException("Invalid player faction classification.");
        return factions.Select(f => data.Factions[f].Id).ToHashSet(StringComparer.Ordinal);
    }

    static Document Decode(ReadOnlySpan<byte> bytes)
    {
        Document document;
        try { document = JsonSerializer.Deserialize<Document>(bytes, Json) ?? throw new InvalidDataException("Empty player registry."); }
        catch (JsonException e) { throw new InvalidDataException("Malformed player registry.", e); }
        if (document.Version is not (2 or 3 or 4 or 5 or 6 or 7 or 8 or 9) || document.Catalogue?.Factions is null || document.Catalogue.Factions.Any(f => f is null) ||
            document.Diplomacy is null || document.Entities is null || document.Authority is null || document.Resources is null)
            throw new InvalidDataException("Unsupported player registry schema.");
        if (document.Version < 4)
        {
            if (document.Qualifications is { } newer && (newer.LastId != 0 || newer.Revision != 0 || newer.Grants is null || newer.Grants.Length != 0))
                throw new InvalidDataException("Legacy metadata cannot contain newer creation approvals.");
            if (document.Authority.Players is null || document.Authority.Players.Any(p => p is null || p.Memberships is null ||
                p.Memberships.Any(m => m is null || (m.Permissions & FactionPermission.CreateCharacters) != 0)))
                throw new InvalidDataException("Legacy metadata cannot introduce character-creation permission.");
        }
        if (document.Version == 2)
        {
            if (document.Authority.Version != 1 || document.Authority.Players is null || document.Authority.Controls is null || document.Authority.Players.Any(p => p is null))
                throw new InvalidDataException("Invalid legacy player registry.");
            // No current grant does not prove that a role was never assigned. Preserve that uncertainty on migration.
            document = document with
            {
                Version = 3,
                Authority = document.Authority with
                {
                    Version = 2,
                    Players = document.Authority.Players.Select(p => p with
                    {
                        RoleHistory = document.Authority.Controls.Any(c => c.Player == p.Id)
                        ? PlayerRoleHistory.Assigned : PlayerRoleHistory.Unknown
                    }).ToArray(),
                }
            };
        }
        if (document.Authority.Version != (document.Version >= 8 ? 4 : document.Version >= 6 ? 3 : 2) || document.Authority.Players is null ||
            document.Authority.Players.Any(p => p is null || !Enum.IsDefined(p.RoleHistory)))
            throw new InvalidDataException("Missing or invalid player role history.");
        if (document.Version < 4) document = document with { Version = 4, Qualifications = new CreationQualificationState(1, 0, 0, []) };
        if (document.Qualifications is null) throw new InvalidDataException("Missing creation qualification state.");
        if (document.Version < 5)
        {
            if (document.Qualifications.Version != 1 || document.Qualifications.Grants is null ||
                document.Qualifications.Grants.Any(g => g is null || g.Status == CreationQualificationStatus.Consumed) || document.Qualifications.Receipts is { Length: > 0 })
                throw new InvalidDataException("Legacy qualifications cannot contain creation receipts.");
            document = document with { Version = 5, Qualifications = document.Qualifications with { Version = 2, Receipts = [] } };
        }
        if (document.Version < 6)
        {
            if (document.Authority.Players.Any(p => p.Account is not null)) throw new InvalidDataException("Legacy metadata cannot introduce account bindings.");
            document = document with { Version = 6, Authority = document.Authority with { Version = 3 } };
        }
        if (document.Version < 7)
        {
            if (document.Invitations is { } old && (old.Version != 1 || old.Invitations is null || old.Invitations.Length != 0))
                throw new InvalidDataException("Legacy metadata cannot introduce invitations.");
            document = document with { Version = 7, Invitations = new InvitationState(1, []) };
        }
        if (document.Invitations is null) throw new InvalidDataException("Missing invitation state.");
        if (document.Version < 8)
        {
            if (document.Authority.Retired is { Length: > 0 }) throw new InvalidDataException("Legacy metadata cannot introduce retired roles.");
            document = document with { Version = 8, Authority = document.Authority with { Version = 4, Retired = [] } };
        }
        if (document.Authority.Retired is null) throw new InvalidDataException("Missing retired-role state.");
        if (document.Version < 9)
        {
            if (document.Campaigns is { } campaigns && (campaigns.Clocks is null || campaigns.Clocks.Length != 0)
                || document.AreaArrivals is { Length: > 0 })
                throw new InvalidDataException("Legacy metadata cannot introduce campaign target clocks or arrival memory.");
            document = document with { Version = 9, Campaigns = new CampaignTargetState([]), AreaArrivals = [] };
        }
        if (document.Campaigns is null || document.AreaArrivals is null || document.AreaArrivals.Any(a => a is null || a.Leader == 0
            || string.IsNullOrEmpty(a.GroupId) || !document.Entities.Contains(a.Leader)) || document.AreaArrivals.Select(a => a.Leader).Distinct().Count() != document.AreaArrivals.Length)
            throw new InvalidDataException("Missing or invalid campaign/arrival state.");
        FactionRelations.ValidateState(document.Diplomacy, document.Catalogue.Factions.Select(f => f.Id).ToArray());
        return document;
    }

    static Document ValidateWorld(World world, PopulationData data, ReadOnlySpan<byte> bytes)
    {
        var document = Decode(bytes);
        FactionCatalogueSave.Validate(data, document.Catalogue, complete: true);
        if (document.EntityHighWater != world.Entities.LastAllocated || !document.Entities.SequenceEqual(world.Entities.All.Select(e => e.Value)))
            throw new InvalidDataException("Player registry does not match the world's faction catalogue or entity reservations.");
        FactionResourceSave.Validate(document.Resources, data);
        CampaignTargetSave.Validate(document.Campaigns!, data);
        if (document.Authority.PlayerOwnedFactions is null || document.Authority.PlayerOwnedFactions.Any(f => !document.Resources.Any(r => r.Faction == f)))
            throw new InvalidDataException("A player faction has no resource record.");
        world.Players.ValidateState(document.Authority, data.Factions.Count);
        CreationQualifications.Validate(document.Qualifications!, document.Authority, data.Factions.Count, world.Entities.All.ToHashSet());
        FactionInvitations.Validate(document.Invitations!, document.Authority, document.Qualifications!, data.Factions.Count, world.Entities.All.ToHashSet());
        return document;
    }

    /// <summary>
    /// Candidate loading only: restore faction resources, research and ownership classification before asset initialization.
    /// </summary>
    internal static void PrepareResources(World world, PopulationData data, ReadOnlySpan<byte> bytes)
    {
        var document = ValidateWorld(world, data, bytes);
        foreach (int faction in document.Authority.PlayerOwnedFactions) world.Factions.GetOrCreate(faction).PlayerOwned = true;
        FactionResourceSave.Restore(world, document.Resources, data.Db);
    }

    /// <summary>
    /// Use the same checkpoint's already-loaded world; a different catalogue or entity allocator is rejected.
    /// </summary>
    public static void Restore(World world, PopulationData data, ReadOnlySpan<byte> bytes)
    {
        var document = ValidateWorld(world, data, bytes);
        world.Players.Restore(document.Authority, data.Factions.Count);
        FactionResourceSave.Restore(world, document.Resources, data.Db);
        data.Relations.Restore(document.Diplomacy);
        world.CreationQualifications.Restore(document.Qualifications!, data.Factions.Count);
        world.Invitations.Restore(document.Invitations!);
        CampaignTargetSave.Restore(world, data, document.Campaigns!);
        foreach (var dialogue in world.Systems.OfType<Dialogue.DialogueSystem>()) dialogue.RestoreAreaArrivals(document.AreaArrivals!);
    }
}