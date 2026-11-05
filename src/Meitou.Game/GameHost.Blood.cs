using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Audio;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The blood on the ground (docs/game/blood-decals.md): with the game's <c>Blood</c> setting on, the simulation's <see cref="World.BloodMarks"/> become terrain decals in
/// a <see cref="TerrainDecalStore"/> that ages with game time, drawn by the world frame's <see cref="TerrainDecalRenderer"/> within <c>Decal Range</c> at
/// <c>Decal Resolution</c> (the install's <c>settings.cfg</c>). Display only: the simulation is not affected.
/// </summary>
sealed partial class GameHost
{
    /// <summary>The <c>Blood</c> resource group's folders by decal type (<see cref="TerrainDecalType"/>), the game's names for them.</summary>
    static readonly string[] BloodFolders = ["line", "splash", "pool", "smear"];

    /// <summary>How far above the ground a mark is still left on it (an engine rule: a character on an upper floor of a building bleeds onto the floor, which has no decals).</summary>
    const float BloodAboveGround = 20;

    TerrainDecalStore? decalStore;
    object? bloodFor;
    long bloodTick;
    double debrisClock;
    readonly Random bloodRandom = new();

    /// <summary>Makes the store and the renderer when the install's <c>Blood</c> setting is on (missing: on).</summary>
    void SetUpBlood(IGpuContext context)
    {
        gpu.Debris ??= new DebrisRenderer(context, gpu.Objects?.Resolver, assets.Find) { SharedTextures = () => gpu.Characters?.Textures };   // made now: its shaders compile here, not at the first burst
        gpu.Particles?.PrepareBursts(BurstSplash, BurstSpray, SparksSystem);
        if (gpu.Decals is not null || AudioSystem.Setting(install, "Blood", 1) == 0) return;
        var files = BloodTextureFiles(assets.Configured);
        decalStore = new TerrainDecalStore([.. files.Select(list => (IReadOnlyList<(int, int)>)[.. list.Select(DdsSize)])]);
        gpu.Decals = TerrainDecalRenderer.Create(context, files);
        gpu.Decals.Meitou = o.MeitouBlood;
        gpu.Decals.GroundHeight = (x, z) => gpu.Terrain.HeightAt(x, z);
        gpu.Decals.Surfaces = o.MeitouBloodSurfaces;
        gpu.Decals.Resolution = (int)AudioSystem.Setting(install, "Decal Resolution", 1024, 512, 2048);
        gpu.Decals.Range = AudioSystem.Setting(install, "Decal Range", 1000, 0, 5000);
        gpu.DecalStore = decalStore;
    }

