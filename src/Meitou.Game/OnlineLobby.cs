using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meitou.Content;
using Meitou.Data;
using Meitou.Networking;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>One graphical admission attempt. Mutations are journalled before transmission.</summary>
internal sealed class OnlineLobby(GameInstall install, GameDatabase database) : IAsyncDisposable
{
    readonly CancellationTokenSource lifetime = new();
    RemoteGameSession? connection;
    FileStream? journalLock;
    string journal = "";
    public CreationCatalog? Catalog { get; private set; }
    public PlayerCreationPreview? Preview { get; private set; }
    public PlayerCreationRequest? Request { get; private set; }
    public bool Ready { get; private set; }
    public CancellationToken Token => lifetime.Token;
    public void Cancel() => lifetime.Cancel();

    public async Task ConnectAsync(string address)
    {
        var profile = await DeviceConnection.PrepareAsync(install, address, Token).ConfigureAwait(false);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{profile.Host.ToLowerInvariant()}:{profile.Port}/{profile.Account}")));
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "connections");
        Directory.CreateDirectory(folder);
        journal = Path.Combine(folder, key + ".creation.json");
        journalLock = new FileStream(journal + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var userConfig = UserConfig.Load();
        var hello = ServerCompatibility.CreateHello(install, ServerCompatibility.EngineBuild, profile.Rules, profile.GameplayAssets,
            userConfig.TrustedCodeMods.ToHashSet(StringComparer.Ordinal), userConfig.ModSettings);
        if (new CompatibilityHandshake(hello).Receive(profile.Hello) is var mismatch && mismatch != CompatibilityFailure.None)
            throw new InvalidDataException($"Local content does not match the connection profile: {mismatch}.");
        connection = await RemoteGameSession.ConnectAsync(profile.Host, profile.Port, hello,
            new(profile.Account, profile.Token), new WorldViewCodec(database), Token, deviceIdentity: true).ConfigureAwait(false);
        await PrepareAdmissionAsync(connection, journal).ConfigureAwait(false);
    }

    internal async Task PrepareAdmissionAsync(RemoteGameSession authenticated, string journalPath)
    {
        connection = authenticated;
        journal = journalPath;
        await OnlineBootstrap.WaitAdmissionAsync(connection, Token).ConfigureAwait(false);
        if (OnlineBootstrap.SelectRole(connection.State.Admission.State.Roles, null) is { } role)
        { await EnterAsync(role).ConfigureAwait(false); return; }
        if (File.Exists(journal))
        {
            Request = JsonSerializer.Deserialize<PlayerCreationRequest>(await File.ReadAllBytesAsync(journal, Token).ConfigureAwait(false))
                ?? throw new InvalidDataException("Invalid saved creation request.");
            var recovered = await OnlineBootstrap.LookupUntilSettledAsync(connection, Request.Id, Token).ConfigureAwait(false);
            if (recovered.Outcome == CreationOutcome.Committed) { await EnterAsync(recovered.Receipt!.Roles[0]).ConfigureAwait(false); return; }
            if (recovered.Outcome != CreationOutcome.Unknown) throw Failure(recovered);
            // An unanswered commit is immutable: reconnect resumes this exact draft.
            await CommitAsync(Request, recovering: true).ConfigureAwait(false);
            if (Ready) return;
        }
        CreationReply reply;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(Token))
        {
            wait.CancelAfter(TimeSpan.FromMinutes(5));
            do
            {
                reply = await connection.CatalogAsync(new(Guid.NewGuid()), wait.Token).ConfigureAwait(false);
                if (reply.Outcome == CreationOutcome.Busy) await Task.Delay(500, wait.Token).ConfigureAwait(false);
            } while (reply.Outcome == CreationOutcome.Busy);
        }
        if (reply is not { Outcome: CreationOutcome.Catalog, Catalog: { } catalog }) throw Failure(reply);
        if (catalog.Starts.Length == 0) throw new InvalidOperationException("No starts are available for this account. Ask the server administrator for a role assignment.");
        Catalog = catalog;
    }

    public async Task PreviewAsync(PlayerCreationRequest request)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Token);
        wait.CancelAfter(TimeSpan.FromMinutes(2));
        var reply = await connection!.PreviewAsync(new(Guid.NewGuid(), 1, request), wait.Token).ConfigureAwait(false);
        if (reply.Outcome != CreationOutcome.Preview) throw Failure(reply);
        Request = request;
        Preview = reply.Preview;
    }

    public async Task CommitAsync(PlayerCreationRequest request, bool recovering = false)
    {
        if (!File.Exists(journal))
        {
            string temporary = journal + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, request); file.Flush(true); }
            File.Move(temporary, journal);
        }
        Request = request;
        CreationReply reply;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(Token))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            try { reply = await connection!.CommitAsync(new(Guid.NewGuid(), 1, request), wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!Token.IsCancellationRequested)
            { reply = await OnlineBootstrap.LookupUntilSettledAsync(connection!, request.Id, Token).ConfigureAwait(false); }
        }
        if (reply.Outcome is CreationOutcome.Busy or CreationOutcome.PendingPersistence)
            reply = await OnlineBootstrap.LookupUntilSettledAsync(connection!, request.Id, Token).ConfigureAwait(false);
        if (reply.Outcome != CreationOutcome.Committed)
        {
            // A definite rejection created nothing; an interrupted/unknown operation retains the journal.
            if (reply.Outcome == CreationOutcome.Rejected && reply.Failure != CreationFailure.Persistence) File.Delete(journal);
            // A saved draft can outlive its qualification (for example after a server reset).
            // Only a definitive qualification rejection permits returning to the catalogue;
            // never replace an unknown or still-pending durable operation.
            if (recovering && reply is { Outcome: CreationOutcome.Rejected, Failure: CreationFailure.Qualification })
            {
                Console.WriteLine("online    saved creation qualification rejected; requesting current catalogue");
                Request = null;
                return;
            }
            throw Failure(reply);
        }
        await EnterAsync(reply.Receipt!.Roles[0]).ConfigureAwait(false);
    }

    async Task EnterAsync(EntityId role)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        connection!.RequestEntry(role);
        await OnlineBootstrap.WaitReadyAsync(connection, role, deadline.Token).ConfigureAwait(false);
        if (File.Exists(journal)) File.Delete(journal);
        Ready = true;
    }

    public RemoteGameSession TakeConnection()
    {
        if (!Ready || connection is null) throw new InvalidOperationException("Admission is not ready.");
        var result = connection;
        connection = null;
        return result;
    }

    static Exception Failure(CreationReply reply) => new InvalidOperationException($"Server: {reply.Outcome} ({reply.Failure}).");
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        journalLock?.Dispose();
        lifetime.Dispose();
    }
}
