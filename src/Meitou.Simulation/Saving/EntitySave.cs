using System.Globalization;
using System.Text.Json;
using Meitou.Data.Fcs;
using Meitou.Data.Save;

namespace Meitou.Simulation.Saving;

/// <summary>
/// Meitou extension: persistent identities, including unloaded/dead/removed entities, in the same save as character records.
/// </summary>
public static class EntitySave
{
    public const string CharacterKey = "meitou entity";
    public const string RegistryKey = "meitou entity registry";
    const int Version = 1;
    sealed record Registry(int Version, ulong LastAllocated, ulong[] Entities);

    public static EntityId Read(SaveCharacter character)
    {
        if (character.GameState?.Strings.TryGetValue(CharacterKey, out var text) != true) return default;
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) || value == 0)
            throw new InvalidDataException($"Invalid persistent identity for character '{character.Name}'.");
        return new EntityId(value);
    }

    internal static void Write(FcsRecord state, EntityId entity)
    {
        if (entity.IsNone) state.Strings.Remove(CharacterKey);
        else state.Strings[CharacterKey] = entity.Value.ToString(CultureInfo.InvariantCulture);
    }

    internal static void Capture(World world, SaveGame save)
    {
        var registry = new Registry(Version, world.Entities.LastAllocated, world.Entities.All.Select(e => e.Value).ToArray());
        Validate(registry, save);
        if (registry.LastAllocated == 0) save.Camera.Record.Strings.Remove(RegistryKey);
        else save.Camera.Record.Strings[RegistryKey] = JsonSerializer.Serialize(registry);
    }

    /// <summary>
    /// Validate the entire identity map before modifying the target world or allocating any entity.
    /// </summary>
    internal static void Restore(World world, SaveGame save)
    {
        Registry registry;
        try
        {
            registry = save.Camera.Record.Strings.TryGetValue(RegistryKey, out var text)
                ? JsonSerializer.Deserialize<Registry>(text) ?? throw new InvalidDataException("Missing identity registry.")
                : new Registry(Version, 0, []);
        }
        catch (JsonException e) { throw new InvalidDataException("Malformed identity registry.", e); }
        Validate(registry, save);
        world.Entities.Restore(registry.LastAllocated, registry.Entities);
    }

    static void Validate(Registry registry, SaveGame save)
    {
        if (registry.Version != Version || registry.Entities is null)
            throw new InvalidDataException("Unsupported identity registry.");
        var known = new HashSet<ulong>();
        foreach (ulong value in registry.Entities)
            if (value == 0 || value > registry.LastAllocated || !known.Add(value))
                throw new InvalidDataException("Invalid or duplicate reserved identity.");
        var characters = new HashSet<ulong>();
        foreach (var character in save.Platoons.SelectMany(p => p.Characters))
        {
            var entity = Read(character);
            if (!entity.IsNone && (!known.Contains(entity.Value) || !characters.Add(entity.Value)))
                throw new InvalidDataException("A character identity is unreserved or names more than one character.");
        }
    }
}