    /// <summary>
    /// Once a frame: luckyren Mode handed to the world, the marks of the ticks since the last frame into the store (bursts thrown), and the decals aged and the
    /// pieces moved by the game time they took.
    /// </summary>
    void UpdateBlood()
    {
        if (session is Meitou.Engine.WorldSession local) local.World.Luckyren = config.LuckyrenMode;
        if (!session.HasWorldView)
        {
            if (bloodFor is not null) { BloodForNewWorld(null); bloodFor = null; }
            return;
        }
        if (!ReferenceEquals(bloodFor, session.WorldIdentity)) BloodForNewWorld(null);
        UpdateBlockSparks();
        bool enabled = decalStore is not null || session.IsRemote || config.LuckyrenMode || pieces.Count > 0;
        session.EnableBlood(enabled);
        if (!enabled)
        {
            gpu.DebrisPieces.Clear();
            AddBolts();   // the bolts are drawn whatever the Blood setting
            AddTracers();
            UpdateFalls();   // and the severed limbs falling
            return;
        }
        float tick = session.SimulationStepSeconds;
        while (session.TryDequeueBlood(out var visual))
        {
            var m = visual.Mark;
            if (m.Kind == BloodMarkKind.Explosion)
            {
                Burst(visual);
                continue;
            }
            if (decalStore is not { } store) continue;
            uint opaque = 0xFF000000 | Abgr(m.Colour);
            long who = (long)(uint)m.Who.Slot << 32 | (uint)m.Who.Generation;
            if (gpu.Decals is { Surfaces: true } surfaces) SurfaceMark(surfaces, m, who, opaque, visual.TickSeconds);
            if (m.Position.Y > gpu.Terrain.HeightAt(m.Position.X, m.Position.Z) + BloodAboveGround) continue;
            switch (m.Kind)
            {
                case BloodMarkKind.Pool: store.Bleed(who, m.Position, m.Amount, visual.TickSeconds, opaque, bloodRandom); break;
                case BloodMarkKind.Smear:
                    store.Add(TerrainDecalType.Smear, m.Position, m.Direction, 0.2f, (uint)(Math.Clamp(m.Amount, 0, 1) * 255) << 24 | Abgr(m.Colour), TerrainDecalStore.NewLife, bloodRandom);
                    break;
                case BloodMarkKind.Drip: store.Add(TerrainDecalType.Pool, m.Position, m.Direction, m.Amount, opaque, TerrainDecalStore.NewLife, bloodRandom); break;
                case BloodMarkKind.Splash: store.Add(TerrainDecalType.Splash, m.Position, m.Direction, m.Amount, opaque, TerrainDecalStore.NewLife, bloodRandom); break;
            }
        }
        long ticks = session.CommandTick - bloodTick;
        bloodTick = session.CommandTick;
        if (ticks > 0 && decalStore is { } aging && gpu.Decals is { } decals) { aging.Age(ticks * tick, decals.Drawn); decals.AgeSurfaces(ticks * tick); }
        // The pieces move by the game time between frames, the tick count plus the simulation's alpha, so they fly smoothly at the display rate, not in tick steps.
        double clock = session.PresentationTick;   // the shown tick: one run ahead is not on screen yet
        UpdateDebris((float)Math.Clamp((clock - debrisClock) * tick, 0, 0.5));
        debrisClock = clock;
    }

    /// <summary>One mark as the <c>bloodsurfaces</c> switch's decals (<see cref="TerrainDecalRenderer.Surfaces"/>); the same marks and sizes as the terrain's.</summary>
    void SurfaceMark(TerrainDecalRenderer surfaces, BloodMark m, long who, uint opaque, float tick)
    {
        switch (m.Kind)
        {
            case BloodMarkKind.Pool: surfaces.BleedSurface(who, m.Position, m.Amount, tick, opaque, bloodRandom); break;
            case BloodMarkKind.Smear:
                surfaces.AddSurface(TerrainDecalType.Smear, m.Position, m.Direction, 0.2f, (uint)(Math.Clamp(m.Amount, 0, 1) * 255) << 24 | Abgr(m.Colour), bloodRandom);
                break;
            case BloodMarkKind.Drip: surfaces.AddSurface(TerrainDecalType.Pool, m.Position, m.Direction, m.Amount, opaque, bloodRandom); break;
            case BloodMarkKind.Splash: surfaces.AddSurface(TerrainDecalType.Splash, m.Position, m.Direction, m.Amount, opaque, bloodRandom); break;
        }
    }

    /// <summary>The EFFECT `small- sparks1` (5463-nodes_furniture1_otto.mod) the original throws on a parry: its particle system.</summary>
    const string SparksSystem = "kenshi_sparks";

    /// <summary>
    /// Once a frame: the parries of the ticks since the last frame throw <see cref="SparksSystem"/> at the parrying character's <c>Bip01 Prop2</c> (its weapon), 0 or 1 unit higher
    /// at random, when that character is drawn (the original's <c>FUN_1404394f0</c>: only for a character on screen; docs/game/combat.md "Block sparks").
    /// </summary>
    void UpdateBlockSparks()
    {
        if (gpu.Particles is not { } particles || gpu.Characters is not { } characters)
        {
            session.EnableBlockSparks(false);
            return;
        }
        session.EnableBlockSparks(true);
        while (session.TryDequeueBlockSpark(out var who))
            if (characters.BonePosition(DrawKey(who), "Bip01 Prop2") is { } at)
                particles.Burst(SparksSystem, at + new Vector3(0, bloodRandom.Next(2), 0), Quaternion.Identity, 0.1f);
    }

