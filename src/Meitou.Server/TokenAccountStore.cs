using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Networking;
using Meitou.Simulation;

namespace Meitou.Server;
public sealed class AuthenticatedAccount
{
    internal AuthenticatedAccount(object issuer, Guid id, ulong revision) => (Issuer, Id, Revision) = (issuer, id, revision);
    internal object Issuer { get; }
    public Guid Id { get; }
    public ulong Revision { get; }
    public AccountIdentity Identity => new("local-token", Id.ToString("N"));
}

public sealed record IssuedCredential(Guid Account, string Token)
{
    public override string ToString() => $"IssuedCredential {{ Account = {Account}, Token = [redacted] }}";
}

public sealed record ServerAccount(Guid Id, string Name, ulong Revision, bool Enabled);
/// <summary>
/// Random bearer credentials, issued by the server or enrolled by a device over compatible TLS. No passwords or plaintext tokens are stored.
/// </summary>
public sealed class TokenAccountStore
{
    const int MaximumBytes = 8 * 1024 * 1024, MaximumAccounts = 10000;
    static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 8,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    readonly string _path;
    readonly object _issuer = new();
    readonly Action? _beforePublish;
    readonly Lock _cacheGate = new();
    Dictionary<Guid, Entry>? _cached;
    long _cacheUntil;
    readonly Queue<long> _deviceRegistrations = new();
    public TokenAccountStore(string path, Action? beforePublish = null) => (this._path, this._beforePublish) = (Path.GetFullPath(path), beforePublish);
    sealed record Entry(Guid Id, string Name, ulong Revision, bool Enabled, string Digest);
    sealed record Document(int Version, Entry[] Accounts);
    public IReadOnlyList<ServerAccount> List() => Read().Accounts.Select(e => new ServerAccount(e.Id, e.Name, e.Revision, e.Enabled)).ToArray();
    public IssuedCredential Create(string name)
    {
        if (!ValidName(name))
            throw new ArgumentException("Account label must contain 1–64 non-control characters without surrounding whitespace.", nameof(name));
        var credential = new IssuedCredential(Guid.NewGuid(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Mutate(doc =>
        {
            if (doc.Accounts.Length >= MaximumAccounts || doc.Accounts.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Account limit or duplicate account label.");
            return doc with
            {
                Accounts = [..doc.Accounts, new(credential.Account, name, 1, true, Digest(credential.Token))]
            };
        });
        return credential;
    }

    public bool RegisterDevice(AuthenticationRequest request)
    {
        if (!AuthenticationProtocol.ValidCredential(request))
            return false;
        if (Read().Accounts.Any(e => e.Id == request.Account))
            return Authenticate(request)is not null;
        lock (_deviceRegistrations)
        {
            long now = Stopwatch.GetTimestamp();
            while (_deviceRegistrations.TryPeek(out long previous) && Stopwatch.GetElapsedTime(previous, now).TotalMinutes >= 1)
                _deviceRegistrations.Dequeue();
            if (_deviceRegistrations.Count >= 32)
                return false;
            _deviceRegistrations.Enqueue(now);
            Mutate(doc =>
            {
                if (doc.Accounts.Any(e => e.Id == request.Account))
                    return doc;
                if (doc.Accounts.Length >= MaximumAccounts)
                    throw new InvalidOperationException("Account limit reached.");
                string name = "device-" + request.Account.ToString("N");
                if (doc.Accounts.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || e.Digest == Digest(request.Token)))
                    throw new InvalidOperationException("Device identity conflicts with an existing account.");
                return doc with
                {
                    Accounts = [..doc.Accounts, new(request.Account, name, 1, true, Digest(request.Token))]
                };
            });
        }

        return Authenticate(request)is not null;
    }

    public IssuedCredential Rotate(Guid account)
    {
        var credential = new IssuedCredential(account, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Mutate(doc => Replace(doc, account, e => e with { Digest = Digest(credential.Token), Revision = checked(e.Revision + 1) }));
        return credential;
    }

    public void SetEnabled(Guid account, bool enabled) => Mutate(doc => Replace(doc, account, e => e.Enabled == enabled ? e : e with { Enabled = enabled, Revision = checked(e.Revision + 1) }));
    public AuthenticatedAccount? Authenticate(AuthenticationRequest request)
    {
        if (!AuthenticationProtocol.ValidCredential(request))
            return null;
        var entry = Read().Accounts.FirstOrDefault(e => e.Id == request.Account);
        byte[] supplied = SHA256.HashData(Convert.FromHexString(request.Token));
        byte[] expected = entry is not null ? Convert.FromHexString(entry.Digest) : new byte[32];
        bool matches = CryptographicOperations.FixedTimeEquals(supplied, expected);
        if (!matches || entry is not { Enabled: true })
            return null;
        lock (_cacheGate)
            _cached = null; // a freshly verified login must be rechecked against current revisions
        return new(_issuer, entry.Id, entry.Revision);
    }

    public bool IsCurrent(AuthenticatedAccount principal)
    {
        if (!ReferenceEquals(principal.Issuer, _issuer))
            return false;
        lock (_cacheGate)
        {
            long now = Stopwatch.GetTimestamp();
            if (_cached is null || now >= _cacheUntil)
            {
                _cached = Read().Accounts.ToDictionary(e => e.Id);
                _cacheUntil = now + Stopwatch.Frequency;
            }

            return _cached.TryGetValue(principal.Id, out var entry) && entry.Enabled && entry.Revision == principal.Revision;
        }
    }

    static string Digest(string token) => Convert.ToHexString(SHA256.HashData(Convert.FromHexString(token)));
    static bool ValidName(string? name) => name is { Length: > 0 and <= 64 } && name.Trim() == name && !name.Any(char.IsControl);
    static Document Replace(Document doc, Guid account, Func<Entry, Entry> replace)
    {
        if (!doc.Accounts.Any(e => e.Id == account))
            throw new KeyNotFoundException("Unknown account.");
        return doc with
        {
            Accounts = doc.Accounts.Select(e => e.Id == account ? replace(e) : e).ToArray()
        };
    }

    Document Read()
    {
        if (!File.Exists(_path))
            return new(1, []);
        using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("Invalid account-store size.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 8 });
            var fields = new Stack<HashSet<string>>();
            while (reader.Read())
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        fields.Push(new(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        fields.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (!fields.Peek().Add(reader.GetString()!))
                            throw new InvalidDataException("Duplicate account-store field.");
                        break;
                }

            var doc = JsonSerializer.Deserialize<Document>(bytes, Json) ?? throw new InvalidDataException("Missing account store.");
            Validate(doc);
            return doc;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Malformed account store.", e);
        }
    }

    static void Validate(Document doc)
    {
        if (doc.Version != 1 || doc.Accounts is null || doc.Accounts.Length > MaximumAccounts || doc.Accounts.Any(e => e is null || e.Id == Guid.Empty || !ValidName(e.Name) || e.Revision == 0 || e.Digest is not { Length: 64 } || e.Digest.Any(c => !char.IsAsciiHexDigitUpper(c))) || doc.Accounts.Select(e => e.Id).Distinct().Count() != doc.Accounts.Length || doc.Accounts.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != doc.Accounts.Length || doc.Accounts.Select(e => e.Digest).Distinct(StringComparer.Ordinal).Count() != doc.Accounts.Length)
            throw new InvalidDataException("Invalid account-store records.");
    }

    void Mutate(Func<Document, Document> change)
    {
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        using var gate = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var updated = change(Read());
        Validate(updated);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(updated, Json);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("Account store is too large.");
        string temporary = Path.Combine(directory, ".accounts-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }

            _beforePublish?.Invoke();
            lock (_cacheGate)
            {
                File.Move(temporary, _path, overwrite: true);
                _cached = null;
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}