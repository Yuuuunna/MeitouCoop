using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using System.Globalization;
using Meitou.Content;
using Meitou.Simulation;
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
    public GameplayRules Gameplay { get; init; } = new()
    {
        AutoSaveSeconds = 300
    };

    // Legacy server.json key; normalized into Gameplay.AutoSaveSeconds on load.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SaveIntervalSeconds { get; init; }
    public string[] GameplayAssets { get; init; } = [];
    public string? ModsCfg { get; init; }
    public string[] TrustedCodeMods { get; init; } = [];
    public Dictionary<string, Dictionary<string, string>> ModSettings { get; init; } = new(StringComparer.Ordinal);
    public int SimulationThreads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    public IReadOnlyDictionary<string, string> Rules(GameplayRules? effective = null)
    {
        var rules = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["experimental"] = "1",
            ["default-start"] = DefaultStart,
            ["character-limit"] = CharacterLimit.ToString(CultureInfo.InvariantCulture),
            ["world-seed"] = WorldSeed.ToString(CultureInfo.InvariantCulture),
            ["pause-when-empty"] = PauseWhenEmpty ? "true" : "false",
        };
        foreach (var setting in JsonSerializer.SerializeToElement(effective ?? Gameplay).EnumerateObject())
            rules.Add("gameplay/" + setting.Name, setting.Value.GetRawText());
        return rules;
    }

    static bool ValidGameplay(GameplayRules? rules)
    {
        static bool Range(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;
        return rules is not null && Enum.IsDefined(rules.Dismemberment) && (rules.AttackSlots is null or >= 1 and <= 5) && rules.HarpoonLimit is >= 10 and <= 10000 && rules.TownAttacks is >= 0 and <= 5 && rules.GroundItemLimit is >= 1 and <= Meitou.Simulation.Items.GroundItems.MaxLimit && (rules.AutoSaveSeconds == 0 || rules.AutoSaveSeconds is >= 10 and <= 86400) && Range(rules.SquadSize, .5f, 3) && Range(rules.Population, .5f, 4) && Range(rules.RaidSize, .25f, 4) && Range(rules.RaidFrequency, .25f, 3) && Range(rules.GlobalDamageMultiplier, .5f, 4) && Range(rules.HungerTime, .25f, 8) && Range(rules.ChanceOfDeath, .5f, 4) && Range(rules.ProductionSpeed, .5f, 2) && Range(rules.ResearchSpeed, .5f, 2) && Range(rules.BuildingSpeed, .5f, 2) && Range(rules.NestsMultiplier, .5f, 4);
    }

    public static ServerConfiguration Load(string path)
    {
        path = Path.GetFullPath(path);
        var json = new JsonSerializerOptions
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new ServerConfiguration(), json));
        }

        var config = JsonSerializer.Deserialize<ServerConfiguration>(File.ReadAllBytes(path), json) ?? throw new InvalidDataException("Missing server configuration.");
        if (config.Version != 1 || !IPAddress.TryParse(config.BindAddress, out _) || string.IsNullOrWhiteSpace(config.AdvertisedHost) || config.Port is < 1 or > 65535 || config.MaximumPlayers is < 1 or > 1024 || string.IsNullOrWhiteSpace(config.DataDirectory) || string.IsNullOrWhiteSpace(config.DefaultStart) || config.CharacterLimit is < 1 or > 1024 || !ValidGameplay(config.Gameplay) || config.SaveIntervalSeconds is < 0 or > 86400 || config.SaveIntervalSeconds is> 0 and < 10 || config.SimulationThreads is < 1 or > 64 || config.GameplayAssets is null || config.TrustedCodeMods is null || config.ModSettings is null || config.ModsCfg is not null && string.IsNullOrWhiteSpace(config.ModsCfg) || config.TrustedCodeMods.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." || name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':')) || config.TrustedCodeMods.Distinct(StringComparer.Ordinal).Count() != config.TrustedCodeMods.Length || config.ModSettings.Any(p => !CodeModManifest.IsValidId(p.Key) || p.Value is null || p.Value.Any(setting => string.IsNullOrWhiteSpace(setting.Key) || setting.Key.Length > 256 || setting.Key.Any(char.IsControl) || setting.Value is null)) || config.GameplayAssets.Any(asset => string.IsNullOrWhiteSpace(asset) || asset.Length > 1024 || Path.IsPathRooted(asset) || asset.Any(c => char.IsControl(c) || c == ':') || asset.Replace('\\', '/').Split('/').Any(part => part is "" or "." or "..")))
            throw new InvalidDataException("Invalid server configuration.");
        return config with
        {
            Gameplay = config.Gameplay with
            {
                AutoSaveSeconds = config.SaveIntervalSeconds ?? config.Gameplay.AutoSaveSeconds
            },
            SaveIntervalSeconds = null,
            DataDirectory = Path.GetFullPath(config.DataDirectory, Path.GetDirectoryName(path)!),
            ModsCfg = config.ModsCfg is null ? null : Path.GetFullPath(config.ModsCfg, Path.GetDirectoryName(path)!)
        };
    }
}