    /// <summary>
    /// The store emptied for the session's world and hooked to its marks, then given the decals of the save's TERRAIN_DECALS record <paramref name="saved"/> when the
    /// world was loaded from one (the load calls it before the new world's first frame).
    /// </summary>
    void BloodForNewWorld(FcsRecord? saved)
    {
        pieces.Clear();
        if (gpu is not null) { gpu.DebrisPieces.Clear(); gpu.Particles?.ClearBursts(); }
        bloodFor = session.WorldIdentity;
        bloodTick = session.CommandTick;
        debrisClock = session.PresentationTick;
        session.EnableBlood(decalStore is not null || session.IsRemote || config.LuckyrenMode);
        if (decalStore is not { } store) return;
        store.Clear();
        gpu?.Decals?.ClearSurfaces();
        if (saved is not null) { store.Load(SaveDecals.Read(saved)); gpu?.Decals?.LoadSurfaces(SaveSurfaceDecals.Read(saved)); }
    }

    /// <summary>The store's decals into the save's TERRAIN_DECALS record (with <c>Blood</c> off the record keeps what the loaded save had).</summary>
    void WriteDecals(SaveGame game)
    {
        if (decalStore is { } store && game.Decals is { } record) { SaveDecals.Write(record, store.All); if (gpu?.Decals is { } decals) SaveSurfaceDecals.Write(record, decals.SavedSurfaces); }
    }

    /// <summary>The default <c>blood colour</c> (RACE, 0x800000), the red the blood particles' textures already are.</summary>
    const uint DefaultBloodColour = 0x800000;

    /// <summary>
    /// The recolour a blood particle burst takes for a race's <c>blood colour</c> <paramref name="rgb"/> (<see cref="ParticleRenderer.Burst"/>): none for the default red, which the
    /// textures show as they are; else the colour scaled as the default would be to full red, so a race's blood is as bright against its hue as red blood is.
    /// </summary>
    static Vector3? BloodRecolour(uint rgb) =>
        rgb == DefaultBloodColour ? null : new Vector3(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF) / 0x80;

    /// <summary>0xRRGGBB as the decal's ABGR colour bytes (alpha 0).</summary>
    static uint Abgr(uint rgb) => (rgb >> 16 & 0xFF) | (rgb & 0xFF00) | (rgb & 0xFF) << 16;

    /// <summary>The <c>.dds</c> files of the <c>Blood</c> group's folders, by <see cref="BloodFolders"/> type, in registration order and by name within a folder.</summary>
    static List<IReadOnlyList<string>> BloodTextureFiles(OgreScriptResources resources)
    {
        var files = BloodFolders.Select(_ => new List<string>()).ToList();
        foreach (var location in resources.Locations)
        {
            if (!location.Group.Equals("Blood", StringComparison.Ordinal) || !Directory.Exists(location.Path)) continue;
            int type = Array.FindIndex(BloodFolders, f => f.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(location.Path)), StringComparison.OrdinalIgnoreCase));
            if (type < 0) continue;
            files[type].AddRange(Directory.EnumerateFiles(location.Path, "*.dds").Order(StringComparer.OrdinalIgnoreCase));
        }
        return [.. files.Cast<IReadOnlyList<string>>()];
    }

    /// <summary>A DDS file's size in texels from its header (0 × 0 when unreadable).</summary>
    static (int Width, int Height) DdsSize(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[20];
            using var file = File.OpenRead(path);
            file.ReadExactly(header);
            return (BitConverter.ToInt32(header[16..]), BitConverter.ToInt32(header[12..]));
        }
        catch (IOException) { return (0, 0); }
    }
}
