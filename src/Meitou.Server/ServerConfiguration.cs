using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meitou.Server;

public sealed record ServerConfiguration
{
    public int Version { get; init; } = 1;
    public string BindAddress { get; init; } = "127.0.0.1";
    public string AdvertisedHost { get; init; } = "localhost";
    public int Port { get; init; } = 27800;
    public int MaximumPlayers { get; init; } = 32;
    public bool AllowDeviceRegistration { get; init; } = true;
    public string DataDirectory { get; init; } = "server-data";
    public ulong WorldSeed { get; init; } = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
    public string DefaultStart { get; init; } = "Wanderer";
    public int CharacterLimit { get; init; } = 30;
    public bool PauseWhenEmpty { get; init; } = true;
    public int SaveIntervalSeconds { get; init; } = 300;
    public string[] GameplayAssets { get; init; } = [];
    public int SimulationThreads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    public IReadOnlyDictionary<string, string> Rules() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["experimental"] = "1", ["default-start"] = DefaultStart,
        ["character-limit"] = CharacterLimit.ToString(CultureInfo.InvariantCulture),
        ["world-seed"] = WorldSeed.ToString(CultureInfo.InvariantCulture),
        ["pause-when-empty"] = PauseWhenEmpty ? "true" : "false",
    };
    public static ServerConfiguration Load(string path)
    {
        path = Path.GetFullPath(path);
        var json = new JsonSerializerOptions { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new ServerConfiguration(), json));
        }
        var config = JsonSerializer.Deserialize<ServerConfiguration>(File.ReadAllBytes(path), json) ?? throw new InvalidDataException("Missing server configuration.");
        if (config.Version != 1 || !IPAddress.TryParse(config.BindAddress, out _) || string.IsNullOrWhiteSpace(config.AdvertisedHost)
            || config.Port is < 1 or > 65535 || config.MaximumPlayers is < 1 or > 1024 || string.IsNullOrWhiteSpace(config.DataDirectory)
            || string.IsNullOrWhiteSpace(config.DefaultStart) || config.CharacterLimit is < 1 or > 1024 || config.SaveIntervalSeconds is < 10 or > 86400
            || config.SimulationThreads is < 1 or > 64 || config.GameplayAssets is null)
            throw new InvalidDataException("Invalid server configuration.");
        return config with { DataDirectory = Path.GetFullPath(config.DataDirectory, Path.GetDirectoryName(path)!) };
    }
}
