using System.Text.Json;
using Meitou.Content;
using Meitou.Game.Host;
using Meitou.Data;
using Meitou.Networking;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>Headless online admission before the graphical host starts; never creates a local world.</summary>
internal static class OnlineBootstrap
{
    sealed record SavedCreation(string Host, int Port, Guid Account, PlayerCreationRequest Request);
    static readonly TimeSpan PreparationTimeout = TimeSpan.FromMinutes(5);

    public static async Task<RemoteGameSession> ConnectAsync(GameInstall install, string profileFile,
        string? createFaction, ulong? role, CancellationToken token)
    {
        if (role == 0) throw new ArgumentException("--role requires a nonzero persistent EntityId.");
        if (createFaction is not null && (string.IsNullOrWhiteSpace(createFaction) || createFaction.Trim().Length > 30))
            throw new ArgumentException("--create-faction requires a name of 1–30 characters.");
        bool device = !profileFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        var profile = device ? await DeviceConnection.PrepareAsync(install, profileFile, token).ConfigureAwait(false) : ServerConnectionProfile.Read(profileFile);
        if (device)
        {
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{profile.Host.ToLowerInvariant()}:{profile.Port}/{profile.Account}")));
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "connections");
            Directory.CreateDirectory(folder);
            profileFile = Path.Combine(folder, key);
        }
        Console.WriteLine("Online: checking local content and loading the compatible database...");
        token.ThrowIfCancellationRequested();
        var userConfig = UserConfig.Load();
        var hello = ServerCompatibility.CreateHello(install, ServerCompatibility.EngineBuild, profile.Rules, profile.GameplayAssets,
            userConfig.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), userConfig.ModSettings);
        var compatibility = new CompatibilityHandshake(hello).Receive(profile.Hello);
        if (compatibility != CompatibilityFailure.None)
            throw new InvalidDataException($"Connection profile does not match this client's local {compatibility}; check the install, mod order and server profile.");
        var database = GameDatabase.Load(LoadOrder.FromInstall(install));
        token.ThrowIfCancellationRequested();
        Console.WriteLine("Online: connecting and authenticating...");
        var session = await RemoteGameSession.ConnectAsync(profile.Host, profile.Port, hello,
            new AuthenticationRequest(profile.Account, profile.Token), new WorldViewCodec(database), token, deviceIdentity: device).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(PreparationTimeout);
            await WaitAdmissionAsync(session, deadline.Token).ConfigureAwait(false);
            var roles = session.State.Admission.State.Roles;
            var selected = SelectRole(roles, role);
            if (selected is null && role is not null)
                throw new InvalidOperationException("The requested role is dead, unavailable, or is not controlled by this account.");
            if (selected is null)
            {
                if (createFaction is null)
                    throw new InvalidOperationException("This account has no available role. For an eligible new account, use --create-faction <name>; otherwise request a role assignment from the server administrator.");
                var entry = session.State.Admission.State.Entry;
                if (entry?.History != PlayerRoleHistory.NeverAssigned)
                    throw new InvalidOperationException("This account previously held characters. Reconnection cannot grant a free new start; request an approved successor or role assignment.");
                var created = await CreateAsync(session, profile, profileFile, createFaction.Trim(), deadline.Token).ConfigureAwait(false);
                selected = created;
            }
            Console.WriteLine($"Online: preparing role {selected.Value.Value} and receiving the world baseline...");
            session.RequestEntry(selected.Value);
            await WaitReadyAsync(session, selected.Value, deadline.Token).ConfigureAwait(false);
            Console.WriteLine("Online: admitted; authoritative world view is ready.");
            return session;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException("Online preparation timed out. Retry the same profile and creation command: any saved creation request is recovered by its original identity.");
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static EntityId? SelectRole(IEnumerable<PlayerRoleView> roles, ulong? requested) => roles
        .Where(r => requested is null || r.Entity.Value == requested.Value)
        .Where(r => r.Availability is PlayerRoleAvailability.Available or PlayerRoleAvailability.Stored)
        .OrderBy(r => r.Availability == PlayerRoleAvailability.Available ? 0 : 1)
        .ThenBy(r => r.Entity.Value).Select(r => (EntityId?)r.Entity).FirstOrDefault();

    static void CheckConnected(RemoteGameSession session)
    {
        if (session.State.Disconnection is { } error) throw new IOException("The server connection closed during admission. Retry the same profile to recover any committed creation.", error);
    }

    internal static async Task WaitAdmissionAsync(RemoteGameSession session, CancellationToken token)
    {
        bool reported = false;
        while (session.State.Admission.State.Busy)
        {
            CheckConnected(session);
            if (!reported) { Console.WriteLine("Online: resolving pending server persistence..."); reported = true; }
            // Resume only this account's previously accepted operation; never create a replacement.
            await session.LookupAsync(new(Guid.NewGuid(), Guid.Empty, Resume: true), token).ConfigureAwait(false);
            await Task.Delay(500, token).ConfigureAwait(false);
            session.RefreshAdmission();
        }
        CheckConnected(session);
    }

    internal static async Task WaitReadyAsync(RemoteGameSession session, EntityId role, CancellationToken token)
    {
        int polls = 0;
        bool observedRole = false;
        while (!session.State.CanControl || session.State.View is null)
        {
            CheckConnected(session);
            var state = session.State.Admission.State;
            var visible = state.Roles.FirstOrDefault(r => r.Entity == role);
            if (!state.Busy && (visible?.Availability is PlayerRoleAvailability.Dead or PlayerRoleAvailability.Unavailable || observedRole && visible is null))
                throw new InvalidOperationException("The selected role died or lost authorization during preparation. No replacement character was created.");
            observedRole |= visible is not null;
            await Task.Delay(250, token).ConfigureAwait(false);
            if (++polls % 8 == 0) session.RefreshAdmission();
        }
    }

    static async Task<EntityId> CreateAsync(RemoteGameSession session, ServerConnectionProfile profile,
        string profileFile, string faction, CancellationToken token)
    {
        string journal = Path.GetFullPath(profileFile) + ".creation.json";
        // One launcher at a time may choose the durable request for this profile. No credential is stored here.
        using var journalLock = new FileStream(journal + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        SavedCreation saved;
        if (File.Exists(journal))
        {
            saved = JsonSerializer.Deserialize<SavedCreation>(await File.ReadAllBytesAsync(journal, token).ConfigureAwait(false))
                ?? throw new InvalidDataException("Invalid saved online creation request.");
            if (saved.Host != profile.Host || saved.Port != profile.Port || saved.Account != profile.Account
                || saved.Request is null || saved.Request.Id == Guid.Empty || saved.Request.FactionName != faction)
                throw new InvalidOperationException("The saved creation request belongs to a different server/account/faction. Retry its original profile and faction name; do not replace an unresolved request.");
            Console.WriteLine("Online: recovering the saved creation request...");
            var recovered = await LookupUntilSettledAsync(session, saved.Request.Id, token).ConfigureAwait(false);
            if (recovered.Outcome == CreationOutcome.Committed) return ReceiptRole(recovered);
            if (recovered.Outcome != CreationOutcome.Unknown) throw CreationError(recovered);
        }
        else
        {
            Console.WriteLine("Online: requesting the server's default new-player start...");
            var catalogReply = await session.CatalogAsync(new(Guid.NewGuid()), token).ConfigureAwait(false);
            while (catalogReply.Outcome == CreationOutcome.Busy)
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                catalogReply = await session.CatalogAsync(new(Guid.NewGuid()), token).ConfigureAwait(false);
            }
            if (catalogReply is not { Outcome: CreationOutcome.Catalog, Catalog: { } catalog }) throw CreationError(catalogReply);
            if (catalog.Qualification.Grant.Kind != CreationQualificationKind.InitialStart || catalog.Starts.Length == 0)
                throw new InvalidOperationException("The server did not offer an independent new-player start.");
            saved = new(profile.Host, profile.Port, profile.Account,
                new(Guid.NewGuid(), catalog.Qualification.Grant.Id, catalog.Qualification.Grant.Revision, catalog.Starts[0].Id, faction));
            // The request reaches disk before any commit can be sent, including after a lost reply or process crash.
            WriteJournal(journal, saved);
        }
        var preview = await session.PreviewAsync(new(Guid.NewGuid(), 1, saved.Request), token).ConfigureAwait(false);
        if (preview.Outcome != CreationOutcome.Preview) throw CreationError(preview);
        Console.WriteLine($"Online: creating {preview.Preview!.Members.Length} character(s) using server-generated default appearances...");
        CreationReply commit;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            try { commit = await session.CommitAsync(new(Guid.NewGuid(), 1, saved.Request), wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Accepted commit may already be durable. Query its original identity; never choose a replacement ID.
                commit = await LookupUntilSettledAsync(session, saved.Request.Id, token).ConfigureAwait(false);
            }
        }
        if (commit.Outcome is CreationOutcome.PendingPersistence or CreationOutcome.Busy)
        {
            Console.WriteLine("Online: waiting for durable creation persistence...");
            commit = await LookupUntilSettledAsync(session, saved.Request.Id, token).ConfigureAwait(false);
        }
        if (commit.Outcome != CreationOutcome.Committed) throw CreationError(commit);
        return ReceiptRole(commit);
    }

    internal static async Task<CreationReply> LookupUntilSettledAsync(RemoteGameSession session, Guid request, CancellationToken token)
    {
        while (true)
        {
            CheckConnected(session);
            var reply = await session.LookupAsync(new(Guid.NewGuid(), request, Resume: true), token).ConfigureAwait(false);
            if (reply.Outcome is not (CreationOutcome.PendingPersistence or CreationOutcome.Busy)) return reply;
            await Task.Delay(1000, token).ConfigureAwait(false);
        }
    }

    static EntityId ReceiptRole(CreationReply reply) => reply.Receipt is { Roles.Length: > 0 } receipt
        ? receipt.Roles[0] : throw new InvalidDataException("The server's committed creation has no role receipt.");

    static InvalidOperationException CreationError(CreationReply reply) => new(
        $"Server creation returned {reply.Outcome} ({reply.Failure}). The saved request has been retained; retry the same command to recover it. An existing/dead role never receives an automatic free restart.");

    static void WriteJournal(string path, SavedCreation value)
    {
        string temporary = path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(file, value);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
