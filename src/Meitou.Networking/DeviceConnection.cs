using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meitou.Connections;
using Meitou.Content;

namespace Meitou.Networking;

/// <summary>
/// Persistent per-server device identity, saved before the server can register it. Never a user-managed connection profile.
/// </summary>
public static class DeviceConnection
{
    sealed record Identity(Guid Account, string Token)
    {
        public override string ToString() => $"Device identity {Account} [redacted]";
    }

    /// <summary>
    /// Parses the address with the same syntax the launcher accepts.
    /// </summary>
    public static (string Host, int Port) ParseAddress(string address) => ServerAddress.Parse(address);

    public static async Task<ServerConnectionProfile> PrepareAsync(GameInstall install, string address, CancellationToken token, string? identityDirectory = null)
    {
        var(host, port) = ParseAddress(address);
        string? configuredDirectory = Environment.GetEnvironmentVariable("MEITOU_IDENTITY_DIRECTORY");
        string folder = identityDirectory ?? (string.IsNullOrWhiteSpace(configuredDirectory) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "identities") : configuredDirectory);
        Directory.CreateDirectory(folder);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{host}:{port}")));
        string path = Path.Combine(folder, key + ".json");
        using var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Identity? identity = null;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 4096)
                throw new InvalidDataException("Invalid saved device identity; it has not been replaced.");
            identity = JsonSerializer.Deserialize<Identity>(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
            if (identity is null || !AuthenticationProtocol.ValidCredential(new(identity.Account, identity.Token)))
                throw new InvalidDataException("Invalid saved device identity; it has not been replaced.");
        }

        var information = await ServerDiscovery.QueryAsync(host, port, token).ConfigureAwait(false);
        if (!information.DeviceRegistration && identity is null)
            throw new InvalidOperationException("This server has disabled automatic player registration.");
        var hello = ServerCompatibility.CreateHello(install, ServerCompatibility.EngineBuild, information.Rules, information.GameplayAssets);
        if (new CompatibilityHandshake(hello).Receive(information.Hello)is var mismatch && mismatch != CompatibilityFailure.None)
            throw new InvalidDataException($"Local game content does not match the server: {mismatch}.");
        if (identity is null)
        {
            identity = new(Guid.NewGuid(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            string temporary = path + ".tmp";
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(file, identity);
                file.Flush(true);
            }

            File.Move(temporary, path, overwrite: true);
        }

        return new(host, port, identity.Account, identity.Token, hello)
        {
            Rules = information.Rules,
            GameplayAssets = information.GameplayAssets
        };
    }
}
